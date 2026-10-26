using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Training;

/// <summary>Short cross-process transactions, separate from long artifact ownership.</summary>
internal sealed class TrainingRecordStore(string root, string projectId)
{
    private const int MaximumBytes = 1_048_576;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private string PathFor(string relative) => ProjectPaths.Resolve(root, "training/" + relative);

    public async Task<T> TransactionAsync<T>(Func<TrainingEnvelope, (TrainingEnvelope? Write, T Value)> action, CancellationToken token)
    {
        // A missing read has an empty committed baseline and creates no directory or lock file.
        if (!Directory.Exists(ProjectPaths.Resolve(root, "training")))
        {
            var empty = action(new(1, projectId, null, []));
            if (empty.Write == null) return empty.Value;
        }
        Directory.CreateDirectory(ProjectPaths.Resolve(root, "training"));
        // Resolve again after creation to reject a link substituted at the boundary.
        string lockPath = PathFor("record.lock");
        FileStream? ownership = null;
        var elapsed = Stopwatch.StartNew();
        while (ownership == null)
        {
            token.ThrowIfCancellationRequested();
            try { ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new StudioException("training.busy");
                await Task.Delay(20, token);
            }
        }
        await using (ownership)
        {
            var envelope = await ReadAsync(token);
            var result = action(envelope);
            if (result.Write != null) await SaveAsync(result.Write, token);
            return result.Value;
        }
    }

    private async Task<TrainingEnvelope> ReadAsync(CancellationToken token)
    {
        string path = PathFor("state.json");
        if (!File.Exists(path)) return new(1, projectId, null, []);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumBytes) throw new StudioException("training.state.invalid");
        try
        {
            var envelope = await JsonSerializer.DeserializeAsync<TrainingEnvelope>(input, Json, token);
            Validate(envelope); return envelope!;
        }
        catch (JsonException) { throw new StudioException("training.state.invalid"); }
    }

    private async Task SaveAsync(TrainingEnvelope envelope, CancellationToken token)
    {
        Validate(envelope);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        if (bytes.Length > MaximumBytes) throw new StudioException("training.state.limit");
        string part = PathFor(Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(bytes, token); await output.FlushAsync(token); output.Flush(true); }
            token.ThrowIfCancellationRequested();
            File.Move(part, PathFor("state.json"), overwrite: true);
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }

    private void Validate(TrainingEnvelope? envelope)
    {
        if (envelope == null || envelope.Format != 1 || envelope.ProjectId != projectId || envelope.Receipts == null
            || envelope.Receipts.Count > 256 || envelope.Receipts.Any(x => x == null || !HashValid(x.KeyHash) || !HashValid(x.RequestHash)))
            throw new StudioException("training.state.invalid");
        if (envelope.Current is not { } record) return;
        var s = record.Snapshot;
        if (s == null || s.ProjectId != projectId || !Id(s.Id) || s.Revision < 1 || s.GlobalStep < 0 || s.CompletedEpoch < 0 || s.EpochSize < 0
            || s.State is not ("queued" or "preparing" or "training" or "stop_requested" or "paused" or "completed" or "interrupted" or "failed")
            || s.Models == null || s.Models.Count > 256 || s.Models.Any(x => x == null || !Id(x.Id) || x.GlobalStep < 1 || x.CompletedEpoch < 0 || x.QualityEstablished)
            || s.Models.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != s.Models.Count
            || record.Recording == null || record.Recording.Id != s.SourceRecordingId || !record.Recording.RightsConfirmed
            || !Id(record.ActionId) || record.OutputRelativeRoot != $"training/actions/{record.ActionId}" || record.SourceProjectRevision < 1)
            throw new StudioException("training.state.invalid");
        _ = ProjectPaths.Resolve(root, record.OutputRelativeRoot);
        _ = ProjectPaths.Resolve(root, record.Recording.WavRelativePath);
        if (record.Accepted is { } accepted)
        {
            ValidateReference(accepted);
        }
        if (record.ModelReferences is { } references)
        {
            if (references.Count > s.Models.Count) throw new StudioException("training.state.invalid");
            foreach (var (id, reference) in references)
            {
                var model = s.Models.SingleOrDefault(x => x.Id == id);
                if (!Id(id) || model == null || reference == null || model.GlobalStep != reference.GlobalStep || model.CompletedEpoch != reference.CompletedEpoch)
                    throw new StudioException("training.state.invalid");
                ValidateReference(reference);
            }
        }
    }
    private void ValidateReference(WorkflowReference reference)
    {
        if (!HashValid(reference.Sha256) || reference.GlobalStep < 1 || reference.CompletedEpoch < 0 || reference.EpochSize < 1
            || !WithinTraining(root, reference.Path)) throw new StudioException("training.state.invalid");
    }
    internal static WorkflowReference? ModelReference(TrainingRecord record, string modelId)
    {
        if (!record.Snapshot.Models.Any(x => x.Id == modelId)) return null;
        if (record.ModelReferences?.TryGetValue(modelId, out var reference) == true) return reference;
        if (record.ModelReferences != null) return null;
        // Old records only know the latest accepted state. Never discover files
        // or guess which historical checkpoint an old display ID meant.
        if (record.Accepted is not { } accepted || record.Snapshot.Models.Count == 0
            || record.Snapshot.Models[^1].Id != modelId) return null;
        var matches = record.Snapshot.Models.Where(x => x.GlobalStep == accepted.GlobalStep && x.CompletedEpoch == accepted.CompletedEpoch).ToArray();
        return matches.Length == 1 && matches[0].Id == modelId && record.Snapshot.Models.All(x => x.GlobalStep <= accepted.GlobalStep)
            ? accepted : null;
    }
    internal static TrainingSnapshotDto PublicSnapshot(TrainingRecord record)
        => record.Snapshot with { ExecutionId = record.ActionId,
            Models = record.Snapshot.Models.Select(x => x with { Selectable = ModelReference(record, x.Id) != null }).ToArray() };
    internal static bool Active(string state) => state is "queued" or "preparing" or "training" or "stop_requested";
    internal static bool Id(string? value) => value != null && Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    internal static bool HashValid(string? value) => value is { Length: 64 } && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool WithinTraining(string root, string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!relative.StartsWith("training/actions/", StringComparison.Ordinal)) return false;
        try { return string.Equals(ProjectPaths.Resolve(root, relative), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    internal static void Key(string key)
    { if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(char.IsControl)) throw new StudioException("training.input.invalid"); }
    internal static TrainingSnapshotDto? Replay(TrainingEnvelope envelope, string key, string request)
    {
        Key(key);
        var receipt = envelope.Receipts.SingleOrDefault(x => x.KeyHash == Hash(key));
        if (receipt == null) return null;
        if (receipt.RequestHash != Hash(request)) throw new StudioException("training.idempotency.conflict");
        return receipt.Snapshot;
    }
    internal static TrainingEnvelope Receipt(TrainingEnvelope envelope, TrainingRecord record, string key, string request)
    {
        if (envelope.Receipts.Count >= 256) throw new StudioException("training.state.limit");
        return envelope with { Current = record, Receipts = [.. envelope.Receipts, new(Hash(key), Hash(request), record.Snapshot)] };
    }
}

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace LocalAudiobookStudio.Application.VoxCpmTraining;

/// <summary>Short, cross-process atomic records. Never holds a transaction across native work.</summary>
internal sealed class VoxCpmTrainingRecordStore(string root, string projectId)
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 64 };
    private string PathFor(string relative) => ProjectPaths.Resolve(root, "voxcpm-training/" + relative);
    private VoxCpmTrainingEnvelope Empty() => new(1, projectId, null, [], [], []);

    internal async Task<T> TransactionAsync<T>(Func<VoxCpmTrainingEnvelope, (VoxCpmTrainingEnvelope? Write, T Value)> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string directory = ProjectPaths.Resolve(root, "voxcpm-training");
        if (!Directory.Exists(directory)) { var empty = action(Empty()); if (empty.Write == null) return empty.Value; }
        Directory.CreateDirectory(directory);
        using var pins = new CatalogPaths.Pins(); CatalogPaths.PinDirectories(directory, pins);
        FileStream? held = null; var waiting = Stopwatch.StartNew();
        while (held == null)
        {
            token.ThrowIfCancellationRequested();
            try { held = OpenLock(PathFor("record.lock")); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
            { if (waiting.Elapsed > TimeSpan.FromSeconds(3)) throw Error("busy"); await Task.Delay(20, token); }
        }
        await using (held)
        {
            var envelope = await ReadAsync(token); var result = action(envelope);
            if (result.Write != null) await SaveAsync(result.Write, token);
            return result.Value;
        }
    }
    private static FileStream OpenLock(string path)
    {
        var handle = CreateFile(CatalogPaths.SqlitePath(path), 0xc0000000, 0, 0, 4, 0x00200000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new IOException("voxcpm_training.storage", unchecked((int)0x80070000) | error);
        }
        try { CatalogPaths.ValidateHandle(handle, false); return new FileStream(handle, FileAccess.ReadWrite, 1); }
        catch { handle.Dispose(); throw; }
    }
    private async Task<VoxCpmTrainingEnvelope> ReadAsync(CancellationToken token)
    {
        string path = PathFor("state.json"); if (!File.Exists(path)) return Empty();
        try
        {
            await using var input = TtsCorpusSource.OpenRead(path);
            Require(input.Length is > 0 and <= MaximumBytes);
            byte[] bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, token);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 }); Unique(document.RootElement);
            var envelope = document.RootElement.Deserialize<VoxCpmTrainingEnvelope>(Json); Validate(envelope); return envelope!;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or NullReferenceException or ArgumentException or OverflowException)
        { throw Error("state.invalid"); }
    }
    private async Task SaveAsync(VoxCpmTrainingEnvelope envelope, CancellationToken token)
    {
        Validate(envelope, 257); var expired = new List<VoxCpmTrainingReceipt>();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        while (envelope.Receipts.Count > 0 && (envelope.Receipts.Count > 256 || bytes.Length > MaximumBytes))
        {
            expired.Add(envelope.Receipts[0]); envelope = envelope with { Receipts = envelope.Receipts.Skip(1).ToArray() };
            bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        }
        if (bytes.Length > MaximumBytes) throw Error("state.limit");
        Validate(envelope);
        foreach (var receipt in expired) PersistExpiredKey(receipt.KeyHash, token);
        string part = PathFor(Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(bytes, token); await output.FlushAsync(token); output.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(part, PathFor("state.json"), true);
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }

    private void Validate(VoxCpmTrainingEnvelope? e, int maxReceipts = 256)
    {
        Require(e != null && e.Format == 1 && e.ProjectId == projectId && e.Lineages != null && e.ModelReferences != null && e.Receipts != null);
        Require(e!.Receipts.Count <= maxReceipts && e.Lineages.Select(l => l?.TrainingId).Distinct().Count() == e.Lineages.Count
            && e.ModelReferences.Select(m => m?.Id).Distinct().Count() == e.ModelReferences.Count);
        var lineages = new Dictionary<string, VoxCpmTrainingLineage>(StringComparer.Ordinal);
        foreach (var lineage in e.Lineages)
        {
            Require(lineage != null && Id(lineage.TrainingId) && Id(lineage.ActionId) && ValidIdentity(lineage.Identity));
            ValidateCorpus(lineage!.Corpus); lineages.Add(lineage.TrainingId, lineage);
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var model in e.ModelReferences)
        {
            Require(model != null && Id(model.Id) && Id(model.TrainingId) && lineages.ContainsKey(model.TrainingId)
                && Id(model.ActionId) && Id(model.ExecutionId) && Id(model.WindowId) && HashValid(model.ManifestSha256) && model.Cursor != null);
            var lineage = lineages[model!.TrainingId]; Require(model.ActionId == lineage.ActionId);
            counts.TryGetValue(model.TrainingId, out int count); counts[model.TrainingId] = ++count;
            Require(count <= 512 && model.Cursor.GlobalStep == count);
            model.Cursor.Validate(lineage.Corpus.Clips.Select(c => c.Id).ToArray());
            Require(model.ManifestRelativePath == $"voxcpm-training/actions/{model.ActionId}/executions/{model.ExecutionId}/windows/{model.WindowId}/checkpoints/{model.Id}/manifest.json");
            _ = ProjectPaths.Resolve(root, model.ManifestRelativePath);
        }
        var modelDtos = Models(e); var byId = modelDtos.ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var receipt in e.Receipts)
        {
            Require(receipt != null && HashValid(receipt.KeyHash) && HashValid(receipt.RequestHash));
            ValidateSnapshot(receipt!.Snapshot, lineages);
            Require(receipt.Snapshot.Models.All(m => byId.TryGetValue(m.Id, out var known) && m == known));
        }
        Require(e.Receipts.Select(r => r.KeyHash).Distinct().Count() == e.Receipts.Count);
        if (e.Current == null) { Require(e.Lineages.Count == 0 && e.ModelReferences.Count == 0 && e.Receipts.Count == 0); return; }
        var r = e.Current; ValidateSnapshot(r.Snapshot, lineages);
        var ownLineage = lineages[r.Snapshot.Id];
        Require(r.ActionId == ownLineage.ActionId && SameCorpus(r.Corpus, ownLineage.Corpus) && r.Identity == ownLineage.Identity
            && r.Cursor != null && r.TargetCompletedEpochs == r.Snapshot.TargetCompletedEpochs
            && r.OutputRelativeRoot == $"voxcpm-training/actions/{r.ActionId}/executions/{r.Snapshot.ExecutionId}");
        r.Cursor!.Validate(r.Corpus.Clips.Select(c => c.Id).ToArray());
        Require(r.Cursor.GlobalStep == r.Snapshot.GlobalStep && r.Cursor.CompletedEpochs == r.Snapshot.CompletedEpochs
            && r.Cursor.NextRetainedOrdinal == r.Snapshot.NextRetainedOrdinal && r.Cursor.NextClipId == r.Snapshot.NextClipId
            && r.Snapshot.Models.SequenceEqual(modelDtos));
        var ownModels = e.ModelReferences.Where(m => m.TrainingId == r.Snapshot.Id).ToArray();
        Require(r.Cursor.GlobalStep == ownModels.Length && (ownModels.Length == 0 ? r.Head == null : r.Head == ownModels[^1]));
        _ = ProjectPaths.Resolve(root, r.OutputRelativeRoot);
    }
    private void ValidateCorpus(ApprovedTtsCorpusBinding? b)
    {
        Require(b != null && b.ProjectId == projectId && Id(b.CorpusId) && Id(b.VersionId) && Id(b.ActionId)
            && HashValid(b.ManifestSha256) && HashValid(b.BindingSha256) && HashValid(b.CorpusRuntimeFingerprint) && HashValid(b.PlanSha256)
            && b.SourceProjectRevision >= 1 && b.Source != null && b.Source.RightsConfirmed && Id(b.Source.Id) && HashValid(b.Source.Sha256)
            && b.Source.SampleCount is > 0 and <= 57600000 && b.Source.ByteLength >= 44 + b.Source.SampleCount * 2
            && b.Source.WavRelativePath == $"voice-recording/{b.Source.Id}.wav" && b.Clips != null && b.Clips.Count is >= 1 and <= 512);
        RequireCorpusPath(b!.ManifestRelativePath, b.ActionId);
        var ids = new HashSet<string>(StringComparer.Ordinal); int sourceIndex = -1; long end = 0, retained = 0;
        for (int i = 0; i < b.Clips.Count; i++)
        {
            var c = b.Clips[i];
            Require(c != null && Id(c.Id) && ids.Add(c.Id) && c.TrainingOrdinal == i && c.SourceIndex > sourceIndex && c.SourceIndex < 512
                && c.StartSample >= end && c.EndSample > c.StartSample && c.EndSample <= (b.Source.SampleCount + 2) / 3 && c.EndSample - c.StartSample <= 256000
                && c.CorrectedText is { Length: > 0 and <= 4000 } && !c.CorrectedText.Any(char.IsControl)
                && TtsCorpusRecordStore.Normalize(c.CorrectedText) == c.CorrectedText && Hash(c.CorrectedText) == c.TextSha256
                && HashValid(c.AudioSha256) && HashValid(c.OriginalTextPayloadSha256));
            RequireCorpusPath(c!.AudioRelativePath, b.ActionId); sourceIndex = c.SourceIndex; end = c.EndSample; retained += c.EndSample - c.StartSample;
        }
        Require(double.IsFinite(b.RetainedDurationSeconds) && b.RetainedDurationSeconds == retained / 16000.0);
    }
    private void RequireCorpusPath(string path, string action)
    {
        Require(path != null && !path.Contains('\\') && path.StartsWith($"tts-corpus/actions/{action}/", StringComparison.Ordinal));
        _ = ProjectPaths.Resolve(root, path!);
    }
    private void ValidateSnapshot(VoxCpmTrainingSnapshotDto? s, Dictionary<string, VoxCpmTrainingLineage> lineages)
    {
        Require(s != null && Id(s.Id) && s.ProjectId == projectId && s.Revision >= 1 && lineages.ContainsKey(s.Id) && Id(s.ExecutionId)
            && ValidState(s.State) && ValidState(s.Phase) && SafeDiagnostic(s.DiagnosticCode) && !s.QualityEstablished && s.Models != null
            && s.Models.All(m => m != null && Id(m.Id)) && s.Models.Select(m => m.Id).Distinct().Count() == s.Models.Count
            && s.GlobalStep is >= 0 and <= 512 && s.CompletedEpochs >= 0 && s.TargetCompletedEpochs >= 1
            && s.TargetCompletedEpochs >= s.CompletedEpochs && !(Active(s.State) && s.CanResume) && s.UpdatedUtc >= s.CreatedUtc);
        var corpus = lineages[s!.Id].Corpus;
        Require(s.CorpusVersionId == corpus.VersionId && s.CorpusManifestSha256 == corpus.ManifestSha256
            && s.CorpusClipCount == corpus.Clips.Count && s.RetainedDurationSeconds == corpus.RetainedDurationSeconds);
        new VoxCpmTrainingCursor(s.GlobalStep, s.CompletedEpochs, s.CorpusClipCount, s.NextRetainedOrdinal, s.NextClipId)
            .Validate(corpus.Clips.Select(c => c.Id).ToArray());
        Require(s.CompletedEpochs != s.TargetCompletedEpochs || s.NextRetainedOrdinal == 0);
    }

    internal static bool SameCorpus(ApprovedTtsCorpusBinding a, ApprovedTtsCorpusBinding b)
        => a with { Clips = Array.Empty<ApprovedTtsCorpusClip>() } == b with { Clips = Array.Empty<ApprovedTtsCorpusClip>() } && a.Clips.SequenceEqual(b.Clips);
    internal static bool ValidIdentity(VoxCpmTrainingIdentity? i)
        => i != null && i.SourceCommit is { Length: 40 } && i.SourceCommit.All(char.IsAsciiHexDigit)
        && i.BaseRevision is { Length: 40 } && i.BaseRevision.All(char.IsAsciiHexDigit)
        && new[] { i.Fingerprint, i.SourceLockSha256, i.ModelLockSha256, i.CacheSha256, i.CacheLayoutSha256, i.LoaderSha256, i.RecipeSha256, i.AdapterLayoutSha256 }.All(HashValid);
    internal static IReadOnlyList<VoxCpmTrainingModelDto> Models(VoxCpmTrainingEnvelope e)
        => e.ModelReferences.Select(m => new VoxCpmTrainingModelDto(m.Id, m.TrainingId,
            e.Lineages.Single(l => l.TrainingId == m.TrainingId).Corpus.VersionId, m.ManifestSha256,
            m.Cursor.GlobalStep, m.Cursor.CompletedEpochs, m.CreatedUtc)).ToArray();
    internal static VoxCpmTrainingEnvelope AcceptCheckpoint(VoxCpmTrainingEnvelope e, VoxCpmCheckpointReference checkpoint)
    {
        var r = e.Current ?? throw Error("state.invalid");
        if (r.Cursor.GlobalStep >= 512) throw Error("state.limit");
        if (checkpoint.TrainingId != r.Snapshot.Id || checkpoint.ActionId != r.ActionId || checkpoint.ExecutionId != r.Snapshot.ExecutionId
            || checkpoint.Cursor != r.Cursor.Advance(r.Corpus.Clips.Select(c => c.Id).ToArray()) || checkpoint.Cursor.CompletedEpochs > r.TargetCompletedEpochs)
            throw Error("checkpoint.invalid");
        e = e with { ModelReferences = [.. e.ModelReferences, checkpoint] };
        string state = checkpoint.Cursor.CompletedEpochs >= r.TargetCompletedEpochs ? "completed" : r.StopRequested ? "stop_requested" : "training";
        var s = r.Snapshot with { Revision = checked(r.Snapshot.Revision + 1), State = state, Phase = state,
            GlobalStep = checkpoint.Cursor.GlobalStep, CompletedEpochs = checkpoint.Cursor.CompletedEpochs,
            NextRetainedOrdinal = checkpoint.Cursor.NextRetainedOrdinal, NextClipId = checkpoint.Cursor.NextClipId,
            Models = Models(e), CanResume = state == "completed" && checkpoint.Cursor.GlobalStep < 512, UpdatedUtc = DateTimeOffset.UtcNow };
        return e with { Current = r with { Snapshot = s, Cursor = checkpoint.Cursor, Head = checkpoint } };
    }
    internal VoxCpmTrainingSnapshotDto? Replay(VoxCpmTrainingEnvelope e, string key, string request)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(char.IsControl)) throw Error("input.invalid");
        string keyHash = Hash(key); var receipt = e.Receipts.SingleOrDefault(r => r.KeyHash == keyHash);
        if (receipt != null) { if (receipt.RequestHash != Hash(request)) throw Error("idempotency.conflict"); return receipt.Snapshot; }
        if (Expired(keyHash)) throw Error("idempotency.expired"); return null;
    }
    internal static VoxCpmTrainingEnvelope Receipt(VoxCpmTrainingEnvelope e, VoxCpmTrainingRecord record, string key, string request)
        => e with { Current = record, Receipts = [.. e.Receipts, new(Hash(key), Hash(request), record.Snapshot)] };
    private bool Expired(string hash)
    {
        try { _ = File.GetAttributes(PathFor($"expired-keys/{hash}.expired")); return true; }
        catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }
    private void PersistExpiredKey(string hash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); string directory = PathFor("expired-keys"); Directory.CreateDirectory(directory);
        using var pins = new CatalogPaths.Pins(); CatalogPaths.PinDirectories(directory, pins);
        if (Expired(hash)) return;
        using var output = new FileStream(PathFor($"expired-keys/{hash}.expired"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.WriteByte(1); output.Flush(true);
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject()) { Require(names.Add(item.Name)); Unique(item.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    internal static bool Active(string state) => state is "queued" or "loading" or "training" or "checkpointing" or "stop_requested";
    private static bool ValidState(string state) => Active(state) || state is "paused" or "completed" or "interrupted" or "failed";
    internal static bool Id(string? value) => value != null && CatalogPaths.IsId(value);
    internal static bool HashValid(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool SafeDiagnostic(string? code) => code == null || code.Length <= 100 && code.StartsWith("voxcpm_training.", StringComparison.Ordinal)
        && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.');
    private static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw Error("state.invalid"); }
    private static StudioException Error(string suffix) => new("voxcpm_training." + suffix);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}

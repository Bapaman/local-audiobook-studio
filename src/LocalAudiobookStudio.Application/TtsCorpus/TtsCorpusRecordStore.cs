using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.TtsCorpus;

/// <summary>Short cross-process transactions; never waits for the long artifact lease.</summary>
internal sealed class TtsCorpusRecordStore(string root, string projectId)
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private string PathFor(string relative) => ProjectPaths.Resolve(root, "tts-corpus/" + relative);
    public async Task<T> TransactionAsync<T>(Func<TtsCorpusEnvelope, (TtsCorpusEnvelope? Write, T Value)> action, CancellationToken token)
    {
        if (!Directory.Exists(ProjectPaths.Resolve(root, "tts-corpus")))
        {
            var empty = action(new(1, projectId, null, [])); if (empty.Write == null) return empty.Value;
        }
        Directory.CreateDirectory(ProjectPaths.Resolve(root, "tts-corpus"));
        using var pins = new CatalogPaths.Pins();
        CatalogPaths.PinDirectories(ProjectPaths.Resolve(root, "tts-corpus"), pins);
        FileStream? held = null; var elapsed = Stopwatch.StartNew();
        while (held == null)
        {
            token.ThrowIfCancellationRequested();
            try { held = new FileStream(PathFor("record.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
            { if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new StudioException("tts_corpus.busy"); await Task.Delay(20, token); }
        }
        await using (held)
        {
            var e = await ReadAsync(token); var result = action(e);
            if (result.Write != null) await SaveAsync(result.Write, token); return result.Value;
        }
    }
    private async Task<TtsCorpusEnvelope> ReadAsync(CancellationToken token)
    {
        string path = PathFor("state.json"); if (!File.Exists(path)) return new(1, projectId, null, []);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaximumBytes) throw Invalid();
        try { var e = await JsonSerializer.DeserializeAsync<TtsCorpusEnvelope>(input, Json, token); Validate(e); return e!; }
        catch (Exception e) when (e is JsonException or NotSupportedException or NullReferenceException or ArgumentException or OverflowException) { throw Invalid(); }
    }
    private async Task SaveAsync(TtsCorpusEnvelope e, CancellationToken token)
    {
        // One pending mutation may append receipt 257. Expire oldest hot snapshots by
        // count and actual serialized byte budget, without forgetting their keys.
        Validate(e, 257);
        var expired = new List<TtsCorpusReceipt>();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(e, Json);
        while (e.Receipts.Count > 0 && (e.Receipts.Count > 256 || bytes.Length > MaximumBytes))
        {
            expired.Add(e.Receipts[0]); e = e with { Receipts = e.Receipts.Skip(1).ToArray() };
            bytes = JsonSerializer.SerializeToUtf8Bytes(e, Json);
        }
        if (bytes.Length > MaximumBytes) throw new StudioException("tts_corpus.state.limit");
        Validate(e);
        // Tombstones must be durable before state replacement. A failed later save
        // leaves the old hot receipt authoritative because replay checks hot first.
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
    private void Validate(TtsCorpusEnvelope? e, int maximumReceipts = 256)
    {
        if (e == null || e.Format != 1 || e.ProjectId != projectId || e.Receipts == null || e.Receipts.Count > maximumReceipts
            || e.Receipts.Any(x => x == null || !HashValid(x.KeyHash) || !HashValid(x.RequestHash) || !SnapshotValid(x.Snapshot))
            || e.Receipts.Select(x => x.KeyHash).Distinct().Count() != e.Receipts.Count) throw Invalid();
        if (e.Current is not { } r) return;
        var s = r.Snapshot;
        if (!SnapshotValid(s) || r.Recording == null || r.Recording.Id != s.SourceRecordingId || !r.Recording.RightsConfirmed
            || !HashValid(r.Recording.Sha256) || r.Recording.SampleCount <= 0 || r.Recording.ByteLength < 44 + r.Recording.SampleCount * 2
            || r.Recording.WavRelativePath != $"voice-recording/{r.Recording.Id}.wav" || s.SourceDurationSeconds != r.Recording.SampleCount / 48000.0
            || !Id(r.ActionId) || !Id(s.ExecutionId) || r.OutputRelativeRoot != $"tts-corpus/actions/{r.ActionId}/executions/{s.ExecutionId}" || r.SourceProjectRevision < 1
            || !HashValid(r.RuntimeFingerprint) || r.Accepted == null || r.Accepted.Count > 512 || r.VersionReferences == null
            || r.VersionReferences.Count != s.Versions.Count || r.Accepted.Count != s.CompletedClipCount || r.Accepted.Count != s.Clips.Count) throw Invalid();
        _ = ProjectPaths.Resolve(root, r.OutputRelativeRoot); _ = ProjectPaths.Resolve(root, r.Recording.WavRelativePath);
        if (r.Plan is { } p)
        {
            RequireActionPath(p.PlanRelativePath, r.ActionId);
            if (!HashValid(p.Sha256) || !SameHash(p.SourceSha256, r.Recording.Sha256) || p.RuntimeFingerprint != r.RuntimeFingerprint
                || p.Clips == null || p.Clips.Count is < 1 or > 512 || p.TotalSamples != (r.Recording.SampleCount + 2) / 3
                || s.TotalClipCount != p.Clips.Count || p.Clips.Select(x => x.Id).Distinct().Count() != p.Clips.Count) throw Invalid();
            long end = 0;
            for (int i = 0; i < p.Clips.Count; i++)
            {
                var c = p.Clips[i];
                if (c == null || !Id(c.Id) || c.Index != i || c.StartSample != end || c.EndSample <= end || c.EndSample - end > 256000 || !HashValid(c.AudioSha256)) throw Invalid();
                RequireActionPath(c.AudioRelativePath, r.ActionId); end = c.EndSample;
            }
            if (end != p.TotalSamples) throw Invalid();
        }
        else if (r.Accepted.Count != 0 || s.TotalClipCount != null) throw Invalid();
        for (int i = 0; i < r.Accepted.Count; i++)
        {
            var c = r.Accepted[i]; var d = s.Clips[i];
            if (c == null || r.Plan == null || c.Plan != r.Plan.Clips[i] || !HashValid(c.TextSha256) || c.DraftText == null || c.DraftText.Length > 4000
                || d.Id != c.Plan.Id || d.Index != i || d.StartSample != c.Plan.StartSample || d.EndSample != c.Plan.EndSample
                || !AnnotationsValid(c.WarningCodes, c.Words, (c.Plan.EndSample - c.Plan.StartSample) / 16000.0)) throw Invalid();
            RequireActionPath(c.TextRelativePath, r.ActionId);
        }
        foreach (var v in r.VersionReferences)
        {
            if (v == null || !Id(v.Id) || !Id(v.ActionId) || !HashValid(v.Sha256)
                || !s.Versions.Any(x => x.Id == v.Id && x.Sha256 == v.Sha256)) throw Invalid();
            RequireActionPath(v.ManifestRelativePath, v.ActionId);
        }
    }
    private bool SnapshotValid(TtsCorpusSnapshotDto? s)
        => s != null && s.ProjectId == projectId && Id(s.Id) && Id(s.SourceRecordingId) && s.Revision >= 1
        && s.State is "queued" or "preparing" or "transcribing" or "stop_requested" or "paused" or "completed" or "interrupted" or "failed"
        && s.Phase is "queued" or "preparing" or "transcribing" or "stop_requested" or "paused" or "completed" or "interrupted" or "failed"
        && SafeDiagnostic(s.DiagnosticCode) && double.IsFinite(s.SourceDurationSeconds) && s.SourceDurationSeconds > 0
        && s.CompletedClipCount is >= 0 and <= 512 && (s.TotalClipCount == null || s.TotalClipCount is >= 1 and <= 512 && s.CompletedClipCount <= s.TotalClipCount)
        && s.Clips != null && s.Clips.Count <= 512 && s.Clips.Select(x => x?.Id).Distinct().Count() == s.Clips.Count
        && s.Clips.All(x => x != null && Id(x.Id) && x.Index >= 0 && x.StartSample >= 0 && x.EndSample > x.StartSample && x.Text != null && x.Text.Length <= 4000
            && !x.Text.Any(char.IsControl) && Normalize(x.Text) == x.Text && (!x.Reviewed || x.Excluded || x.Text.Length > 0) && AnnotationsValid(x.WarningCodes, x.Words, (x.EndSample - x.StartSample) / 16000.0))
        && s.Versions != null && s.Versions.Count <= 128 && s.Versions.Select(x => x?.Id).Distinct().Count() == s.Versions.Count
        && s.Versions.All(x => x != null && Id(x.Id) && HashValid(x.Sha256) && x.ClipCount is > 0 and <= 512 && double.IsFinite(x.RetainedDurationSeconds) && x.RetainedDurationSeconds > 0);
    private static bool AnnotationsValid(IReadOnlyList<string>? warnings, IReadOnlyList<TtsCorpusWordDto>? words, double duration)
        => warnings != null && warnings.Count <= 32 && warnings.All(x => x != null && x.Length <= 100 && x.StartsWith("tts_corpus.", StringComparison.Ordinal) && x.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.'))
        && words != null && words.Count <= 4000 && words.All(x => x != null && !string.IsNullOrWhiteSpace(x.Text) && x.Text.Length <= 4000
            && double.IsFinite(x.Start) && x.Start >= 0 && x.Start <= duration && (x.End == null || double.IsFinite(x.End.Value) && x.End >= x.Start && x.End <= duration));
    internal void RequireActionPath(string path, string action)
    {
        if (path == null || !path.StartsWith($"tts-corpus/actions/{action}/", StringComparison.Ordinal) || path.Contains('\\')) throw Invalid();
        _ = ProjectPaths.Resolve(root, path);
    }
    internal static TtsCorpusSnapshotDto PublicSnapshot(TtsCorpusRecord r) => r.Snapshot with
    {
        CanResume = CanResume(r) && r.Snapshot.CanResume,
        RetainedDurationSeconds = r.Snapshot.Clips.Where(x => !x.Excluded).Sum(x => (x.EndSample - x.StartSample) / 16000.0)
    };
    internal static bool Active(string state) => state is "queued" or "preparing" or "transcribing" or "stop_requested";
    internal static bool CanResume(TtsCorpusRecord r) => !Active(r.Snapshot.State)
        && (r.Plan == null ? r.Accepted.Count == 0 : r.Accepted.Count < r.Plan.Clips.Count);
    internal static bool Id(string? value) => value != null && Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    internal static bool HashValid(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static bool SafeDiagnostic(string? code) => code == null || code.Length <= 100 && code.StartsWith("tts_corpus.", StringComparison.Ordinal) && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.');
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    internal static StudioException Invalid() => new("tts_corpus.state.invalid");
    internal TtsCorpusSnapshotDto? Replay(TtsCorpusEnvelope e, string key, string request)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(char.IsControl)) throw new StudioException("tts_corpus.input.invalid");
        string hash = Hash(key); var receipt = e.Receipts.SingleOrDefault(x => x.KeyHash == hash);
        if (receipt == null)
        {
            if (ExpiredKeyExists(hash)) throw new StudioException("tts_corpus.idempotency.expired");
            return null;
        }
        if (receipt.RequestHash != Hash(request)) throw new StudioException("tts_corpus.idempotency.conflict"); return receipt.Snapshot;
    }
    private bool ExpiredKeyExists(string hash)
    {
        // Direct deterministic lookup only; never discover or scan historical files.
        try { _ = File.GetAttributes(PathFor($"expired-keys/{hash}.expired")); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private void PersistExpiredKey(string hash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string directory = PathFor("expired-keys"); Directory.CreateDirectory(directory);
        using var pins = new CatalogPaths.Pins(); CatalogPaths.PinDirectories(directory, pins);
        if (ExpiredKeyExists(hash)) return;
        using var marker = new FileStream(PathFor($"expired-keys/{hash}.expired"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        marker.WriteByte(1); marker.Flush(true);
    }
    internal static TtsCorpusEnvelope Receipt(TtsCorpusEnvelope e, TtsCorpusRecord r, string key, string request)
    {
        r = r with { Snapshot = PublicSnapshot(r) };
        return e with { Current = r, Receipts = [.. e.Receipts, new(Hash(key), Hash(request), r.Snapshot)] };
    }
}

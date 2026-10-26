using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.TtsCorpus;

public sealed class StudioTtsCorpusService(ProjectJobAccess access, ITtsCorpusRuntime runtime) : IStudioTtsCorpusService
{
    public Task<TtsCorpusReadinessDto> GetReadinessAsync(CancellationToken token = default) => runtime.GetReadinessAsync(token);
    public Task<TtsCorpusSnapshotDto?> GetAsync(string projectId, CancellationToken token = default)
        => WithProject(projectId, (db, ct) => Store(db).TransactionAsync(e => ((TtsCorpusEnvelope?)null,
            e.Current is { } r ? TtsCorpusRecordStore.PublicSnapshot(r) : null), ct), token);
    public Task<TtsCorpusSnapshotDto> StartAsync(string projectId, long expectedProjectRevision, string key, bool experimentalConfirmed, CancellationToken token = default)
        => WithProject(projectId, async (db, ct) =>
        {
            string request = Request("start", experimentalConfirmed); var store = Store(db);
            var replay = await Replay(store, key, request, ct); if (replay != null) return replay;
            if (!experimentalConfirmed) throw new StudioException("tts_corpus.confirmation.required");
            string fingerprint = await ReadyFingerprint(ct);
            await using var lease = await db.AcquireInputMutationLeaseAsync(ct);
            if (db.State.Revision != expectedProjectRevision) throw new StudioException("project.conflict");
            var recording = db.State.VoiceRecording ?? throw new StudioException("tts_corpus.source.missing");
            using var source = await TtsCorpusSource.VerifyAsync(db, recording, ct);
            return await store.TransactionAsync(e =>
            {
                var repeated = store.Replay(e, key, request); if (repeated != null) return ((TtsCorpusEnvelope?)null, repeated);
                if (e.Current is { } current && TtsCorpusRecordStore.Active(current.Snapshot.State)) throw new StudioException("tts_corpus.active");
                string id = NewId(), action = NewId(), execution = NewId(); var now = DateTimeOffset.UtcNow;
                var s = new TtsCorpusSnapshotDto(id, projectId, 1, "queued", "queued", recording.Id, recording.SampleCount / 48000.0,
                    null, 0, [], e.Current?.Snapshot.Versions ?? [], false, null, now, now, ExecutionId: execution);
                var r = new TtsCorpusRecord(s, recording, expectedProjectRevision, action, $"tts-corpus/actions/{action}/executions/{execution}", fingerprint, null, [], e.Current?.VersionReferences ?? []);
                var updated = TtsCorpusRecordStore.Receipt(e, r, key, request); return (updated, updated.Current!.Snapshot);
            }, ct);
        }, token);
    public Task<TtsCorpusSnapshotDto> ControlAsync(string projectId, string corpusId, long expectedCorpusRevision, string key, string command, CancellationToken token = default)
        => WithProject(projectId, async (db, ct) =>
        {
            string request = Request("control", corpusId, command); var store = Store(db);
            var replay = await Replay(store, key, request, ct); if (replay != null) return replay;
            if (command is not ("stop" or "resume")) throw new StudioException("tts_corpus.input.invalid");
            ProjectArtifactLease? lease = null; TtsCorpusSource? source = null;
            try
            {
                if (command == "resume")
                {
                    string fingerprint = await ReadyFingerprint(ct);
                    lease = await db.AcquireInputMutationLeaseAsync(ct);
                    var r = await store.TransactionAsync(e => ((TtsCorpusEnvelope?)null, Require(e, corpusId, expectedCorpusRevision)), ct);
                    if (r.RuntimeFingerprint != fingerprint) throw new StudioException("tts_corpus.runtime.changed");
                    RequireResumable(r); source = await TtsCorpusSource.VerifyAsync(db, r.Recording, ct);
                    if (r.Plan != null) await VerifyPayloads(db, r, ct);
                }
                var result = await store.TransactionAsync(e =>
                {
                    var repeated = store.Replay(e, key, request); if (repeated != null) return ((TtsCorpusEnvelope?)null, (Snapshot: repeated, StopRoot: (string?)null));
                    var r = Require(e, corpusId, expectedCorpusRevision); string state; string? stopRoot = null;
                    if (command == "stop")
                    {
                        if (!TtsCorpusRecordStore.Active(r.Snapshot.State)) throw new StudioException("tts_corpus.transition.invalid");
                        state = r.Snapshot.State == "queued" ? "paused" : "stop_requested"; r = r with { StopRequested = true };
                        if (state == "stop_requested") stopRoot = r.OutputRelativeRoot;
                    }
                    else
                    {
                        RequireResumable(r); state = "queued"; string execution = NewId();
                        r = r with { StopRequested = false, OutputRelativeRoot = $"tts-corpus/actions/{r.ActionId}/executions/{execution}", Snapshot = r.Snapshot with { ExecutionId = execution } };
                    }
                    r = r with { Snapshot = Changed(r.Snapshot) with { State = state, Phase = state, DiagnosticCode = null,
                        CanResume = state == "paused" && (r.Plan == null ? r.Accepted.Count == 0 : r.Accepted.Count < r.Plan.Clips.Count) } };
                    var updated = TtsCorpusRecordStore.Receipt(e, r, key, request); return (updated, (Snapshot: updated.Current!.Snapshot, StopRoot: stopRoot));
                }, ct);
                if (result.StopRoot != null) SignalStop(db, result.StopRoot);
                return result.Snapshot;
            }
            finally { source?.Dispose(); if (lease != null) await lease.DisposeAsync(); }
        }, token);
    public Task<TtsCorpusSnapshotDto> UpdateClipAsync(string projectId, string corpusId, string clipId, long expectedCorpusRevision, string key, TtsCorpusUpdateClipRequest request, CancellationToken token = default)
        => WithProject(projectId, async (db, ct) =>
        {
            string identity = Request("clip", corpusId, clipId, request); var store = Store(db);
            var replay = await Replay(store, key, identity, ct); if (replay != null) return replay;
            if (request?.Text == null || request.Text.Length > 16000 || request.Text.Any(x => char.IsControl(x) && !char.IsWhiteSpace(x))) throw new StudioException("tts_corpus.text.invalid");
            string text = TtsCorpusRecordStore.Normalize(request.Text);
            if (text.Length > 4000 || !request.Excluded && text.Length == 0) throw new StudioException("tts_corpus.text.invalid");
            await using var lease = await db.AcquireInputMutationLeaseAsync(ct);
            var current = await store.TransactionAsync(e => ((TtsCorpusEnvelope?)null, Require(e, corpusId, expectedCorpusRevision)), ct);
            using var source = await TtsCorpusSource.VerifyAsync(db, current.Recording, ct);
            return await store.TransactionAsync(e =>
            {
                var repeated = store.Replay(e, key, identity); if (repeated != null) return ((TtsCorpusEnvelope?)null, repeated);
                var r = Require(e, corpusId, expectedCorpusRevision); Terminal(r);
                var old = r.Snapshot.Clips.SingleOrDefault(x => x.Id == clipId) ?? throw new StudioException("tts_corpus.clip.missing");
                bool changed = old.Text != text || old.Excluded != request.Excluded;
                var edited = old with { Text = text, Excluded = request.Excluded, Reviewed = !changed && request.Reviewed };
                r = r with { Snapshot = Changed(r.Snapshot) with { Clips = r.Snapshot.Clips.Select(x => x.Id == clipId ? edited : x).ToArray() } };
                var updated = TtsCorpusRecordStore.Receipt(e, r, key, identity); return (updated, updated.Current!.Snapshot);
            }, ct);
        }, token);
    public Task<TtsCorpusSnapshotDto> ApproveAsync(string projectId, string corpusId, long expectedCorpusRevision, string key, bool transcriptAndBoundariesConfirmed, CancellationToken token = default)
        => WithProject(projectId, async (db, ct) =>
        {
            string request = Request("approve", corpusId, transcriptAndBoundariesConfirmed); var store = Store(db);
            var replay = await Replay(store, key, request, ct); if (replay != null) return replay;
            if (!transcriptAndBoundariesConfirmed) throw new StudioException("tts_corpus.confirmation.required");
            await using var lease = await db.AcquireInputMutationLeaseAsync(ct);
            var r = await store.TransactionAsync(e => ((TtsCorpusEnvelope?)null, Require(e, corpusId, expectedCorpusRevision)), ct);
            Terminal(r); var retained = r.Snapshot.Clips.Where(x => !x.Excluded).ToArray();
            if (retained.Length == 0) throw new StudioException("tts_corpus.empty");
            if (retained.Any(x => !x.Reviewed || string.IsNullOrWhiteSpace(x.Text))) throw new StudioException("tts_corpus.review.required");
            if (r.Plan == null) throw new StudioException("tts_corpus.plan.missing");
            if (r.Snapshot.Versions.Count >= 128) throw new StudioException("tts_corpus.state.limit");
            using var source = await TtsCorpusSource.VerifyAsync(db, r.Recording, ct);
            await VerifyPayloads(db, r, ct);
            string versionId = NewId(); string relative = $"{r.OutputRelativeRoot}/approved/{versionId}.json";
            var manifest = new { Format = 1, ProjectId = projectId, CorpusId = corpusId, VersionId = versionId,
                Source = r.Recording, r.SourceProjectRevision, r.RuntimeFingerprint, PlanSha256 = r.Plan.Sha256,
                Clips = retained.Select(c => new { c.Id, c.Index, c.StartSample, c.EndSample, c.Text,
                    AudioSha256 = r.Accepted[c.Index].Plan.AudioSha256, r.Accepted[c.Index].Plan.AudioRelativePath,
                    OriginalTextSha256 = r.Accepted[c.Index].TextSha256 }).ToArray() };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest); string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            string directory = db.ResolveOwnedPath(r.OutputRelativeRoot + "/approved"); Directory.CreateDirectory(directory);
            using var pins = new CatalogPaths.Pins(); CatalogPaths.PinDirectories(directory, pins);
            string path = db.ResolveOwnedPath(relative);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); output.Flush(true); }
            await TtsCorpusSource.VerifyHashAsync(db, relative, sha, TtsCorpusRecordStore.MaximumBytes, ct);
            return await store.TransactionAsync(e =>
            {
                var repeated = store.Replay(e, key, request); if (repeated != null) return ((TtsCorpusEnvelope?)null, repeated);
                var latest = Require(e, corpusId, expectedCorpusRevision); Terminal(latest);
                var version = new TtsCorpusVersionDto(versionId, sha, retained.Length, retained.Sum(x => (x.EndSample - x.StartSample) / 16000.0), DateTimeOffset.UtcNow);
                latest = latest with { Snapshot = Changed(latest.Snapshot) with { Versions = [.. latest.Snapshot.Versions, version] },
                    VersionReferences = [.. latest.VersionReferences, new(versionId, r.ActionId, relative, sha)] };
                var updated = TtsCorpusRecordStore.Receipt(e, latest, key, request); return (updated, updated.Current!.Snapshot);
            }, ct);
        }, token);
    public async Task<TtsCorpusAudioHandle> OpenAudioAsync(string projectId, string corpusId, string clipId, CancellationToken token = default)
    {
        var pins = new CatalogPaths.Pins(); FileStream? content = null; bool transferred = false;
        try
        {
            return await WithProject(projectId, async (db, ct) =>
            {
                var r = await Store(db).TransactionAsync(e => ((TtsCorpusEnvelope?)null, Require(e, corpusId)), ct);
                var accepted = r.Accepted.SingleOrDefault(x => x.Plan.Id == clipId) ?? throw new StudioException("tts_corpus.clip.missing");
                string path = db.ResolveOwnedPath(accepted.Plan.AudioRelativePath); CatalogPaths.PinDirectories(Path.GetDirectoryName(path)!, pins);
                content = TtsCorpusSource.OpenRead(path); await TtsCorpusSource.ValidateAudioAsync(content, accepted.Plan, ct);
                var check = await Store(db).TransactionAsync(e => ((TtsCorpusEnvelope?)null, Require(e, corpusId).Accepted.SingleOrDefault(x => x.Plan.Id == clipId)), ct);
                if (check?.Plan != accepted.Plan || check.TextSha256 != accepted.TextSha256 || check.TextRelativePath != accepted.TextRelativePath)
                    throw new StudioException("tts_corpus.clip.missing");
                var handle = new TtsCorpusAudioHandle(content, pins); transferred = true; return handle;
            }, token);
        }
        catch (StudioException e) when (e.Code == "tts_corpus.storage") { throw new StudioException("tts_corpus.audio.invalid"); }
        finally { if (!transferred) { try { if (content != null) await content.DisposeAsync(); } finally { pins.Dispose(); } } }
    }
    internal static TtsCorpusRecordStore Store(ProjectDatabase db) => new(db.Root, db.State.Metadata.Id);
    internal static void SignalStop(ProjectDatabase db, string relativeRoot)
    {
        string root = db.ResolveOwnedPath(relativeRoot); if (!Directory.Exists(root)) return;
        using var pins = new CatalogPaths.Pins(); CatalogPaths.PinDirectories(root, pins);
        using var file = new FileStream(db.ResolveOwnedPath(relativeRoot + "/STOP"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read); file.Flush(true);
    }
    internal static async Task VerifyPayloads(ProjectDatabase db, TtsCorpusRecord r, CancellationToken token)
    {
        if (r.Plan == null) throw new StudioException("tts_corpus.plan.missing");
        await TtsCorpusSource.VerifyHashAsync(db, r.Plan.PlanRelativePath, r.Plan.Sha256, 2 * 1024 * 1024, token);
        foreach (var clip in r.Accepted)
        {
            await TtsCorpusSource.VerifyHashAsync(db, clip.TextRelativePath, clip.TextSha256, 1024 * 1024, token);
            using var pins = new CatalogPaths.Pins(); string path = db.ResolveOwnedPath(clip.Plan.AudioRelativePath);
            CatalogPaths.PinDirectories(Path.GetDirectoryName(path)!, pins); await using var audio = TtsCorpusSource.OpenRead(path);
            await TtsCorpusSource.ValidateAudioAsync(audio, clip.Plan, token);
        }
    }
    internal static TtsCorpusRecord Require(TtsCorpusEnvelope e, string corpusId, long? revision = null)
    {
        var r = e.Current;
        if (!TtsCorpusRecordStore.Id(corpusId) || r == null || r.Snapshot.Id != corpusId) throw new StudioException("tts_corpus.missing");
        if (revision != null && r.Snapshot.Revision != revision) throw new StudioException("tts_corpus.conflict"); return r;
    }
    private static void Terminal(TtsCorpusRecord r) { if (TtsCorpusRecordStore.Active(r.Snapshot.State)) throw new StudioException("tts_corpus.active"); }
    private static void RequireResumable(TtsCorpusRecord r)
    { if (!TtsCorpusRecordStore.CanResume(r)) throw new StudioException("tts_corpus.transition.invalid"); }
    private static TtsCorpusSnapshotDto Changed(TtsCorpusSnapshotDto s) => s with { Revision = checked(s.Revision + 1), UpdatedUtc = DateTimeOffset.UtcNow };
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string Request(params object?[] values) => JsonSerializer.Serialize(values);
    private static Task<TtsCorpusSnapshotDto?> Replay(TtsCorpusRecordStore store, string key, string request, CancellationToken token)
        => store.TransactionAsync(e => ((TtsCorpusEnvelope?)null, store.Replay(e, key, request)), token);
    private async Task<string> ReadyFingerprint(CancellationToken token)
    {
        if (!(await runtime.GetReadinessAsync(token)).Ready) throw new StudioException("tts_corpus.runtime.not_ready");
        string fingerprint = await runtime.GetFingerprintAsync(token);
        if (!TtsCorpusRecordStore.HashValid(fingerprint)) throw new StudioException("tts_corpus.runtime.not_ready"); return fingerprint;
    }
    private async Task<T> WithProject<T>(string id, Func<ProjectDatabase, CancellationToken, Task<T>> action, CancellationToken token)
    {
        try { await using var session = await access.OpenCurrentAsync(id, token); return await action(session.Project, token); }
        catch (ProjectException e) when (e.Code is "project.path" or "project.storage") { throw new StudioException("tts_corpus.storage"); }
        catch (ProjectException e) { throw new StudioException(e.Code); }
        catch (TtsCorpusExecutionException e) { throw new StudioException(TtsCorpusRecordStore.SafeDiagnostic(e.Code) ? e.Code : "tts_corpus.runtime.not_ready"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new StudioException("tts_corpus.storage"); }
    }
}

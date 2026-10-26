using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.VoxCpmTraining;

public sealed class StudioVoxCpmTrainingService(ProjectJobAccess access, IApprovedTtsCorpusResolver resolver,
    IVoxCpmTrainingRuntime runtime, IVoxCpmTrainingCheckpointValidator checkpoints) : IStudioVoxCpmTrainingService
{
    public Task<VoxCpmTrainingReadinessDto> GetReadinessAsync(CancellationToken token = default) => runtime.GetReadinessAsync(token);
    public Task<VoxCpmTrainingSnapshotDto?> GetAsync(string projectId, CancellationToken token = default)
        => WithProject(projectId, (db, ct) => Store(db).TransactionAsync(e => ((VoxCpmTrainingEnvelope?)null, e.Current?.Snapshot), ct), token);

    public Task<VoxCpmTrainingSnapshotDto> StartAsync(string projectId, long expectedProjectRevision, string key,
        string corpusVersionId, bool experimentalConfirmed, CancellationToken token = default)
        => WithProject(projectId, async (db, ct) =>
        {
            var store = Store(db); string request = Request("start", corpusVersionId, experimentalConfirmed);
            var replay = await Replay(store, key, request, ct); if (replay != null) return replay;
            if (!experimentalConfirmed) throw Error("confirmation.required");
            var identity = await ReadyIdentity(ct);
            await using var ownership = await db.AcquireInputMutationLeaseAsync(ct);
            await ownership.RefreshAsync(ct);
            if (db.State.Revision != expectedProjectRevision) throw new StudioException("project.conflict");
            using var corpus = await resolver.ResolveAsync(db, corpusVersionId, null, ct);
            return await store.TransactionAsync(e =>
            {
                var repeated = store.Replay(e, key, request); if (repeated != null) return ((VoxCpmTrainingEnvelope?)null, repeated);
                if (e.Current is { } active && VoxCpmTrainingRecordStore.Active(active.Snapshot.State)) throw Error("active");
                string id = NewId(), action = NewId(), execution = NewId(); var now = DateTimeOffset.UtcNow;
                var b = corpus.Binding; var cursor = VoxCpmTrainingCursor.Initial(b.Clips.Select(c => c.Id).ToArray());
                var snapshot = new VoxCpmTrainingSnapshotDto(id, projectId, 1, "queued", "queued", b.VersionId, b.ManifestSha256,
                    b.Clips.Count, b.RetainedDurationSeconds, 0, 0, 0, cursor.NextClipId, 1, execution, VoxCpmTrainingRecordStore.Models(e),
                    false, false, null, now, now);
                var record = new VoxCpmTrainingRecord(snapshot, action, $"voxcpm-training/actions/{action}/executions/{execution}", b, identity, cursor, 1, null);
                e = e with { Lineages = [.. e.Lineages, new(id, action, b, identity)] };
                var updated = VoxCpmTrainingRecordStore.Receipt(e, record, key, request); return (updated, snapshot);
            }, ct);
        }, token);

    public Task<VoxCpmTrainingSnapshotDto> ControlAsync(string projectId, string trainingId, long expectedTrainingRevision,
        string key, string command, CancellationToken token = default)
        => WithProject(projectId, async (db, ct) =>
        {
            var store = Store(db); string request = Request("control", trainingId, command);
            var replay = await Replay(store, key, request, ct); if (replay != null) return replay;
            if (command is not ("stop" or "resume")) throw Error("input.invalid");
            ProjectArtifactLease? ownership = null; ApprovedTtsCorpusLease? corpus = null;
            try
            {
                if (command == "resume")
                {
                    var identity = await ReadyIdentity(ct);
                    ownership = await db.AcquireInputMutationLeaseAsync(ct); await ownership.RefreshAsync(ct);
                    var observed = await store.TransactionAsync(e => ((VoxCpmTrainingEnvelope?)null, Require(e, trainingId, expectedTrainingRevision)), ct);
                    RequireResumable(observed);
                    if (observed.Identity != identity) throw Error("runtime.changed");
                    if (db.State.VoiceRecording != observed.Corpus.Source) throw Error("source.changed");
                    corpus = await resolver.ResolveAsync(db, observed.Corpus.VersionId, observed.Corpus.ManifestSha256, ct);
                    if (!VoxCpmTrainingRecordStore.SameCorpus(corpus.Binding, observed.Corpus)) throw Error("corpus.changed");
                    if (observed.Head != null) await checkpoints.VerifyAsync(db.Root, observed.Head, corpus.Binding, identity, ct);
                }
                var result = await store.TransactionAsync(e =>
                {
                    var repeated = store.Replay(e, key, request);
                    if (repeated != null) return ((VoxCpmTrainingEnvelope?)null, (Snapshot: repeated, SignalRoot: (string?)null));
                    var r = Require(e, trainingId, expectedTrainingRevision); string state; string? signalRoot = null;
                    if (command == "stop")
                    {
                        if (!VoxCpmTrainingRecordStore.Active(r.Snapshot.State)) throw Error("transition.invalid");
                        state = r.Cursor.CompletedEpochs >= r.TargetCompletedEpochs ? "completed" : r.Snapshot.State == "queued" ? "paused" : "stop_requested";
                        r = r with { StopRequested = true }; if (state == "stop_requested") signalRoot = r.OutputRelativeRoot;
                    }
                    else
                    {
                        RequireResumable(r); state = "queued"; string execution = NewId();
                        long target = r.Cursor.CompletedEpochs >= r.TargetCompletedEpochs ? checked(r.Cursor.CompletedEpochs + 1) : r.TargetCompletedEpochs;
                        r = r with { StopRequested = false, TargetCompletedEpochs = target,
                            OutputRelativeRoot = $"voxcpm-training/actions/{r.ActionId}/executions/{execution}",
                            Snapshot = r.Snapshot with { ExecutionId = execution, TargetCompletedEpochs = target } };
                    }
                    r = r with { Snapshot = r.Snapshot with { Revision = checked(r.Snapshot.Revision + 1), State = state, Phase = state,
                        DiagnosticCode = null, CanResume = !VoxCpmTrainingRecordStore.Active(state) && r.Cursor.GlobalStep < 512, UpdatedUtc = DateTimeOffset.UtcNow } };
                    var updated = VoxCpmTrainingRecordStore.Receipt(e, r, key, request);
                    return (updated, (Snapshot: r.Snapshot, SignalRoot: signalRoot));
                }, ct);
                if (result.SignalRoot != null) SignalStop(db, result.SignalRoot);
                return result.Snapshot;
            }
            finally { corpus?.Dispose(); if (ownership != null) await ownership.DisposeAsync(); }
        }, token);

    internal static VoxCpmTrainingRecordStore Store(ProjectDatabase db) => new(db.Root, db.State.Metadata.Id);
    internal static VoxCpmTrainingRecord Require(VoxCpmTrainingEnvelope e, string id, long? revision = null)
    {
        var r = e.Current;
        if (!VoxCpmTrainingRecordStore.Id(id) || r == null || r.Snapshot.Id != id) throw Error("missing");
        if (revision != null && revision != r.Snapshot.Revision) throw Error("conflict"); return r;
    }
    private static void RequireResumable(VoxCpmTrainingRecord r)
    {
        if (r.Cursor.GlobalStep >= 512) throw Error("state.limit");
        if (VoxCpmTrainingRecordStore.Active(r.Snapshot.State) || !r.Snapshot.CanResume) throw Error("transition.invalid");
    }
    internal static void SignalStop(ProjectDatabase db, string relativeRoot)
    {
        string root = db.ResolveOwnedPath(relativeRoot); if (!Directory.Exists(root)) return;
        using var pins = new CatalogPaths.Pins(); CatalogPaths.PinDirectories(root, pins);
        string path = db.ResolveOwnedPath(relativeRoot + "/STOP"); if (File.Exists(path)) return;
        try { using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); output.Flush(true); }
        catch (IOException e) when ((e.HResult & 0xffff) is 80 or 183) { }
    }
    private async Task<VoxCpmTrainingIdentity> ReadyIdentity(CancellationToken token)
    {
        if (!(await runtime.GetReadinessAsync(token)).Ready) throw Error("runtime.not_ready");
        var identity = await runtime.GetIdentityAsync(token);
        if (!VoxCpmTrainingRecordStore.ValidIdentity(identity)) throw Error("runtime.not_ready"); return identity;
    }
    private static Task<VoxCpmTrainingSnapshotDto?> Replay(VoxCpmTrainingRecordStore store, string key, string request, CancellationToken token)
        => store.TransactionAsync(e => ((VoxCpmTrainingEnvelope?)null, store.Replay(e, key, request)), token);
    private static string Request(params object[] values) => JsonSerializer.Serialize(values);
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static StudioException Error(string suffix) => new("voxcpm_training." + suffix);
    private async Task<T> WithProject<T>(string id, Func<ProjectDatabase, CancellationToken, Task<T>> action, CancellationToken token)
    {
        try { await using var session = await access.OpenCurrentAsync(id, token); return await action(session.Project, token); }
        catch (StudioException e) when (e.Code.StartsWith("tts_corpus.", StringComparison.Ordinal)) { throw Error("corpus.invalid"); }
        catch (StudioException e) when (e.Code.StartsWith("catalog.", StringComparison.Ordinal)) { throw Error("storage"); }
        catch (VoxCpmTrainingExecutionException e) when (e.CleanupConfirmed)
        { throw new StudioException(VoxCpmTrainingRecordStore.SafeDiagnostic(e.Code) ? e.Code : "voxcpm_training.failed"); }
        catch (ProjectException e) when (e.Code is "project.path" or "project.storage") { throw Error("storage"); }
        catch (ProjectException e) { throw new StudioException(e.Code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw Error("storage"); }
    }
}

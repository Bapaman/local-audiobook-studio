using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Training;

public sealed class StudioTrainingService(ProjectJobAccess access, TrainingRuntime runtime) : IStudioTrainingService
{
    public Task<TrainingReadinessDto> GetReadinessAsync(CancellationToken token = default) => runtime.GetAsync(token);
    public Task<TrainingSnapshotDto?> GetAsync(string projectId, CancellationToken token = default)
        => WithProject(projectId, (db, ct) => Store(db).TransactionAsync(e => ((TrainingEnvelope?)null,
            e.Current is { } record ? TrainingRecordStore.PublicSnapshot(record) : null), ct), token);

    public Task<TrainingSnapshotDto> StartAsync(string projectId, long expectedProjectRevision, string key, bool experimentalConfirmed, CancellationToken token = default)
        => WithProject(projectId, async (db, ct) =>
        {
            string request = "start:" + experimentalConfirmed;
            var store = Store(db);
            var replay = await store.TransactionAsync(e => ((TrainingEnvelope?)null, TrainingRecordStore.Replay(e, key, request)), ct);
            if (replay != null) return replay;
            if (!experimentalConfirmed) throw new StudioException("training.confirmation.required");
            if (!(await runtime.GetAsync(ct)).Ready) throw new StudioException("training.runtime.not_ready");
            TrainingPathPolicy.RequireOutputRoot(db.ResolveOwnedPath("training/actions/" + new string('a', 32)));
            await using var lease = await db.AcquireInputMutationLeaseAsync(ct);
            await lease.RefreshAsync(ct);
            if (db.State.Revision != expectedProjectRevision) throw new StudioException("project.conflict");
            var recording = db.State.VoiceRecording ?? throw new StudioException("voice.recording.missing");
            using var pin = await VerifySourceAsync(db, recording, ct);
            return await store.TransactionAsync(e =>
            {
                var repeated = TrainingRecordStore.Replay(e, key, request);
                if (repeated != null) return ((TrainingEnvelope?)null, repeated);
                if (e.Current != null && (TrainingRecordStore.Active(e.Current.Snapshot.State) || e.Current.Accepted != null))
                    throw new StudioException(TrainingRecordStore.Active(e.Current.Snapshot.State) ? "training.active" : "training.exists");
                var now = DateTimeOffset.UtcNow; string id = Guid.NewGuid().ToString("N"), action = Guid.NewGuid().ToString("N");
                var snapshot = new TrainingSnapshotDto(id, projectId, 1, "queued", "queued", recording.Id, 0, 0, 0, now, now, null, false, [], action);
                var record = new TrainingRecord(snapshot, recording, expectedProjectRevision, action, $"training/actions/{action}", null);
                return (TrainingRecordStore.Receipt(e, record, key, request), snapshot);
            }, ct);
        }, token);

    public Task<TrainingSnapshotDto> ControlAsync(string projectId, string trainingId, long expectedTrainingRevision, string key, string command, CancellationToken token = default)
        => WithProject(projectId, async (db, ct) =>
        {
            if (!TrainingRecordStore.Id(trainingId) || command is not ("stop" or "resume")) throw new StudioException("training.input.invalid");
            string request = "control:" + trainingId + ":" + command;
            var store = Store(db);
            var replay = await store.TransactionAsync(e => ((TrainingEnvelope?)null, TrainingRecordStore.Replay(e, key, request)), ct);
            if (replay != null) return replay;
            ProjectArtifactLease? lease = null; FileStream? pin = null;
            try
            {
                if (command == "resume")
                {
                    if (!(await runtime.GetAsync(ct)).Ready) throw new StudioException("training.runtime.not_ready");
                    TrainingPathPolicy.RequireOutputRoot(db.ResolveOwnedPath("training/actions/" + new string('a', 32)));
                    lease = await db.AcquireInputMutationLeaseAsync(ct); await lease.RefreshAsync(ct);
                    var current = await store.TransactionAsync(e => ((TrainingEnvelope?)null, e.Current), ct);
                    if (current == null || current.Snapshot.Id != trainingId) throw new StudioException("training.missing");
                    pin = await VerifySourceAsync(db, current.Recording, ct);
                }
                var controlled = await store.TransactionAsync(e =>
                {
                    var repeated = TrainingRecordStore.Replay(e, key, request);
                    if (repeated != null) return ((TrainingEnvelope?)null, (Snapshot: repeated, Root: (string?)null));
                    var record = e.Current ?? throw new StudioException("training.missing");
                    var s = record.Snapshot;
                    if (s.Id != trainingId) throw new StudioException("training.missing");
                    if (s.Revision != expectedTrainingRevision) throw new StudioException("training.conflict");
                    string state; string? stopRoot = null;
                    if (command == "stop")
                    {
                        if (!TrainingRecordStore.Active(s.State)) throw new StudioException("training.transition.invalid");
                        state = s.State == "queued" ? "paused" : "stop_requested";
                        if (state == "stop_requested") stopRoot = db.ResolveOwnedPath(record.OutputRelativeRoot);
                        record = record with { StopRequested = true };
                    }
                    else
                    {
                        if (s.State is not ("paused" or "completed" or "interrupted" or "failed") || record.Accepted == null)
                            throw new StudioException("training.transition.invalid");
                        state = "queued"; string action = Guid.NewGuid().ToString("N");
                        record = record with { ActionId = action, OutputRelativeRoot = $"training/actions/{action}", StopRequested = false };
                    }
                    s = s with { Revision = checked(s.Revision + 1), State = state, Phase = state, UpdatedUtc = DateTimeOffset.UtcNow,
                        DiagnosticCode = null, CanResume = !TrainingRecordStore.Active(state) && record.Accepted != null, ExecutionId = record.ActionId };
                    record = record with { Snapshot = s };
                    return (TrainingRecordStore.Receipt(e, record, key, request), (Snapshot: s, Root: stopRoot));
                }, ct);
                if (controlled.Root != null) SignalStop(controlled.Root);
                return controlled.Snapshot;
            }
            finally { pin?.Dispose(); if (lease != null) await lease.DisposeAsync(); }
        }, token);

    internal static TrainingRecordStore Store(ProjectDatabase db) => new(db.Root, db.State.Metadata.Id);
    internal static void SignalStop(string outputRoot)
    {
        if (!Directory.Exists(outputRoot)) return;
        string stop = ProjectPaths.Resolve(outputRoot, "STOP");
        using var file = new FileStream(stop, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read); file.Flush(true);
    }
    internal static async Task<FileStream> VerifySourceAsync(ProjectDatabase db, VoiceRecordingSnapshot expected, CancellationToken token)
    {
        if (db.State.VoiceRecording != expected || !expected.RightsConfirmed) throw new StudioException("training.source.changed");
        string path = db.ResolveOwnedPath(expected.WavRelativePath);
        FileStream? pin = null;
        try
        {
            pin = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var wav = await WavInspector.InspectAsync(path, token);
            if (pin.Length != expected.ByteLength || wav.Sha256 != expected.Sha256 || wav.SampleCount != expected.SampleCount
                || wav.SampleRate != 48000 || wav.Channels != 1 || wav.BitsPerSample != 16) throw new StudioException("training.source.changed");
            return pin;
        }
        catch { pin?.Dispose(); throw; }
    }
    private async Task<T> WithProject<T>(string id, Func<ProjectDatabase, CancellationToken, Task<T>> action, CancellationToken token)
    {
        try { await using var session = await access.OpenCurrentAsync(id, token); return await action(session.Project, token); }
        catch (ProjectException e) { throw new StudioException(e.Code); }
        catch (WavInspectionException) { throw new StudioException("training.source.changed"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new StudioException("training.storage"); }
    }
}

public static class TrainingPathPolicy
{
    public static void RequireOutputRoot(string root)
    {
        // Native Python/Win32 publication adds temporary directories and payload names.
        // Do not change global Windows settings to bypass the legacy path limit.
        if (root.Length > 170) throw new StudioException("training.path_too_long");
    }
}

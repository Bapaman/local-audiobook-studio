using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.VoxCpmTraining;

/// <summary>One explicit requested pass, one native update per window, one atomic accepted cursor.</summary>
public sealed class VoxCpmTrainingRunner(ProjectJobAccess access, ComputeGate compute, IApprovedTtsCorpusResolver resolver,
    IVoxCpmTrainingRuntime runtime, IVoxCpmTrainingCheckpointValidator checkpoints, IVoxCpmTrainingExecutor executor)
{
    private readonly SemaphoreSlim dispatch = new(1, 1);
    private readonly object sync = new();
    private Task<bool>? active;
    private Action? signalStop;
    private volatile bool stopping, cleanupFault;
    private object? retained;

    public async Task RecoverAsync(CancellationToken token)
    {
        foreach (string id in await access.RegisteredIdsAsync(token))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!await access.HasVoxCpmTrainingStateAsync(id, token)) continue;
                await using var session = await access.OpenCurrentAsync(id, token);
                await StudioVoxCpmTrainingService.Store(session.Project).TransactionAsync(e =>
                {
                    if (e.Current is not { } r || !VoxCpmTrainingRecordStore.Active(r.Snapshot.State)) return ((VoxCpmTrainingEnvelope?)null, false);
                    var next = Change(r, r with { StopRequested = false, Snapshot = r.Snapshot with { State = "interrupted", Phase = "interrupted",
                        CanResume = r.Cursor.GlobalStep < 512, DiagnosticCode = "voxcpm_training.interrupted" } });
                    return (e with { Current = next }, true);
                }, token);
            }
            catch (Exception e) when (SafeFailure(e)) { /* A bad project does not trigger replay or block other metadata recovery. */ }
        }
    }
    public async Task<bool> RunNextAsync(CancellationToken token)
    {
        if (!await dispatch.WaitAsync(0, token)) return false;
        Task<bool> pending;
        lock (sync)
        {
            if (stopping || cleanupFault) { dispatch.Release(); return false; }
            pending = active = RunCore(token);
        }
        try { return await pending; }
        finally
        {
            lock (sync) { active = null; if (!cleanupFault) signalStop = null; }
            if (!cleanupFault) dispatch.Release();
        }
    }
    public async Task StopAsync(CancellationToken token)
    {
        Task<bool>? pending;
        lock (sync)
        {
            stopping = true;
            try { signalStop?.Invoke(); } catch { throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false); }
            pending = active;
        }
        if (pending != null)
        {
            try { await pending.WaitAsync(TimeSpan.FromSeconds(30), token); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            { throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false); }
        }
        if (cleanupFault || retained != null) throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false);
    }
    private async Task<bool> RunCore(CancellationToken token)
    {
        await Task.Yield();
        foreach (string id in await access.RegisteredIdsAsync(token))
        {
            if (stopping || token.IsCancellationRequested) return false;
            ProjectJobAccess.JobProjectSession? session = null; ProjectArtifactLease? ownership = null;
            ApprovedTtsCorpusLease? corpus = null; CatalogPaths.Pins? outputPins = null;
            bool slot = false; VoxCpmTrainingRecord? observed = null, claimed = null;
            try
            {
                if (!await access.HasVoxCpmTrainingStateAsync(id, token)) continue;
                session = await access.OpenCurrentAsync(id, token); var db = session.Project; var store = StudioVoxCpmTrainingService.Store(db);
                observed = await Read(store, token); if (observed?.Snapshot.State != "queued") continue;
                if (!await compute.WaitAsync(0, token)) return false; slot = true;
                ownership = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), token);
                await ownership.RefreshAsync(token); await ownership.RequireNoActiveJobsAsync(token);
                if (db.State.VoiceRecording != observed.Corpus.Source) throw Error("source.changed");
                corpus = await resolver.ResolveAsync(db, observed.Corpus.VersionId, observed.Corpus.ManifestSha256, token);
                if (!VoxCpmTrainingRecordStore.SameCorpus(corpus.Binding, observed.Corpus)) throw Error("corpus.changed");
                if (await runtime.GetIdentityAsync(token) != observed.Identity) throw Error("runtime.changed");
                if (observed.Head != null) await checkpoints.VerifyAsync(db.Root, observed.Head, corpus.Binding, observed.Identity, token);
                claimed = await store.TransactionAsync(e =>
                {
                    if (e.Current is not { } r || !SameExecution(r, observed) || r.Snapshot.State != "queued" || r.Cursor != observed.Cursor || r.Head != observed.Head)
                        return ((VoxCpmTrainingEnvelope?)null, (VoxCpmTrainingRecord?)null);
                    var next = Change(r, r with { Snapshot = r.Snapshot with { State = "loading", Phase = "loading", CanResume = false, DiagnosticCode = null } });
                    return (e with { Current = next }, next);
                }, token);
                if (claimed == null) continue;
                string executionRoot = db.ResolveOwnedPath(claimed.OutputRelativeRoot); outputPins = new();
                CreateExecution(db.Root, claimed.OutputRelativeRoot, outputPins);
                lock (sync)
                {
                    signalStop = () => StudioVoxCpmTrainingService.SignalStop(db, claimed.OutputRelativeRoot);
                    if (stopping || token.IsCancellationRequested) signalStop();
                }
                while (true)
                {
                    var current = await Read(store, CancellationToken.None);
                    if (current == null || !SameExecution(current, claimed) || !VoxCpmTrainingRecordStore.Active(current.Snapshot.State)) throw Error("conflict");
                    // This refresh also catches a STOP committed before the output namespace existed.
                    if (current.StopRequested || stopping || token.IsCancellationRequested)
                    { StudioVoxCpmTrainingService.SignalStop(db, current.OutputRelativeRoot); await Finish(store, current, "paused"); return true; }
                    EnsureCapacity(current.Cursor.GlobalStep, new DriveInfo(Path.GetPathRoot(db.Root)!).AvailableFreeSpace);
                    string window = Guid.NewGuid().ToString("N"), checkpointId = Guid.NewGuid().ToString("N");
                    var execution = new VoxCpmTrainingExecution(id, current.Snapshot.Id, current.ActionId, current.Snapshot.ExecutionId,
                        window, checkpointId, db.Root, Path.Combine(executionRoot, "windows", window), corpus.Binding,
                        current.Identity, current.Cursor, current.Head, current.TargetCompletedEpochs);
                    VoxCpmTrainingWindowResult result;
                    try { result = await executor.RunAsync(execution, p => Progress(store, execution, p), CancellationToken.None); }
                    catch (VoxCpmTrainingExecutionException) { throw; }
                    catch { throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false); }
                    if (result == null || !result.CleanupConfirmed) throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false);
                    if (result.Checkpoint == null && !result.Stopped) throw Error("window.invalid");
                    if (result.Checkpoint is { } candidate)
                    {
                        if (candidate.Id != checkpointId || candidate.WindowId != window || candidate.ManifestRelativePath != $"{current.OutputRelativeRoot}/windows/{window}/checkpoints/{checkpointId}/manifest.json") throw Error("window.invalid");
                        await checkpoints.VerifyAsync(db.Root, candidate, corpus.Binding, current.Identity, CancellationToken.None);
                        await store.TransactionAsync(e =>
                        {
                            if (e.Current is not { } live || !SameExecution(live, current) || !VoxCpmTrainingRecordStore.Active(live.Snapshot.State)
                                || live.Cursor != current.Cursor || live.Head != current.Head) throw Error("conflict");
                            return (VoxCpmTrainingRecordStore.AcceptCheckpoint(e, candidate), true);
                        }, CancellationToken.None);
                    }
                    var accepted = await Read(store, CancellationToken.None);
                    if (accepted == null || !SameExecution(accepted, current)) throw Error("conflict");
                    if (accepted.Cursor.CompletedEpochs >= accepted.TargetCompletedEpochs || result.Stopped || accepted.StopRequested || stopping || token.IsCancellationRequested)
                    { await Finish(store, accepted, "paused"); return true; }
                }
            }
            catch (VoxCpmTrainingExecutionException e) when (!e.CleanupConfirmed)
            {
                cleanupFault = true;
                var store = session == null ? null : StudioVoxCpmTrainingService.Store(session.Project);
                retained = (session, ownership, corpus, outputPins); session = null; ownership = null; corpus = null; outputPins = null; slot = false;
                try { if (store != null && claimed != null) await Fail(store, claimed, e.Code, false); } catch { }
                throw;
            }
            catch (Exception e) when (SafeFailure(e))
            {
                string code = e switch { VoxCpmTrainingExecutionException x => x.Code, StudioException s => s.Code, ProjectException p => p.Code, _ => "voxcpm_training.failed" };
                if (claimed == null && code is "project.busy" or "job.active") continue;
                if (session != null && (claimed ?? observed) is { } failed)
                    try { await Fail(StudioVoxCpmTrainingService.Store(session.Project), failed, code, true); } catch (Exception error) when (SafeFailure(error)) { }
                return claimed != null;
            }
            finally
            {
                lock (sync) { if (!cleanupFault) signalStop = null; }
                try { outputPins?.Dispose(); } finally
                {
                    try { corpus?.Dispose(); } finally
                    {
                        try { if (ownership != null) await ownership.DisposeAsync(); } finally
                        { try { if (session != null) await session.DisposeAsync(); } finally { if (slot) compute.Release(); } }
                    }
                }
            }
        }
        return false;
    }
    internal static void EnsureCapacity(long acceptedSteps, long availableBytes)
    {
        if (acceptedSteps >= 512) throw Error("state.limit");
        // Maximum new bundle + result/request and two bounded state versions,
        // rounded up; no deletion or implicit history rotation on low disk.
        if (availableBytes < 64 * 1048576) throw Error("disk.insufficient");
    }
    private static void CreateExecution(string root, string relative, CatalogPaths.Pins pins)
    {
        CatalogPaths.PinDirectories(root, pins); string current = root;
        foreach (string part in relative.Split('/'))
        { current = Path.Combine(current, part); Directory.CreateDirectory(current); CatalogPaths.PinDirectories(current, pins); }
    }
    private static bool SameExecution(VoxCpmTrainingRecord a, VoxCpmTrainingRecord b)
        => a.Snapshot.Id == b.Snapshot.Id && a.ActionId == b.ActionId && a.Snapshot.ExecutionId == b.Snapshot.ExecutionId;
    private static Task<VoxCpmTrainingRecord?> Read(VoxCpmTrainingRecordStore store, CancellationToken token)
        => store.TransactionAsync(e => ((VoxCpmTrainingEnvelope?)null, e.Current), token);
    private static Task<bool> Update(VoxCpmTrainingRecordStore store, VoxCpmTrainingRecord identity, Func<VoxCpmTrainingRecord, VoxCpmTrainingRecord> update, bool activeOnly = true)
        => store.TransactionAsync(e =>
        {
            if (e.Current is not { } r || !SameExecution(r, identity) || activeOnly && !VoxCpmTrainingRecordStore.Active(r.Snapshot.State)) return ((VoxCpmTrainingEnvelope?)null, false);
            var next = update(r); if (next == r) return ((VoxCpmTrainingEnvelope?)null, false);
            return (e with { Current = Change(r, next) }, true);
        }, CancellationToken.None);
    private static VoxCpmTrainingRecord Change(VoxCpmTrainingRecord old, VoxCpmTrainingRecord next)
        => next with { Snapshot = next.Snapshot with { Revision = checked(old.Snapshot.Revision + 1), UpdatedUtc = DateTimeOffset.UtcNow } };
    private static Task Progress(VoxCpmTrainingRecordStore store, VoxCpmTrainingExecution execution, VoxCpmTrainingProgress progress)
        => store.TransactionAsync(e =>
        {
            if (progress.Phase is not ("loading" or "training" or "checkpointing") || e.Current is not { } r
                || r.Snapshot.Id != execution.TrainingId || r.ActionId != execution.ActionId || r.Snapshot.ExecutionId != execution.ExecutionId
                || r.Cursor != execution.Cursor || r.Head != execution.ParentCheckpoint || !VoxCpmTrainingRecordStore.Active(r.Snapshot.State)) return ((VoxCpmTrainingEnvelope?)null, false);
            string phase = r.StopRequested ? "stop_requested" : progress.Phase;
            var next = Change(r, r with { Snapshot = r.Snapshot with { State = phase, Phase = phase } }); return (e with { Current = next }, true);
        }, CancellationToken.None);
    private static Task<bool> Finish(VoxCpmTrainingRecordStore store, VoxCpmTrainingRecord identity, string fallback)
        => Update(store, identity, r =>
        {
            string state = r.Cursor.CompletedEpochs >= r.TargetCompletedEpochs ? "completed" : fallback;
            return r with { Snapshot = r.Snapshot with { State = state, Phase = state, CanResume = r.Cursor.GlobalStep < 512, DiagnosticCode = null } };
        }, activeOnly: false);
    private static Task<bool> Fail(VoxCpmTrainingRecordStore store, VoxCpmTrainingRecord identity, string code, bool resume)
        => Update(store, identity, r => r with { Snapshot = r.Snapshot with { State = "failed", Phase = "failed", CanResume = resume && r.Cursor.GlobalStep < 512,
            DiagnosticCode = VoxCpmTrainingRecordStore.SafeDiagnostic(code) ? code : "voxcpm_training.failed" } });
    private static VoxCpmTrainingExecutionException Error(string suffix) => new("voxcpm_training." + suffix);
    private static bool SafeFailure(Exception e) => e is StudioException or ProjectException or VoxCpmTrainingExecutionException
        or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NullReferenceException;
}

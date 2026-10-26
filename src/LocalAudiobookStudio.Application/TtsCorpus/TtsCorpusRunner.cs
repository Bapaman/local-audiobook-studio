using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.TtsCorpus;

/// <summary>Explicit queued dispatch; accepted windows survive stop and restart. Unknown child cleanup retains all ownership.</summary>
public sealed class TtsCorpusRunner(ProjectJobAccess access, ComputeGate compute, ITtsCorpusRuntime runtime, ITtsCorpusExecutor executor)
{
    private readonly SemaphoreSlim dispatch = new(1, 1);
    private readonly object sync = new();
    private Task<bool>? active;
    private Action? signalStop;
    private volatile bool stopping;
    private volatile bool cleanupFault;
    private object? retained;

    public async Task RecoverAsync(CancellationToken token)
    {
        foreach (string id in await access.RegisteredIdsAsync(token))
        {
            if (!await access.HasTtsCorpusStateAsync(id, token)) continue;
            await using var session = await access.OpenCurrentAsync(id, token);
            var db = session.Project; var store = StudioTtsCorpusService.Store(db);
            var observed = await Read(store, token);
            if (observed == null) continue;
            bool wasActive = TtsCorpusRecordStore.Active(observed.Snapshot.State);
            if (!wasActive && !TtsCorpusRecordStore.CanResume(observed)) continue;
            bool resumable = false;
            try
            {
                await using var lease = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), token);
                await lease.RefreshAsync(token); await lease.RequireNoActiveJobsAsync(token);
                using var source = await TtsCorpusSource.VerifyAsync(db, observed.Recording, token);
                await ValidateRuntime(observed, token);
                if (observed.Plan != null) await VerifyPlanAndPayloads(db, observed, token);
                resumable = TtsCorpusRecordStore.CanResume(observed with { Snapshot = observed.Snapshot with { State = "interrupted" } });
            }
            catch (Exception e) when (SafeFailure(e)) { /* Invalid inputs cannot be reused; the interrupted draft remains readable. */ }
            await Update(store, observed, r =>
            {
                if (!wasActive) return r.Snapshot.CanResume == resumable ? r : r with { Snapshot = r.Snapshot with { CanResume = resumable } };
                if (!TtsCorpusRecordStore.Active(r.Snapshot.State)) return r;
                return r with { StopRequested = false, Snapshot = r.Snapshot with {
                    State = "interrupted", Phase = "interrupted", CanResume = resumable, DiagnosticCode = "tts_corpus.interrupted" } };
            }, activeOnly: false);
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
        lock (sync) { stopping = true; signalStop?.Invoke(); pending = active; }
        if (pending != null)
        {
            try { await pending.WaitAsync(TimeSpan.FromSeconds(30), token); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            { throw new TtsCorpusExecutionException("tts_corpus.cleanup", false); }
        }
        if (cleanupFault || retained != null) throw new TtsCorpusExecutionException("tts_corpus.cleanup", false);
    }

    private async Task<bool> RunCore(CancellationToken token)
    {
        await Task.Yield();
        foreach (string id in await access.RegisteredIdsAsync(token))
        {
            if (stopping) return false;
            ProjectJobAccess.JobProjectSession? session = null; ProjectArtifactLease? lease = null;
            TtsCorpusSource? source = null; CatalogPaths.Pins? outputPins = null;
            bool slot = false; TtsCorpusRecord? observed = null; TtsCorpusRecord? claimed = null;
            try
            {
                if (!await access.HasTtsCorpusStateAsync(id, token)) continue;
                session = await access.OpenCurrentAsync(id, token);
                var db = session.Project; var store = StudioTtsCorpusService.Store(db);
                observed = await Read(store, token);
                if (observed?.Snapshot.State != "queued") continue;
                if (!await compute.WaitAsync(0, token)) return false;
                slot = true;
                lease = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), token);
                await lease.RefreshAsync(token); await lease.RequireNoActiveJobsAsync(token);
                source = await TtsCorpusSource.VerifyAsync(db, observed.Recording, token);
                await ValidateRuntime(observed, token);
                if (observed.Plan != null) await VerifyPlanAndPayloads(db, observed, token);
                claimed = await store.TransactionAsync(e =>
                {
                    if (e.Current is not { } r || !SameExecution(r, observed) || r.Snapshot.State != "queued")
                        return ((TtsCorpusEnvelope?)null, (TtsCorpusRecord?)null);
                    var next = Change(r, r with { Snapshot = r.Snapshot with { State = r.Plan == null ? "preparing" : "transcribing",
                        Phase = r.Plan == null ? "preparing" : "transcribing", CanResume = false, DiagnosticCode = null } });
                    return (e with { Current = next }, next);
                }, token);
                if (claimed == null) continue;
                string output = db.ResolveOwnedPath(claimed.OutputRelativeRoot); Directory.CreateDirectory(output);
                outputPins = new(); CatalogPaths.PinDirectories(output, outputPins);
                lock (sync)
                {
                    string stopRoot = claimed.OutputRelativeRoot;
                    signalStop = () => StudioTtsCorpusService.SignalStop(db, stopRoot);
                    if (stopping) signalStop();
                }
                // STOP may have committed before the execution namespace existed.
                var latest = await Read(store, CancellationToken.None);
                if (latest == null || !SameExecution(latest, claimed)) throw new TtsCorpusExecutionException("tts_corpus.conflict");
                if (latest.StopRequested) StudioTtsCorpusService.SignalStop(db, claimed.OutputRelativeRoot);
                while (true)
                {
                    var current = await Read(store, CancellationToken.None);
                    if (current == null || !SameExecution(current, claimed) || !TtsCorpusRecordStore.Active(current.Snapshot.State))
                        throw new TtsCorpusExecutionException("tts_corpus.conflict");
                    if (stopping || current.StopRequested || token.IsCancellationRequested)
                    { await Finish(store, current, "paused"); return true; }
                    var execution = new TtsCorpusExecution(id, current.Snapshot.Id, current.ActionId, current.Snapshot.ExecutionId!, db.Root,
                        output, db.ResolveOwnedPath(current.Recording.WavRelativePath), current.Recording.Id, current.Recording.Sha256.ToLowerInvariant(),
                        current.Recording.SampleCount, current.RuntimeFingerprint, current.Plan, current.Accepted.Count);
                    TtsCorpusWindowResult result;
                    try
                    {
                        // Caller cancellation never releases a possibly living native child.
                        result = await executor.RunAsync(execution, p => Progress(store, claimed, p), CancellationToken.None);
                    }
                    catch (TtsCorpusExecutionException) { throw; }
                    catch (Exception) { throw new TtsCorpusExecutionException("tts_corpus.cleanup", false); }
                    if (result == null || !result.CleanupConfirmed) throw new TtsCorpusExecutionException("tts_corpus.cleanup", false);
                    var candidate = Candidate(current, result);
                    if (candidate.Plan != null) await VerifyPlanAndPayloads(db, candidate, CancellationToken.None);
                    await Update(store, claimed, r =>
                    {
                        if (r.Accepted.Count != current.Accepted.Count || r.Plan?.Sha256 != current.Plan?.Sha256)
                            throw new TtsCorpusExecutionException("tts_corpus.conflict");
                        return candidate with { StopRequested = r.StopRequested, Snapshot = candidate.Snapshot with {
                            State = r.StopRequested ? "stop_requested" : candidate.Snapshot.State,
                            Phase = r.StopRequested ? "stop_requested" : candidate.Snapshot.Phase } };
                    });
                    bool complete = candidate.Plan != null && candidate.Accepted.Count == candidate.Plan.Clips.Count;
                    if (result.Stopped || complete)
                    {
                        await Finish(store, claimed, result.Stopped || stopping ? "paused" : "completed"); return true;
                    }
                }
            }
            catch (TtsCorpusExecutionException e) when (!e.CleanupConfirmed)
            {
                cleanupFault = true;
                var store = session == null ? null : StudioTtsCorpusService.Store(session.Project);
                retained = (session, lease, source, outputPins); session = null; lease = null; source = null; outputPins = null; slot = false;
                try { if (store != null && claimed != null) await Fail(store, claimed, e.Code, false); } catch { }
                throw;
            }
            catch (Exception e) when (SafeFailure(e))
            {
                string code = e switch { StudioException s => s.Code, ProjectException p => p.Code, TtsCorpusExecutionException x => x.Code, _ => "tts_corpus.failed" };
                if (claimed == null && code is "project.busy" or "job.active") continue;
                if (session != null && (claimed ?? observed) is { } failed)
                    await Fail(StudioTtsCorpusService.Store(session.Project), failed, code, claimed != null && !code.Contains(".changed", StringComparison.Ordinal));
                return claimed != null;
            }
            finally
            {
                lock (sync) { if (!cleanupFault) signalStop = null; }
                outputPins?.Dispose(); source?.Dispose();
                if (lease != null) await lease.DisposeAsync();
                if (session != null) await session.DisposeAsync();
                if (slot) compute.Release();
            }
        }
        return false;
    }

    private async Task ValidateRuntime(TtsCorpusRecord r, CancellationToken token)
    {
        if (!(await runtime.GetReadinessAsync(token)).Ready) throw new TtsCorpusExecutionException("tts_corpus.runtime.not_ready");
        if (!TtsCorpusRecordStore.SameHash(await runtime.GetFingerprintAsync(token), r.RuntimeFingerprint))
            throw new TtsCorpusExecutionException("tts_corpus.runtime.changed");
    }

    private static TtsCorpusRecord Candidate(TtsCorpusRecord current, TtsCorpusWindowResult result)
    {
        if (result.Clips == null) throw InvalidWindow();
        if (current.Plan == null)
        {
            if (result.Clips.Count != 0 || result.PreparedPlan == null && !result.Stopped) throw InvalidWindow();
            if (result.PreparedPlan == null) return current;
            ValidatePlan(current, result.PreparedPlan);
            return current with { Plan = result.PreparedPlan, Snapshot = current.Snapshot with {
                TotalClipCount = result.PreparedPlan.Clips.Count, State = "transcribing", Phase = "transcribing" } };
        }
        if (result.PreparedPlan != null) throw InvalidWindow();
        int expected = Math.Min(8, current.Plan.Clips.Count - current.Accepted.Count);
        if (result.Clips.Count > expected || !result.Stopped && result.Clips.Count != expected || expected == 0) throw InvalidWindow();
        for (int i = 0; i < result.Clips.Count; i++)
        {
            var c = result.Clips[i];
            if (c == null || c.Plan != current.Plan.Clips[current.Accepted.Count + i] || !TtsCorpusRecordStore.HashValid(c.TextSha256)) throw InvalidWindow();
            RequireOutputPath(current, c.TextRelativePath);
        }
        var clips = result.Clips.Select(c => new TtsCorpusClipDto(c.Plan.Id, c.Plan.Index, c.Plan.StartSample, c.Plan.EndSample,
            TtsCorpusRecordStore.Normalize(c.DraftText), false, false, c.WarningCodes, c.Words)).ToArray();
        return current with { Accepted = [.. current.Accepted, .. result.Clips], Snapshot = current.Snapshot with {
            Clips = [.. current.Snapshot.Clips, .. clips], CompletedClipCount = current.Accepted.Count + clips.Length } };
    }
    private static void ValidatePlan(TtsCorpusRecord r, TtsCorpusPreparedPlan p)
    {
        RequireOutputPath(r, p.PlanRelativePath);
        if (!TtsCorpusRecordStore.HashValid(p.Sha256) || !TtsCorpusRecordStore.SameHash(p.SourceSha256, r.Recording.Sha256)
            || p.RuntimeFingerprint != r.RuntimeFingerprint || p.TotalSamples != (r.Recording.SampleCount + 2) / 3
            || p.Clips == null || p.Clips.Count is < 1 or > 512 || p.Clips.Select(c => c?.Id).Distinct().Count() != p.Clips.Count) throw InvalidWindow();
        long end = 0;
        for (int i = 0; i < p.Clips.Count; i++)
        {
            var c = p.Clips[i];
            if (c == null || !TtsCorpusRecordStore.Id(c.Id) || c.Index != i || c.StartSample != end || c.EndSample <= end
                || c.EndSample - end > 256000 || !TtsCorpusRecordStore.HashValid(c.AudioSha256)) throw InvalidWindow();
            RequireOutputPath(r, c.AudioRelativePath); end = c.EndSample;
        }
        if (end != p.TotalSamples) throw InvalidWindow();
    }
    private static void RequireOutputPath(TtsCorpusRecord r, string path)
    { if (path == null || !path.StartsWith(r.OutputRelativeRoot + "/", StringComparison.Ordinal) || path.Contains('\\')) throw InvalidWindow(); }
    private static async Task VerifyPlanAndPayloads(ProjectDatabase db, TtsCorpusRecord r, CancellationToken token)
    {
        await StudioTtsCorpusService.VerifyPayloads(db, r, token);
        // Validate unaccepted planned audio too, before preparation becomes reusable.
        foreach (var c in r.Plan!.Clips)
        {
            using var pins = new CatalogPaths.Pins(); string path = db.ResolveOwnedPath(c.AudioRelativePath);
            CatalogPaths.PinDirectories(Path.GetDirectoryName(path)!, pins);
            await using var file = TtsCorpusSource.OpenRead(path); await TtsCorpusSource.ValidateAudioAsync(file, c, token);
        }
    }
    private static bool SameExecution(TtsCorpusRecord a, TtsCorpusRecord b) => a.Snapshot.Id == b.Snapshot.Id
        && a.ActionId == b.ActionId && a.Snapshot.ExecutionId == b.Snapshot.ExecutionId;
    private static Task<TtsCorpusRecord?> Read(TtsCorpusRecordStore store, CancellationToken token)
        => store.TransactionAsync(e => ((TtsCorpusEnvelope?)null, e.Current), token);
    private static Task<bool> Update(TtsCorpusRecordStore store, TtsCorpusRecord identity, Func<TtsCorpusRecord, TtsCorpusRecord> update, bool activeOnly = true)
        => store.TransactionAsync(e =>
        {
            if (e.Current is not { } r || !SameExecution(r, identity) || activeOnly && !TtsCorpusRecordStore.Active(r.Snapshot.State)) return ((TtsCorpusEnvelope?)null, false);
            var next = update(r); if (next == r) return ((TtsCorpusEnvelope?)null, false);
            return (e with { Current = Change(r, next) }, true);
        }, CancellationToken.None);
    private static TtsCorpusRecord Change(TtsCorpusRecord old, TtsCorpusRecord next) => next with { Snapshot = next.Snapshot with {
        Revision = checked(old.Snapshot.Revision + 1), UpdatedUtc = DateTimeOffset.UtcNow } };
    private static Task<bool> Progress(TtsCorpusRecordStore store, TtsCorpusRecord identity, TtsCorpusProgress progress)
        => progress.Phase is "preparing" or "transcribing" ? Update(store, identity, r => r with { Snapshot = r.Snapshot with {
            Phase = r.StopRequested ? "stop_requested" : progress.Phase } }) : Task.FromResult(false);
    private static Task<bool> Finish(TtsCorpusRecordStore store, TtsCorpusRecord identity, string state)
        => Update(store, identity, r => r with { Snapshot = r.Snapshot with {
            State = r.StopRequested ? "paused" : state, Phase = r.StopRequested ? "paused" : state,
            CanResume = (r.StopRequested || state == "paused") && (r.Plan == null || r.Accepted.Count < r.Plan.Clips.Count), DiagnosticCode = null } });
    private static Task<bool> Fail(TtsCorpusRecordStore store, TtsCorpusRecord identity, string code, bool resumable)
        => Update(store, identity, r => r with { Snapshot = r.Snapshot with { State = "failed", Phase = "failed",
            CanResume = resumable && (r.Plan == null || r.Accepted.Count < r.Plan.Clips.Count),
            DiagnosticCode = TtsCorpusRecordStore.SafeDiagnostic(code) ? code : "tts_corpus.failed" } });
    private static TtsCorpusExecutionException InvalidWindow() => new("tts_corpus.window.invalid");
    private static bool SafeFailure(Exception e) => e is StudioException or ProjectException or TtsCorpusExecutionException
        or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NullReferenceException;
}

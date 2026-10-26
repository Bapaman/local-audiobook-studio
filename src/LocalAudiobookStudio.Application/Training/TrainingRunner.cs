using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Audio;

namespace LocalAudiobookStudio.Application.Training;

/// <summary>Hosted work survives HTTP aborts; each action runs once and never automatically resumes.</summary>
public sealed class TrainingRunner(ProjectJobAccess access, ComputeGate compute, ITrainingWorkflowExecutor executor)
{
    private readonly SemaphoreSlim dispatch = new(1, 1);
    private readonly object sync = new();
    private Task<bool>? active;
    private string? activeOutput;
    private volatile bool stopping;
    private bool cleanupFault;
    private object? retained;
    public async Task RecoverAsync(CancellationToken token)
    {
        foreach (string id in await access.RegisteredIdsAsync(token))
        {
            if (!await access.HasTrainingStateAsync(id, token)) continue;
            await using var session = await access.OpenCurrentAsync(id, token);
            await StudioTrainingService.Store(session.Project).TransactionAsync(e =>
            {
                if (e.Current is not { } record || !TrainingRecordStore.Active(record.Snapshot.State)) return ((TrainingEnvelope?)null, false);
                var s = record.Snapshot with { State = "interrupted", Phase = "interrupted", Revision = record.Snapshot.Revision + 1,
                    UpdatedUtc = DateTimeOffset.UtcNow, DiagnosticCode = "training.interrupted", CanResume = record.Accepted != null };
                return (e with { Current = record with { Snapshot = s, StopRequested = false } }, true);
            }, token);
        }
    }
    public async Task<bool> RunNextAsync(CancellationToken token)
    {
        var ids = await access.RegisteredIdsAsync(token);
        if (!await dispatch.WaitAsync(0, token)) return false;
        Task<bool> task;
        lock (sync)
        {
            if (stopping || cleanupFault) { dispatch.Release(); return false; }
            task = active = RunCore(ids, token);
        }
        try { return await task; }
        finally { lock (sync) { active = null; activeOutput = null; } if (!cleanupFault) dispatch.Release(); }
    }
    public async Task StopAsync(CancellationToken token)
    {
        Task<bool>? pending; string? output;
        lock (sync) { stopping = true; pending = active; output = activeOutput; }
        if (output != null) StudioTrainingService.SignalStop(output);
        if (pending != null)
        {
            try { await pending.WaitAsync(TimeSpan.FromSeconds(30), token); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { throw new TrainingExecutionException("training.cleanup", false); }
        }
        if (cleanupFault || retained != null) throw new TrainingExecutionException("training.cleanup", false);
    }
    private async Task<bool> RunCore(IReadOnlyList<string> ids, CancellationToken token)
    {
        await Task.Yield();
        foreach (var id in ids)
        {
            if (stopping) return false;
            ProjectJobAccess.JobProjectSession? session = null; ProjectArtifactLease? lease = null; FileStream? source = null;
            bool slot = false; TrainingRecord? claimed = null; TrainingRecord? observed = null;
            try
            {
                if (!await access.HasTrainingStateAsync(id, token)) continue;
                session = await access.OpenCurrentAsync(id, token);
                var db = session.Project; var store = StudioTrainingService.Store(db);
                var queued = await store.TransactionAsync(e => ((TrainingEnvelope?)null, e.Current is { Snapshot.State: "queued" } ? e.Current : null), token);
                if (queued == null) continue;
                observed = queued;
                if (!await compute.WaitAsync(0, token)) return false; slot = true;
                lease = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), token);
                await lease.RefreshAsync(token); await lease.RequireNoActiveJobsAsync(token);
                source = await StudioTrainingService.VerifySourceAsync(db, queued.Recording, token);
                claimed = await store.TransactionAsync(e =>
                {
                    if (e.Current is not { } current || current.Snapshot.State != "queued" || current.ActionId != queued.ActionId)
                        return ((TrainingEnvelope?)null, (TrainingRecord?)null);
                    var next = current with { Snapshot = current.Snapshot with { State = "preparing", Phase = "preparing",
                        Revision = current.Snapshot.Revision + 1, UpdatedUtc = DateTimeOffset.UtcNow, CanResume = false } };
                    return (e with { Current = next }, next);
                }, token);
                if (claimed == null) continue;
                string output = db.ResolveOwnedPath(claimed.OutputRelativeRoot); Directory.CreateDirectory(output);
                lock (sync) { activeOutput = output; if (stopping) StudioTrainingService.SignalStop(output); }
                // A committed stop that raced directory creation must not be lost.
                var latest = await store.TransactionAsync(e => ((TrainingEnvelope?)null, e.Current), CancellationToken.None);
                if (latest?.StopRequested == true) StudioTrainingService.SignalStop(output);
                var execution = new TrainingExecution(db.Root, output, db.ResolveOwnedPath(claimed.Recording.WavRelativePath), claimed.Recording.Sha256.ToLowerInvariant(), claimed.Accepted);
                var result = await executor.RunAsync(execution,
                    p => Update(store, claimed.ActionId, r => r with { Snapshot = r.Snapshot with { Phase = p.Phase,
                        State = r.StopRequested ? "stop_requested" : p.Phase.Contains("training", StringComparison.Ordinal) || p.Phase.StartsWith("session_", StringComparison.Ordinal) ? "training" : "preparing",
                        GlobalStep = p.GlobalStep, CompletedEpoch = p.CompletedEpoch, EpochSize = p.EpochSize } }),
                    point => Accept(store, claimed.ActionId, point), token);
                if (result != null && result.GlobalStep > 0) await Accept(store, claimed.ActionId, result);
                await Update(store, claimed.ActionId, r => r with { Snapshot = r.Snapshot with {
                    State = r.StopRequested || result?.StopReason is "user_stop" or "time_budget" or "checkpoint_budget" ? "paused" : "completed", Phase = "idle",
                    CanResume = r.Accepted != null, DiagnosticCode = result?.StopReason is "time_budget" or "checkpoint_budget" ? "training." + result.StopReason : null } });
                return true;
            }
            catch (TrainingExecutionException e)
            {
                if (!e.CleanupConfirmed)
                {
                    var store = session == null ? null : StudioTrainingService.Store(session.Project);
                    cleanupFault = true; retained = (session!, lease!, source!); session = null; lease = null; source = null; slot = false;
                    try { if (claimed != null && store != null) await Fail(store, claimed.ActionId, e.Code); }
                    catch { /* Retain identities even when the interruption record cannot be committed. */ }
                    throw;
                }
                if (claimed != null && session != null) await Fail(StudioTrainingService.Store(session.Project), claimed.ActionId, e.Code);
                return claimed != null;
            }
            catch (Exception e) when (e is StudioException or LocalAudiobookStudio.Core.Projects.ProjectException or IOException or UnauthorizedAccessException or WavInspectionException)
            {
                string code = e is StudioException se ? se.Code : e is LocalAudiobookStudio.Core.Projects.ProjectException pe ? pe.Code : "training.failed";
                if (claimed == null && code is "project.busy" or "job.active") continue;
                if (session != null && (claimed ?? observed) is { } failed)
                {
                    // Never apply an old preclaim failure to a replacement accepted by another tab.
                    await FailIfCurrent(StudioTrainingService.Store(session.Project), failed.ActionId, code);
                }
                return claimed != null;
            }
            finally
            {
                source?.Dispose(); if (lease != null) await lease.DisposeAsync(); if (session != null) await session.DisposeAsync();
                if (slot) compute.Release();
            }
        }
        return false;
    }
    private static Task<bool> Update(TrainingRecordStore store, string action, Func<TrainingRecord, TrainingRecord> update)
        => store.TransactionAsync(e =>
        {
            if (e.Current is not { } record || record.ActionId != action) throw new TrainingExecutionException("training.conflict");
            var next = update(record);
            // Filtered batch-ready events often carry exactly the same public
            // cursor. They are not state changes and must not invalidate STOP.
            if (next == record) return ((TrainingEnvelope?)null, false);
            next = next with { Snapshot = next.Snapshot with { Revision = record.Snapshot.Revision + 1, UpdatedUtc = DateTimeOffset.UtcNow } };
            return (e with { Current = next }, true);
        }, CancellationToken.None);
    private static Task<bool> Accept(TrainingRecordStore store, string action, WorkflowReference point)
        => Update(store, action, r =>
        {
            if (r.Accepted?.Sha256 == point.Sha256) return r;
            var models = r.Snapshot.Models;
            if (models.Count >= 256) throw new TrainingExecutionException("training.state.limit");
            var references = new Dictionary<string, WorkflowReference>(r.ModelReferences ?? new Dictionary<string, WorkflowReference>(), StringComparer.Ordinal);
            // Freeze the only unambiguous legacy latest reference before a new
            // accepted cursor overwrites it. Historical files are never scanned.
            if (models.Count > 0 && TrainingRecordStore.ModelReference(r, models[^1].Id) is { } legacy)
                references.TryAdd(models[^1].Id, legacy);
            if (r.Accepted == null || r.Accepted.GlobalStep < point.GlobalStep)
            {
                string modelId = Guid.NewGuid().ToString("N");
                models = [.. models, new TrainingModelDto(modelId, point.GlobalStep, point.CompletedEpoch, DateTimeOffset.UtcNow, Selectable: true)];
                references.Add(modelId, point);
            }
            return r with { Accepted = point, ModelReferences = references,
                Snapshot = r.Snapshot with { GlobalStep = point.GlobalStep, CompletedEpoch = point.CompletedEpoch, EpochSize = point.EpochSize,
                    Models = models.Select(x => x with { Selectable = references.ContainsKey(x.Id) }).ToArray() } };
        });
    private static Task<bool> Fail(TrainingRecordStore store, string action, string code)
        => Update(store, action, r => r with { Snapshot = r.Snapshot with { State = "failed", Phase = "failed",
            DiagnosticCode = SafeDiagnostic(code), CanResume = r.Accepted != null } });
    private static Task<bool> FailIfCurrent(TrainingRecordStore store, string action, string code)
        => store.TransactionAsync(e =>
        {
            if (e.Current is not { } r || r.ActionId != action || !TrainingRecordStore.Active(r.Snapshot.State))
                return ((TrainingEnvelope?)null, false);
            var next = r with { Snapshot = r.Snapshot with { State = "failed", Phase = "failed", Revision = r.Snapshot.Revision + 1,
                UpdatedUtc = DateTimeOffset.UtcNow, DiagnosticCode = SafeDiagnostic(code), CanResume = r.Accepted != null } };
            return (e with { Current = next }, true);
        }, CancellationToken.None);
    private static string SafeDiagnostic(string code) => code.Length <= 80 && code.StartsWith("training.", StringComparison.Ordinal)
        && code.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '_') ? code : "training.failed";
}

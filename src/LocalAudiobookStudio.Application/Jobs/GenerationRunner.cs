using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Application.Artifacts;
using LocalAudiobookStudio.Application.VoxCpmInference;

namespace LocalAudiobookStudio.Application.Jobs;

/// <summary>One local inference slot. SQLite, not a wakeup, decides eligible work.</summary>
public sealed class GenerationRunner(ProjectJobAccess access, ProjectService core, IWorkerClientFactory workers, TimeProvider time, ComputeGate? computeGate = null, ISavedVoiceConverter? savedVoiceConverter = null,
    IVoxCpmInferenceRuntime? voxRuntime = null, IVoxCpmSavedVoiceResolver? voxSavedVoices = null, IVoxCpmInferenceExecutor? voxExecutor = null)
{
    private readonly SemaphoreSlim dispatch = new(1, 1);
    private readonly ComputeGate inferenceSlot = computeGate ?? new();
    private readonly ISavedVoiceConverter? voiceConverter = savedVoiceConverter;
    private readonly object sync = new();
    private readonly TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<bool>? active;
    private Unit? retained;
    private string? lastProject;
    private Task? stop;

    public async Task<bool> RunNextAsync(CancellationToken token, bool legacyReady = true)
    {
        // Catalog resolution precedes inference ownership. Never invert these locks.
        var ids = await access.RegisteredIdsAsync(token);
        if (!await dispatch.WaitAsync(0, token)) return false;
        Task<bool> operation;
        lock (sync)
        {
            if (stopping.Task.IsCompleted) { dispatch.Release(); return false; }
            operation = active = RunCore(ids, recoverOnly: false, token, legacyReady);
        }
        try { return await operation; }
        finally { lock (sync) { active = null; if (retained == null) dispatch.Release(); } }
    }

    public async Task RecoverAsync(CancellationToken token)
    {
        var ids = await access.RegisteredIdsAsync(token);
        if (!await dispatch.WaitAsync(0, token)) return;
        Task<bool> operation;
        lock (sync)
        {
            if (stopping.Task.IsCompleted) { dispatch.Release(); return; }
            operation = active = RunCore(ids, recoverOnly: true, token);
        }
        try { await operation; }
        finally { lock (sync) { active = null; if (retained == null) dispatch.Release(); } }
    }

    public Task StopAsync(CancellationToken token)
    {
        lock (sync) return stop ??= StopCore(token);
    }

    private async Task StopCore(CancellationToken token)
    {
        long started = time.GetTimestamp(); stopping.TrySetResult();
        Task<bool>? running; lock (sync) running = active;
        if (running != null)
        {
            TimeSpan remaining = TimeSpan.FromSeconds(30) - time.GetElapsedTime(started);
            try { await running.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, time, token); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            { throw new WorkerClientException("worker.cleanup", false); }
            catch when (running.IsCompleted)
            { /* Run reports its completed error separately; retained ownership below is the exit authority. */ }
            catch { throw new WorkerClientException("worker.cleanup", false); }
        }
        lock (sync) if (retained != null) throw new WorkerClientException("worker.cleanup", false);
    }

    private async Task<bool> RunCore(IReadOnlyList<string> ids, bool recoverOnly, CancellationToken token, bool legacyReady = true)
    {
        await Task.Yield(); // Publish the active task before Stop can observe it.
        int start = lastProject == null ? 0 : (ids.ToList().IndexOf(lastProject) + 1) % Math.Max(1, ids.Count);
        for (int offset = 0; offset < ids.Count; offset++)
        {
            if (stopping.Task.IsCompleted) return false;
            token.ThrowIfCancellationRequested(); string id = ids[(start + offset) % ids.Count];
            ProjectJobAccess.JobProjectSession? session = null; ProjectArtifactLease? lease = null; Unit? unit = null;
            ProjectJobAccess.JobPreparation? preparation = null;
            bool slotHeld = false;
            try
            {
                try
                {
                    try { session = await access.OpenCurrentAsync(id, token, requireJobSchema: true); }
                    catch (ProjectException e) when (e.Code == "project.storage")
                    { preparation = await access.CapturePreparationAsync(id, token); }
                }
                catch (ProjectException e) when (e.Code is "project.busy" or "project.storage") { continue; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException || e is StudioException { Code: "catalog.storage" }) { continue; }
                if (!await inferenceSlot.WaitAsync(0, token)) return false;
                slotHeld = true;
                try
                {
                    if (preparation != null)
                    { session = await preparation.OpenOwnedAsync(token); lease = session.PreparationLease!; }
                    else lease = await session!.Project.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), token);
                }
                catch (ProjectException e) when (e.Code == "project.busy") { continue; }
                preparation?.Dispose(); preparation = null;
                bool recovered = await core.RecoverUnderLeaseWithResultAsync(session!.Project, lease, token);
                if (recoverOnly) continue;
                if (recovered) { lastProject = id; return true; }
                if (stopping.Task.IsCompleted) return false;
                JobRecord? job = null; RuntimeReadiness? voxReady = null;
                foreach (var queued in (await session.Project.Jobs.ListAsync(token)).Where(x => x.State == JobState.Queued && x.RequestedState == JobRequestedState.Run))
                {
                    if (queued.Input.SavedVoxCpmVoice != null)
                    {
                        if (voxRuntime != null) voxReady ??= await voxRuntime.GetReadinessAsync(token);
                        if (voxReady is not { Ready: true, DiagnosticCode: null, ProfileId: VoxCpmInferenceProfile.Id })
                        { await lease.ReportGenerationUnavailableAsync(queued.Id, token); continue; }
                    }
                    else if (!legacyReady) continue;
                    job = queued; break;
                }
                if (job == null) continue;
                var item = (await session.Project.Jobs.ItemsAsync(job.Id, token)).FirstOrDefault(x => x.Artifact == null);
                if (item == null) continue;
                var preset = item.CpuFallbackUsed ? job.Input.Preset with { ProfileId = "q8-f16-cpu" } : job.Input.Preset;
                var claim = await lease.BeginGenerationUnitAsync(job.Id, item.SegmentId, preset, token);
                if (claim == null) continue;
                unit = new(session, lease, claim); lastProject = id;
                await ProcessUnit(unit, job, token); return true;
            }
            finally
            {
                preparation?.Dispose();
                if (unit is { CleanupConfirmed: false })
                {
                    lock (sync) retained = unit;
                    if (!unit.Ended)
                    {
                        try { await InterruptRetaining(unit); }
                        catch (ProjectException) { /* Keep every identity even if the DB cannot commit interruption. */ }
                    }
                }
                if (unit == null || !ReferenceEquals(retained, unit))
                {
                    try
                    {
                        try
                        {
                            unit?.Prompt?.Dispose(); unit?.StagePins?.Dispose(); unit?.VoxVoice?.Dispose();
                            if (unit is { Ended: true, CleanupConfirmed: true, Composite: true } && lease != null)
                                await lease.CleanPendingArtifactsAsync(CancellationToken.None);
                        }
                        finally { if (lease != null) await lease.DisposeAsync(); }
                    }
                    finally
                    {
                        try { if (session != null) await session.DisposeAsync(); }
                        finally { if (slotHeld) inferenceSlot.Release(); }
                    }
                }
            }
        }
        return false;
    }

    private async Task ProcessUnit(Unit unit, JobRecord job, CancellationToken caller)
    {
        var db = unit.Session.Project;
        await unit.Lease.ReportGenerationProgressAsync(unit.Claim, "validating_inputs", CancellationToken.None);
        try
        {
            if (job.Input.SavedVoxCpmVoice == null)
            {
                unit.Prompt = await PinnedWave.OpenAsync(db.ResolveOwnedPath(job.Input.Voice.WavRelativePath));
                var inspection = unit.Prompt.Inspection;
                long expectedSamples = checked((job.Input.Voice.EndMilliseconds!.Value - job.Input.Voice.StartMilliseconds!.Value) * 48);
                if (inspection.Sha256 != job.Input.Voice.WavHash || inspection.SampleCount != expectedSamples || inspection.IsClipped)
                    throw new StudioException("job.input_changed");
            }
        }
        catch (Exception e) when (e is StudioException or WavInspectionException or IOException or UnauthorizedAccessException or ProjectException)
        {
            await unit.Lease.FailGenerationUnitAsync(unit.Claim, "job.input_changed", CancellationToken.None); unit.Ended = true; return;
        }
        if (stopping.Task.IsCompleted || caller.IsCancellationRequested)
        { await InterruptRetaining(unit); await core.RecoverUnderLeaseAsync(db, unit.Lease, CancellationToken.None); return; }

        using var cancel = new CancellationTokenSource();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = caller.Register(() => canceled.TrySetResult());
        string? phase = null;
        Task<Outcome> execution = Execute();
        Exception? monitoringError = null;
        try
        {
            while (!execution.IsCompleted)
            {
                if (!unit.Ended && (stopping.Task.IsCompleted || caller.IsCancellationRequested))
                { await InterruptRetaining(unit); cancel.Cancel(); }
                if (!unit.Ended)
                {
                    string? latest = Interlocked.Exchange(ref phase, null);
                    if (latest != null) await unit.Lease.ReportGenerationProgressAsync(unit.Claim, latest, CancellationToken.None);
                    var current = await db.Jobs.GetAsync(job.Id, CancellationToken.None);
                    if (current.RequestedState == JobRequestedState.Cancel) cancel.Cancel();
                }
                using var delayCancellation = new CancellationTokenSource();
                var delay = Task.Delay(TimeSpan.FromSeconds(1), time, delayCancellation.Token);
                await Task.WhenAny(unit.Ended ? [execution, delay] : new Task[] { execution, delay, stopping.Task, canceled.Task });
                await delayCancellation.CancelAsync();
            }
        }
        catch (Exception e)
        {
            monitoringError = e;
            try { if (!unit.Ended) await InterruptRetaining(unit); }
            catch (ProjectException) { /* The final ownership guard still retains an unconfirmed session. */ }
            finally { cancel.Cancel(); }
        }
        var outcome = await execution;
        if (!outcome.CleanupConfirmed)
        {
            if (!unit.Ended) await InterruptRetaining(unit);
            lock (sync) retained = unit; // Keep file/ancestry/lease/session identities, not just a boolean.
            throw new WorkerClientException("worker.cleanup", false);
        }
        if (!unit.Ended && (stopping.Task.IsCompleted || caller.IsCancellationRequested)) await InterruptRetaining(unit);
        if (unit.Ended)
        {
            await core.RecoverUnderLeaseAsync(db, unit.Lease, CancellationToken.None);
            if (monitoringError != null) throw new WorkerClientException("worker.internal", true);
            return;
        }
        if (monitoringError != null) throw new WorkerClientException("worker.internal", true);
        // A valid publication-won terminal survives a later pipe/shutdown error,
        // provided actual owned cleanup was proved above. Core still inspects the bytes.
        if (outcome.Result == null && outcome.Error != null)
        {
            string code = outcome.Error is WorkerClientException worker ? worker.Code : "worker.internal";
            bool retry = code == "worker.cuda.oom" && unit.Claim.EffectivePreset.ProfileId == "q8-f16-hybrid-16"
                && job.Input.Preset.AllowCpuFallback && !(await db.Jobs.ItemsAsync(job.Id, CancellationToken.None)).Any(x => x.CpuFallbackUsed);
            if (code == "worker.interrupted") await unit.Lease.InterruptGenerationUnitAsync(unit.Claim, code, CancellationToken.None);
            else await unit.Lease.FailGenerationUnitAsync(unit.Claim, code, retry ? GenerationFailureDisposition.RetryCpu : GenerationFailureDisposition.Terminal, CancellationToken.None);
            unit.Ended = true; return;
        }
        try
        {
            var result = outcome.Result!;
            if (result.FinalPath != db.ResolveOwnedPath(unit.Claim.FinalRelativePath)) throw new ProjectException("worker.output.invalid");
            using var output = await PinnedWave.OpenAsync(result.FinalPath);
            var accepted = new AcceptedJobArtifact(Guid.NewGuid().ToString("N"), unit.Claim.FinalRelativePath, result.Sha256,
                result.Length, output.Inspection.SampleCount, result.ProfileId, result.WorkerVersion, result.ModelVersion);
            await unit.Lease.AcceptGenerationUnitAsync(unit.Claim, accepted, CancellationToken.None); unit.Ended = true;
        }
        catch (Exception e) when (e is ProjectException or StudioException or WavInspectionException or IOException or UnauthorizedAccessException)
        { await unit.Lease.FailGenerationUnitAsync(unit.Claim, "worker.output.invalid", CancellationToken.None); unit.Ended = true; }

        async Task<Outcome> Execute()
        {
            if (job.Input.SavedVoxCpmVoice != null) return await ExecuteVox();
            SynthesisResult? result = null; Exception? error = null; bool confirmed = true, ttsReleased = false;
            unit.CleanupConfirmed = false;
            try
            {
                if (job.Input.SavedVoice != null && voiceConverter == null) throw new WorkerClientException("worker.runtime.not_ready", true);
                unit.Client = await workers.OpenAsync(db.Root, cancel.Token);
                var p = unit.Claim.EffectivePreset;
                string final = db.ResolveOwnedPath(unit.Claim.FinalRelativePath);
                string target = final;
                string? conversionRoot = null;
                if (job.Input.SavedVoice != null)
                {
                    unit.Composite = true;
                    string relativeRoot = $"segments/{unit.Claim.JobId}/{unit.Claim.AttemptId}-vc";
                    string stageRoot = db.ResolveOwnedPath(relativeRoot);
                    if (Directory.Exists(stageRoot) || File.Exists(stageRoot)) throw new WorkerClientException("worker.output.exists", true);
                    unit.StagePins = new CatalogPaths.Pins();
                    CatalogPaths.PinDirectories(Path.GetDirectoryName(stageRoot)!, unit.StagePins);
                    Directory.CreateDirectory(stageRoot); CatalogPaths.PinDirectories(stageRoot, unit.StagePins);
                    conversionRoot = db.ResolveOwnedPath(relativeRoot + "/convert");
                    Directory.CreateDirectory(conversionRoot); CatalogPaths.PinDirectories(conversionRoot, unit.StagePins);
                    // Recovery tracks exact leaves, never recursively deletes an attempt directory.
                    foreach (string leaf in new[] { "source.wav", "convert/request.json", "convert/START", "convert/STOP", "convert/candidate/final.wav", "convert/candidate/manifest.json", "convert/candidate/READY" })
                        await unit.Lease.RegisterPendingArtifactAsync(relativeRoot + "/" + leaf + ".part", relativeRoot + "/" + leaf, cancel.Token);
                    target = db.ResolveOwnedPath(relativeRoot + "/source.wav");
                }
                result = await unit.Client.SynthesizeAsync(unit.Claim.AttemptId,
                    new(unit.Claim.Input.SpokenText, unit.Prompt!.Path, job.Input.Voice.Transcript, target,
                        p.ProfileId, p.Steps, p.Timesteps, p.Cfg, p.Temperature, p.Seed),
                    progress => Interlocked.Exchange(ref phase, progress.Phase switch { "validatingInputs" => "validating_inputs", "generating" => "generating", "validatingOutput" => "validating_output", "publishing" => "publishing", _ => null }), cancel.Token);
                if (job.Input.SavedVoice is { } binding)
                {
                    // Native TTS ownership must be released before feature/G models are loaded.
                    await unit.Client.DisposeAsync(); unit.Client = null; confirmed = true; ttsReleased = true;
                    cancel.Token.ThrowIfCancellationRequested();
                    if (result.FinalPath != target || result.ProfileId != p.ProfileId || result.WorkerVersion != unit.Claim.Input.Versions.Worker
                        || result.ModelVersion != unit.Claim.Input.Versions.Model) throw new WorkerClientException("worker.output.invalid", true);
                    using var source = await PinnedWave.OpenAsync(target);
                    if (source.Inspection.Sha256.ToLowerInvariant() != result.Sha256 || source.Inspection.SampleCount is <= 0 or > 5760000
                        || source.Inspection.IsClipped || new FileInfo(target).Length != result.Length) throw new WorkerClientException("worker.output.invalid", true);
                    var candidate = await voiceConverter!.ConvertAsync(new(db.Root, conversionRoot!, binding, target, result.Sha256),
                        _ => Interlocked.Exchange(ref phase, "generating"), cancel.Token);
                    cancel.Token.ThrowIfCancellationRequested();
                    string expected = db.ResolveOwnedPath(Path.GetRelativePath(db.Root, Path.Combine(conversionRoot!, "candidate", "final.wav")));
                    if (!string.Equals(candidate.Path, expected, StringComparison.OrdinalIgnoreCase)) throw new WorkerClientException("worker.output.invalid", true);
                    using var audio = await PinnedWave.OpenAsync(candidate.Path);
                    if (audio.Inspection.Sha256.ToLowerInvariant() != candidate.Sha256 || audio.Inspection.SampleCount != candidate.SampleCount
                        || audio.Inspection.SampleCount != source.Inspection.SampleCount || audio.Inspection.IsClipped
                        || new FileInfo(candidate.Path).Length != candidate.Length) throw new WorkerClientException("worker.output.invalid", true);
                    await ConvertedWavePublication.PublishAsync(candidate.Path, final, candidate.Sha256, candidate.Length, cancel.Token);
                    result = new(final, candidate.Length, candidate.Sha256, 1, 48000, 16, candidate.SampleCount / 48000.0,
                        p.ProfileId, unit.Claim.Input.Versions.Worker, unit.Claim.Input.Versions.Model);
                }
            }
            catch (Exception e)
            {
                error = e;
                confirmed = e is WorkerClientException { CleanupConfirmed: true } || unit.Client != null || ttsReleased;
                if (e is WorkerClientException { CleanupConfirmed: false } or TrainingExecutionException { CleanupConfirmed: false }) confirmed = false;
                if (job.Input.SavedVoice != null) result = null; // Plain TTS can never win saved-voice publication.
            }
            if (unit.Client != null)
            {
                try { await unit.Client.DisposeAsync(); confirmed = true; }
                catch (WorkerClientException e) { confirmed = e.CleanupConfirmed; error ??= e; }
                catch { confirmed = false; error ??= new WorkerClientException("worker.cleanup", false); }
            }
            unit.CleanupConfirmed = confirmed;
            return new(result, error, confirmed);
        }

        async Task<Outcome> ExecuteVox()
        {
            bool confirmed = true; unit.CleanupConfirmed = false;
            try
            {
                if (voxSavedVoices == null || voxExecutor == null) throw new WorkerClientException("worker.runtime.not_ready", true);
                unit.VoxVoice = await voxSavedVoices.ResolveForExecutionAsync(db, job.Input.SavedVoxCpmVoice!, cancel.Token);
                string relative = $"segments/{unit.Claim.JobId}/{unit.Claim.AttemptId}-vox", stage = db.ResolveOwnedPath(relative);
                if (Directory.Exists(stage) || File.Exists(stage)) throw new WorkerClientException("worker.output.exists", true);
                unit.Composite = true; unit.StagePins = new CatalogPaths.Pins();
                CatalogPaths.PinDirectories(Path.GetDirectoryName(stage)!, unit.StagePins);
                Directory.CreateDirectory(stage); CatalogPaths.PinDirectories(stage, unit.StagePins);
                string[] leaves = ["request.json", "START", "candidate/final.wav", "candidate/manifest.json", "candidate/READY"];
                if (unit.VoxVoice.Binding.Options != null) leaves = [..leaves,"candidate/raw.wav"];
                foreach (string leaf in leaves)
                    await unit.Lease.RegisterPendingArtifactAsync(relative + "/" + leaf + ".part", relative + "/" + leaf, cancel.Token);
                cancel.Token.ThrowIfCancellationRequested(); confirmed = false;
                var candidate = await voxExecutor.RunAsync(new(db.Root, stage, unit.Claim.JobId, unit.Claim.SegmentId,
                    unit.Claim.AttemptId, unit.Claim.Input.SpokenText, unit.VoxVoice),
                    value => { Interlocked.Exchange(ref phase, value == "generating" ? "generating" : "validating_inputs"); return Task.CompletedTask; }, cancel.Token);
                confirmed = true; cancel.Token.ThrowIfCancellationRequested();
                if (!string.Equals(candidate.Path, Path.Combine(stage, "candidate", "final.wav"), StringComparison.OrdinalIgnoreCase)) throw new WorkerClientException("worker.output.invalid", true);
                using var wave = await PinnedWave.OpenAsync(candidate.Path);
                long maximumSamples = unit.VoxVoice.Binding.ProfileVersion == VoxCpmInferenceProfile.ControlsId ? 2457600 : 1966080;
                if (wave.Inspection.Sha256.ToLowerInvariant() != candidate.Sha256 || wave.Inspection.SampleCount != candidate.SampleCount
                    || wave.Inspection.SampleCount <= 0 || wave.Inspection.SampleCount > maximumSamples || wave.Inspection.IsClipped || wave.Inspection.Peak <= 0
                    || new FileInfo(candidate.Path).Length != candidate.Length) throw new WorkerClientException("worker.output.invalid", true);
                string final = db.ResolveOwnedPath(unit.Claim.FinalRelativePath);
                await ConvertedWavePublication.PublishAsync(candidate.Path, final, candidate.Sha256, candidate.Length, cancel.Token);
                return new(new(final, candidate.Length, candidate.Sha256, 1, 48000, 16, candidate.SampleCount / 48000.0,
                    unit.Claim.EffectivePreset.ProfileId, unit.Claim.Input.Versions.Worker, unit.Claim.Input.Versions.Model), null, true);
            }
            catch (Exception e)
            {
                if (e is WorkerClientException worker) confirmed = worker.CleanupConfirmed;
                Exception error = e is WorkerClientException ? e : new WorkerClientException(e is OperationCanceledException ? "worker.canceled" : "worker.output.invalid", confirmed);
                return new(null, error, confirmed);
            }
            finally { unit.CleanupConfirmed = confirmed; }
        }
    }

    private static async Task InterruptRetaining(Unit unit)
    {
        await unit.Lease.InterruptGenerationUnitRetainingStagingAsync(unit.Claim, "worker.interrupted", CancellationToken.None);
        unit.Ended = true;
    }
    private sealed record Outcome(SynthesisResult? Result, Exception? Error, bool CleanupConfirmed);
    private sealed class Unit(ProjectJobAccess.JobProjectSession session, ProjectArtifactLease lease, GenerationUnitClaim claim)
    {
        public ProjectJobAccess.JobProjectSession Session { get; } = session;
        public ProjectArtifactLease Lease { get; } = lease;
        public GenerationUnitClaim Claim { get; } = claim;
        public PinnedWave? Prompt { get; set; }
        public CatalogPaths.Pins? StagePins { get; set; }
        public ResolvedVoxCpmSavedVoice? VoxVoice { get; set; }
        public bool Composite { get; set; }
        public IWorkerClient? Client { get; set; }
        public bool Ended { get; set; }
        public bool CleanupConfirmed { get; set; } = true;
    }
}

/// <summary>Immutable actual file identity; pathname validation alone is not a lifetime guard.</summary>
internal sealed class PinnedWave(string path, CatalogPaths.Pins pins, WavInspection inspection) : IDisposable
{
    public string Path { get; } = path;
    public WavInspection Inspection { get; } = inspection;
    public static async Task<PinnedWave> OpenAsync(string path)
    {
        var pins = new CatalogPaths.Pins();
        try
        {
            CatalogPaths.PinDirectories(System.IO.Path.GetDirectoryName(path)!, pins);
            var file = CatalogPaths.OpenFile(path, 0x80000000, 1, 3); pins.Add(file);
            var inspection = await WavInspector.InspectAsync(path, CancellationToken.None);
            return new(path, pins, inspection);
        }
        catch { pins.Dispose(); throw; }
    }
    public void Dispose() => pins.Dispose();
}

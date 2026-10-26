using System.Text.Json;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using Microsoft.Data.Sqlite;

namespace LocalAudiobookStudio.Core.Storage;

public sealed partial class ProjectDatabase
{
    internal Task<JobRecord> ReportGenerationUnavailableAsync(ProjectArtifactLease lease, string jobId, CancellationToken token)
    {
        lease.ValidateOwner(this);
        return JobTransactionAsync(true, async (db, transaction) =>
        {
            lease.ValidateOwner(this); var job = await Jobs.GetAsync(db, transaction, jobId, token);
            JobValidation.Require(job.Input.SavedVoxCpmVoice != null,"job.transition.invalid");
            if (job.State != JobState.Queued || job.RequestedState != JobRequestedState.Run || job.ClaimToken != null
                || job.DiagnosticCode == "worker.runtime.not_ready") return job;
            var next = Advance(job with { DiagnosticCode = "worker.runtime.not_ready" }, DateTimeOffset.UtcNow);
            await WriteJobAsync(transaction, next, token); return next;
        }, token);
    }
    internal Task<GenerationUnitClaim?> BeginGenerationUnitAsync(ProjectArtifactLease lease, string jobId, string segmentId, GenerationPreset effectivePreset, CancellationToken token)
    {
        lease.ValidateOwner(this);
        JobValidation.Require(JobValidation.Id(jobId) && JobValidation.Id(segmentId), "job.input.invalid");
        ProjectState? committedState = null;
        return JobTransactionAsync<GenerationUnitClaim?>(true, async (db, transaction) =>
        {
            lease.ValidateOwner(this);
            var job = await Jobs.GetAsync(db, transaction, jobId, token);
            var items = await Jobs.ItemsAsync(db, transaction, job, token);
            if (job.State != JobState.Queued || job.RequestedState != JobRequestedState.Run || job.ClaimToken != null) return null;
            var item = items.SingleOrDefault(x => x.SegmentId == segmentId);
            JobValidation.Require(item != null, "job.input.invalid");
            if (item.Artifact != null || items.Any(x => x.State is JobState.Preparing or JobState.Running)) return null;
            if (items.FirstOrDefault(x => x.Artifact == null)?.SegmentId != segmentId) return null;
            var preset = EffectivePreset(job, item);
            JobValidation.Require(effectivePreset == preset, "job.input.invalid");
            var state = await ReadJobProjectAsync(transaction, token);
            // Revision is a CAS concern, not immutable content. Accepted units themselves advance it.
            JobValidation.Authoritative(job.Input with { ProjectRevision = state.Revision }, state);
            var input = job.Input.Items.Single(x => x.SegmentId == segmentId);
            var segment = state.Segments.Single(x => x.Id == segmentId);
            string attemptId = Guid.NewGuid().ToString("N"), claimToken = Guid.NewGuid().ToString("N");
            string final = $"segments/{job.Id}/{attemptId}.wav", part = final + ".part";
            lease.PinOutputParent(job.Id); ValidatePendingArtifact(part, final);
            JobValidation.Require(IsProvenAbsent(final) && IsProvenAbsent(part), "worker.output.exists");
            string fingerprint = GenerationFingerprint.ForItem(job.Input, input, preset);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var attempt = new AttemptRecord(attemptId, segmentId, state.Attempts.Where(x => x.SegmentId == segmentId).Select(x => x.Order).DefaultIfEmpty(0).Max() + 1,
                preset.Seed, input.Versions, segment.Parameters, fingerprint, "running", part, null, now, null, null);
            segment = segment with { Status = "running", UpdatedUtc = now, DiagnosticCode = null, GenerationFingerprint = fingerprint, Versions = input.Versions, Seed = preset.Seed };
            await InsertAsync("attempts", attempt.Id, attempt, transaction, token, attempt.Order, attempt.SegmentId);
            await UpdateSegmentAsync(transaction, segment, token);
            await ProjectJobStore.ExecuteAsync(db, transaction, "INSERT INTO staging(relative_path,final_relative_path) VALUES($part,$final)", token, ("$part", part), ("$final", final));
            await WriteItemAsync(transaction, item with { State = JobState.Running, AttemptId = attemptId, EffectiveFingerprint = fingerprint }, token);
            await WriteJobAsync(transaction, Advance(job with { State = JobState.Preparing, Phase = "preparing", ClaimToken = claimToken, EffectiveProfileId = preset.ProfileId, DiagnosticCode = null }, now), token);
            committedState = await AdvanceProjectAsync(transaction, state with { Segments = ReplaceSegment(state, segment), Attempts = state.Attempts.Append(attempt).ToArray() }, token);
            return new(job.Id, segmentId, attemptId, claimToken, final, input, preset);
        }, token, claim => { if (committedState != null) State = committedState; if (claim != null) lease.RememberClaim(claim); });
    }

    internal async Task<JobRecord> AcceptGenerationUnitAsync(ProjectArtifactLease lease, GenerationUnitClaim claim, AcceptedJobArtifact result, CancellationToken token)
    {
        lease.ValidateClaim(this, claim);
        // Fence before touching the reported path, then recheck after inspection in the write transaction.
        await JobTransactionAsync(false, async (db, transaction) =>
        { lease.ValidateClaim(this, claim); return await ClaimedAsync(transaction, claim, token); }, token);
        JobValidation.Require(result != null && JobValidation.Id(result.Id) && result.RelativePath == claim.FinalRelativePath && JobValidation.Hash(result.Sha256)
            && result.Length is >= 46 and <= 1073741824 && result.SampleCount is > 0 and <= 86400000
            && result.ProfileId == claim.EffectivePreset.ProfileId && result.WorkerVersion == claim.Input.Versions.Worker && result.ModelVersion == claim.Input.Versions.Model, "worker.output.invalid");
        using var pins = new GenerationPathPins(); string path = ResolveOwnedPath(claim.FinalRelativePath);
        pins.PinDirectories(Path.GetDirectoryName(path)!);
        var file = pins.PinFile(path, allowWrites: false);
        try
        {
            long length = RandomAccess.GetLength(file);
            JobValidation.Require(length == result.Length, "worker.output.invalid");
            // The native handle denies write/delete and all ancestors are pinned. The inspector opens this same immutable identity.
            var inspection = await WavInspector.InspectAsync(path, token);
            JobValidation.Require(inspection.Sha256.ToLowerInvariant() == result.Sha256 && inspection.SampleCount == result.SampleCount
                && inspection.Channels == 1 && inspection.SampleRate == 48000 && inspection.BitsPerSample == 16 && inspection.BlockAlignment == 2, "worker.output.invalid");
            GenerationPathPins.Check(file);
            ProjectState? committedState = null;
            return await JobTransactionAsync(true, async (db, transaction) =>
            {
                lease.ValidateClaim(this, claim);
                var (job, item, state, attempt, segment) = await ClaimedAsync(transaction, claim, token);
                // A native publication moves the exact registered .part. A remaining counterpart is not a completed publication.
                JobValidation.Require(IsProvenAbsent(claim.FinalRelativePath + ".part"), "worker.output.invalid");
                GenerationPathPins.Check(file);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                segment = segment with { Status = "done", OutputRelativePath = result.RelativePath, OutputHash = result.Sha256, UpdatedUtc = now, DiagnosticCode = null };
                attempt = attempt with { Status = "done", OutputHash = result.Sha256, CompletedUtc = now, DiagnosticCode = null };
                await UpdateSegmentAsync(transaction, segment, token); await UpdateAttemptAsync(transaction, attempt, token);
                await WriteItemAsync(transaction, item with { State = JobState.Completed, Artifact = result }, token);
                await ProjectJobStore.ExecuteAsync(db, transaction, "DELETE FROM staging WHERE relative_path=$part AND final_relative_path=$final", token, ("$part", claim.FinalRelativePath + ".part"), ("$final", claim.FinalRelativePath));
                int completed = job.CompletedUnits + 1;
                var next = Finish(job with { CompletedUnits = completed }, completed == job.TotalUnits ? JobState.Completed : JobState.Queued, now, null);
                await WriteJobAsync(transaction, next, token);
                committedState = await AdvanceProjectAsync(transaction, state with { Segments = ReplaceSegment(state, segment), Attempts = ReplaceAttempt(state, attempt) }, token);
                return next;
            }, token, _ => { State = committedState!; lease.ReleaseClaim(); });
        }
        catch (WavInspectionException) { throw new ProjectException("worker.output.invalid"); }
        catch (IOException) { throw new ProjectException("worker.output.invalid"); }
    }

    internal Task<JobRecord> ReportGenerationProgressAsync(ProjectArtifactLease lease, GenerationUnitClaim claim, string phase, CancellationToken token)
    {
        lease.ValidateClaim(this, claim);
        JobValidation.Require(phase is "preparing" or "validating_inputs" or "generating" or "validating_output" or "publishing", "job.input.invalid");
        return JobTransactionAsync(true, async (db, transaction) =>
        {
            lease.ValidateClaim(this, claim);
            var current = await ClaimedAsync(transaction, claim, token);
            var state = current.Job.State == JobState.Preparing && phase is "generating" or "validating_output" or "publishing" ? JobState.Running : current.Job.State;
            var next = Advance(current.Job with { State = state, Phase = phase }, DateTimeOffset.UtcNow);
            await WriteJobAsync(transaction, next, token); return next;
        }, token);
    }

    internal async Task<JobRecord> EndGenerationUnitAsync(ProjectArtifactLease lease, GenerationUnitClaim claim, string code, bool interrupted, GenerationFailureDisposition disposition, CancellationToken token, bool retainStaging = false)
    {
        lease.ValidateClaim(this, claim);
        JobValidation.Require(code != null && Enum.IsDefined(disposition), "job.input.invalid");
        JobValidation.Require(!retainStaging || interrupted && disposition == GenerationFailureDisposition.Terminal, "job.transition.invalid");
        ProjectState? committedState = null;
        var result = await JobTransactionAsync(true, async (db, transaction) =>
        {
            lease.ValidateClaim(this, claim);
            var (job, item, state, attempt, segment) = await ClaimedAsync(transaction, claim, token);
            bool retry = disposition == GenerationFailureDisposition.RetryCpu;
            var items = await Jobs.ItemsAsync(db, transaction, job, token);
            if (retry) JobValidation.Require(!interrupted && code == "worker.cuda.oom" && job.Input.Preset.AllowCpuFallback
                && claim.EffectivePreset.ProfileId == "q8-f16-hybrid-16" && !items.Any(x => x.CpuFallbackUsed), "job.transition.invalid");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            attempt = attempt with { Status = interrupted ? "interrupted" : "failed", CompletedUtc = now, DiagnosticCode = code };
            segment = segment with { Status = "queued", UpdatedUtc = now, DiagnosticCode = code };
            var preset = retry ? job.Input.Preset with { ProfileId = "q8-f16-cpu" } : claim.EffectivePreset;
            item = item with { State = retry ? JobState.Queued : interrupted ? JobState.Interrupted : JobState.Failed, CpuFallbackUsed = item.CpuFallbackUsed || retry,
                EffectiveFingerprint = GenerationFingerprint.ForItem(job.Input, claim.Input, preset) };
            var next = Finish(job, retry ? JobState.Queued : interrupted ? JobState.Interrupted : JobState.Failed, now, code);
            if (retry && next.State == JobState.Queued) next = next with { Phase = "retrying_cpu" };
            // Validate fixed diagnostics before any durable mutation.
            JobValidation.Record(next, this);
            await UpdateAttemptAsync(transaction, attempt, token); await UpdateSegmentAsync(transaction, segment, token);
            await WriteItemAsync(transaction, item, token);
            if (retry)
                foreach (var pending in items.Where(x => x.Artifact == null && x.SegmentId != item.SegmentId))
                {
                    var input = job.Input.Items.Single(x => x.SegmentId == pending.SegmentId);
                    await WriteItemAsync(transaction, pending with { CpuFallbackUsed = true, EffectiveFingerprint = GenerationFingerprint.ForItem(job.Input, input, preset) }, token);
                }
            await WriteJobAsync(transaction, next, token);
            committedState = await AdvanceProjectAsync(transaction, state with { Segments = ReplaceSegment(state, segment), Attempts = ReplaceAttempt(state, attempt) }, token);
            return next;
        }, token, _ => { State = committedState!; lease.ReleaseClaim(); });
        // Leave registration durable until exact owned cleanup succeeds; interruption/crash can safely resume recovery.
        if (!retainStaging) await CleanStagingAsync(lease, token);
        return result;
    }

    internal Task<bool> RecoverGenerationUnitsAsync(ProjectArtifactLease lease, CancellationToken token)
    {
        lease.ValidateOwner(this); ProjectState? committedState = null;
        return JobTransactionAsync(true, async (db, transaction) =>
        {
            lease.ValidateOwner(this);
            if (!await ProjectJobStore.HasSchemaAsync(db, transaction, token)) return false;
            foreach (var job in await Jobs.JobsAsync(db, transaction, false, token))
            {
                if (job.ClaimToken == null) continue;
                var items = await Jobs.ItemsAsync(db, transaction, job, token);
                var running = items.Where(x => x.State is JobState.Running or JobState.Preparing).ToArray();
                JobValidation.Require(running.Length == 1 && running[0].AttemptId != null);
                var item = running[0]; var input = job.Input.Items.Single(x => x.SegmentId == item.SegmentId);
                var claim = new GenerationUnitClaim(job.Id, item.SegmentId, item.AttemptId!, job.ClaimToken, $"segments/{job.Id}/{item.AttemptId}.wav", input, EffectivePreset(job, item));
                var (_, _, state, attempt, segment) = await ClaimedAsync(transaction, claim, token);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                attempt = attempt with { Status = "interrupted", CompletedUtc = now, DiagnosticCode = "project.interrupted" };
                segment = segment with { Status = "queued", UpdatedUtc = now, DiagnosticCode = "project.interrupted" };
                await UpdateAttemptAsync(transaction, attempt, token); await UpdateSegmentAsync(transaction, segment, token);
                await WriteItemAsync(transaction, item with { State = JobState.Interrupted }, token);
                await WriteJobAsync(transaction, Finish(job, JobState.Interrupted, now, "project.interrupted"), token);
                committedState = await AdvanceProjectAsync(transaction, state with { Segments = ReplaceSegment(state, segment), Attempts = ReplaceAttempt(state, attempt) }, token);
            }
            return committedState != null;
        }, token, _ => { if (committedState != null) State = committedState; });
    }

    private async Task<(JobRecord Job, JobItemRecord Item, ProjectState State, AttemptRecord Attempt, SegmentRecord Segment)> ClaimedAsync(SqliteTransaction transaction, GenerationUnitClaim claim, CancellationToken token)
    {
        JobValidation.Require(claim != null && JobValidation.Id(claim.JobId) && JobValidation.Id(claim.SegmentId) && JobValidation.Id(claim.AttemptId) && JobValidation.Id(claim.Token), "job.conflict");
        var job = await Jobs.GetAsync(connection, transaction, claim.JobId, token);
        JobValidation.Require(job.ClaimToken == claim.Token && job.State is JobState.Running or JobState.Preparing or JobState.PauseRequested or JobState.CancelRequested, "job.conflict");
        var items = await Jobs.ItemsAsync(connection, transaction, job, token);
        var item = items.SingleOrDefault(x => x.SegmentId == claim.SegmentId);
        JobValidation.Require(item != null && item.Artifact == null && item.State is JobState.Running or JobState.Preparing && item.AttemptId == claim.AttemptId
            && items.Count(x => x.State is JobState.Running or JobState.Preparing) == 1, "job.conflict");
        JobValidation.Require(claim.Input == job.Input.Items.Single(x => x.SegmentId == claim.SegmentId) && claim.EffectivePreset == EffectivePreset(job, item)
            && claim.FinalRelativePath == $"segments/{job.Id}/{claim.AttemptId}.wav", "job.conflict");
        var state = await ReadJobProjectAsync(transaction, token);
        var attempt = state.Attempts.SingleOrDefault(x => x.Id == claim.AttemptId); var segment = state.Segments.SingleOrDefault(x => x.Id == claim.SegmentId);
        JobValidation.Require(attempt != null && attempt.Status == "running" && attempt.SegmentId == claim.SegmentId && attempt.StagingRelativePath == claim.FinalRelativePath + ".part"
            && attempt.GenerationFingerprint == item.EffectiveFingerprint && segment != null && segment.Status == "running" && segment.GenerationFingerprint == item.EffectiveFingerprint, "job.conflict");
        using var registration = ProjectJobStore.Command(connection, transaction, "SELECT COUNT(*) FROM staging WHERE relative_path=$part AND final_relative_path=$final", ("$part", attempt.StagingRelativePath), ("$final", claim.FinalRelativePath));
        JobValidation.Require((long)(await registration.ExecuteScalarAsync(token))! == 1, "job.conflict");
        return (job, item, state, attempt, segment);
    }

    private static GenerationPreset EffectivePreset(JobRecord job, JobItemRecord item) => item.CpuFallbackUsed ? job.Input.Preset with { ProfileId = "q8-f16-cpu" } : job.Input.Preset;
    private static JobRecord Advance(JobRecord job, DateTimeOffset now) => job with { Revision = checked(job.Revision + 1), UpdatedUtc = now };
    private static JobRecord Finish(JobRecord job, JobState normal, DateTimeOffset now, string? code)
    {
        JobState state = job.RequestedState switch { JobRequestedState.Cancel => JobState.Canceled, JobRequestedState.Pause => JobState.Paused, _ => normal };
        string phase = state switch { JobState.Canceled => "canceled", JobState.Paused => "paused", JobState.Completed => "completed", JobState.Failed => "failed", JobState.Interrupted => "interrupted", _ => "queued" };
        return Advance(job with { State = state, Phase = phase, ClaimToken = null, DiagnosticCode = code, CompletedUtc = JobValidation.Terminal(state) ? now : null }, now);
    }
    private async Task WriteJobAsync(SqliteTransaction transaction, JobRecord job, CancellationToken token)
    {
        JobValidation.Record(job, this);
        await ProjectJobStore.ExecuteAsync(connection, transaction, "UPDATE jobs SET active=$active,payload=$payload WHERE id=$id", token,
            ("$active", JobValidation.Terminal(job.State) ? 0 : 1), ("$payload", JsonSerializer.Serialize(job)), ("$id", job.Id));
        await ProjectJobStore.AddEventAsync(connection, transaction, job, token);
    }
    private Task WriteItemAsync(SqliteTransaction transaction, JobItemRecord item, CancellationToken token) => ProjectJobStore.ExecuteAsync(connection, transaction,
        "UPDATE job_items SET payload=$payload WHERE job_id=$job AND segment_id=$segment", token, ("$payload", JsonSerializer.Serialize(item)), ("$job", item.JobId), ("$segment", item.SegmentId));
    private Task UpdateSegmentAsync(SqliteTransaction transaction, SegmentRecord segment, CancellationToken token) => ProjectJobStore.ExecuteAsync(connection, transaction,
        "UPDATE segments SET payload=$payload WHERE id=$id", token, ("$payload", JsonSerializer.Serialize(segment)), ("$id", segment.Id));
    private Task UpdateAttemptAsync(SqliteTransaction transaction, AttemptRecord attempt, CancellationToken token) => ProjectJobStore.ExecuteAsync(connection, transaction,
        "UPDATE attempts SET payload=$payload WHERE id=$id", token, ("$payload", JsonSerializer.Serialize(attempt)), ("$id", attempt.Id));
    private async Task<ProjectState> AdvanceProjectAsync(SqliteTransaction transaction, ProjectState state, CancellationToken token)
    {
        Validate(state);
        using var update = ProjectJobStore.Command(connection, transaction, "UPDATE project SET revision=revision+1 WHERE revision=$revision", ("$revision", state.Revision));
        if (await update.ExecuteNonQueryAsync(token) != 1) throw new ProjectException("project.conflict");
        return state with { Revision = checked(state.Revision + 1), Attempts = state.Attempts.OrderBy(x => x.SegmentId).ThenBy(x => x.Order).ToArray() };
    }
    private static IReadOnlyList<SegmentRecord> ReplaceSegment(ProjectState state, SegmentRecord segment) => state.Segments.Select(x => x.Id == segment.Id ? segment : x).ToArray();
    private static IReadOnlyList<AttemptRecord> ReplaceAttempt(ProjectState state, AttemptRecord attempt) => state.Attempts.Select(x => x.Id == attempt.Id ? attempt : x).ToArray();
}

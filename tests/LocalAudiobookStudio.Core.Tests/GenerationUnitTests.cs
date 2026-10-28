using System.Buffers.Binary;
using System.Security.Cryptography;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Core.Tests;

public class GenerationUnitTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Begin_atomically_registers_final_part_attempt_and_claim()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await Enqueue(db, lease); long revision = db.State.Revision;
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        Assert.NotNull(claim); Assert.Matches("^[a-f0-9]{32}$", claim.Token);
        Assert.Equal($"segments/{job.Id}/{claim.AttemptId}.wav", claim.FinalRelativePath);
        string parent = Path.GetDirectoryName(db.ResolveOwnedPath(claim.FinalRelativePath))!;
        Assert.True(Directory.Exists(parent)); Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
        var attempt = Assert.Single(db.State.Attempts); Assert.Equal(claim.AttemptId, attempt.Id);
        Assert.Equal("running", attempt.Status); Assert.Equal(claim.FinalRelativePath + ".part", attempt.StagingRelativePath);
        Assert.Equal("running", db.State.Segments[0].Status); Assert.Equal(revision + 1, db.State.Revision);
        Assert.Equal(1, Scalar(box, "SELECT COUNT(*) FROM staging WHERE final_relative_path IS NOT NULL"));
        job = await db.Jobs.GetAsync(job.Id, Token); Assert.Equal(claim.Token, job.ClaimToken); Assert.Equal(2, job.Revision);
        Assert.Equal(JobState.Preparing, job.State); Assert.Equal("preparing", job.Phase);
        Assert.Null(await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[1].SegmentId, job.Input.Preset, Token));
    }

    [Fact]
    public async Task Accept_atomically_advances_both_revisions_and_removes_staging()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        var artifact = Publish(db, claim); long revision = db.State.Revision;
        job = await lease.AcceptGenerationUnitAsync(claim, artifact, Token);
        Assert.Equal(JobState.Queued, job.State); Assert.Equal(1, job.CompletedUnits); Assert.Equal(3, job.Revision);
        Assert.Equal(revision + 1, db.State.Revision); Assert.Null(job.ClaimToken);
        Assert.Equal("done", db.State.Segments[0].Status); Assert.Equal(artifact.Sha256, db.State.Segments[0].OutputHash);
        Assert.Equal("done", db.State.Attempts[0].Status); Assert.Equal(0, Scalar(box, "SELECT COUNT(*) FROM staging"));
        var item = (await db.Jobs.ItemsAsync(job.Id, Token))[0]; Assert.Equal(artifact, item.Artifact);
        Assert.Equal(4801, item.Artifact!.SampleCount); Assert.Equal(job.Revision, (await db.Jobs.ReadEventsAsync(0, Token))[^1].JobRevision);
        Assert.Null(await lease.BeginGenerationUnitAsync(job.Id, claim.SegmentId, job.Input.Preset, Token));
        var next = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[1].SegmentId, job.Input.Preset, Token))!;
        Assert.NotEqual(claim.FinalRelativePath, next.FinalRelativePath);
        job = await lease.AcceptGenerationUnitAsync(next, Publish(db, next), Token); Assert.Equal(JobState.Completed, job.State);
        Assert.Null(await lease.BeginGenerationUnitAsync(job.Id, next.SegmentId, job.Input.Preset, Token));
    }

    [Fact]
    public async Task Old_claim_cannot_accept()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        var old = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        var result = Publish(db, old); job = await lease.InterruptGenerationUnitAsync(old, "worker.interrupted", Token);
        job = await db.Jobs.RequestStateAsync(job.Id, job.Revision, "resume", JobControlCommand.Resume, Token);
        var current = (await lease.BeginGenerationUnitAsync(job.Id, old.SegmentId, job.Input.Preset, Token))!;
        Assert.NotEqual(old.Token, current.Token); Assert.NotEqual(old.FinalRelativePath, current.FinalRelativePath);
        await Assert.ThrowsAsync<ProjectException>(() => lease.AcceptGenerationUnitAsync(old, result, Token));
        await Assert.ThrowsAsync<ProjectException>(() => lease.FailGenerationUnitAsync(old, "worker.crash", Token));
        await Assert.ThrowsAsync<ProjectException>(() => lease.InterruptGenerationUnitAsync(old, "worker.interrupted", Token));
        await Assert.ThrowsAsync<ProjectException>(() => lease.ReportGenerationProgressAsync(old, "publishing", Token));
        Assert.Equal(current.Token, (await db.Jobs.GetAsync(job.Id, Token)).ClaimToken);
    }

    [Theory]
    [InlineData(null, JobState.Interrupted)]
    [InlineData(JobControlCommand.Pause, JobState.Paused)]
    [InlineData(JobControlCommand.Cancel, JobState.Canceled)]
    public async Task Unconfirmed_tree_interruption_retains_registered_files_until_owned_recovery(JobControlCommand? intent, JobState expected)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        string part = db.ResolveOwnedPath(claim.FinalRelativePath + ".part");
        byte[] synthetic = [1, 2, 3, 4]; File.WriteAllBytes(part, synthetic);
        if (intent is { } command) job = await db.Jobs.RequestStateAsync(job.Id, 2, "intent", command, Token);
        using (var potentiallyLiveWriter = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            long before = db.State.Revision;
            job = await lease.InterruptGenerationUnitRetainingStagingAsync(claim, "worker.cleanup", Token);
            Assert.Equal(expected, job.State); Assert.Null(job.ClaimToken); Assert.Equal(0, job.CompletedUnits);
            Assert.Equal(before + 1, db.State.Revision); Assert.Equal("interrupted", db.State.Attempts.Single().Status);
            Assert.Equal(1, Scalar(box, "SELECT COUNT(*) FROM staging"));
            Assert.True(File.Exists(part)); Assert.False(File.Exists(db.ResolveOwnedPath(claim.FinalRelativePath)));
            potentiallyLiveWriter.Position = 0; byte[] observed = new byte[4]; await potentiallyLiveWriter.ReadExactlyAsync(observed, Token);
            Assert.Equal(synthetic, observed);
            await Assert.ThrowsAsync<ProjectException>(() => lease.ReportGenerationProgressAsync(claim, "generating", Token));
        }
        // Only after the controlled writer/owned tree is actually gone may normal
        // recovery remove the exact pending artifact. No retry or forced release.
        await new ProjectService().RecoverUnderLeaseAsync(db, lease, Token);
        Assert.False(File.Exists(part)); Assert.Equal(0, Scalar(box, "SELECT COUNT(*) FROM staging"));
        Assert.Equal(expected, (await db.Jobs.GetAsync(job.Id, Token)).State);
    }

    [Theory]
    [InlineData(JobControlCommand.Cancel, JobState.Canceled)]
    [InlineData(JobControlCommand.Pause, JobState.Paused)]
    public async Task Racing_cancel_preserved_after_publication_success(JobControlCommand command, JobState expected)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease, preview: true);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        var artifact = Publish(db, claim);
        await using var controller = await new ProjectService().OpenCurrentAsync(box.ProjectRoot, Token);
        await controller.Jobs.RequestStateAsync(job.Id, 2, "control", command, Token);
        job = await lease.AcceptGenerationUnitAsync(claim, artifact, Token);
        Assert.Equal(expected, job.State); Assert.Equal(1, job.CompletedUnits); Assert.Equal(4, job.Revision);
        Assert.True(File.Exists(db.ResolveOwnedPath(artifact.RelativePath)));
        Assert.Null(await lease.BeginGenerationUnitAsync(job.Id, claim.SegmentId, job.Input.Preset, Token));
    }

    [Theory]
    [InlineData("accept")] [InlineData("progress")] [InlineData("fail")] [InlineData("interrupt")]
    public async Task Released_lease_cannot_commit_unit_operation_waiting_for_database_gate(string operation)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await Enqueue(db, lease);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        var artifact = Publish(db, claim); long revision = db.State.Revision;
        var gate = (SemaphoreSlim)typeof(ProjectDatabase).GetField("gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(db)!;
        await gate.WaitAsync(Token);
        Task<JobRecord> pending;
        try
        {
            pending = operation switch
            {
                "accept" => lease.AcceptGenerationUnitAsync(claim, artifact, Token),
                "progress" => lease.ReportGenerationProgressAsync(claim, "generating", Token),
                "fail" => lease.FailGenerationUnitAsync(claim, "worker.crash", Token),
                _ => lease.InterruptGenerationUnitAsync(claim, "worker.interrupted", Token)
            };
            Assert.False(pending.IsCompleted);
            await lease.DisposeAsync();
        }
        finally { gate.Release(); }
        Assert.Equal("project.lease.invalid", (await Assert.ThrowsAsync<ProjectException>(() => pending)).Code);
        Assert.Equal(revision, db.State.Revision); Assert.Equal(claim.Token, (await db.Jobs.GetAsync(job.Id, Token)).ClaimToken);
        Assert.Equal(1, Scalar(box, "SELECT COUNT(*) FROM staging"));
    }

    [Theory]
    [InlineData("accept")] [InlineData("progress")] [InlineData("fail")] [InlineData("interrupt")]
    public async Task Reacquired_lease_cannot_use_claim_from_released_ownership(string operation)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var first = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await Enqueue(db, first);
        var claim = (await first.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        var artifact = Publish(db, claim); long revision = db.State.Revision;
        await first.DisposeAsync();
        await using var replacement = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        Task<JobRecord> Invoke() => operation switch
        {
            "accept" => replacement.AcceptGenerationUnitAsync(claim, artifact, Token),
            "progress" => replacement.ReportGenerationProgressAsync(claim, "generating", Token),
            "fail" => replacement.FailGenerationUnitAsync(claim, "worker.crash", Token),
            _ => replacement.InterruptGenerationUnitAsync(claim, "worker.interrupted", Token)
        };
        Assert.Equal("job.conflict", (await Assert.ThrowsAsync<ProjectException>(Invoke)).Code);
        Assert.Equal(revision, db.State.Revision);
        Assert.Equal(claim.Token, (await db.Jobs.GetAsync(job.Id, Token)).ClaimToken);
        await new ProjectService().RecoverUnderLeaseAsync(db, replacement, Token);
        var recovered = await db.Jobs.GetAsync(job.Id, Token);
        Assert.Equal(JobState.Interrupted, recovered.State); Assert.Null(recovered.ClaimToken);
        recovered = await db.Jobs.RequestStateAsync(job.Id, recovered.Revision, "resume", JobControlCommand.Resume, Token);
        var next = (await replacement.BeginGenerationUnitAsync(job.Id, claim.SegmentId, job.Input.Preset, Token))!;
        Assert.NotEqual(claim.Token, next.Token);
        Assert.Equal("generating", (await replacement.ReportGenerationProgressAsync(next, "generating", Token)).Phase);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Resume_after_last_accepted_unit_completes_without_new_attempt_and_replays_after_restart(bool interrupted)
    {
        using var box = new ProjectSandbox(); string id; long projectRevision, jobRevision;
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        {
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease, preview: true); id = job.Id;
            var claim = (await lease.BeginGenerationUnitAsync(id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
            await db.Jobs.RequestStateAsync(id, 2, "pause", JobControlCommand.Pause, Token);
            job = await lease.AcceptGenerationUnitAsync(claim, Publish(db, claim), Token);
            Assert.Equal(JobState.Paused, job.State); Assert.Equal(1, job.CompletedUnits); Assert.Null(job.CompletedUtc);
            projectRevision = db.State.Revision; jobRevision = job.Revision;
        }
        if (interrupted) VoiceRecordingServiceTests.Sql(box.ProjectRoot,
            "UPDATE jobs SET payload=json_set(payload,'$.State',7,'$.RequestedState',0,'$.Phase','interrupted')");
        JobRecord completed;
        await using (var db = await new ProjectService().OpenAsync(box.ProjectRoot, Token))
        {
            long sequence = await db.Jobs.GetEventSequenceAsync(Token);
            completed = await db.Jobs.RequestStateAsync(id, jobRevision, "resume", JobControlCommand.Resume, Token);
            Assert.Equal(JobState.Completed, completed.State); Assert.Equal("completed", completed.Phase);
            Assert.Equal(JobRequestedState.Run, completed.RequestedState); Assert.NotNull(completed.CompletedUtc); Assert.Null(completed.ClaimToken);
            Assert.Equal(jobRevision + 1, completed.Revision); Assert.Equal(sequence + 1, await db.Jobs.GetEventSequenceAsync(Token));
            Assert.Equal(projectRevision, db.State.Revision); Assert.Equal(projectRevision, Scalar(box, "SELECT revision FROM project"));
            Assert.Single(db.State.Attempts); Assert.Equal(0, Scalar(box, "SELECT COUNT(*) FROM staging"));
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            Assert.Null(await lease.BeginGenerationUnitAsync(id, completed.Input.Items[0].SegmentId, completed.Input.Preset, Token));
        }
        await using var reopened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        long head = await reopened.Jobs.GetEventSequenceAsync(Token);
        var replay = await reopened.Jobs.RequestStateAsync(id, jobRevision, "resume", JobControlCommand.Resume, Token);
        Assert.Equal(JobState.Completed, replay.State); Assert.Equal(completed.Revision, replay.Revision); Assert.Equal(completed.CompletedUtc, replay.CompletedUtc);
        Assert.Equal(head, await reopened.Jobs.GetEventSequenceAsync(Token)); Assert.Single(reopened.State.Attempts);
        Assert.Equal(projectRevision, reopened.State.Revision); Assert.Equal(3, Scalar(box, "SELECT COUNT(*) FROM job_receipts"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Crash_before_after_publication_and_acceptance_preserves_only_accepted(int boundary)
    {
        using var box = new ProjectSandbox(); string relative; string id;
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        {
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease, preview: true); id = job.Id;
            var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!; relative = claim.FinalRelativePath;
            if (boundary == 0) File.WriteAllBytes(db.ResolveOwnedPath(relative + ".part"), Wav());
            else { var artifact = Publish(db, claim); if (boundary == 2) await lease.AcceptGenerationUnitAsync(claim, artifact, Token); }
        }
        await using var opened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        Assert.Equal(boundary == 2, File.Exists(opened.ResolveOwnedPath(relative))); Assert.False(File.Exists(opened.ResolveOwnedPath(relative + ".part")));
        var recovered = await opened.Jobs.GetAsync(id, Token); Assert.Equal(boundary == 2 ? JobState.Completed : JobState.Interrupted, recovered.State);
        Assert.Null(recovered.ClaimToken); Assert.Equal(boundary == 2 ? 1 : 0, recovered.CompletedUnits);
        Assert.Equal(0, Scalar(box, "SELECT COUNT(*) FROM staging"));
    }

    [Fact]
    public async Task Recovery_uses_held_lease_without_reacquisition()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        await new ProjectService().RecoverUnderLeaseAsync(db, lease, Token).WaitAsync(TimeSpan.FromSeconds(2), Token);
        job = await db.Jobs.GetAsync(job.Id, Token); Assert.Equal(JobState.Interrupted, job.State); Assert.Null(job.ClaimToken);
        long revision = job.Revision; await new ProjectService().RecoverUnderLeaseAsync(db, lease, Token);
        Assert.Equal(revision, (await db.Jobs.GetAsync(job.Id, Token)).Revision);
    }

    [Fact]
    public async Task Open_current_never_recovers_active_unit()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        var artifact = Publish(db, claim);
        await using var reader = await new ProjectService().OpenCurrentAsync(box.ProjectRoot, Token).WaitAsync(TimeSpan.FromSeconds(2), Token);
        Assert.Equal("running", reader.State.Segments[0].Status); Assert.Equal(claim.Token, (await reader.Jobs.GetAsync(job.Id, Token)).ClaimToken);
        Assert.True(File.Exists(db.ResolveOwnedPath(artifact.RelativePath))); Assert.Equal(1, Scalar(box, "SELECT COUNT(*) FROM staging"));
    }

    [Fact]
    public async Task Open_current_rollback_control_commits_without_acquiring_active_lease()
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var prepared = await ProjectJobStoreTests.Prepared(box)) { }
        Assert.Equal("delete", VoiceRecordingServiceTests.Sql(box.ProjectRoot, "PRAGMA journal_mode=DELETE"));
        await using var db = await service.OpenAsync(box.ProjectRoot, Token, retainSidecars: true);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await Enqueue(db, lease, preview: true);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        long revision = db.State.Revision;
        await using (var controller = await service.OpenCurrentAsync(box.ProjectRoot, Token, retainSidecars: false))
        {
            var canceled = await controller.Jobs.RequestStateAsync(job.Id, 2, "cancel", JobControlCommand.Cancel, Token);
            Assert.Equal(JobState.CancelRequested, canceled.State); Assert.Equal(claim.Token, canceled.ClaimToken);
            Assert.Equal(revision, controller.State.Revision);
            string journal = Path.Combine(box.ProjectRoot, "project.sqlite-journal");
            Assert.True(File.Exists(journal)); Assert.Throws<IOException>(() => File.Move(journal, journal + ".moved"));
        }
        job = await lease.AcceptGenerationUnitAsync(claim, Publish(db, claim), Token);
        Assert.Equal(JobState.Canceled, job.State); Assert.Equal(1, job.CompletedUnits);
        Assert.Equal("delete", VoiceRecordingServiceTests.Sql(box.ProjectRoot, "PRAGMA journal_mode"));
    }

    [Fact]
    public async Task Stale_progress_cannot_undo_intent()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        await db.Jobs.RequestStateAsync(job.Id, 2, "cancel", JobControlCommand.Cancel, Token); long revision = db.State.Revision;
        job = await lease.ReportGenerationProgressAsync(claim, "publishing", Token);
        Assert.Equal(JobState.CancelRequested, job.State); Assert.Equal(JobRequestedState.Cancel, job.RequestedState); Assert.Equal(4, job.Revision);
        Assert.Equal(revision, db.State.Revision); Assert.Equal(revision, Scalar(box, "SELECT revision FROM project"));
        await Assert.ThrowsAsync<ProjectException>(() => lease.ReportGenerationProgressAsync(claim with { Token = Guid.NewGuid().ToString("N") }, "generating", Token));
        await Assert.ThrowsAsync<ProjectException>(() => lease.ReportGenerationProgressAsync(claim, "private content", Token));
        Assert.Equal(4, (await db.Jobs.GetAsync(job.Id, Token)).Revision);
    }

    [Fact]
    public async Task Preparing_progress_marks_generating_only_after_input_validation()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box, emptyAppPolicy: true);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        string hash = job.InputHash; long inputRevision = job.Input.ProjectRevision;
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        long revision = db.State.Revision;
        job = await lease.ReportGenerationProgressAsync(claim, "validating_inputs", Token); Assert.Equal(JobState.Preparing, job.State);
        job = await lease.ReportGenerationProgressAsync(claim, "generating", Token); Assert.Equal(JobState.Running, job.State);
        Assert.Equal(revision, db.State.Revision); Assert.Equal(hash, job.InputHash); Assert.Equal(inputRevision, job.Input.ProjectRevision);
        Assert.Equal("", job.Input.Items[0].Versions.App); Assert.Equal("", job.Input.Items[0].Versions.Policy);
    }

    [Theory]
    [InlineData(JobControlCommand.Pause, JobState.Paused)] [InlineData(JobControlCommand.Cancel, JobState.Canceled)]
    public async Task Interruption_preserves_accepted_and_intent(JobControlCommand command, JobState expected)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        var first = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        var artifact = Publish(db, first); await lease.AcceptGenerationUnitAsync(first, artifact, Token);
        var next = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[1].SegmentId, job.Input.Preset, Token))!; Publish(db, next);
        job = await db.Jobs.GetAsync(job.Id, Token); await db.Jobs.RequestStateAsync(job.Id, job.Revision, "intent", command, Token);
        job = await lease.InterruptGenerationUnitAsync(next, "worker.interrupted", Token);
        Assert.Equal(expected, job.State); Assert.Equal(1, job.CompletedUnits); Assert.Null(job.ClaimToken);
        Assert.Equal(artifact, (await db.Jobs.ItemsAsync(job.Id, Token))[0].Artifact);
        Assert.True(File.Exists(db.ResolveOwnedPath(artifact.RelativePath))); Assert.False(File.Exists(db.ResolveOwnedPath(next.FinalRelativePath)));
    }

    [Fact]
    public async Task Cpu_fallback_consumption_survives_restart()
    {
        using var box = new ProjectSandbox(); string id; GenerationInput original; GenerationUnitClaim hybrid;
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        {
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease, hybrid: true); id = job.Id; original = job.Input;
            hybrid = (await lease.BeginGenerationUnitAsync(id, original.Items[0].SegmentId, original.Preset, Token))!;
            job = await lease.FailGenerationUnitAsync(hybrid, "worker.cuda.oom", GenerationFailureDisposition.RetryCpu, Token);
            Assert.Equal(JobState.Queued, job.State); Assert.True((await db.Jobs.ItemsAsync(id, Token))[0].CpuFallbackUsed); Assert.Equal("failed", db.State.Attempts[0].Status);
        }
        await using var opened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        await using var owned = await opened.AcquireArtifactLeaseAsync(cancellationToken: Token); var cpu = original.Preset with { ProfileId = "q8-f16-cpu" };
        await Assert.ThrowsAsync<ProjectException>(() => owned.BeginGenerationUnitAsync(id, hybrid.SegmentId, original.Preset, Token));
        var claim = (await owned.BeginGenerationUnitAsync(id, hybrid.SegmentId, cpu, Token))!; Assert.NotEqual(hybrid.FinalRelativePath, claim.FinalRelativePath);
        var interrupted = await owned.InterruptGenerationUnitAsync(claim, "worker.interrupted", Token);
        await opened.Jobs.RequestStateAsync(id, interrupted.Revision, "resume", JobControlCommand.Resume, Token);
        claim = (await owned.BeginGenerationUnitAsync(id, hybrid.SegmentId, cpu, Token))!;
        await Assert.ThrowsAsync<ProjectException>(() => owned.FailGenerationUnitAsync(claim, "worker.cuda.oom", GenerationFailureDisposition.RetryCpu, Token));
        var failed = await owned.FailGenerationUnitAsync(claim, "worker.cuda.oom", Token); Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal(original.Preset, failed.Input.Preset); Assert.Equal(original.ProjectRevision, failed.Input.ProjectRevision); Assert.Equal("q8-f16-cpu", failed.EffectiveProfileId);
        Assert.Null(await owned.BeginGenerationUnitAsync(id, hybrid.SegmentId, cpu, Token));
    }

    [Fact]
    public async Task Cpu_fallback_is_job_wide_and_next_item_keeps_CPU_after_restart()
    {
        using var box = new ProjectSandbox(); string id, inputHash; GenerationInput original; AcceptedJobArtifact accepted;
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        {
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease, hybrid: true);
            id = job.Id; inputHash = job.InputHash; original = job.Input;
            var claim = (await lease.BeginGenerationUnitAsync(id, original.Items[0].SegmentId, original.Preset, Token))!;
            job = await lease.FailGenerationUnitAsync(claim, "worker.cuda.oom", GenerationFailureDisposition.RetryCpu, Token);
            Assert.Equal("q8-f16-hybrid-16", job.EffectiveProfileId); Assert.All(await db.Jobs.ItemsAsync(id, Token), x => Assert.True(x.CpuFallbackUsed));
            claim = (await lease.BeginGenerationUnitAsync(id, original.Items[0].SegmentId, original.Preset with { ProfileId = "q8-f16-cpu" }, Token))!;
            accepted = Publish(db, claim); await lease.AcceptGenerationUnitAsync(claim, accepted, Token);
        }
        await using var opened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        await using var owned = await opened.AcquireArtifactLeaseAsync(cancellationToken: Token);
        await Assert.ThrowsAsync<ProjectException>(() => owned.BeginGenerationUnitAsync(id, original.Items[1].SegmentId, original.Preset, Token));
        var next = (await owned.BeginGenerationUnitAsync(id, original.Items[1].SegmentId, original.Preset with { ProfileId = "q8-f16-cpu" }, Token))!;
        var current = await opened.Jobs.GetAsync(id, Token); Assert.Equal(inputHash, current.InputHash); Assert.Equal(original.ProjectRevision, current.Input.ProjectRevision);
        Assert.Equal(accepted, (await opened.Jobs.ItemsAsync(id, Token))[0].Artifact);
        await Assert.ThrowsAsync<ProjectException>(() => owned.FailGenerationUnitAsync(next, "worker.cuda.oom", GenerationFailureDisposition.RetryCpu, Token));
        current = await owned.FailGenerationUnitAsync(next, "worker.cuda.oom", Token); Assert.Equal(JobState.Failed, current.State);
        Assert.Equal(3, opened.State.Attempts.Count); Assert.All(await opened.Jobs.ItemsAsync(id, Token), x => Assert.True(x.CpuFallbackUsed));
    }

    [Fact]
    public async Task Begin_revalidates_authoritative_input_and_rejects_arbitrary_preset()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        await Assert.ThrowsAsync<ProjectException>(() => lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset with { Seed = 7 }, Token));
        await lease.SaveAsync(db.State with { Segments = db.State.Segments.Select(x => x with { SpokenText = "changed synthetic" }).ToArray() }, Token);
        Assert.Equal("job.input_changed", (await Assert.ThrowsAsync<ProjectException>(() => lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))).Code);
        Assert.Empty(db.State.Attempts); Assert.Equal(0, Scalar(box, "SELECT COUNT(*) FROM staging"));
    }

    [Fact]
    public async Task Cleanup_preserves_prior_job_acceptance_when_segment_is_generating_again()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var original = await Enqueue(db, lease, preview: true);
        var first = (await lease.BeginGenerationUnitAsync(original.Id, original.Input.Items[0].SegmentId, original.Input.Preset, Token))!;
        var artifact = Publish(db, first); await lease.AcceptGenerationUnitAsync(first, artifact, Token);
        var input = ProjectJobStoreTests.Input(db); input = input with { Items = [input.Items[0]] };
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "again", JobKind.Preview, input, Token);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, input.Items[0].SegmentId, input.Preset, Token))!;
        await lease.RegisterPendingArtifactAsync(artifact.RelativePath + ".part", artifact.RelativePath, Token);
        await lease.InterruptGenerationUnitAsync(claim, "worker.interrupted", Token);
        Assert.True(File.Exists(db.ResolveOwnedPath(artifact.RelativePath)));
        Assert.Equal(artifact, (await db.Jobs.ItemsAsync(original.Id, Token))[0].Artifact);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Accept_inspects_PCM_and_requires_exclusive_ordinary_single_link_file(int variant)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        var artifact = Publish(db, claim); string path = db.ResolveOwnedPath(claim.FinalRelativePath);
        FileStream? writer = null;
        try
        {
            if (variant == 0)
            {
                byte[] bytes = File.ReadAllBytes(path); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 44100); File.WriteAllBytes(path, bytes);
                artifact = artifact with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
            }
            else if (variant == 1) writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            else Assert.True(CreateHardLinkW("\\\\?\\" + path + ".alias", "\\\\?\\" + path, 0));
            await Assert.ThrowsAsync<ProjectException>(() => lease.AcceptGenerationUnitAsync(claim, artifact, Token));
            Assert.Equal(0, (await db.Jobs.GetAsync(job.Id, Token)).CompletedUnits); Assert.Equal("running", db.State.Attempts[0].Status);
        }
        finally { writer?.Dispose(); if (variant == 2) File.Delete(path + ".alias"); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Transaction_failure_rolls_back_all_unit_rows_and_both_revisions(bool accepting)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        GenerationUnitClaim? claim = null; AcceptedJobArtifact? artifact = null;
        if (accepting) { claim = await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token); artifact = Publish(db, claim!); }
        long revision = db.State.Revision, jobRevision = (await db.Jobs.GetAsync(job.Id, Token)).Revision, sequence = await db.Jobs.GetEventSequenceAsync(Token);
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "CREATE TRIGGER reject_unit_event BEFORE INSERT ON job_events BEGIN SELECT RAISE(ABORT,'synthetic'); END");
        try
        {
            if (accepting) await Assert.ThrowsAsync<ProjectException>(() => lease.AcceptGenerationUnitAsync(claim!, artifact!, Token));
            else await Assert.ThrowsAsync<ProjectException>(() => lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token));
        }
        finally { VoiceRecordingServiceTests.Sql(box.ProjectRoot, "DROP TRIGGER reject_unit_event"); }
        Assert.Equal(revision, Scalar(box, "SELECT revision FROM project")); Assert.Equal(revision, db.State.Revision);
        Assert.Equal(jobRevision, (await db.Jobs.GetAsync(job.Id, Token)).Revision); Assert.Equal(sequence, await db.Jobs.GetEventSequenceAsync(Token));
        Assert.Equal(accepting ? 1 : 0, Scalar(box, "SELECT COUNT(*) FROM attempts")); Assert.Equal(accepting ? 1 : 0, Scalar(box, "SELECT COUNT(*) FROM staging"));
        Assert.Equal(0, (await db.Jobs.GetAsync(job.Id, Token)).CompletedUnits);
    }

    [Theory]
    [InlineData(JobControlCommand.Pause, JobState.Paused)] [InlineData(JobControlCommand.Cancel, JobState.Canceled)]
    public async Task Recovery_preserves_durable_intent_and_invalidates_token(JobControlCommand command, JobState expected)
    {
        using var box = new ProjectSandbox(); string id;
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        {
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease); id = job.Id;
            await lease.BeginGenerationUnitAsync(id, job.Input.Items[0].SegmentId, job.Input.Preset, Token);
            await db.Jobs.RequestStateAsync(id, 2, "control", command, Token);
        }
        await using var opened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        var recovered = await opened.Jobs.GetAsync(id, Token); Assert.Equal(expected, recovered.State); Assert.Null(recovered.ClaimToken);
        Assert.Equal("interrupted", Assert.Single(opened.State.Attempts).Status);
    }

    [Theory]
    [InlineData("worker.timeout", true)] [InlineData("worker.crash", true)] [InlineData("worker.cuda.oom", false)]
    [InlineData("worker.memory.insufficient", true)] [InlineData("worker.memory.reserve", true)] [InlineData("worker.memory.policy", true)]
    public async Task Retry_requires_hybrid_oom_and_immutable_permission(string code, bool allowed)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var input = ProjectJobStoreTests.Input(db); input = input with { Preset = input.Preset with { ProfileId = "q8-f16-hybrid-16", AllowCpuFallback = allowed } };
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "enqueue", JobKind.Generation, input, Token);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, input.Items[0].SegmentId, input.Preset, Token))!;
        await Assert.ThrowsAsync<ProjectException>(() => lease.FailGenerationUnitAsync(claim, code, GenerationFailureDisposition.RetryCpu, Token));
        job = await lease.FailGenerationUnitAsync(claim, code, Token); Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(code, (await db.Jobs.GetAsync(job.Id, Token)).DiagnosticCode);
        Assert.False((await db.Jobs.ItemsAsync(job.Id, Token))[0].CpuFallbackUsed);
        Assert.Null(await lease.BeginGenerationUnitAsync(job.Id, claim.SegmentId, input.Preset, Token));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task Accept_rejects_unbound_or_uninspected_result_without_mutation(int variant)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var job = await Enqueue(db, lease);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        var artifact = Publish(db, claim); long revision = db.State.Revision;
        artifact = variant switch { 0 => artifact with { SampleCount = 4800 }, 1 => artifact with { Sha256 = artifact.Sha256.ToUpperInvariant() }, 2 => artifact with { Length = artifact.Length + 2 }, 3 => artifact with { ProfileId = "q8-f16-hybrid-16" }, 4 => artifact with { WorkerVersion = new string('3', 40) }, 5 => artifact with { RelativePath = "segments/other.wav" }, _ => artifact with { ModelVersion = new string('3', 40) } };
        await Assert.ThrowsAsync<ProjectException>(() => lease.AcceptGenerationUnitAsync(claim, artifact, Token));
        Assert.Equal(revision, db.State.Revision); Assert.Equal(0, (await db.Jobs.GetAsync(job.Id, Token)).CompletedUnits); Assert.Equal(1, Scalar(box, "SELECT COUNT(*) FROM staging"));
    }

    private static async Task<JobRecord> Enqueue(ProjectDatabase db, ProjectArtifactLease lease, bool preview = false, bool hybrid = false)
    {
        var input = ProjectJobStoreTests.Input(db); if (preview) input = input with { Items = [input.Items[0]] };
        if (hybrid) input = input with { Preset = input.Preset with { ProfileId = "q8-f16-hybrid-16" } };
        return await db.Jobs.EnqueueAsync(lease, db.State.Revision, "enqueue", preview ? JobKind.Preview : JobKind.Generation, input, Token);
    }
    private static long Scalar(ProjectSandbox box, string sql) => (long)VoiceRecordingServiceTests.Sql(box.ProjectRoot, sql)!;
    private static AcceptedJobArtifact Publish(ProjectDatabase db, GenerationUnitClaim claim)
    {
        byte[] bytes = Wav(); string path = db.ResolveOwnedPath(claim.FinalRelativePath);
        File.WriteAllBytes(path + ".part", bytes); File.Move(path + ".part", path, false);
        return new(Guid.NewGuid().ToString("N"), claim.FinalRelativePath, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, 4801, claim.EffectivePreset.ProfileId, claim.Input.Versions.Worker, claim.Input.Versions.Model);
    }
    private static byte[] Wav()
    {
        byte[] bytes = new byte[44 + 4801 * 2]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 48000); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 96000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 9602); return bytes;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string target, nint security);
}

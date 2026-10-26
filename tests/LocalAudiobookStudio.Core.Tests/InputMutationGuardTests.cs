using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Core.Tests;

public class InputMutationGuardTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(JobState.Queued)] [InlineData(JobState.Paused)] [InlineData(JobState.Interrupted)]
    [InlineData(JobState.Preparing)] [InlineData(JobState.Running)]
    [InlineData(JobState.PauseRequested)] [InlineData(JobState.CancelRequested)]
    public async Task Idle_guard_rejects_every_nonterminal_state_without_mutating_it(JobState requested)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "enqueue", JobKind.Generation, ProjectJobStoreTests.Input(db), Token);
        GenerationUnitClaim? claim = null;
        if (requested == JobState.Paused)
            job = await db.Jobs.RequestStateAsync(job.Id, job.Revision, "pause", JobControlCommand.Pause, Token);
        else if (requested != JobState.Queued)
        {
            claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
            await File.WriteAllBytesAsync(db.ResolveOwnedPath(claim.FinalRelativePath + ".part"), [5, 6, 7], Token);
            if (requested == JobState.Interrupted)
                job = await lease.InterruptGenerationUnitRetainingStagingAsync(claim, "project.interrupted", Token);
            else if (requested == JobState.Running)
                job = await lease.ReportGenerationProgressAsync(claim, "generating", Token);
            else if (requested is JobState.PauseRequested or JobState.CancelRequested)
            {
                job = await db.Jobs.GetAsync(job.Id, Token);
                job = await db.Jobs.RequestStateAsync(job.Id, job.Revision, "control", requested == JobState.PauseRequested ? JobControlCommand.Pause : JobControlCommand.Cancel, Token);
            }
            else job = await db.Jobs.GetAsync(job.Id, Token);
        }
        Assert.Equal(requested, job.State);
        var before = await db.ReadCurrentSnapshotAsync(Token); long head = await db.Jobs.GetEventSequenceAsync(Token);
        Assert.True(before.HasActiveJob);
        var error = await Assert.ThrowsAsync<ProjectException>(() => lease.RequireNoActiveJobsAsync(Token));
        Assert.Equal("job.active", error.Code); Assert.Equal(error.Code, error.Message); Assert.Null(error.InnerException);
        var after = await db.ReadCurrentSnapshotAsync(Token);
        Assert.Equal(before.State.Revision, after.State.Revision); Assert.True(after.HasActiveJob);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(job), System.Text.Json.JsonSerializer.Serialize(await db.Jobs.GetAsync(job.Id, Token)));
        Assert.Equal(head, await db.Jobs.GetEventSequenceAsync(Token));
        if (claim != null) Assert.Equal(new byte[] { 5, 6, 7 }, await File.ReadAllBytesAsync(db.ResolveOwnedPath(claim.FinalRelativePath + ".part"), Token));
    }

    [Fact]
    public async Task Idle_guard_requires_the_original_live_lease_and_allows_terminal_jobs()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        await lease.RequireNoActiveJobsAsync(Token); Assert.False((await db.ReadCurrentSnapshotAsync(Token)).HasActiveJob);
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "enqueue", JobKind.Generation, ProjectJobStoreTests.Input(db), Token);
        await db.Jobs.RequestStateAsync(job.Id, job.Revision, "cancel", JobControlCommand.Cancel, Token);
        await lease.RequireNoActiveJobsAsync(Token); Assert.False((await db.ReadCurrentSnapshotAsync(Token)).HasActiveJob);
        await lease.DisposeAsync();
        Assert.Equal("project.lease.invalid", (await Assert.ThrowsAsync<ProjectException>(() => lease.RequireNoActiveJobsAsync(Token))).Code);
    }

    [Fact]
    public async Task Mutation_open_rejects_an_orphan_claim_before_recovery()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        GenerationUnitClaim claim; JobRecord before;
        await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
        {
            var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "enqueue", JobKind.Generation, ProjectJobStoreTests.Input(db), Token);
            claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
            await File.WriteAllBytesAsync(db.ResolveOwnedPath(claim.FinalRelativePath + ".part"), [1, 2, 3], Token);
            before = await db.Jobs.GetAsync(job.Id, Token);
        }
        long revision = db.State.Revision;
        var error = await Assert.ThrowsAsync<ProjectException>(async () =>
        { await using var unexpected = await new ProjectService().OpenForInputMutationAsync(box.ProjectRoot, Token); });
        Assert.Equal("job.active", error.Code);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(await db.Jobs.GetAsync(before.Id, Token)));
        Assert.Equal(revision, (await db.ReadCurrentSnapshotAsync(Token)).State.Revision);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(db.ResolveOwnedPath(claim.FinalRelativePath + ".part"), Token));
    }

    [Fact]
    public async Task Idle_mutation_open_never_performs_implicit_segment_or_staging_recovery()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        const string part = "segments/old.wav.part";
        Directory.CreateDirectory(Path.Combine(box.ProjectRoot, "segments"));
        await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
        {
            await lease.RegisterPendingArtifactAsync(part, "segments/old.wav", Token);
            await File.WriteAllBytesAsync(db.ResolveOwnedPath(part), [1, 2, 3], Token);
            await lease.SaveAsync(db.State with { Segments = db.State.Segments.Select((x, i) => i == 0 ? x with { Status = "running" } : x).ToArray() }, Token);
        }
        await using var opened = await new ProjectService().OpenForInputMutationAsync(box.ProjectRoot, Token);
        Assert.Equal(db.State.Revision, opened.State.Revision); Assert.Equal("running", opened.State.Segments[0].Status);
        Assert.Empty(opened.State.Attempts); Assert.False((await opened.ReadCurrentSnapshotAsync(Token)).HasActiveJob);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(opened.ResolveOwnedPath(part), Token));
        await using var available = await opened.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token);
    }

    [Fact]
    public async Task Guard_revalidates_lease_after_entering_the_database_gate()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var gate = (SemaphoreSlim)typeof(ProjectDatabase).GetField("gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(db)!;
        await gate.WaitAsync(Token);
        Task guard;
        try
        {
            guard = lease.RequireNoActiveJobsAsync(Token);
            Assert.False(guard.IsCompleted);
            await lease.DisposeAsync();
        }
        finally { gate.Release(); }
        var error = await Assert.ThrowsAsync<ProjectException>(() => guard);
        Assert.Equal("project.lease.invalid", error.Code);
    }

    [Fact]
    public async Task Mutation_lease_rechecks_a_job_enqueued_after_mutation_open()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var opened = await new ProjectService().OpenForInputMutationAsync(box.ProjectRoot, Token);
        await using (var owner = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
            await db.Jobs.EnqueueAsync(owner, db.State.Revision, "late-enqueue", JobKind.Generation, ProjectJobStoreTests.Input(db), Token);
        var error = await Assert.ThrowsAsync<ProjectException>(async () =>
        { await using var unexpected = await opened.AcquireInputMutationLeaseAsync(Token); });
        Assert.Equal("job.active", error.Code);
        await using var released = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token);
        Assert.Equal(db.State.Revision, (await opened.ReadCurrentSnapshotAsync(Token)).State.Revision);
    }

    [Fact]
    public async Task Mutation_lease_reports_active_job_even_when_another_owner_holds_the_lease()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var opened = await new ProjectService().OpenForInputMutationAsync(box.ProjectRoot, Token);
        await using var owner = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await db.Jobs.EnqueueAsync(owner, db.State.Revision, "late-enqueue", JobKind.Generation, ProjectJobStoreTests.Input(db), Token);
        var error = await Assert.ThrowsAsync<ProjectException>(async () =>
        { await using var unexpected = await opened.AcquireInputMutationLeaseAsync(Token); });
        Assert.Equal("job.active", error.Code);
        Assert.Equal(JobState.Queued, (await db.Jobs.GetAsync(job.Id, Token)).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Guarded_voice_import_rejects_late_job_before_validation_stream_or_staging(bool validRights)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var opened = await new ProjectService().OpenForInputMutationAsync(box.ProjectRoot, Token);
        await using (var owner = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
            await db.Jobs.EnqueueAsync(owner, db.State.Revision, "late-enqueue", JobKind.Generation, ProjectJobStoreTests.Input(db), Token);
        using var content = new UnreadableContent();
        long head = await db.Jobs.GetEventSequenceAsync(Token);
        var error = await Assert.ThrowsAsync<ProjectException>(() => new VoiceRecordingService().ImportAsync(opened,
            new("sample.wav", validRights), content, -1, Token, requireNoActiveJob: true));
        Assert.Equal("job.active", error.Code); Assert.Equal(0, content.ReadCount);
        Assert.False(Directory.Exists(Path.Combine(box.ProjectRoot, "voice-recording")));
        Assert.Equal(head, await db.Jobs.GetEventSequenceAsync(Token));
        await using var released = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Guarded_anchor_confirmation_rejects_late_job_before_validation_or_staging(bool confirmed)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var opened = await new ProjectService().OpenForInputMutationAsync(box.ProjectRoot, Token);
        await using (var owner = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
            await db.Jobs.EnqueueAsync(owner, db.State.Revision, "late-enqueue", JobKind.Generation, ProjectJobStoreTests.Input(db), Token);
        long head = await db.Jobs.GetEventSequenceAsync(Token);
        var error = await Assert.ThrowsAsync<ProjectException>(() => new VoiceRecordingService().ConfirmAnchorAsync(opened,
            new(-1, -2, "", confirmed), -1, Token, requireNoActiveJob: true));
        Assert.Equal("job.active", error.Code);
        Assert.False(Directory.Exists(Path.Combine(box.ProjectRoot, "voice-anchor-staging")));
        Assert.Equal(head, await db.Jobs.GetEventSequenceAsync(Token));
        await using var released = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token);
    }

    private sealed class UnreadableContent : MemoryStream
    {
        public int ReadCount { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { ReadCount++; throw new IOException("SYNTHETIC_CONTENT_MUST_NOT_BE_READ"); }
    }
}

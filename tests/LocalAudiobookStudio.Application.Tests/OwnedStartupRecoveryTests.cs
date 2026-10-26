using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Text;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Application.Tests;

public class OwnedStartupRecoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Startup_prepares_closed_wal_without_running_queued_or_resuming_paused_jobs(bool pause)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(); var before = await fixture.Job();
        if (pause) before = await fixture.Jobs.RequestStateAsync(fixture.Id, before.Id, before.Revision, "pause", JobControlCommand.Pause, Token);
        CloseWal(fixture); byte[] bytes = await File.ReadAllBytesAsync(DatabasePath(fixture), Token);
        await fixture.Runner.RecoverAsync(Token);
        Assert.True(File.Exists(DatabasePath(fixture) + "-wal")); Assert.True(File.Exists(DatabasePath(fixture) + "-shm"));
        Assert.Equal(before, await fixture.Job()); Assert.Equal(0, fixture.Workers.OpenCount);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(DatabasePath(fixture), Token));
        if (pause) Assert.False(await fixture.Runner.RunNextAsync(Token));
    }

    [Fact]
    public async Task Startup_prepares_closed_orphan_then_recovers_under_same_owner_preserving_accepted_audio()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(); fixture.Workers.Release.TrySetResult();
        Assert.True(await fixture.Runner.RunNextAsync(Token)); string accepted, pending; string jobId;
        await using (var db = await fixture.Open())
        await using (var owner = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
        {
            var job = await db.Jobs.GetAsync((await fixture.Job()).Id, Token); jobId = job.Id;
            var items = await db.Jobs.ItemsAsync(job.Id, Token); accepted = db.ResolveOwnedPath(items[0].Artifact!.RelativePath);
            var claim = (await owner.BeginGenerationUnitAsync(job.Id, items[1].SegmentId, job.Input.Preset, Token))!;
            pending = db.ResolveOwnedPath(claim.FinalRelativePath + ".part"); await File.WriteAllBytesAsync(pending, [1, 2, 3], Token);
        }
        byte[] audio = await File.ReadAllBytesAsync(accepted, Token); CloseWal(fixture);
        await fixture.Runner.RecoverAsync(Token);
        var recovered = await fixture.Job(); Assert.Equal(jobId, recovered.Id); Assert.Equal(JobState.Interrupted, recovered.State);
        Assert.Equal(1, recovered.CompletedUnits); Assert.False(File.Exists(pending));
        Assert.Equal(audio, await File.ReadAllBytesAsync(accepted, Token)); Assert.False(await fixture.Runner.RunNextAsync(Token));
        Assert.Equal(1, fixture.Workers.OpenCount);
        await fixture.Jobs.RequestStateAsync(fixture.Id, jobId, recovered.Revision, "resume", JobControlCommand.Resume, Token);
        Assert.True(await fixture.Runner.RunNextAsync(Token)); Assert.Equal(JobState.Completed, (await fixture.Job()).State);
    }

    [Fact]
    public async Task Busy_closed_project_is_deferred_without_names_while_other_project_is_prepared()
    {
        using var box = new CatalogSandbox();
        await using var first = await GenerationRunnerTests.Fixture.Create(shared: box, creationKey: "first");
        await using var other = await GenerationRunnerTests.Fixture.Create(shared: box, creationKey: "other");
        CloseWal(first); CloseWal(other);
        using var actualOwner = File.Open(Path.Combine(box.ProjectRoot(first.Id), "artifact-ownership.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await first.Runner.RecoverAsync(Token);
        Assert.False(File.Exists(DatabasePath(first) + "-wal")); Assert.False(File.Exists(DatabasePath(first) + "-shm"));
        Assert.True(File.Exists(DatabasePath(other) + "-wal")); Assert.True(File.Exists(DatabasePath(other) + "-shm"));
        Assert.Equal(JobState.Queued, (await other.Job()).State); Assert.Equal(0, first.Workers.OpenCount); Assert.Equal(0, other.Workers.OpenCount);
    }

    [Fact]
    public async Task Input_edit_does_not_reserve_missing_names_before_actual_ownership()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(); var job = await fixture.Job();
        await fixture.Jobs.RequestStateAsync(fixture.Id, job.Id, job.Revision, "cancel", JobControlCommand.Cancel, Token);
        CloseWal(fixture); byte[] before = await File.ReadAllBytesAsync(DatabasePath(fixture), Token);
        string journal = DatabasePath(fixture) + "-journal";
        byte[]? existingJournal = File.Exists(journal) ? await File.ReadAllBytesAsync(journal, Token) : null;
        using (var actualOwner = File.Open(Path.Combine(fixture.Box.ProjectRoot(fixture.Id), "artifact-ownership.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<StudioException>(() => new StudioTextService(fixture.Box.Service()).UpdateSpokenTextAsync(
                fixture.Id, fixture.Prepared.Segments[0].Id, fixture.Prepared.Revision, new("Новый текст."), Token));
            Assert.Equal("project.busy", error.Code);
            Assert.False(File.Exists(DatabasePath(fixture) + "-wal"), "Source WAL appeared before ownership");
            Assert.False(File.Exists(DatabasePath(fixture) + "-shm"), "Source SHM appeared before ownership");
            Assert.Equal(existingJournal != null, File.Exists(journal));
            if (existingJournal != null) Assert.Equal(existingJournal, await File.ReadAllBytesAsync(journal, Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(DatabasePath(fixture), Token));
        }
        var edited = await new StudioTextService(fixture.Box.Service()).UpdateSpokenTextAsync(fixture.Id,
            fixture.Prepared.Segments[0].Id, fixture.Prepared.Revision, new("Новый текст."), Token);
        Assert.Equal("Новый текст.", edited.Segments[0].SpokenText); Assert.Equal(fixture.Prepared.Revision + 1, edited.Revision);
    }

    private static string DatabasePath(GenerationRunnerTests.Fixture fixture) => Path.Combine(fixture.Box.ProjectRoot(fixture.Id), "project.sqlite");

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Startup_migrates_registered_legacy_project_only_after_actual_owner_is_available(int schema)
    {
        using var box = new CatalogSandbox();
        var created = await box.Service().CreateAsync(new("Legacy"), "legacy", Token);
        string id = created.Snapshot.Id, root = box.ProjectRoot(id), main = Path.Combine(root, "project.sqlite");
        CatalogSandbox.Sql(root, "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs;" +
            (schema == 1 ? "DROP TABLE voice_recording;" : "") + $"PRAGMA user_version={schema}; PRAGMA journal_mode=DELETE");
        var workers = new GenerationRunnerTests.FakeFactory(); var core = new ProjectService();
        var runner = new GenerationRunner(new ProjectJobAccess(new(box.Root), box.Catalog(), core), core, workers, TimeProvider.System);
        byte[] before = await File.ReadAllBytesAsync(main, Token); bool journal = File.Exists(main + "-journal");
        using (var owner = File.Open(Path.Combine(root, "artifact-ownership.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await runner.RecoverAsync(Token);
            Assert.Equal(before, await File.ReadAllBytesAsync(main, Token)); Assert.Equal(journal, File.Exists(main + "-journal"));
        }
        try
        {
            await runner.RecoverAsync(Token);
            await using var current = await core.OpenCurrentAsync(root, Token, requireJobSchema: true);
            Assert.Equal(created.Snapshot.Revision, current.State.Revision); Assert.Equal("Legacy", current.State.Metadata.Title);
            Assert.Empty(await current.Jobs.ListAsync(Token)); Assert.Equal(0, workers.OpenCount);
        }
        finally { await runner.StopAsync(Token); }
    }
    private static void CloseWal(GenerationRunnerTests.Fixture fixture)
    {
        CatalogSandbox.Sql(fixture.Box.ProjectRoot(fixture.Id), "PRAGMA wal_checkpoint(TRUNCATE)");
        Assert.False(File.Exists(DatabasePath(fixture) + "-wal")); Assert.False(File.Exists(DatabasePath(fixture) + "-shm"));
    }

    [Theory]
    [InlineData(null, JobState.Interrupted)] [InlineData(JobControlCommand.Pause, JobState.Paused)] [InlineData(JobControlCommand.Cancel, JobState.Canceled)]
    public async Task RunNext_reports_an_owned_recovery_transition_without_launching_a_worker(JobControlCommand? control, JobState expected)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(preview: true); string pending; long revision;
        await using (var db = await fixture.Open())
        await using (var owner = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
        {
            var job = await db.Jobs.GetAsync((await fixture.Job()).Id, Token);
            var claim = (await owner.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
            pending = db.ResolveOwnedPath(claim.FinalRelativePath + ".part"); await File.WriteAllBytesAsync(pending, [1, 2, 3], Token);
            job = await db.Jobs.GetAsync(job.Id, Token);
            if (control.HasValue) job = await db.Jobs.RequestStateAsync(job.Id, job.Revision, "control", control.Value, Token);
            revision = job.Revision;
        }
        CloseWal(fixture);
        Assert.True(await fixture.Runner.RunNextAsync(Token));
        var recovered = await fixture.Job(); Assert.Equal(expected, recovered.State); Assert.Equal(revision + 1, recovered.Revision);
        Assert.Equal(0, fixture.Workers.OpenCount); Assert.False(File.Exists(pending));
        Assert.False(await fixture.Runner.RunNextAsync(Token));
    }

    [Theory]
    [InlineData("get")] [InlineData("open")] [InlineData("edit")] [InlineData("runner")]
    public async Task Busy_schema3_rollback_current_paths_do_not_create_a_missing_journal(string operation)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(); var job = await fixture.Job();
        await fixture.Jobs.RequestStateAsync(fixture.Id, job.Id, job.Revision, "cancel", JobControlCommand.Cancel, Token);
        string root = fixture.Box.ProjectRoot(fixture.Id), main = DatabasePath(fixture), journal = main + "-journal";
        CatalogSandbox.Sql(root, "PRAGMA journal_mode=DELETE; BEGIN IMMEDIATE; UPDATE project SET revision=revision; COMMIT");
        Assert.False(File.Exists(journal)); byte[] before = await File.ReadAllBytesAsync(main, Token);
        using (var owner = File.Open(Path.Combine(root, "artifact-ownership.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (operation == "get") Assert.Equal(fixture.Id, (await fixture.Box.Service().GetAsync(fixture.Id, Token)).Id);
            else if (operation == "open") Assert.Equal(fixture.Id, (await fixture.Box.Service().OpenAsync(fixture.Id, Token)).Id);
            else if (operation == "runner") await fixture.Runner.RecoverAsync(Token);
            else
                Assert.Equal("project.busy", (await Assert.ThrowsAsync<StudioException>(() => new StudioTextService(fixture.Box.Service()).UpdateSpokenTextAsync(
                    fixture.Id, fixture.Prepared.Segments[0].Id, fixture.Prepared.Revision, new("Изменение."), Token))).Code);
            Assert.False(File.Exists(journal)); Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
        }
        await fixture.Runner.RecoverAsync(Token);
        Assert.True(File.Exists(journal)); Assert.Equal(JobState.Canceled, (await fixture.Job()).State); Assert.Equal(0, fixture.Workers.OpenCount);
        Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
    }
}

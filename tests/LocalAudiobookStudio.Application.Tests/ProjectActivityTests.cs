using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.Application.Tests;

public class ProjectActivityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Browser_reopen_does_not_recover_active_or_wait_for_studio_ownership(bool open)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        fixture.Workers.WritePendingAtStart = true;
        var running = fixture.InFlight = fixture.Runner.RunNextAsync(Token);
        await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var before = await fixture.Job();
        await using var db = await fixture.Open();
        var claimToken = (await db.Jobs.GetAsync(before.Id, Token)).ClaimToken;
        string pending = fixture.Workers.LastCommand!.FinalPath + ".part";
        byte[] bytes = await File.ReadAllBytesAsync(pending, Token);
        using var studio = File.Open(Path.Combine(fixture.Box.Root, "studio.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var snapshot = await Read(fixture.Box.Service(), fixture.Id, open, deadline.Token);
        Assert.Equal(fixture.Id, snapshot.Id); Assert.Equal(fixture.Prepared.Revision + 1, snapshot.Revision);
        Assert.All(snapshot.Segments, segment => Assert.Null(segment.OutputArtifactId));
        var after = await fixture.Job();
        Assert.Equal(before, after); Assert.False(running.IsCompleted); Assert.Equal(1, fixture.Workers.OpenCount);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(pending, Token));
        Assert.Equal("running", Assert.Single(db.State.Attempts).Status);
        Assert.Equal(claimToken, (await db.Jobs.GetAsync(before.Id, Token)).ClaimToken);
    }

    [Fact]
    public async Task List_keeps_the_actively_owned_project_visible()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        var running = fixture.InFlight = fixture.Runner.RunNextAsync(Token);
        await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var item = Assert.Single(await fixture.Box.Service().ListAsync(Token));
        Assert.Equal(fixture.Id, item.Id); Assert.Equal(fixture.Prepared.Revision + 1, item.Revision);
        Assert.False(running.IsCompleted); Assert.Equal(1, fixture.Workers.OpenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Browser_read_of_an_orphan_claim_leaves_recovery_to_its_owner(bool open)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(preview: true);
        GenerationUnitClaim claim;
        await using (var db = await fixture.Open())
        await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
        {
            var job = await db.Jobs.GetAsync((await fixture.Job()).Id, Token);
            claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
            await File.WriteAllBytesAsync(db.ResolveOwnedPath(claim.FinalRelativePath + ".part"), [7, 8, 9], Token);
        }
        var before = await fixture.Job();
        var snapshot = await Read(fixture.Box.Service(), fixture.Id, open, Token);
        Assert.Equal(fixture.Prepared.Revision + 1, snapshot.Revision);
        Assert.Equal(before, await fixture.Job());
        await using var check = await fixture.Open();
        Assert.Equal(claim.Token, (await check.Jobs.GetAsync(before.Id, Token)).ClaimToken);
        Assert.Equal("running", Assert.Single(check.State.Attempts).Status);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(check.ResolveOwnedPath(claim.FinalRelativePath + ".part"), Token));
        Assert.Equal(0, fixture.Workers.OpenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Browser_snapshot_exposes_only_committed_opaque_output_ids(bool open)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(preview: true);
        fixture.Workers.Release.TrySetResult(); Assert.True(await fixture.Runner.RunNextAsync(Token));
        var job = await fixture.Job();
        await using var db = await fixture.Open();
        var artifact = Assert.Single(await db.Jobs.ItemsAsync(job.Id, Token)).Artifact!;
        var snapshot = await Read(fixture.Box.Service(), fixture.Id, open, Token);
        Assert.Equal(db.State.Revision, snapshot.Revision);
        Assert.Equal(artifact.Id, snapshot.Segments.Single(x => x.Id == fixture.Prepared.Segments[0].Id).OutputArtifactId);
        Assert.All(snapshot.Segments.Where(x => x.Id != fixture.Prepared.Segments[0].Id), x => Assert.Null(x.OutputArtifactId));
        string json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(fixture.Box.Root, json); Assert.DoesNotContain(".wav", json);
        Assert.DoesNotContain(artifact.RelativePath, json); Assert.Equal(1, fixture.Workers.OpenCount);
    }

    [Fact]
    public async Task Current_browser_read_still_enforces_the_source_snapshot_size_limit()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        var limited = new StudioProjectService(new(fixture.Box.Root) { MaximumSnapshotBytes = 1 }, fixture.Box.Catalog(), new());
        var error = await Assert.ThrowsAsync<StudioException>(() => limited.GetAsync(fixture.Id, Token));
        Assert.Equal("project.unavailable", error.Code);
        Assert.NotNull(await fixture.Box.Catalog().FindAsync(fixture.Id, Token));
        Assert.Equal(fixture.Id, (await fixture.Box.Service().GetAsync(fixture.Id, Token)).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_wal_browser_snapshot_keeps_output_ids_without_repairing_source_names(bool open)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(preview: true);
        fixture.Workers.Release.TrySetResult(); Assert.True(await fixture.Runner.RunNextAsync(Token));
        string artifactId;
        await using (var db = await fixture.Open())
            artifactId = Assert.Single(await db.Jobs.ItemsAsync((await fixture.Job()).Id, Token)).Artifact!.Id;
        string root = fixture.Box.ProjectRoot(fixture.Id), main = Path.Combine(root, "project.sqlite");
        CatalogSandbox.Sql(root, "PRAGMA wal_checkpoint(TRUNCATE)");
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
        byte[] before = await File.ReadAllBytesAsync(main, Token);
        var snapshot = await Read(fixture.Box.Service(), fixture.Id, open, Token);
        Assert.Equal(artifactId, snapshot.Segments[0].OutputArtifactId);
        Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
    }

    [Theory]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public async Task Legacy_browser_read_keeps_schema_bytes_and_absent_journal_names(int schema, bool open)
    {
        using var box = new CatalogSandbox(); string id = Guid.NewGuid().ToString("N"), root = box.ProjectRoot(id);
        await using (var created = await new LocalAudiobookStudio.Core.Projects.ProjectService().CreateAsync(root, "Legacy", id, Token)) { }
        CatalogSandbox.Sql(root, "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs;" +
            (schema == 1 ? "DROP TABLE voice_recording;" : "") + $"PRAGMA user_version={schema}; PRAGMA journal_mode=DELETE");
        await box.Catalog().InsertAsync(new(id, DateTimeOffset.MinValue), Token);
        string main = Path.Combine(root, "project.sqlite");
        Assert.False(File.Exists(main + "-journal"));
        byte[] before = await File.ReadAllBytesAsync(main, Token);
        var snapshot = await Read(box.Service(), id, open, Token);
        Assert.Equal("Legacy", snapshot.DisplayName); Assert.Empty(snapshot.Segments);
        Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
        Assert.False(File.Exists(main + "-journal"));
    }

    private static Task<ProjectSnapshotDto> Read(StudioProjectService service, string id, bool open, CancellationToken token)
        => open ? service.OpenAsync(id, token) : service.GetAsync(id, token);

    [Fact]
    public async Task Completed_create_replay_returns_the_same_current_output_mapping_as_Get()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(preview: true, creationKey: "known-create");
        fixture.Workers.Release.TrySetResult(); Assert.True(await fixture.Runner.RunNextAsync(Token));
        var current = await fixture.Box.Service().GetAsync(fixture.Id, Token);
        var replay = await fixture.Box.Service().CreateAsync(new("Synthetic"), "known-create", Token);
        Assert.NotNull(replay.Snapshot.Segments[0].OutputArtifactId);
        Assert.False(replay.Created); Assert.Equal(current.Segments, replay.Snapshot.Segments);
        Assert.Equal(current with { Segments = replay.Snapshot.Segments }, replay.Snapshot);
    }

    [Fact]
    public async Task Editing_completed_text_clears_only_its_output_id_and_keeps_other_accepted_outputs()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        fixture.Workers.Release.TrySetResult(); Assert.True(await fixture.Runner.RunNextAsync(Token));
        Assert.True(await fixture.Runner.RunNextAsync(Token));
        var current = await fixture.Box.Service().GetAsync(fixture.Id, Token);
        Assert.All(current.Segments, x => Assert.NotNull(x.OutputArtifactId));
        var edited = await new LocalAudiobookStudio.Application.Text.StudioTextService(fixture.Box.Service()).UpdateSpokenTextAsync(
            fixture.Id, current.Segments[0].Id, current.Revision, new("Синтетический пример."), Token);
        Assert.Null(edited.Segments[0].OutputArtifactId);
        Assert.Equal(current.Segments[1].OutputArtifactId, edited.Segments[1].OutputArtifactId);
        var reread = await fixture.Box.Service().GetAsync(fixture.Id, Token);
        Assert.Equal(edited.Segments, reread.Segments);
        Assert.Equal(edited with { Segments = reread.Segments }, reread);
    }
}

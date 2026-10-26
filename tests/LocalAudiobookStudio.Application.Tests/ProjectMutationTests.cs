using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Text;
using LocalAudiobookStudio.Application.Voices;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Application.Tests;

public class ProjectMutationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(JobState.Queued)] [InlineData(JobState.Paused)] [InlineData(JobState.Interrupted)]
    [InlineData(JobState.Preparing)] [InlineData(JobState.Running)]
    [InlineData(JobState.PauseRequested)] [InlineData(JobState.CancelRequested)]
    public async Task Edit_during_nonterminal_job_is_rejected_before_stale_revision_or_text_validation(JobState requested)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        await SetState(fixture, requested);
        var before = await fixture.Box.Service().GetAsync(fixture.Id, Token);
        var job = await fixture.Job(); Assert.Equal(requested, job.State);
        await using var db = await fixture.Open();
        var stored = JsonSerializer.Serialize(await db.Jobs.GetAsync(job.Id, Token));
        long head = await db.Jobs.GetEventSequenceAsync(Token);
        var error = await Assert.ThrowsAsync<StudioException>(() => new StudioTextService(fixture.Box.Service()).UpdateSpokenTextAsync(
            fixture.Id, fixture.Prepared.Segments[0].Id, -1, new(""), Token));
        Assert.Equal("job.active", error.Code); Assert.Equal(error.Code, error.Message); Assert.Null(error.InnerException);
        Assert.Equal(stored, JsonSerializer.Serialize(await db.Jobs.GetAsync(job.Id, Token)));
        Assert.Equal(head, await db.Jobs.GetEventSequenceAsync(Token));
        var after = await fixture.Box.Service().GetAsync(fixture.Id, Token);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }

    [Theory]
    [InlineData("file")] [InlineData("paste")] [InlineData("recording")] [InlineData("anchor")]
    public async Task Every_input_facade_rejects_active_job_before_content_or_command_validation(string facade)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        using var content = new UnreadableContent();
        var projects = fixture.Box.Service(); var text = new StudioTextService(projects);
        var voice = new StudioVoiceService(projects, new VoiceRecordingService());
        long revision = fixture.Prepared.Revision;
        Task<ProjectSnapshotDto> action = facade switch
        {
            "file" => text.ImportFileAsync(fixture.Id, revision, "\n", content, Token),
            "paste" => text.ImportPastedAsync(fixture.Id, revision, new("\n", ""), Token),
            "recording" => voice.UploadRecordingAsync(fixture.Id, revision, new("sample.wav", false), content, Token),
            _ => voice.ConfirmAnchorAsync(fixture.Id, revision, new(-1, -2, "", false), Token)
        };
        var error = await Assert.ThrowsAsync<StudioException>(() => action);
        Assert.Equal("job.active", error.Code); Assert.Equal(0, content.ReadCount);
        Assert.Equal(JobState.Queued, (await fixture.Job()).State);
        Assert.Equal(revision, (await projects.GetAsync(fixture.Id, Token)).Revision);
    }

    [Fact]
    public async Task Edit_rejects_live_inference_without_waiting_for_studio_or_recovering_pending_output()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        fixture.Workers.WritePendingAtStart = true;
        var running = fixture.InFlight = fixture.Runner.RunNextAsync(Token);
        await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var job = await fixture.Job();
        await using var db = await fixture.Open();
        var before = JsonSerializer.Serialize(await db.Jobs.GetAsync(job.Id, Token));
        using var studio = File.Open(Path.Combine(fixture.Box.Root, "studio.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<StudioException>(() => new StudioTextService(fixture.Box.Service()).UpdateSpokenTextAsync(
            fixture.Id, fixture.Prepared.Segments[0].Id, db.State.Revision, new("Изменение."), deadline.Token));
        Assert.Equal("job.active", error.Code); Assert.False(running.IsCompleted);
        Assert.Equal(before, JsonSerializer.Serialize(await db.Jobs.GetAsync(job.Id, Token)));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(fixture.Workers.LastCommand!.FinalPath + ".part", Token));
        Assert.Equal(1, fixture.Workers.OpenCount);
    }

    [Fact]
    public async Task Paused_closed_wal_is_rejected_without_recreating_missing_source_sidecars()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        var job = await fixture.Job();
        await fixture.Jobs.RequestStateAsync(fixture.Id, job.Id, job.Revision, "pause", JobControlCommand.Pause, Token);
        string root = fixture.Box.ProjectRoot(fixture.Id), main = Path.Combine(root, "project.sqlite");
        CatalogSandbox.Sql(root, "PRAGMA wal_checkpoint(TRUNCATE)");
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
        byte[] before = await File.ReadAllBytesAsync(main, Token);
        var error = await Assert.ThrowsAsync<StudioException>(() => new StudioTextService(fixture.Box.Service()).UpdateSpokenTextAsync(
            fixture.Id, fixture.Prepared.Segments[0].Id, fixture.Prepared.Revision, new("Изменение."), Token));
        Assert.Equal("job.active", error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Enqueue_racing_the_early_edit_check_is_rejected_without_recovery(bool live)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        var original = await fixture.Job();
        await fixture.Jobs.RequestStateAsync(fixture.Id, original.Id, original.Revision, "cancel", JobControlCommand.Cancel, Token);
        JobSnapshotDto? raced = null; GenerationUnitClaim? claim = null; string? pending = null;
        var catalog = new OnSecondFindCatalog(fixture.Box.Catalog(), async () =>
        {
            raced = await fixture.Jobs.EnqueueAsync(fixture.Id, fixture.Prepared.Revision, "racing-enqueue",
                new(JobKind.Generation, fixture.Prepared.Segments.Select(x => x.Id).ToArray()), Token);
            if (live)
            {
                fixture.Workers.WritePendingAtStart = true;
                fixture.InFlight = fixture.Runner.RunNextAsync(Token);
                await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
                pending = fixture.Workers.LastCommand!.FinalPath + ".part";
            }
            else
            {
                await using var db = await fixture.Open();
                await using var owner = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
                var stored = await db.Jobs.GetAsync(raced.Id, Token);
                claim = (await owner.BeginGenerationUnitAsync(stored.Id, stored.Input.Items[0].SegmentId, stored.Input.Preset, Token))!;
                pending = db.ResolveOwnedPath(claim.FinalRelativePath + ".part");
                await File.WriteAllBytesAsync(pending, [1, 2, 3], Token);
            }
            raced = await fixture.Jobs.GetAsync(fixture.Id, raced.Id, Token);
        });
        var projects = new StudioProjectService(new(fixture.Box.Root), catalog, new ProjectService());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<StudioException>(() => new StudioTextService(projects).UpdateSpokenTextAsync(
            fixture.Id, fixture.Prepared.Segments[0].Id, fixture.Prepared.Revision, new("Изменение."), deadline.Token));
        Assert.Equal("job.active", error.Code); Assert.NotNull(raced);
        Assert.Equal(raced, await fixture.Jobs.GetAsync(fixture.Id, raced.Id, Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(pending!, Token));
        await using var after = await fixture.Open();
        Assert.Equal(fixture.Prepared.Revision + 1, after.State.Revision);
        Assert.Equal(fixture.Prepared.Segments[0].SpokenText, after.State.Segments[0].SpokenText);
        Assert.Equal("running", Assert.Single(after.State.Attempts).Status);
        if (claim != null) Assert.Equal(claim.Token, (await after.Jobs.GetAsync(raced.Id, Token)).ClaimToken);
    }

    [Fact]
    public async Task Canceled_job_does_not_prevent_future_edits()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        var job = await fixture.Job();
        await fixture.Jobs.RequestStateAsync(fixture.Id, job.Id, job.Revision, "cancel", JobControlCommand.Cancel, Token);
        var edited = await new StudioTextService(fixture.Box.Service()).UpdateSpokenTextAsync(fixture.Id,
            fixture.Prepared.Segments[0].Id, fixture.Prepared.Revision, new("Новый синтетический текст."), Token);
        Assert.Equal("Новый синтетический текст.", edited.Segments[0].SpokenText);
        Assert.Equal(fixture.Prepared.Revision + 1, edited.Revision);
    }

    private static async Task SetState(GenerationRunnerTests.Fixture fixture, JobState requested)
    {
        await using var db = await fixture.Open();
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await db.Jobs.GetAsync((await fixture.Job()).Id, Token);
        if (requested == JobState.Paused)
            await db.Jobs.RequestStateAsync(job.Id, job.Revision, "pause", JobControlCommand.Pause, Token);
        else if (requested != JobState.Queued)
        {
            var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
            await File.WriteAllBytesAsync(db.ResolveOwnedPath(claim.FinalRelativePath + ".part"), [1, 2, 3], Token);
            if (requested == JobState.Interrupted) await lease.InterruptGenerationUnitRetainingStagingAsync(claim, "project.interrupted", Token);
            else if (requested == JobState.Running) await lease.ReportGenerationProgressAsync(claim, "generating", Token);
            else if (requested is JobState.PauseRequested or JobState.CancelRequested)
            {
                job = await db.Jobs.GetAsync(job.Id, Token);
                await db.Jobs.RequestStateAsync(job.Id, job.Revision, "control", requested == JobState.PauseRequested ? JobControlCommand.Pause : JobControlCommand.Cancel, Token);
            }
        }
    }

    private sealed class UnreadableContent : MemoryStream
    {
        public int ReadCount { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { ReadCount++; throw new IOException("SYNTHETIC_CONTENT_MUST_NOT_BE_READ"); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { ReadCount++; throw new IOException("SYNTHETIC_CONTENT_MUST_NOT_BE_READ"); }
    }

    private sealed class OnSecondFindCatalog(IProjectCatalog inner, Func<Task> afterEarlyRead) : IProjectCatalog
    {
        private int finds;
        public Task<IReadOnlyList<CatalogProject>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public async Task<CatalogProject?> FindAsync(string projectId, CancellationToken cancellationToken = default)
        {
            var result = await inner.FindAsync(projectId, cancellationToken);
            // Registered current projects need no catalog reinsertion. Race after
            // the early snapshot, before the subsequent validated current open.
            if (Interlocked.Increment(ref finds) == 2) await afterEarlyRead();
            return result;
        }
        public Task InsertAsync(CatalogProject project, CancellationToken cancellationToken = default) => inner.InsertAsync(project, cancellationToken);
        public Task<IdempotencyReceipt?> FindReceiptAsync(string key, CancellationToken cancellationToken = default) => inner.FindReceiptAsync(key, cancellationToken);
        public Task StoreReceiptAsync(IdempotencyReceipt receipt, CancellationToken cancellationToken = default) => inner.StoreReceiptAsync(receipt, cancellationToken);
        public Task TouchOpenedAsync(string projectId, DateTimeOffset openedUtc, CancellationToken cancellationToken = default) => inner.TouchOpenedAsync(projectId, openedUtc, cancellationToken);
        public Task<IReadOnlyList<CatalogCandidate>> ReconcileAsync(CancellationToken cancellationToken = default) => inner.ReconcileAsync(cancellationToken);
        public Task FinalizeAsync(CatalogProject project, IdempotencyReceipt? receipt, CancellationToken cancellationToken = default) => inner.FinalizeAsync(project, receipt, cancellationToken);
        public Task QuarantineAsync(string projectId, CancellationToken cancellationToken = default) => inner.QuarantineAsync(projectId, cancellationToken);
    }

    [Fact]
    public async Task Changed_registered_identity_is_rejected_before_owned_legacy_migration()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(); var job = await fixture.Job();
        await fixture.Jobs.RequestStateAsync(fixture.Id, job.Id, job.Revision, "cancel", JobControlCommand.Cancel, Token);
        string root = fixture.Box.ProjectRoot(fixture.Id), main = Path.Combine(root, "project.sqlite");
        CatalogSandbox.Sql(root, "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs; PRAGMA user_version=2; PRAGMA journal_mode=DELETE");
        byte[]? raced = null; int inserts = 0;
        var changing = new OnEachInsertCatalog(fixture.Box.Catalog(), () =>
        {
            if (++inserts == 2)
            {
                string replacement = Guid.NewGuid().ToString("N");
                CatalogSandbox.Sql(root, $"UPDATE project SET id='{replacement}',payload=json_set(payload,'$.Id','{replacement}')");
                raced = File.ReadAllBytes(main);
            }
            return Task.CompletedTask;
        });
        var projects = new StudioProjectService(new(fixture.Box.Root), changing, new ProjectService());
        Assert.Equal("project.unavailable", (await Assert.ThrowsAsync<StudioException>(() => new StudioTextService(projects).UpdateSpokenTextAsync(
            fixture.Id, fixture.Prepared.Segments[0].Id, fixture.Prepared.Revision, new("Изменение."), Token))).Code);
        Assert.NotNull(raced); Assert.Equal(raced, await File.ReadAllBytesAsync(main, Token));
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
    }

    private sealed class OnEachInsertCatalog(IProjectCatalog inner, Func<Task> onInsert) : IProjectCatalog
    {
        public Task<IReadOnlyList<CatalogProject>> ListAsync(CancellationToken token = default) => inner.ListAsync(token);
        public Task<CatalogProject?> FindAsync(string id, CancellationToken token = default) => inner.FindAsync(id, token);
        public async Task InsertAsync(CatalogProject project, CancellationToken token = default) { await inner.InsertAsync(project, token); await onInsert(); }
        public Task<IdempotencyReceipt?> FindReceiptAsync(string key, CancellationToken token = default) => inner.FindReceiptAsync(key, token);
        public Task StoreReceiptAsync(IdempotencyReceipt receipt, CancellationToken token = default) => inner.StoreReceiptAsync(receipt, token);
        public Task TouchOpenedAsync(string id, DateTimeOffset opened, CancellationToken token = default) => inner.TouchOpenedAsync(id, opened, token);
        public Task<IReadOnlyList<CatalogCandidate>> ReconcileAsync(CancellationToken token = default) => inner.ReconcileAsync(token);
        public Task FinalizeAsync(CatalogProject project, IdempotencyReceipt? receipt, CancellationToken token = default) => inner.FinalizeAsync(project, receipt, token);
        public Task QuarantineAsync(string id, CancellationToken token = default) => inner.QuarantineAsync(id, token);
    }
}

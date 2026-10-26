using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Application.Tests;

public class StudioJobServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Enqueue_captures_Core_order_and_only_runtime_pins_without_private_DTO_content()
    {
        using var box = new CatalogSandbox(); var prepared = await Prepare(box); var runtime = new Runtime(); var jobs = Service(box, runtime);
        var result = await jobs.EnqueueAsync(prepared.Metadata.Id, prepared.Revision, "enqueue", new(JobKind.Generation, prepared.Segments.Reverse().Select(x => x.Id).ToArray()), Token);
        Assert.Equal(JobState.Queued, result.State); Assert.Equal(2, result.TotalUnits); Assert.Equal(1, result.Revision); Assert.Equal(1, runtime.Calls);
        await using var db = await new ProjectService().OpenCurrentAsync(box.ProjectRoot(prepared.Metadata.Id), Token);
        var stored = await db.Jobs.GetAsync(result.Id, Token);
        Assert.Equal(prepared.Segments.Select(x => x.Id), stored.Input.Items.Select(x => x.SegmentId));
        Assert.Equal("", stored.Input.Items[0].Versions.App); Assert.Equal("", stored.Input.Items[0].Versions.Policy);
        Assert.Equal(prepared.Metadata.Versions.TextFrontend, stored.Input.Items[0].Versions.TextFrontend);
        Assert.Equal(runtime.Value.Versions.Worker, stored.Input.Items[0].Versions.Worker);
        string json = JsonSerializer.Serialize(result);
        foreach (string value in new[] { "PRIVATE_SYNTHETIC_TEXT", "PRIVATE_SYNTHETIC_TRANSCRIPT", "voice-snapshot", box.Root }) Assert.DoesNotContain(value, json);
    }

    [Fact]
    public async Task Replay_precedes_runtime_stale_revision_and_artifact_ownership()
    {
        using var box = new CatalogSandbox(); var prepared = await Prepare(box); var runtime = new Runtime(); var jobs = Service(box, runtime);
        var request = new GenerationRequest(JobKind.Generation, prepared.Segments.Select(x => x.Id).ToArray());
        var first = await jobs.EnqueueAsync(prepared.Metadata.Id, prepared.Revision, "same", request, Token);
        runtime.Ready = false;
        await using var db = await new ProjectService().OpenCurrentAsync(box.ProjectRoot(prepared.Metadata.Id), Token);
        await using var ownership = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var replay = await jobs.EnqueueAsync(prepared.Metadata.Id, -1, "same", request with { SegmentIds = request.SegmentIds.Reverse().ToArray() }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(first, replay); Assert.Equal(1, runtime.Calls);
        var conflict = await Assert.ThrowsAsync<StudioException>(() => jobs.EnqueueAsync(prepared.Metadata.Id, -1, "same", new(JobKind.Preview, [request.SegmentIds[0]]), Token));
        Assert.Equal("job.idempotency.conflict", conflict.Code); Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task Cancel_commits_while_inference_holds_lease_and_studio_lock()
    {
        using var box = new CatalogSandbox(); var prepared = await Prepare(box); var runtime = new Runtime(); var jobs = Service(box, runtime);
        var job = await jobs.EnqueueAsync(prepared.Metadata.Id, prepared.Revision, "enqueue", new(JobKind.Generation, prepared.Segments.Select(x => x.Id).ToArray()), Token);
        await using var db = await new ProjectService().OpenCurrentAsync(box.ProjectRoot(prepared.Metadata.Id), Token);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var stored = await db.Jobs.GetAsync(job.Id, Token);
        Assert.NotNull(await lease.BeginGenerationUnitAsync(job.Id, prepared.Segments[0].Id, stored.Input.Preset, Token));
        using var studioLock = new FileStream(Path.Combine(box.Root, "studio.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var before = await jobs.GetAsync(prepared.Metadata.Id, job.Id, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        var canceled = await jobs.RequestStateAsync(prepared.Metadata.Id, job.Id, before.Revision, "cancel", JobControlCommand.Cancel, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(JobState.CancelRequested, canceled.State); Assert.Equal(JobRequestedState.Cancel, canceled.RequestedState);
        Assert.Equal(before.Revision + 1, canceled.Revision);
        Assert.Equal(db.State.Revision, (await new ProjectService().ProbeAsync(box.ProjectRoot(prepared.Metadata.Id), Token)).Revision);
        Assert.Equal(canceled, await jobs.RequestStateAsync(prepared.Metadata.Id, job.Id, -1, "cancel", JobControlCommand.Cancel, Token));
    }

    [Fact]
    public async Task Current_job_reads_preserve_claim_and_atomic_event_baseline()
    {
        using var box = new CatalogSandbox(); var prepared = await Prepare(box); var jobs = Service(box, new());
        var job = await jobs.EnqueueAsync(prepared.Metadata.Id, prepared.Revision, "enqueue", new(JobKind.Preview, [prepared.Segments[0].Id]), Token);
        await using var db = await new ProjectService().OpenCurrentAsync(box.ProjectRoot(prepared.Metadata.Id), Token);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var stored = await db.Jobs.GetAsync(job.Id, Token);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, prepared.Segments[0].Id, stored.Input.Preset, Token))!;
        var page = await jobs.ReadEventPageAsync(prepared.Metadata.Id, null, Token);
        Assert.Equal(JobState.Preparing, Assert.Single(page.Jobs).State); Assert.Equal(2, page.HeadSequence);
        Assert.Equal(claim.Token, (await db.Jobs.GetAsync(job.Id, Token)).ClaimToken);
        Assert.Equal(0, (await db.Jobs.GetAsync(job.Id, Token)).CompletedUnits);
        var replayPage = await jobs.ReadEventPageAsync(prepared.Metadata.Id, 0, Token);
        Assert.Equal(replayPage.HeadSequence, replayPage.Events[^1].Sequence);
        Assert.Equal(replayPage.Jobs.Single().Revision, replayPage.Events[^1].JobRevision);
        Assert.True((await jobs.ReadEventPageAsync(prepared.Metadata.Id, 99, Token)).Reset);
    }

    [Fact]
    public async Task New_enqueue_with_missing_runtime_or_unknown_segment_does_not_create_jobs()
    {
        using var box = new CatalogSandbox(); var prepared = await Prepare(box); var runtime = new Runtime { Ready = false }; var jobs = Service(box, runtime);
        var error = await Assert.ThrowsAsync<StudioException>(() => jobs.EnqueueAsync(prepared.Metadata.Id, prepared.Revision, "no-runtime", new(JobKind.Preview, [prepared.Segments[0].Id]), Token));
        Assert.Equal("worker.runtime.not_ready", error.Code); Assert.Empty(await jobs.ListAsync(prepared.Metadata.Id, Token));
        runtime.Ready = true;
        Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<StudioException>(() => jobs.EnqueueAsync(prepared.Metadata.Id, prepared.Revision, "unknown", new(JobKind.Preview, [Guid.NewGuid().ToString("N")]), Token))).Code);
        Assert.Empty(await jobs.ListAsync(prepared.Metadata.Id, Token));
    }

    [Fact]
    public async Task Enqueue_freezes_request_IDs_before_async_runtime_validation()
    {
        using var box = new CatalogSandbox(); var prepared = await Prepare(box);
        var supplied = prepared.Segments.Select(x => x.Id).ToList();
        var runtime = new Runtime { OnGet = () => supplied.RemoveAt(1) };
        var jobs = Service(box, runtime);
        var job = await jobs.EnqueueAsync(prepared.Metadata.Id, prepared.Revision, "immutable-request", new(JobKind.Generation, supplied), Token);
        Assert.Single(supplied); Assert.Equal(2, job.TotalUnits);
        Assert.Equal(job, await jobs.EnqueueAsync(prepared.Metadata.Id, -1, "immutable-request", new(JobKind.Generation, prepared.Segments.Select(x => x.Id).ToArray()), Token));
    }

    internal static StudioJobService Service(CatalogSandbox box, Runtime runtime) => new(new ProjectJobAccess(new(box.Root), box.Catalog(), new ProjectService()), runtime);
    internal sealed class Runtime : IRuntimeDescriptor
    {
        public bool Ready { get; set; } = true; public int Calls { get; private set; }
        public Action? OnGet { get; init; }
        public RuntimeDescriptor Value { get; } = new(new("voxcpm2-local-v1", "q8-f16-cpu", 70, 10, 2, 1, 42, false), new("IGNORED_APP", new string('1', 40), new string('2', 40), "IGNORED_POLICY", "IGNORED_FRONTEND", "IGNORED_SEGMENTER"));
        public Task<RuntimeDescriptor> GetAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; OnGet?.Invoke(); if (!Ready) throw new WorkerClientException("worker.runtime.not_ready", true); return Task.FromResult(Value); }
    }
    internal static async Task<ProjectState> Prepare(CatalogSandbox box, string? voiceHash = null, string? creationKey = null)
    {
        var created = (await box.Service().CreateAsync(new("Synthetic"), creationKey ?? Guid.NewGuid().ToString("N"), Token)).Snapshot;
        await using var db = await new ProjectService().OpenAsync(box.ProjectRoot(created.Id), Token, retainSidecars: true);
        var now = DateTimeOffset.UtcNow; var pins = new VersionPins("", "", "", "", "frontend-v1", "segmenter-v1");
        string voiceId = Guid.NewGuid().ToString("N"), recordingId = Guid.NewGuid().ToString("N"), chapterId = Guid.NewGuid().ToString("N");
        const string transcript = "PRIVATE_SYNTHETIC_TRANSCRIPT";
        var voice = new VoiceSnapshot(voiceId, $"voice-snapshot/{voiceId}.wav", transcript, voiceHash ?? new string('A', 64), Hash(transcript).ToUpperInvariant(), new string('B', 64), true, now, 0, 10000);
        var chapter = new ChapterRecord(chapterId, 0, "Synthetic", "PRIVATE_SYNTHETIC_TEXT", "PRIVATE_SYNTHETIC_TEXT", "PRIVATE_SYNTHETIC_TEXT", now, new(TextSourceKind.Pasted, "Synthetic", "utf-8", Hash("synthetic"), 9, pins.TextFrontend, pins.Segmenter));
        var segments = Enumerable.Range(0, 2).Select(i => new SegmentRecord(Guid.NewGuid().ToString("N"), chapterId, i, "PRIVATE_SYNTHETIC_TEXT", "PRIVATE_SYNTHETIC_TEXT", "PRIVATE_SYNTHETIC_TEXT", Hash("synthetic" + i), "", null, null, 42, pins, db.State.Metadata.Parameters, "queued", false, now, now, null, BoundaryAfter: SegmentBoundary.Sentence)).ToArray();
        await db.SaveAsync(db.State with { Metadata = db.State.Metadata with { Versions = pins }, Voice = voice, VoiceRecording = new(recordingId, $"voice-recording/{recordingId}.wav", new string('C', 64), 960044, 480000, false, true, now), Chapters = [chapter], Segments = segments }, Token);
        return db.State;
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

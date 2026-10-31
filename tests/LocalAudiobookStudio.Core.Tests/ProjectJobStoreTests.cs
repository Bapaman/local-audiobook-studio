using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Core.Tests;

public class ProjectJobStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Enqueue_persists_exact_input_and_same_key_replays_after_restart()
    {
        using var box = new ProjectSandbox(); string id; GenerationInput input;
        await using (var db = await Prepared(box))
        {
            input = Input(db); var mutable = input.Items.ToList(); input = input with { Items = mutable };
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, input, Token);
            id = job.Id; mutable.Clear(); Assert.Equal(2, job.Input.Items.Count);
            Assert.Equal(JobState.Queued, job.State); Assert.Equal(1, job.Revision);
            Assert.All(await db.Jobs.ItemsAsync(id, Token), x => Assert.False(x.CpuFallbackUsed));
            await lease.SaveAsync(db.State with { Metadata = db.State.Metadata with { UpdatedUtc = DateTimeOffset.UtcNow } }, Token);
        }
        await using var reopened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        var replay = await reopened.Jobs.TryReplayEnqueueAsync("key", JobKind.Generation, Input(reopened).Items.Select(x => x.SegmentId).Reverse().ToArray(), Token);
        Assert.Equal(id, replay!.Id); Assert.Equal(2, replay.Input.Items.Count); Assert.Equal("synthetic  \r\n", replay.Input.Voice.Transcript);
        Assert.Equal(1, Scalar(box, "SELECT COUNT(*) FROM jobs")); Assert.Equal(1, Scalar(box, "SELECT COUNT(*) FROM job_receipts"));
        Assert.DoesNotContain("key", (string)VoiceRecordingServiceTests.Sql(box.ProjectRoot, "SELECT key_hash FROM job_receipts")!);
        Assert.Equal(new string('1', 40), replay.Input.Items[0].Versions.Worker);
    }
    [Fact]
    public async Task Changed_request_conflicts()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); var input = Input(db);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, input, Token);
        Assert.Equal("job.idempotency.conflict", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.TryReplayEnqueueAsync("key", JobKind.Preview, [input.Items[0].SegmentId], Token))).Code);
        Assert.Equal("job.idempotency.conflict", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(lease, -1, "key", JobKind.Generation, input with { Items = [input.Items[0]] }, Token))).Code);
    }
    [Fact]
    public async Task Second_active_job_rejected()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, Input(db), Token);
        await db.Jobs.RequestStateAsync(job.Id, 1, "pause", JobControlCommand.Pause, Token);
        Assert.Equal("job.active", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(lease, db.State.Revision, "second", JobKind.Generation, Input(db), Token))).Code);
    }
    [Fact]
    public async Task Control_changes_job_revision_not_project_revision()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); long revision = db.State.Revision;
        JobRecord job; await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token)) job = await db.Jobs.EnqueueAsync(lease, revision, "key", JobKind.Generation, Input(db), Token);
        job = await db.Jobs.RequestStateAsync(job.Id, 1, "pause", JobControlCommand.Pause, Token); Assert.Equal(JobState.Paused, job.State); Assert.Equal(2, job.Revision);
        var replay = await db.Jobs.RequestStateAsync(job.Id, 1, "pause", JobControlCommand.Pause, Token); Assert.Equal(job.Id, replay.Id); Assert.Equal(job.Revision, replay.Revision); Assert.Equal(job.Input.Items, replay.Input.Items);
        Assert.Equal("job.conflict", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.RequestStateAsync(job.Id, 1, "stale", JobControlCommand.Resume, Token))).Code);
        job = await db.Jobs.RequestStateAsync(job.Id, 2, "resume", JobControlCommand.Resume, Token); Assert.Equal(JobState.Queued, job.State);
        job = await db.Jobs.RequestStateAsync(job.Id, 3, "cancel", JobControlCommand.Cancel, Token); Assert.Equal(JobState.Canceled, job.State); Assert.NotNull(job.CompletedUtc);
        Assert.Equal("job.transition.invalid", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.RequestStateAsync(job.Id, 4, "resume2", JobControlCommand.Resume, Token))).Code);
        Assert.Equal(revision, Scalar(box, "SELECT revision FROM project"));
    }
    [Theory]
    [InlineData("PRAGMA user_version=4", "project.schema.future")]
    [InlineData("PRAGMA user_version=2", "project.schema.invalid")]
    [InlineData("UPDATE jobs SET payload=json_set(payload,'$.Phase','private arbitrary text')", "project.schema.invalid")]
    [InlineData("UPDATE job_items SET payload=json_set(payload,'$.CpuFallbackUsed',2)", "project.schema.invalid")]
    [InlineData("UPDATE jobs SET payload=json_set(payload,'$.Input.Preset.AllowCpuFallback',2)", "project.schema.invalid")]
    [InlineData("UPDATE jobs SET payload=json_set(payload,'$.State',999)", "project.schema.invalid")]
    [InlineData("UPDATE jobs SET input_hash='bad'", "project.schema.invalid")]
    [InlineData("UPDATE sqlite_sequence SET seq=-1 WHERE name='job_events'", "project.schema.invalid")]
    [InlineData("UPDATE sqlite_sequence SET seq='bad' WHERE name='job_events'", "project.schema.invalid")]
    [InlineData("UPDATE sqlite_sequence SET seq=0 WHERE name='job_events'", "project.schema.invalid")]
    [InlineData("DELETE FROM sqlite_sequence WHERE name='job_events'", "project.schema.invalid")]
    [InlineData("UPDATE job_receipts SET request_hash='bad'", "project.schema.invalid")]
    [InlineData("UPDATE job_events SET payload=json_set(payload,'$.DiagnosticCode','arbitrary')", "project.schema.invalid")]
    [InlineData("UPDATE job_items SET payload=json_remove(payload,'$.CpuFallbackUsed')", "project.schema.invalid")]
    [InlineData("UPDATE jobs SET payload=json_remove(payload,'$.Input.Preset.AllowCpuFallback')", "project.schema.invalid")]
    [InlineData("INSERT INTO job_events(sequence,job_id,job_revision,payload) SELECT 0,id,1,'invalid-json' FROM jobs", "project.schema.invalid")]
    [InlineData("INSERT INTO job_events(sequence,job_id,job_revision,payload) SELECT -1,job_id,job_revision,json_set(payload,'$.Sequence',-1,'$.DiagnosticCode','arbitrary') FROM job_events WHERE sequence=1", "project.schema.invalid")]
    public async Task Future_or_poisoned_jobs_preserve_source_bytes(string sql, string code)
    {
        using var box = new ProjectSandbox();
        await using (var db = await Prepared(box)) { await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, Input(db), Token); }
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "PRAGMA journal_mode=DELETE; " + sql);
        string path = Path.Combine(box.ProjectRoot, "project.sqlite"); byte[] before = File.ReadAllBytes(path); var files = Directory.GetFiles(box.ProjectRoot).Order().ToArray();
        Assert.Equal(code, (await Assert.ThrowsAsync<ProjectException>(async () => { await using var opened = await new ProjectService().OpenAsync(box.ProjectRoot, Token); })).Code);
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(files, Directory.GetFiles(box.ProjectRoot).Order().ToArray());
    }
    [Fact]
    public async Task V2_recording_survives_v3()
    {
        using var box = new ProjectSandbox(); ProjectState original;
        await using (var db = await Prepared(box)) original = db.State;
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs; PRAGMA user_version=2");
        await using var opened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        Assert.Equal(original.VoiceRecording, opened.State.VoiceRecording); Assert.Equal(original.Voice, opened.State.Voice); Assert.Equal(original.Segments, opened.State.Segments); Assert.Empty(await opened.Jobs.ListAsync(Token));
        Assert.Equal(3, Scalar(box, "PRAGMA user_version"));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Accepted_uppercase_voice_hashes_survive_but_consistent_lowercase_historical_input_is_rejected(int hashField)
    {
        using var box = new ProjectSandbox(); JobRecord original; IReadOnlyList<JobItemRecord> items;
        await using (var db = await Prepared(box))
        {
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            original = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, Input(db), Token);
            items = await db.Jobs.ItemsAsync(original.Id, Token);
        }
        await using (var opened = await new ProjectService().OpenAsync(box.ProjectRoot, Token))
        {
            var captured = (await opened.Jobs.GetAsync(original.Id, Token)).Input.Voice;
            Assert.Equal(original.Input.Voice, captured);
            Assert.Matches("^[A-F0-9]{64}$", captured.WavHash); Assert.Matches("^[A-F0-9]{64}$", captured.TranscriptHash); Assert.Matches("^[A-F0-9]{64}$", captured.IdentityHash);
        }
        var voice = original.Input.Voice;
        voice = hashField switch { 0 => voice with { WavHash = voice.WavHash.ToLowerInvariant() }, 1 => voice with { TranscriptHash = voice.TranscriptHash.ToLowerInvariant() }, _ => voice with { IdentityHash = voice.IdentityHash.ToLowerInvariant() } };
        var input = original.Input with { Voice = voice };
        var poisoned = original with { Input = input, InputHash = GenerationFingerprint.ForInput(input) };
        string sql = "PRAGMA journal_mode=DELETE; UPDATE jobs SET input_hash='" + poisoned.InputHash + "',payload='" + System.Text.Json.JsonSerializer.Serialize(poisoned).Replace("'", "''") + "';";
        foreach (var item in items)
        {
            var bound = input.Items.Single(x => x.SegmentId == item.SegmentId);
            var changed = item with { EffectiveFingerprint = GenerationFingerprint.ForItem(input, bound, input.Preset) };
            sql += "UPDATE job_items SET payload='" + System.Text.Json.JsonSerializer.Serialize(changed).Replace("'", "''") + "' WHERE segment_id='" + item.SegmentId + "';";
        }
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, sql);
        // The persisted job/item fingerprints match the altered input: rejection must be representation validation.
        Assert.Equal(poisoned.InputHash, (string)VoiceRecordingServiceTests.Sql(box.ProjectRoot, "SELECT input_hash FROM jobs")!);
        foreach (var item in input.Items) Assert.Equal(GenerationFingerprint.ForItem(input, item, input.Preset), (string)VoiceRecordingServiceTests.Sql(box.ProjectRoot, "SELECT json_extract(payload,'$.EffectiveFingerprint') FROM job_items WHERE segment_id='" + item.SegmentId + "'")!);
        string path = Path.Combine(box.ProjectRoot, "project.sqlite"); byte[] before = File.ReadAllBytes(path); var files = Directory.GetFiles(box.ProjectRoot).Order().ToArray();
        Assert.Equal("project.schema.invalid", (await Assert.ThrowsAsync<ProjectException>(async () => { await using var opened = await new ProjectService().OpenAsync(box.ProjectRoot, Token); })).Code);
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(files, Directory.GetFiles(box.ProjectRoot).Order().ToArray());
    }
    [Fact]
    public async Task Fingerprint_changes_for_each_bound_input_and_is_locale_stable()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); var input = Input(db); var item = input.Items[0]; string baseline = GenerationFingerprint.ForItem(input, item, input.Preset);
        var variants = new[] { item with { SegmentId = Guid.NewGuid().ToString("N") }, item with { Order = 99 }, item with { SpokenText = "changed" }, item with { TextHash = Digest("changed") }, item with { PreviousSentenceContextHash = Digest("previous") }, item with { NextSentenceContextHash = Digest("next") }, item with { BoundaryAfter = SegmentBoundary.Paragraph }, item with { Versions = item.Versions with { Worker = new string('3', 40) } }, item with { Versions = item.Versions with { Model = new string('4', 40) } }, item with { Versions = item.Versions with { App = "other" } }, item with { Versions = item.Versions with { Policy = "other" } }, item with { Versions = item.Versions with { TextFrontend = "other" } }, item with { Versions = item.Versions with { Segmenter = "other" } } };
        Assert.All(variants, x => Assert.NotEqual(baseline, GenerationFingerprint.ForItem(input, x, input.Preset)));
        Assert.NotEqual(baseline, GenerationFingerprint.ForItem(input with { Voice = input.Voice with { Transcript = "changed" } }, item, input.Preset));
        Assert.NotEqual(baseline, GenerationFingerprint.ForItem(input, item, input.Preset with { Seed = 43 }));
        Assert.NotEqual(baseline, GenerationFingerprint.ForItem(input, item, input.Preset with { ProfileId = "q8-f16-hybrid-16" }));
        Assert.Equal(baseline, GenerationFingerprint.ForItem(input with { ProjectRevision = 99, Voice = input.Voice with { WavRelativePath = "voice-snapshot/else.wav", ImportedUtc = DateTimeOffset.UtcNow } }, item, input.Preset));
        Assert.NotEqual(GenerationFingerprint.ForInput(input), GenerationFingerprint.ForInput(input with { ProjectRevision = 99 }));
        var previous = CultureInfo.CurrentCulture; try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU"); Assert.Equal(baseline, GenerationFingerprint.ForItem(input, item, input.Preset)); } finally { CultureInfo.CurrentCulture = previous; }
        Assert.Matches("^[a-f0-9]{64}$", baseline);
    }
    [Fact]
    public async Task Events_commit_with_job_and_retain_latest_256()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); JobRecord job;
        await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token)) job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, Input(db), Token);
        for (int i = 0; i < 260; i++) job = await db.Jobs.RequestStateAsync(job.Id, job.Revision, "control" + i, i % 2 == 0 ? JobControlCommand.Pause : JobControlCommand.Resume, Token);
        var events = await db.Jobs.ReadEventsAsync(0, Token); Assert.Equal(256, events.Count); Assert.Equal(6, events[0].Sequence); Assert.Equal(261, events[^1].Sequence);
        Assert.Equal(JobState.Paused, events[0].State); Assert.Equal("paused", events[0].Phase); Assert.Equal(JobState.Queued, events[^1].State); Assert.Equal(job.Revision, events[^1].JobRevision);
        Assert.Equal(261, await db.Jobs.GetEventSequenceAsync(Token));
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "DELETE FROM job_events"); Assert.Equal(261, await db.Jobs.GetEventSequenceAsync(Token));
    }
    [Fact]
    public async Task Replay_does_not_wait_for_inference_lease_or_rebuild_input()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); await using var second = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); var input = Input(db);
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, input, Token);
        var replay = await second.Jobs.TryReplayEnqueueAsync("key", JobKind.Generation, input.Items.Select(x => x.SegmentId).ToArray(), Token).WaitAsync(TimeSpan.FromSeconds(2), Token); Assert.Equal(job.Id, replay!.Id);
        var paused = await second.Jobs.RequestStateAsync(job.Id, 1, "pause", JobControlCommand.Pause, Token).WaitAsync(TimeSpan.FromSeconds(2), Token); Assert.Equal(JobState.Paused, paused.State);
    }
    [Fact]
    public async Task Event_page_is_atomic_and_resets_old_future_cursor()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box);
        var empty = await db.Jobs.ReadEventPageAsync(null, Token); Assert.True(empty.Reset); Assert.Equal(0, empty.HeadSequence); Assert.Equal(0, empty.FirstRetainedSequence);
        JobRecord job; await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token)) job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, Input(db), Token);
        var page = await db.Jobs.ReadEventPageAsync(0, Token); Assert.False(page.Reset); Assert.Equal(1, page.HeadSequence); Assert.Equal(page.Jobs.Single().Revision, page.Events.Single().JobRevision);
        Assert.True((await db.Jobs.ReadEventPageAsync(2, Token)).Reset);
        for (int i = 0; i < 256; i++) job = await db.Jobs.RequestStateAsync(job.Id, job.Revision, "k" + i, i % 2 == 0 ? JobControlCommand.Pause : JobControlCommand.Resume, Token);
        Assert.True((await db.Jobs.ReadEventPageAsync(0, Token)).Reset); Assert.False((await db.Jobs.ReadEventPageAsync(1, Token)).Reset);
    }
    [Fact]
    public async Task Legacy_migration_waits_for_artifact_ownership()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box);
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs; PRAGMA user_version=2");
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token); canceled.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProjectService().OpenAsync(box.ProjectRoot, canceled.Token)); Assert.Equal(2, Scalar(box, "PRAGMA user_version"));
    }
    [Theory]
    [InlineData(JobState.Queued, JobControlCommand.Resume, JobState.Queued, false)]
    [InlineData(JobState.Preparing, JobControlCommand.Pause, JobState.PauseRequested, true)]
    [InlineData(JobState.Preparing, JobControlCommand.Cancel, JobState.CancelRequested, true)]
    [InlineData(JobState.Running, JobControlCommand.Resume, JobState.Running, null)]
    [InlineData(JobState.Running, JobControlCommand.Pause, JobState.PauseRequested, true)]
    [InlineData(JobState.Running, JobControlCommand.Cancel, JobState.CancelRequested, true)]
    [InlineData(JobState.PauseRequested, JobControlCommand.Pause, JobState.PauseRequested, false)]
    [InlineData(JobState.PauseRequested, JobControlCommand.Resume, JobState.PauseRequested, null)]
    [InlineData(JobState.PauseRequested, JobControlCommand.Cancel, JobState.CancelRequested, true)]
    [InlineData(JobState.Paused, JobControlCommand.Pause, JobState.Paused, false)]
    [InlineData(JobState.Interrupted, JobControlCommand.Pause, JobState.Paused, true)]
    [InlineData(JobState.Interrupted, JobControlCommand.Resume, JobState.Queued, true)]
    [InlineData(JobState.Interrupted, JobControlCommand.Cancel, JobState.Canceled, true)]
    [InlineData(JobState.CancelRequested, JobControlCommand.Pause, JobState.CancelRequested, null)]
    [InlineData(JobState.CancelRequested, JobControlCommand.Resume, JobState.CancelRequested, null)]
    [InlineData(JobState.CancelRequested, JobControlCommand.Cancel, JobState.CancelRequested, false)]
    [InlineData(JobState.Completed, JobControlCommand.Cancel, JobState.Completed, null)]
    [InlineData(JobState.Failed, JobControlCommand.Resume, JobState.Failed, null)]
    [InlineData(JobState.Canceled, JobControlCommand.Cancel, JobState.Canceled, false)]
    public async Task Control_matches_transition_table(JobState before, JobControlCommand command, JobState after, bool? changes)
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); JobRecord job;
        await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token)) job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, Input(db), Token);
        var requested = before is JobState.Paused or JobState.PauseRequested ? JobRequestedState.Pause : before is JobState.Canceled or JobState.CancelRequested ? JobRequestedState.Cancel : JobRequestedState.Run;
        var phase = before switch { JobState.Paused => "paused", JobState.Interrupted => "interrupted", JobState.Completed => "completed", JobState.Failed => "failed", JobState.Canceled => "canceled", JobState.Preparing => "preparing", JobState.Running or JobState.PauseRequested or JobState.CancelRequested => "generating", _ => "queued" };
        job = job with { State = before, RequestedState = requested, Phase = phase, ClaimToken = before is JobState.Preparing or JobState.Running or JobState.PauseRequested or JobState.CancelRequested ? new string('a', 32) : null, CompletedUtc = before is JobState.Completed or JobState.Failed or JobState.Canceled ? job.UpdatedUtc : null };
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "UPDATE jobs SET active=" + (job.CompletedUtc.HasValue ? "0" : "1") + ",payload='" + System.Text.Json.JsonSerializer.Serialize(job).Replace("'", "''") + "'");
        if (changes == null) { Assert.Equal("job.transition.invalid", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.RequestStateAsync(job.Id, 1, "control", command, Token))).Code); Assert.Equal(1, Scalar(box, "SELECT COUNT(*) FROM job_receipts")); }
        else { var result = await db.Jobs.RequestStateAsync(job.Id, 1, "control", command, Token); Assert.Equal(after, result.State); Assert.Equal(changes.Value ? 2 : 1, result.Revision); Assert.Equal(changes.Value ? 2 : 1, await db.Jobs.GetEventSequenceAsync(Token)); Assert.Equal(2, Scalar(box, "SELECT COUNT(*) FROM job_receipts")); }
    }
    [Fact]
    public async Task Receipt_replay_precedes_stale_revision_and_control_conflicts()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); var input = Input(db);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, input, Token);
        await lease.SaveAsync(db.State, Token);
        var replay = await db.Jobs.EnqueueAsync(lease, -1, "key", JobKind.Generation, input with { Voice = input.Voice with { Transcript = "changed" } }, Token); Assert.Equal(job.Id, replay.Id); Assert.Equal(input.Voice, replay.Input.Voice);
        var paused = await db.Jobs.RequestStateAsync(job.Id, 1, "pause", JobControlCommand.Pause, Token);
        await db.Jobs.RequestStateAsync(job.Id, 2, "resume", JobControlCommand.Resume, Token);
        var controlReplay = await db.Jobs.RequestStateAsync(job.Id, -1, "pause", JobControlCommand.Pause, Token); Assert.Equal(JobState.Paused, controlReplay.State); Assert.Equal(paused.Revision, controlReplay.Revision);
        Assert.Equal("job.idempotency.conflict", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.RequestStateAsync(job.Id, -1, "pause", JobControlCommand.Cancel, Token))).Code);
    }
    [Fact]
    public async Task Pruned_head_survives_restart_and_event_gap_is_rejected()
    {
        using var box = new ProjectSandbox();
        await using (var db = await Prepared(box))
        {
            JobRecord job; await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token)) job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, Input(db), Token);
            await db.Jobs.RequestStateAsync(job.Id, 1, "pause", JobControlCommand.Pause, Token);
        }
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "DELETE FROM job_events");
        await using (var opened = await new ProjectService().OpenAsync(box.ProjectRoot, Token))
        { Assert.Equal(2, await opened.Jobs.GetEventSequenceAsync(Token)); Assert.True((await opened.Jobs.ReadEventPageAsync(0, Token)).Reset); var job = (await opened.Jobs.ListAsync(Token)).Single(); await opened.Jobs.RequestStateAsync(job.Id, 2, "resume", JobControlCommand.Resume, Token); Assert.Equal(3, await opened.Jobs.GetEventSequenceAsync(Token)); }
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "UPDATE sqlite_sequence SET seq=4 WHERE name='job_events'");
        Assert.Equal("project.schema.invalid", (await Assert.ThrowsAsync<ProjectException>(() => new ProjectService().OpenAsync(box.ProjectRoot, Token))).Code);
    }
    [Fact]
    public async Task Preview_bounds_lease_owner_and_key_validation_roll_back()
    {
        using var box = new ProjectSandbox(); using var otherBox = new ProjectSandbox(); await using var db = await Prepared(box); await using var other = await Prepared(otherBox);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token); await using var otherLease = await other.AcquireArtifactLeaseAsync(cancellationToken: Token); var input = Input(db);
        Assert.Equal("project.lease.invalid", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(otherLease, db.State.Revision, "key", JobKind.Generation, input, Token))).Code);
        Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Preview, input, Token))).Code);
        foreach (string key in new[] { "", " ", "\n", new string('k', 257) }) Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(lease, db.State.Revision, key, JobKind.Preview, input with { Items = [input.Items[0]] }, Token))).Code);
        var preview = await db.Jobs.EnqueueAsync(lease, db.State.Revision, new string('k', 256), JobKind.Preview, input with { Items = [input.Items[0]] }, Token); Assert.Equal(1, preview.TotalUnits);
    }
    [Fact]
    public async Task Fingerprint_binds_all_voice_and_preset_fields()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); var input = Input(db); var item = input.Items[0]; string baseline = GenerationFingerprint.ForItem(input, item, input.Preset);
        var voices = new[] { input.Voice with { Id = new string('e', 32) }, input.Voice with { WavHash = new string('D', 64) }, input.Voice with { TranscriptHash = new string('D', 64) }, input.Voice with { IdentityHash = new string('D', 64) }, input.Voice with { RightsConfirmed = false }, input.Voice with { StartMilliseconds = 1 }, input.Voice with { EndMilliseconds = 10001 } };
        Assert.All(voices, voice => Assert.NotEqual(baseline, GenerationFingerprint.ForItem(input with { Voice = voice }, item, input.Preset)));
        var presets = new[] { input.Preset with { Id = "other" }, input.Preset with { Steps = 71 }, input.Preset with { Timesteps = 11 }, input.Preset with { Cfg = 2.1 }, input.Preset with { Temperature = 1.1 }, input.Preset with { AllowCpuFallback = false } }; Assert.All(presets, preset => Assert.NotEqual(baseline, GenerationFingerprint.ForItem(input, item, preset)));
    }
    [Fact]
    public async Task New_enqueue_captures_core_order_without_mutating_caller_and_read_rejects_poisoned_order()
    {
        using var box = new ProjectSandbox();
        await using (var db = await Prepared(box))
        {
            var ordered = Input(db); var reversed = ordered.Items.Reverse().ToList(); var supplied = ordered with { Items = reversed };
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, supplied, Token);
            Assert.Equal(new[] { 1, 2 }, job.Input.Items.Select(x => x.Order)); Assert.Equal(new[] { 2, 1 }, reversed.Select(x => x.Order)); Assert.Equal(GenerationFingerprint.ForInput(ordered), job.InputHash);
        }
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "UPDATE jobs SET payload=json_set(payload,'$.Input.Items[0].Order',3)");
        Assert.Equal("project.schema.invalid", (await Assert.ThrowsAsync<ProjectException>(async () => { await using var opened = await new ProjectService().OpenAsync(box.ProjectRoot, Token); })).Code);
    }
    [Fact]
    public async Task Event_page_remains_consistent_with_independent_control_connection()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); await using var controller = await new ProjectService().OpenAsync(box.ProjectRoot, Token); JobRecord job;
        await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token)) job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, Input(db), Token);
        var writes = Task.Run(async () => { for (int i = 0; i < 100; i++) job = await controller.Jobs.RequestStateAsync(job.Id, job.Revision, "control" + i, i % 2 == 0 ? JobControlCommand.Pause : JobControlCommand.Resume, Token); }, Token);
        for (int i = 0; i < 100; i++)
        {
            var page = await db.Jobs.ReadEventPageAsync(0, Token); var current = page.Jobs.Single(); var last = page.Events[^1];
            Assert.Equal(page.HeadSequence, last.Sequence); Assert.Equal(current.Revision, last.JobRevision); Assert.Equal(current.State, last.State); Assert.Equal(1, page.FirstRetainedSequence);
        }
        await writes; Assert.Equal(101, await db.Jobs.GetEventSequenceAsync(Token));
    }
    [Fact]
    public async Task New_enqueue_rechecks_committed_revision_and_input_across_connections()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); await using var other = await new ProjectService().OpenAsync(box.ProjectRoot, Token); var input = Input(db);
        await other.SaveAsync(other.State, Token);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        Assert.Equal("project.conflict", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(lease, input.ProjectRevision, "stale", JobKind.Generation, input, Token))).Code);
        Assert.Equal("job.input_changed", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(lease, input.ProjectRevision + 1, "changed", JobKind.Generation, input, Token))).Code);
        Assert.Empty(await db.Jobs.ListAsync(Token)); Assert.Equal(0, Scalar(box, "SELECT COUNT(*) FROM job_receipts"));
    }
    [Fact]
    public async Task Prepared_project_empty_app_policy_are_captured_exactly_with_supplied_runtime_pins()
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box, emptyAppPolicy: true); var input = Input(db);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, input, Token);
        Assert.Equal("", job.Input.Items[0].Versions.App); Assert.Equal("", job.Input.Items[0].Versions.Policy); Assert.Equal(new string('1', 40), job.Input.Items[0].Versions.Worker);
        Assert.NotEqual(job.InputHash, GenerationFingerprint.ForInput(input with { Items = input.Items.Select(x => x with { Versions = x.Versions with { App = "invented" } }).ToArray() }));
        Assert.Equal("job.input_changed", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(lease, db.State.Revision, "other", JobKind.Generation, input with { Items = input.Items.Select(x => x with { Versions = x.Versions with { App = "invented" } }).ToArray() }, Token))).Code);
    }
    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public async Task New_enqueue_rejects_invalid_or_changed_bound_inputs(int variant)
    {
        using var box = new ProjectSandbox(); await using var db = await Prepared(box); var input = Input(db);
        input = variant switch { 0 => input with { Items = [] }, 2 => input with { Items = [input.Items[0], input.Items[0]] }, 3 => input with { Preset = input.Preset with { Cfg = double.NaN } }, 4 => input with { Items = [input.Items[0] with { SpokenText = "changed" }] }, 5 => input with { Voice = input.Voice with { Transcript = "changed" } }, 6 => input with { Items = [input.Items[0] with { Versions = input.Items[0].Versions with { Worker = "" } }] }, 7 => input with { Items = [input.Items[0] with { Versions = input.Items[0].Versions with { App = "other" } }] }, 8 => input with { Preset = input.Preset with { Steps = 1 } }, _ => input with { Items = Enumerable.Range(0, 10001).Select(_ => input.Items[0]).ToArray() } };
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(lease, db.State.Revision, "key", JobKind.Generation, input, Token)); Assert.Empty(await db.Jobs.ListAsync(Token)); Assert.Equal(0, await db.Jobs.GetEventSequenceAsync(Token));
    }
    private static long Scalar(ProjectSandbox box, string sql) => (long)VoiceRecordingServiceTests.Sql(box.ProjectRoot, sql)!;
    internal static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static async Task<ProjectDatabase> Prepared(ProjectSandbox box, bool emptyAppPolicy = false, bool legacyText = false)
    {
        var db = await new ProjectService().CreateAsync(box.ProjectRoot, "Synthetic", Token); var now = DateTimeOffset.UtcNow; var pins = new VersionPins(emptyAppPolicy ? "" : "app-v1", "", "", emptyAppPolicy ? "" : "policy-v1", "frontend-v1", "segmenter-v1");
        string voiceId = Guid.NewGuid().ToString("N"), recordingId = Guid.NewGuid().ToString("N"), chapterId = Guid.NewGuid().ToString("N"); string transcript = "synthetic  \r\n";
        var voice = new VoiceSnapshot(voiceId, $"voice-snapshot/{voiceId}.wav", transcript, new string('A', 64), Digest(transcript).ToUpperInvariant(), new string('B', 64), true, now, 0, 10000);
        var chapter = new ChapterRecord(chapterId, 1, "Synthetic", "one two", "one two", "one two", now, legacyText ? null : new(TextSourceKind.Pasted, "Synthetic", "utf-8", Digest("one two"), 7, pins.TextFrontend, pins.Segmenter));
        var segments = new[] { "one", "two" }.Select((text, i) => new SegmentRecord(Guid.NewGuid().ToString("N"), chapterId, i + 1, text, text, text, Digest(text), "", null, null, 42, pins, db.State.Metadata.Parameters, "queued", false, now, now, null, BoundaryAfter: SegmentBoundary.Sentence)).ToArray();
        await db.SaveAsync(db.State with { Metadata = db.State.Metadata with { Versions = pins }, Voice = voice, VoiceRecording = new(recordingId, $"voice-recording/{recordingId}.wav", new string('C', 64), 960044, 480000, false, true, now), Chapters = [chapter], Segments = segments }, Token); return db;
    }
    internal static GenerationInput Input(ProjectDatabase db) => new(db.State.Metadata.Id, db.State.Revision, db.State.Voice!, new("voxcpm2-local-v1", "q8-f16-cpu", 70, 10, 2, 1, 42, true), db.State.Segments.Select(x => new GenerationItemInput(x.Id, x.Order, x.SpokenText, x.TextHash, x.PreviousSentenceContextHash, x.NextSentenceContextHash, x.BoundaryAfter, x.Versions with { Worker = new string('1', 40), Model = new string('2', 40) })).ToArray());
}

using System.Security.Cryptography;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Application.Tests;

public class GenerationRunnerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Actual_prompt_tamper_fails_durable_claim_without_worker_launch()
    {
        await using var fixture = await Fixture.Create();
        await File.WriteAllBytesAsync(fixture.Prompt, [1, 2, 3], Token);
        Assert.True(await fixture.Runner.RunNextAsync(Token));
        var job = await fixture.Job();
        Assert.Equal(JobState.Failed, job.State); Assert.Equal("job.input_changed", job.DiagnosticCode);
        Assert.Equal(0, fixture.Workers.OpenCount); Assert.Equal(0, job.CompletedUnits);
        await using var db = await fixture.Open();
        Assert.Equal("failed", Assert.Single(db.State.Attempts).Status);
        Assert.Equal(fixture.Prepared.Revision + 2, db.State.Revision);
    }

    [Fact]
    public async Task Matching_prompt_hash_but_wrong_sample_count_still_prevents_launch()
    {
        await using var fixture = await Fixture.Create(promptSamples: 479999);
        Assert.True(await fixture.Runner.RunNextAsync(Token));
        Assert.Equal("job.input_changed", (await fixture.Job()).DiagnosticCode); Assert.Equal(0, fixture.Workers.OpenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_actual_output_or_misreported_hash_never_gets_accepted(bool tamperBytes)
    {
        await using var fixture = await Fixture.Create(preview: true);
        fixture.Workers.BadOutputHash = !tamperBytes; fixture.Workers.TamperOutputDuringCleanup = tamperBytes;
        fixture.Workers.Release.TrySetResult(); Assert.True(await fixture.Runner.RunNextAsync(Token));
        var job = await fixture.Job(); Assert.Equal(JobState.Failed, job.State); Assert.Equal("worker.output.invalid", job.DiagnosticCode);
        Assert.Equal(0, job.CompletedUnits);
        await using var db = await fixture.Open(); Assert.Null(Assert.Single(await db.Jobs.ItemsAsync(job.Id, Token)).Artifact);
        Assert.False(File.Exists(fixture.Workers.LastCommand!.FinalPath));
    }

    [Fact]
    public async Task Pause_accepts_current_and_starts_no_next_and_uses_actual_sample_count()
    {
        await using var fixture = await Fixture.Create();
        var running = fixture.Runner.RunNextAsync(Token);
        await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var before = await fixture.Job();
        var pause = await fixture.Jobs.RequestStateAsync(fixture.Id, before.Id, before.Revision, "pause", JobControlCommand.Pause, Token);
        Assert.Equal(JobState.PauseRequested, pause.State);
        fixture.Workers.Release.TrySetResult();
        Assert.True(await running);
        var paused = await fixture.Job();
        Assert.Equal(JobState.Paused, paused.State); Assert.Equal(1, paused.CompletedUnits);
        Assert.False(await fixture.Runner.RunNextAsync(Token)); Assert.Equal(1, fixture.Workers.OpenCount);
        await using var db = await fixture.Open();
        var items = await db.Jobs.ItemsAsync(paused.Id, Token);
        var artifact = items[0].Artifact!;
        Assert.Equal(4800, artifact.SampleCount); Assert.Equal(9644, artifact.Length);
        var inspected = await WavInspector.InspectAsync(db.ResolveOwnedPath(artifact.RelativePath), Token);
        Assert.Equal(inspected.Sha256.ToLowerInvariant(), artifact.Sha256);
        Assert.Equal(1, fixture.Workers.DisposeCount);
    }

    [Fact]
    public async Task Cancel_commits_while_inference_holds_lease_and_publication_winner_is_accepted()
    {
        await using var fixture = await Fixture.Create();
        var running = fixture.Runner.RunNextAsync(Token);
        await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var before = await fixture.Job();
        var cancel = await fixture.Jobs.RequestStateAsync(fixture.Id, before.Id, before.Revision, "cancel", JobControlCommand.Cancel, Token);
        Assert.Equal(JobState.CancelRequested, cancel.State);
        await fixture.Workers.CancelSeen.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(running.IsCompleted); // Cancellation/ACK is not a terminal result.
        fixture.Workers.Release.TrySetResult();
        Assert.True(await running);
        var result = await fixture.Job(); Assert.Equal(JobState.Canceled, result.State); Assert.Equal(1, result.CompletedUnits);
        Assert.False(await fixture.Runner.RunNextAsync(Token)); Assert.Equal(1, fixture.Workers.OpenCount);
    }

    [Fact]
    public async Task Prompt_identity_is_held_against_write_and_delete_until_worker_cleanup()
    {
        await using var fixture = await Fixture.Create();
        fixture.Workers.CheckPrompt = command =>
        {
            Assert.Equal(fixture.Prompt, command.PromptWav);
            Assert.Throws<IOException>(() => File.Open(command.PromptWav, FileMode.Open, FileAccess.Write, FileShare.Read));
            Assert.Throws<IOException>(() => File.Move(command.PromptWav, command.PromptWav + ".moved"));
        };
        fixture.Workers.Release.TrySetResult();
        Assert.True(await fixture.Runner.RunNextAsync(Token));
        using var writable = File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read);
        Assert.Equal(1, fixture.Workers.DisposeCount);
    }

    [Fact]
    public async Task Other_project_runs_after_busy_ownership_deferral_and_next_scan_recovers_lost_wakeup()
    {
        using var box = new CatalogSandbox();
        await using var first = await Fixture.Create(box);
        await using var second = await Fixture.Create(box);
        await using var db = await first.Open();
        await using var owner = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        first.Workers.Release.TrySetResult();
        Assert.True(await first.Runner.RunNextAsync(Token));
        Assert.Equal(0, (await first.Job()).CompletedUnits); Assert.Equal(1, (await second.Job()).CompletedUnits);
        await owner.DisposeAsync();
        Assert.True(await first.Runner.RunNextAsync(Token));
        Assert.Equal(1, (await first.Job()).CompletedUnits);
    }

    [Fact]
    public async Task Oom_consumes_one_jobwide_cpu_fallback_and_restart_inherits_actual_fingerprint()
    {
        await using var fixture = await Fixture.Create(hybrid: true);
        fixture.Workers.OomFirst = true; fixture.Workers.Release.TrySetResult();
        Assert.True(await fixture.Runner.RunNextAsync(Token));
        Assert.Equal(0, (await fixture.Job()).CompletedUnits);
        Assert.Equal(JobState.Queued, (await fixture.Job()).State);
        Assert.True(await fixture.Runner.RunNextAsync(Token));
        await fixture.Runner.StopAsync(Token);
        fixture.Runner = fixture.NewRunner();
        Assert.True(await fixture.Runner.RunNextAsync(Token));
        var completed = await fixture.Job(); Assert.Equal(JobState.Completed, completed.State);
        Assert.Equal(new[] { "q8-f16-hybrid-16", "q8-f16-cpu", "q8-f16-cpu" }, fixture.Workers.Profiles);
        await using var db = await fixture.Open();
        var stored = await db.Jobs.GetAsync(completed.Id, Token); var items = await db.Jobs.ItemsAsync(completed.Id, Token);
        Assert.All(items, item =>
        {
            Assert.True(item.CpuFallbackUsed); Assert.Equal("q8-f16-cpu", item.Artifact!.ProfileId);
            Assert.Equal(GenerationFingerprint.ForItem(stored.Input, stored.Input.Items.Single(x => x.SegmentId == item.SegmentId), stored.Input.Preset with { ProfileId = "q8-f16-cpu" }), item.EffectiveFingerprint);
        });
        Assert.Equal("q8-f16-hybrid-16", stored.Input.Preset.ProfileId); Assert.Equal(3, fixture.Workers.DisposeCount);
    }

    [Fact]
    public async Task Shutdown_preserves_interrupted_and_ownership_until_confirmed_worker_cleanup()
    {
        await using var fixture = await Fixture.Create();
        fixture.Workers.WritePendingAtStart = true; fixture.Workers.HoldCleanup = true;
        var running = fixture.Runner.RunNextAsync(Token);
        await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var stop = fixture.Runner.StopAsync(Token);
        await fixture.Workers.CancelSeen.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var interrupted = await fixture.Job(); Assert.Equal(JobState.Interrupted, interrupted.State);
        Assert.True(File.Exists(fixture.Workers.LastCommand!.FinalPath + ".part"));
        Assert.False(stop.IsCompleted); Assert.False(await fixture.Runner.RunNextAsync(Token));
        Assert.Throws<IOException>(() => File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read));
        await using (var db = await fixture.Open())
        {
            Assert.Null((await db.Jobs.GetAsync(interrupted.Id, Token)).ClaimToken);
            var error = await Assert.ThrowsAsync<ProjectException>(() => db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token));
            Assert.Equal("project.busy", error.Code);
        }
        fixture.Workers.Release.TrySetResult();
        await fixture.Workers.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(stop.IsCompleted); Assert.True(File.Exists(fixture.Workers.LastCommand.FinalPath));
        fixture.Workers.CleanupRelease.TrySetResult();
        Assert.True(await running); await stop;
        Assert.Equal(0, (await fixture.Job()).CompletedUnits);
        Assert.False(File.Exists(fixture.Workers.LastCommand.FinalPath));
        await using (var db = await fixture.Open())
        await using (var lease = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token)) { }
        fixture.Runner = fixture.NewRunner(); Assert.False(await fixture.Runner.RunNextAsync(Token));
    }

    [Fact]
    public async Task Only_one_unit_runs_and_canceling_a_caller_preserves_interrupted_job()
    {
        await using var fixture = await Fixture.Create(); using var cancel = new CancellationTokenSource();
        var running = fixture.Runner.RunNextAsync(cancel.Token);
        await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(await fixture.Runner.RunNextAsync(Token)); Assert.Equal(1, fixture.Workers.OpenCount);
        cancel.Cancel(); await fixture.Workers.CancelSeen.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(JobState.Interrupted, (await fixture.Job()).State);
        fixture.Workers.Release.TrySetResult(); Assert.True(await running);
        Assert.False(await fixture.Runner.RunNextAsync(Token)); Assert.Equal(0, (await fixture.Job()).CompletedUnits);
    }

    [Fact]
    public async Task Pause_on_last_item_and_resume_completes_without_new_inference_or_project_revision()
    {
        await using var fixture = await Fixture.Create(preview: true);
        var running = fixture.Runner.RunNextAsync(Token);
        await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var job = await fixture.Job(); await fixture.Jobs.RequestStateAsync(fixture.Id, job.Id, job.Revision, "pause", JobControlCommand.Pause, Token);
        fixture.Workers.Release.TrySetResult(); Assert.True(await running);
        job = await fixture.Job(); Assert.Equal(JobState.Paused, job.State); Assert.Equal(1, job.CompletedUnits);
        await using var db = await fixture.Open(); long projectRevision = db.State.Revision;
        var resumed = await fixture.Jobs.RequestStateAsync(fixture.Id, job.Id, job.Revision, "resume", JobControlCommand.Resume, Token);
        Assert.Equal(JobState.Completed, resumed.State); Assert.Equal(job.Revision + 1, resumed.Revision);
        Assert.Equal(resumed, await fixture.Jobs.RequestStateAsync(fixture.Id, job.Id, -1, "resume", JobControlCommand.Resume, Token));
        Assert.False(await fixture.Runner.RunNextAsync(Token)); Assert.Equal(1, fixture.Workers.OpenCount);
        await using var after = await fixture.Open(); Assert.Equal(projectRevision, after.State.Revision); Assert.Single(after.State.Attempts);
    }

    [Fact]
    public async Task Cpu_oom_is_terminal_after_one_fallback_not_an_automatic_retry_loop()
    {
        await using var fixture = await Fixture.Create(hybrid: true);
        fixture.Workers.OomEveryAttempt = true; fixture.Workers.Release.TrySetResult();
        Assert.True(await fixture.Runner.RunNextAsync(Token)); Assert.True(await fixture.Runner.RunNextAsync(Token));
        Assert.Equal(JobState.Failed, (await fixture.Job()).State); Assert.False(await fixture.Runner.RunNextAsync(Token));
        Assert.Equal(new[] { "q8-f16-hybrid-16", "q8-f16-cpu" }, fixture.Workers.Profiles);
        Assert.Equal(2, fixture.Workers.DisposeCount);
    }

    [Fact]
    public async Task Owned_recovery_preserves_accepted_history_and_requires_explicit_resume_of_orphan()
    {
        await using var fixture = await Fixture.Create(); fixture.Workers.Release.TrySetResult();
        Assert.True(await fixture.Runner.RunNextAsync(Token));
        string acceptedPath;
        await using (var db = await fixture.Open())
        await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
        {
            var job = await db.Jobs.GetAsync((await fixture.Job()).Id, Token);
            var items = await db.Jobs.ItemsAsync(job.Id, Token); acceptedPath = db.ResolveOwnedPath(items[0].Artifact!.RelativePath);
            var claim = (await lease.BeginGenerationUnitAsync(job.Id, items[1].SegmentId, job.Input.Preset, Token))!;
            await File.WriteAllBytesAsync(db.ResolveOwnedPath(claim.FinalRelativePath) + ".part", [1, 2, 3], Token);
        }
        await fixture.Runner.RecoverAsync(Token);
        var interrupted = await fixture.Job(); Assert.Equal(JobState.Interrupted, interrupted.State); Assert.Equal(1, interrupted.CompletedUnits);
        Assert.True(File.Exists(acceptedPath)); Assert.False(await fixture.Runner.RunNextAsync(Token));
        await fixture.Jobs.RequestStateAsync(fixture.Id, interrupted.Id, interrupted.Revision, "resume", JobControlCommand.Resume, Token);
        Assert.True(await fixture.Runner.RunNextAsync(Token)); Assert.Equal(2, fixture.Workers.OpenCount);
        Assert.Equal(JobState.Completed, (await fixture.Job()).State); Assert.True(File.Exists(acceptedPath));
    }

    [Fact]
    public async Task Scheduler_fault_cancels_and_waits_for_actual_cleanup_before_releasing_identity()
    {
        await using var fixture = await Fixture.Create(clock: new BrokenClock());
        fixture.Workers.HoldCleanup = true; fixture.Workers.WritePendingAtStart = true;
        var running = fixture.InFlight = fixture.Runner.RunNextAsync(Token);
        await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await fixture.Workers.CancelSeen.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(running.IsCompleted);
        Assert.Throws<IOException>(() => File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read));
        var stopFailure = await Assert.ThrowsAsync<WorkerClientException>(() => fixture.Runner.StopAsync(Token));
        fixture.StopFailureExpected = true;
        Assert.False(stopFailure.CleanupConfirmed); Assert.Equal("worker.cleanup", stopFailure.Code);
        fixture.Workers.Release.TrySetResult();
        await fixture.Workers.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(running.IsCompleted);
        fixture.Workers.CleanupRelease.TrySetResult();
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => running);
        Assert.Equal("worker.internal", error.Code); Assert.True(error.CleanupConfirmed); Assert.Null(error.InnerException);
        Assert.Equal(JobState.Interrupted, (await fixture.Job()).State);
        using var writable = File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read);
        fixture.StopFailureExpected = true;
    }

    private sealed class BrokenClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("PRIVATE_SYNTHETIC_TIMER_FAILURE");
    }

    [Fact]
    public async Task Valid_terminal_is_accepted_after_confirmed_cleanup_despite_shutdown_pipe_error()
    {
        await using var fixture = await Fixture.Create(preview: true);
        fixture.Workers.ErrorAfterConfirmedCleanup = true; fixture.Workers.Release.TrySetResult();
        Assert.True(await fixture.Runner.RunNextAsync(Token));
        var job = await fixture.Job(); Assert.Equal(JobState.Completed, job.State); Assert.Equal(1, job.CompletedUnits);
        await using var db = await fixture.Open();
        var artifact = Assert.Single(await db.Jobs.ItemsAsync(job.Id, Token)).Artifact!;
        Assert.Equal(4800, artifact.SampleCount); Assert.True(File.Exists(db.ResolveOwnedPath(artifact.RelativePath)));
    }

    [Fact]
    public async Task Total_shutdown_deadline_does_not_release_pending_ownership_or_claim_tree_exit()
    {
        var clock = new WorkerPipeClientTests.ManualClock();
        await using var fixture = await Fixture.Create(clock: clock);
        fixture.Workers.HoldCleanup = true; fixture.Workers.WritePendingAtStart = true;
        var running = fixture.InFlight = fixture.Runner.RunNextAsync(Token);
        await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var stop = fixture.Runner.StopAsync(Token);
        await clock.WaitForDelay(TimeSpan.FromSeconds(30), Token);
        clock.Advance(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => stop.WaitAsync(TimeSpan.FromSeconds(5), Token));
        fixture.StopFailureExpected = true;
        Assert.Equal("worker.cleanup", error.Code); Assert.False(error.CleanupConfirmed);
        await fixture.Workers.CancelSeen.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(JobState.Interrupted, (await fixture.Job()).State); Assert.False(running.IsCompleted);
        Assert.True(File.Exists(fixture.Workers.LastCommand!.FinalPath + ".part"));
        Assert.Throws<IOException>(() => File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read));
        Assert.False(await fixture.Runner.RunNextAsync(Token));
        fixture.Workers.Release.TrySetResult(); fixture.Workers.CleanupRelease.TrySetResult();
        Assert.True(await running);
        await using (var db = await fixture.Open())
        await using (var lease = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token)) { }
        // A later proved exit can release owned identities, but never rewrites the earlier timeout as success.
        fixture.StopFailureExpected = true;
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly bool ownsBox;
        private readonly TimeProvider clock;
        public CatalogSandbox Box { get; }
        public ProjectState Prepared { get; private set; } = null!;
        public string Id => Prepared.Metadata.Id;
        public string Prompt => Path.Combine(Box.ProjectRoot(Id), Prepared.Voice!.WavRelativePath.Replace('/', Path.DirectorySeparatorChar));
        public StudioJobService Jobs { get; private set; } = null!;
        public FakeFactory Workers { get; } = new();
        public GenerationRunner Runner { get; set; } = null!;
        public bool StopFailureExpected { get; set; }
        public Task<bool>? InFlight { get; set; }
        private string jobId = "";
        private Fixture(CatalogSandbox box, bool owns, TimeProvider clock) { Box = box; ownsBox = owns; this.clock = clock; }
        public GenerationRunner NewRunner() => new(new ProjectJobAccess(new(Box.Root), Box.Catalog(), new ProjectService()), new ProjectService(), Workers, clock);
        public static async Task<Fixture> Create(CatalogSandbox? shared = null, bool hybrid = false, bool preview = false, TimeProvider? clock = null, int promptSamples = 480000, string? creationKey = null)
        {
            var fixture = new Fixture(shared ?? new CatalogSandbox(), shared == null, clock ?? TimeProvider.System);
            try
            {
                byte[] prompt = Wave(promptSamples);
                fixture.Prepared = await StudioJobServiceTests.Prepare(fixture.Box, Convert.ToHexString(SHA256.HashData(prompt)), creationKey);
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.Prompt)!);
                await File.WriteAllBytesAsync(fixture.Prompt, prompt, Token);
                fixture.Jobs = new(new ProjectJobAccess(new(fixture.Box.Root), fixture.Box.Catalog(), new ProjectService()), new Runtime(hybrid));
                var job = await fixture.Jobs.EnqueueAsync(fixture.Id, fixture.Prepared.Revision, "enqueue", new(preview ? JobKind.Preview : JobKind.Generation, fixture.Prepared.Segments.Take(preview ? 1 : 2).Select(x => x.Id).ToArray()), Token);
                fixture.jobId = job.Id; fixture.Runner = fixture.NewRunner(); return fixture;
            }
            catch { if (fixture.ownsBox) fixture.Box.Dispose(); throw; }
        }
        public Task<JobSnapshotDto> Job() => Jobs.GetAsync(Id, jobId, Token);
        public Task<LocalAudiobookStudio.Core.Storage.ProjectDatabase> Open() => new ProjectService().OpenCurrentAsync(Box.ProjectRoot(Id), Token);
        public async ValueTask DisposeAsync()
        {
            Workers.Release.TrySetResult();
            Workers.CleanupRelease.TrySetResult();
            if (Workers.OpenCount > 0) await Workers.CleanupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            if (InFlight != null)
            {
                try { await InFlight.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
                catch (WorkerClientException e) when (e.CleanupConfirmed) { }
            }
            try { await Runner.StopAsync(CancellationToken.None); }
            catch (WorkerClientException e) when (StopFailureExpected && e.Code == "worker.cleanup" && !e.CleanupConfirmed) { }
            if (ownsBox) Box.Dispose();
        }
    }

    internal sealed class Runtime(bool hybrid) : IRuntimeDescriptor
    {
        public Task<RuntimeDescriptor> GetAsync(CancellationToken token) => Task.FromResult(new RuntimeDescriptor(
            new("voxcpm2-local-v1", hybrid ? "q8-f16-hybrid-16" : "q8-f16-cpu", 70, 10, 2, 1, 42, hybrid), new("", new string('1', 40), new string('2', 40), "", "", "")));
    }

    internal sealed class FakeFactory : IWorkerClientFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OpenCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool OomFirst { get; set; }
        public bool OomEveryAttempt { get; set; }
        public bool WritePendingAtStart { get; set; }
        public bool HoldCleanup { get; set; }
        public bool ErrorAfterConfirmedCleanup { get; set; }
        public bool BadOutputHash { get; set; }
        public bool TamperOutputDuringCleanup { get; set; }
        public SynthesizeSegmentCommand? LastCommand { get; private set; }
        public Action<SynthesizeSegmentCommand>? CheckPrompt { get; set; }
        public List<string> Profiles { get; } = [];
        public Task<IWorkerClient> OpenAsync(string projectRoot, CancellationToken token)
        { OpenCount++; return Task.FromResult<IWorkerClient>(new Client(this)); }
        private sealed class Client(FakeFactory factory) : IWorkerClient
        {
            public async Task<SynthesisResult> SynthesizeAsync(string requestId, SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken token)
            {
                factory.LastCommand = command;
                factory.Profiles.Add(command.ProfileId); factory.CheckPrompt?.Invoke(command); progress(new("generating"));
                using var canceled = token.Register(() => factory.CancelSeen.TrySetResult());
                if (factory.WritePendingAtStart) await File.WriteAllBytesAsync(command.FinalPath + ".part", [1, 2, 3], CancellationToken.None);
                factory.Started.TrySetResult(); await factory.Release.Task;
                if (factory.OomEveryAttempt || factory.OomFirst && factory.Profiles.Count == 1) throw new WorkerClientException("worker.cuda.oom", true);
                byte[] content = Wave(4800); await File.WriteAllBytesAsync(command.FinalPath + ".part", content, CancellationToken.None);
                File.Move(command.FinalPath + ".part", command.FinalPath);
                return new(command.FinalPath, content.Length, factory.BadOutputHash ? new string('a', 64) : Convert.ToHexStringLower(SHA256.HashData(content)), 1, 48000, 16, 123.45, command.ProfileId, new string('1', 40), new string('2', 40));
            }
            public Task CancelAsync(string targetRequestId, CancellationToken token) { factory.CancelSeen.TrySetResult(); return Task.CompletedTask; }
            public async ValueTask DisposeAsync()
            {
                factory.CleanupStarted.TrySetResult(); if (factory.HoldCleanup) await factory.CleanupRelease.Task;
                if (factory.TamperOutputDuringCleanup) await File.WriteAllBytesAsync(factory.LastCommand!.FinalPath, [1, 2, 3], CancellationToken.None);
                factory.DisposeCount++;
                factory.CleanupCompleted.TrySetResult();
                if (factory.ErrorAfterConfirmedCleanup) throw new WorkerClientException("worker.interrupted", true);
            }
        }
    }

    internal static byte[] Wave(int samples)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write((uint)(36 + samples * 2)); writer.Write("WAVEfmt "u8); writer.Write(16u);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(48000u); writer.Write(96000u); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write((uint)(samples * 2)); for (int i = 0; i < samples; i++) writer.Write((short)(i % 100));
        return stream.ToArray();
    }
}

using System.Security.Cryptography;
using System.Text;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Text;
using LocalAudiobookStudio.Web.Hosting;
using LocalAudiobookStudio.Web.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Tests;

public sealed partial class JobCoordinatorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delayed_readiness_scan_is_followed_by_a_new_clock_tick_without_losing_queued_work()
    {
        await using var fixture=await Fixture.Create();fixture.Runtime.ReadinessHold=new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.App.StartAsync(Token);await fixture.Runtime.ReadinessEntered.Task.WaitAsync(TimeSpan.FromSeconds(5),Token);
        fixture.Runtime.Ready=true;fixture.Workers.Release.TrySetResult();fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Runtime.ReadinessHold.SetResult();await fixture.AwaitJob(JobState.Completed);
        Assert.Equal(2,(await fixture.Job()).CompletedUnits);
    }

    // Removing hosted dispatch must leave this durable job queued, not completed.
    [Fact]
    public async Task Host_executes_saved_queue_serially_without_a_browser()
    {
        await using var fixture = await Fixture.Create();
        fixture.Runtime.Ready = true; fixture.Workers.Release.TrySetResult();
        await fixture.App.StartAsync(Token);
        await fixture.AwaitJob(JobState.Completed);
        var completed = await fixture.Job();
        Assert.Equal(2, completed.CompletedUnits);
        Assert.Equal(1, fixture.Workers.MaximumConcurrent);
        await using var db = await fixture.Open();
        Assert.All(await db.Jobs.ItemsAsync(completed.Id, Token), x => Assert.NotNull(x.Artifact));
    }

    // A lost notification or missing readiness gate must not lose/fail queued work.
    [Fact]
    public async Task Idle_durable_scan_finds_new_work_when_runtime_becomes_ready()
    {
        await using var fixture = await Fixture.Create(enqueue: false);
        await fixture.App.StartAsync(Token); await fixture.Clock.WaitForDelay(TimeSpan.FromSeconds(1), 2);
        await fixture.Enqueue();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Clock.WaitForDelay(TimeSpan.FromSeconds(1), 2);
        Assert.Equal(JobState.Queued, (await fixture.Job()).State);
        Assert.Equal(0, (await fixture.Job()).CompletedUnits);
        fixture.Runtime.Ready = true; fixture.Workers.Release.TrySetResult();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.AwaitJob(JobState.Completed);
        Assert.Equal(2, (await fixture.Job()).CompletedUnits);
    }

    // Startup recovery must be owned and independent of model readiness.
    [Fact]
    public async Task Startup_recovers_orphan_and_requires_explicit_resume_without_starting_inference()
    {
        await using var fixture = await Fixture.Create(); string pending;
        await using (var db = await fixture.Open())
        await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
        {
            var job = await db.Jobs.GetAsync(fixture.JobId, Token);
            var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
            pending = db.ResolveOwnedPath(claim.FinalRelativePath) + ".part";
            await File.WriteAllBytesAsync(pending, [1, 2, 3], Token);
        }
        await fixture.App.StartAsync(Token); await fixture.Clock.WaitForDelay(TimeSpan.FromSeconds(1), 2);
        var interrupted = await fixture.Job(); Assert.Equal(JobState.Interrupted, interrupted.State);
        Assert.Equal(0, interrupted.CompletedUnits); Assert.False(File.Exists(pending));
        fixture.Runtime.Ready = true; fixture.Workers.Release.TrySetResult();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1)); await fixture.Clock.WaitForDelay(TimeSpan.FromSeconds(1), 2);
        Assert.Equal(JobState.Interrupted, (await fixture.Job()).State);
        await fixture.Jobs.RequestStateAsync(fixture.Id, fixture.JobId, interrupted.Revision, "resume", JobControlCommand.Resume, Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1)); await fixture.AwaitJob(JobState.Completed);
    }

    // Stop must persist interruption before cancellation and retain actual identities through cleanup.
    [Fact]
    public async Task Host_stop_interrupts_before_worker_cleanup_and_never_starts_the_next_unit()
    {
        await using var fixture = await Fixture.Create(); fixture.Runtime.Ready = true; fixture.Workers.HoldCleanup = true;
        await fixture.App.StartAsync(Token); await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var stop = fixture.App.StopAsync(Token);
        await fixture.Workers.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(JobState.Interrupted, (await fixture.Job()).State); Assert.False(stop.IsCompleted);
        Assert.True(File.Exists(fixture.Workers.FinalPath + ".part"));
        Assert.Throws<IOException>(() => File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read));
        fixture.Workers.Release.TrySetResult();
        await fixture.Workers.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(stop.IsCompleted); Assert.True(File.Exists(fixture.Workers.FinalPath));
        fixture.Workers.CleanupRelease.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var interrupted = await fixture.Job(); Assert.Equal(JobState.Interrupted, interrupted.State);
        Assert.Equal(0, interrupted.CompletedUnits); Assert.False(File.Exists(fixture.Workers.FinalPath));
        using var writable = File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read);
    }

    [Fact]
    public async Task Malformed_ready_provider_cannot_begin_saved_work()
    {
        await using var fixture = await Fixture.Create(); fixture.Runtime.Ready = true; fixture.Runtime.Profile = "PRIVATE_SYNTHETIC_PROFILE";
        await fixture.App.StartAsync(Token); await fixture.Clock.WaitForDelay(TimeSpan.FromSeconds(1), 2);
        Assert.Equal(JobState.Queued, (await fixture.Job()).State);
    }

    [Fact]
    public async Task Event_scan_delivers_durable_control_while_serial_inference_is_blocked()
    {
        await using var fixture = await Fixture.Create(); fixture.Runtime.Ready = true;
        await fixture.App.StartAsync(Token); await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        using var subscription = fixture.App.Services.GetRequiredService<JobEventHub>().Subscribe(fixture.Id);
        var baseline = await fixture.Jobs.ReadEventPageAsync(fixture.Id, null, Token); subscription.EstablishBaseline(baseline.HeadSequence);
        var running = await fixture.Job();
        var paused = await fixture.Jobs.RequestStateAsync(fixture.Id, fixture.JobId, running.Revision, "pause", JobControlCommand.Pause, Token);
        Assert.Equal(JobState.PauseRequested, paused.State);
        await fixture.Clock.WaitForDelay(TimeSpan.FromSeconds(1), 2); fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        JobEventDto hint;
        do { hint = await subscription.Reader.ReadAsync(deadline.Token); } while (hint.State != JobState.PauseRequested);
        Assert.Equal(paused.Revision, hint.JobRevision); Assert.Equal(0, hint.CompletedUnits);
        Assert.Equal(JobState.PauseRequested, (await fixture.Job()).State);
        Assert.False(fixture.Workers.Release.Task.IsCompleted);
        fixture.Workers.Release.TrySetResult(); await fixture.AwaitJob(JobState.Paused);
        Assert.Equal(1, (await fixture.Job()).CompletedUnits);
    }

    [Fact]
    public async Task Busy_startup_recovery_is_retried_on_idle_scan_even_without_a_model()
    {
        await using var fixture = await Fixture.Create();
        await using var db = await fixture.Open();
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await db.Jobs.GetAsync(fixture.JobId, Token);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        await fixture.App.StartAsync(Token); await fixture.Clock.WaitForDelay(TimeSpan.FromSeconds(1), 2);
        Assert.Equal(JobState.Preparing, (await fixture.Job()).State);
        await lease.DisposeAsync(); fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.AwaitJob(JobState.Interrupted);
        Assert.Equal(0, (await fixture.Job()).CompletedUnits);
    }

    [Fact]
    public async Task Stop_deadline_and_repeated_stop_do_not_claim_cleanup_until_the_owned_unit_exits()
    {
        await using var fixture = await Fixture.Create(); fixture.Runtime.Ready = true; fixture.Workers.HoldCleanup = true;
        await fixture.App.StartAsync(Token); await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var stop = fixture.App.StopAsync(Token);
        await fixture.Workers.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await fixture.Clock.WaitForDelay(TimeSpan.FromSeconds(30)); fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.StopFailureExpected = true;
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => stop.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal("worker.cleanup", error.Code); Assert.False(error.CleanupConfirmed);
        var repeated = await Assert.ThrowsAsync<WorkerClientException>(() => fixture.App.StopAsync(Token));
        Assert.False(repeated.CleanupConfirmed);
        Assert.Equal(JobState.Interrupted, (await fixture.Job()).State);
        Assert.True(File.Exists(fixture.Workers.FinalPath + ".part"));
        Assert.Throws<IOException>(() => File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read));
        fixture.Workers.Release.TrySetResult(); fixture.Workers.CleanupRelease.TrySetResult();
        // Wait for the real hosted operation, not a fake Dispose signal, before owned teardown.
        foreach (var background in fixture.App.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Microsoft.Extensions.Hosting.BackgroundService>())
            if (background.ExecuteTask != null) await background.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(File.Exists(fixture.Workers.FinalPath));
        using var writable = File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "las-coordinator-" + Guid.NewGuid().ToString("N"));
        public WebApplication App { get; private set; } = null!;
        public ManualClock Clock { get; } = new();
        public SyntheticRuntime Runtime { get; } = new();
        public SyntheticWorkers Workers { get; } = new();
        public ProjectState Prepared { get; private set; } = null!;
        public string Id => Prepared.Metadata.Id;
        public string JobId { get; set; } = "";
        public bool StopFailureExpected { get; set; }
        public string Prompt => Path.Combine(Root, "projects", Id, Prepared.Voice!.WavRelativePath);
        public IStudioJobService Jobs => App.Services.GetRequiredService<IStudioJobService>();
        public Task<LocalAudiobookStudio.Core.Storage.ProjectDatabase> Open()
            => new ProjectService().OpenCurrentAsync(Path.Combine(Root, "projects", Id), Token);
        public Task<JobSnapshotDto> Job() => Jobs.GetAsync(Id, JobId, Token);
        public async Task Enqueue()
        {
            JobId = (await Jobs.EnqueueAsync(Id, Prepared.Revision, "enqueue", new(JobKind.Generation,
                Prepared.Segments.Select(x => x.Id).ToArray()), Token)).Id;
        }
        public async Task AwaitJob(JobState state)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { if ((await Jobs.GetAsync(Id, JobId, timeout.Token)).State == state) return; }
                catch (LocalAudiobookStudio.Application.Errors.StudioException e) when (e.Code is "project.busy" or "project.storage" or "catalog.storage")
                { /* A concurrent writer may temporarily reject a current read; keep the same bounded observation, not a command retry. */ }
                // Other hosted services also register 1s timers. Counting two of
                // them does not prove that this coordinator has begun its delay.
                Clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(25, timeout.Token);
            }
        }
        public static async Task<Fixture> Create(bool enqueue = true, Action<IServiceCollection>? configure = null)
        {
            var f = new Fixture();
            try
            {
                f.App = LocalWebHost.Build(new(f.Root), services =>
                {
                    services.AddSingleton<TimeProvider>(f.Clock);
                    services.AddSingleton<IRuntimeReadiness>(f.Runtime); services.AddSingleton<IRuntimeDescriptor>(f.Runtime);
                    services.AddSingleton<IWorkerClientFactory>(f.Workers);
                    configure?.Invoke(services);
                });
                var created = await f.App.Services.GetRequiredService<IStudioProjectService>().CreateAsync(new("Synthetic"), "create", Token);
                await using var db = await new ProjectService().OpenAsync(Path.Combine(f.Root, "projects", created.Snapshot.Id), Token, retainSidecars: true);
                byte[] prompt = ApiTestHost.Wav(); var now = DateTimeOffset.UtcNow;
                string voiceId = Guid.NewGuid().ToString("N"), chapterId = Guid.NewGuid().ToString("N"), recordingId = Guid.NewGuid().ToString("N");
                var pins = new VersionPins("", "", "", "", "frontend-v1", "segmenter-v1");
                const string text = "PRIVATE_SYNTHETIC_TEXT";
                var voice = new VoiceSnapshot(voiceId, $"voice-snapshot/{voiceId}.wav", text, Convert.ToHexString(SHA256.HashData(prompt)),
                    Hash(text).ToUpperInvariant(), new string('B', 64), true, now, 0, 10000);
                var chapter = new ChapterRecord(chapterId, 0, "Synthetic", text, text, text, now,
                    new(TextSourceKind.Pasted, "Synthetic", "utf-8", Hash("synthetic"), 9, pins.TextFrontend, pins.Segmenter));
                var segments = Enumerable.Range(0, 2).Select(i => new SegmentRecord(Guid.NewGuid().ToString("N"), chapterId, i,
                    text, text, text, Hash("synthetic" + i), "", null, null, 42, pins, db.State.Metadata.Parameters,
                    "queued", false, now, now, null, BoundaryAfter: SegmentBoundary.Sentence)).ToArray();
                await db.SaveAsync(db.State with { Metadata = db.State.Metadata with { Versions = pins }, Voice = voice,
                    VoiceRecording = new(recordingId, $"voice-recording/{recordingId}.wav", new string('C', 64), 960044, 480000, false, true, now),
                    Chapters = [chapter], Segments = segments }, Token);
                f.Prepared = db.State; Directory.CreateDirectory(Path.GetDirectoryName(f.Prompt)!); await File.WriteAllBytesAsync(f.Prompt, prompt, Token);
                if (enqueue) await f.Enqueue(); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        public async ValueTask DisposeAsync()
        {
            Workers.Release.TrySetResult(); Workers.CleanupRelease.TrySetResult();
            if (App != null)
            {
                try { await App.StopAsync(CancellationToken.None); }
                catch (WorkerClientException e) when (StopFailureExpected && !e.CleanupConfirmed && e.Code == "worker.cleanup") { }
                await App.DisposeAsync();
            }
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class SyntheticRuntime : IRuntimeReadiness, IRuntimeDescriptor
    {
        internal TaskCompletionSource? ReadinessHold;
        internal TaskCompletionSource ReadinessEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Ready { get; set; }
        public string Profile { get; set; } = "q8-f16-cpu";
        public async Task<RuntimeReadiness> GetAsync(CancellationToken token)
        {
            var value=new RuntimeReadiness(Ready,false,Ready?null:"worker.runtime.not_ready",Profile);
            ReadinessEntered.TrySetResult();if(ReadinessHold!=null)await ReadinessHold.Task.WaitAsync(token);return value;
        }
        Task<RuntimeDescriptor> IRuntimeDescriptor.GetAsync(CancellationToken token) => Task.FromResult(new RuntimeDescriptor(
            new("voxcpm2-local-v1", "q8-f16-cpu", 70, 10, 2, 1, 42, false), new("", new string('1', 40), new string('2', 40), "", "", "")));
    }
    private sealed class SyntheticWorkers : IWorkerClientFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string FinalPath { get; private set; } = "";
        public bool HoldCleanup { get; set; }
        public int MaximumConcurrent { get; private set; }
        private int active;
        public Task<IWorkerClient> OpenAsync(string projectRoot, CancellationToken token)
        { MaximumConcurrent = Math.Max(MaximumConcurrent, ++active); return Task.FromResult<IWorkerClient>(new Client(this)); }
        private sealed class Client(SyntheticWorkers owner) : IWorkerClient
        {
            public async Task<SynthesisResult> SynthesizeAsync(string requestId, SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken token)
            {
                owner.FinalPath = command.FinalPath; progress(new("generating"));
                await File.WriteAllBytesAsync(command.FinalPath + ".part", [1, 2, 3], CancellationToken.None);
                using var registration = token.Register(() => owner.Canceled.TrySetResult()); owner.Started.TrySetResult();
                await owner.Release.Task;
                byte[] bytes = ApiTestHost.Wav(4800); await File.WriteAllBytesAsync(command.FinalPath + ".part", bytes, CancellationToken.None);
                File.Move(command.FinalPath + ".part", command.FinalPath);
                return new(command.FinalPath, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), 1, 48000, 16, 0.1,
                    command.ProfileId, new string('1', 40), new string('2', 40));
            }
            public Task CancelAsync(string targetRequestId, CancellationToken token) => Task.CompletedTask;
            public async ValueTask DisposeAsync()
            { owner.CleanupStarted.TrySetResult(); if (owner.HoldCleanup) await owner.CleanupRelease.Task; --owner.active; }
        }
    }

    internal sealed class ManualClock : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ClockTimer> timers = [];
        private long ticks;
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (sync) return ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { lock (sync) { var timer = new ClockTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; } }
        public async Task WaitForDelay(TimeSpan delay, int minimum = 1)
        {
            while (true)
            {
                Task signal; lock (sync) { if (timers.Count(x => x.Due == ticks + delay.Ticks) >= minimum) return; signal = changed.Task; }
                await signal.WaitAsync(TimeSpan.FromSeconds(5), Token);
            }
        }
        public void Advance(TimeSpan amount)
        {
            List<ClockTimer> due;
            lock (sync)
            {
                ticks += amount.Ticks; due = timers.Where(x => x.Due is { } at && at <= ticks).ToList();
                foreach (var timer in due) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? null : ticks + timer.Period.Ticks;
                Pulse();
            }
            foreach (var timer in due) timer.Fire();
        }
        private void Pulse() { var old = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); old.TrySetResult(); }
        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long? Due { get; set; }
            public TimeSpan Period { get; private set; }
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (clock.sync) { if (disposed) return false; Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.ticks + dueTime.Ticks; Period = period; clock.Pulse(); return true; } }
            public void Fire() { lock (clock.sync) { if (disposed) return; } callback(state); }
            public void Dispose() { lock (clock.sync) { disposed = true; clock.timers.Remove(this); clock.Pulse(); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

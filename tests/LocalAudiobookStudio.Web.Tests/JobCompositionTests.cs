using LocalAudiobookStudio.Application;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Text;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Tests;

public class JobCompositionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Default_host_resolves_singleton_jobs_without_discovering_or_starting_a_runtime()
    {
        string root = Path.Combine(Path.GetTempPath(), "las-composition-" + Guid.NewGuid().ToString("N"));
        await using var app = LocalWebHost.Build(new(root));
        var jobs = app.Services.GetRequiredService<IStudioJobService>();
        var runner = app.Services.GetRequiredService<GenerationRunner>();
        using var scope = app.Services.CreateScope();
        Assert.Same(jobs, scope.ServiceProvider.GetRequiredService<IStudioJobService>());
        Assert.Same(jobs, app.Services.GetRequiredService<StudioJobService>());
        Assert.Same(runner, scope.ServiceProvider.GetRequiredService<GenerationRunner>());
        var readiness = await app.Services.GetRequiredService<IRuntimeReadiness>().GetAsync(Token);
        Assert.False(readiness.Ready); Assert.False(readiness.Experimental);
        Assert.Equal("worker.runtime.not_ready", readiness.DiagnosticCode);
        var descriptor = await Assert.ThrowsAsync<WorkerClientException>(() => app.Services.GetRequiredService<IRuntimeDescriptor>().GetAsync(Token));
        Assert.Equal("worker.runtime.not_ready", descriptor.Code); Assert.True(descriptor.CleanupConfirmed);
        var launch = await Assert.ThrowsAsync<WorkerClientException>(() => app.Services.GetRequiredService<IWorkerClientFactory>().OpenAsync(root, Token));
        Assert.Equal("worker.runtime.not_ready", launch.Code); Assert.True(launch.CleanupConfirmed);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Composition_preserves_explicit_runtime_worker_and_clock_dependencies()
    {
        string root = Path.Combine(Path.GetTempPath(), "las-composition-" + Guid.NewGuid().ToString("N"));
        var runtime = new ExplicitRuntime(); var workers = new ExplicitWorkers(); var clock = new ExplicitClock();
        var services = new ServiceCollection();
        services.AddSingleton<IRuntimeReadiness>(runtime); services.AddSingleton<IRuntimeDescriptor>(runtime);
        services.AddSingleton<IWorkerClientFactory>(workers); services.AddSingleton<TimeProvider>(clock);
        services.AddStudioProjects(new(root)); services.AddStudioProjects(new(root));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Same(runtime, provider.GetRequiredService<IRuntimeReadiness>());
        Assert.Same(runtime, provider.GetRequiredService<IRuntimeDescriptor>());
        Assert.Same(workers, provider.GetRequiredService<IWorkerClientFactory>());
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        Assert.Single(provider.GetServices<GenerationRunner>());
        Assert.Single(provider.GetServices<IStudioJobService>());
        Assert.IsType<StudioJobService>(provider.GetRequiredService<IStudioJobService>());
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Unconfigured_composed_jobs_allow_receipt_replay_and_controls_but_not_new_generation()
    {
        string root = Path.Combine(Path.GetTempPath(), "las-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = LocalWebHost.Build(new(root));
            var projects = app.Services.GetRequiredService<IStudioProjectService>();
            var created = await projects.CreateAsync(new("Synthetic"), "create", Token);
            var text = await app.Services.GetRequiredService<IStudioTextService>().ImportPastedAsync(created.Snapshot.Id,
                created.Snapshot.Revision, new("Text", "Синтетический пример."), Token);
            string projectRoot = Path.Combine(root, "projects", text.Id); JobRecord original;
            var request = new GenerationRequest(JobKind.Generation, text.Segments.Select(x => x.Id).ToArray());
            await using (var db = await new ProjectService().OpenCurrentAsync(projectRoot, Token))
            await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
            {
                // Valid first-accepted synthetic metadata only. No file, model or inference is used.
                string voiceId = Guid.NewGuid().ToString("N"), recordingId = Guid.NewGuid().ToString("N");
                var now = DateTimeOffset.UtcNow;
                var voice = new VoiceSnapshot(voiceId, $"voice-snapshot/{voiceId}.wav", "Synthetic", new string('A', 64),
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("Synthetic"u8)), new string('B', 64), true, now, 0, 10000);
                await lease.SaveAsync(db.State with { Voice = voice, VoiceRecording = new(recordingId,
                    $"voice-recording/{recordingId}.wav", new string('C', 64), 960044, 480000, false, true, now) }, Token);
                var input = new GenerationInput(db.State.Metadata.Id, db.State.Revision, voice,
                    new("voxcpm2-local-v1", "q8-f16-cpu", 70, 10, 2, 1, 42, false), db.State.Segments.Select(x => new GenerationItemInput(
                        x.Id, x.Order, x.SpokenText, x.TextHash, x.PreviousSentenceContextHash, x.NextSentenceContextHash, x.BoundaryAfter,
                        x.Versions with { Worker = new string('1', 40), Model = new string('2', 40) })).ToArray());
                original = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "enqueue", JobKind.Generation, input, Token);
            }
            var jobs = app.Services.GetRequiredService<IStudioJobService>();
            var replay = await jobs.EnqueueAsync(text.Id, -1, "enqueue", request, Token);
            Assert.Equal(original.Id, replay.Id); Assert.Equal(original.Revision, replay.Revision);
            var paused = await jobs.RequestStateAsync(text.Id, original.Id, original.Revision, "pause", JobControlCommand.Pause, Token);
            Assert.Equal(JobState.Paused, paused.State);
            var canceled = await jobs.RequestStateAsync(text.Id, original.Id, paused.Revision, "cancel", JobControlCommand.Cancel, Token);
            Assert.Equal(JobState.Canceled, canceled.State);
            long head = await jobs.GetEventSequenceAsync(text.Id, Token);
            var error = await Assert.ThrowsAsync<StudioException>(() => jobs.EnqueueAsync(text.Id, -1, "new", request, Token));
            Assert.Equal("worker.runtime.not_ready", error.Code); Assert.Null(error.InnerException);
            Assert.Equal(head, await jobs.GetEventSequenceAsync(text.Id, Token));
            Assert.Equal(canceled, Assert.Single(await jobs.ListAsync(text.Id, Token)));
        }
        finally
        {
            // Only this fresh, exclusively owned synthetic root, after the host closes.
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class ExplicitClock : TimeProvider { }
    private sealed class ExplicitRuntime : IRuntimeReadiness, IRuntimeDescriptor
    {
        public Task<RuntimeReadiness> GetAsync(CancellationToken token) => throw new InvalidOperationException("SYNTHETIC_RUNTIME_MUST_NOT_BE_USED");
        Task<RuntimeDescriptor> IRuntimeDescriptor.GetAsync(CancellationToken token) => throw new InvalidOperationException("SYNTHETIC_RUNTIME_MUST_NOT_BE_USED");
    }
    private sealed class ExplicitWorkers : IWorkerClientFactory
    {
        public Task<IWorkerClient> OpenAsync(string projectRoot, CancellationToken token) => throw new InvalidOperationException("SYNTHETIC_WORKER_MUST_NOT_BE_USED");
    }
}

using System.Threading.Channels;
using LocalAudiobookStudio.Application;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Web.TtsCorpus;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class TtsCorpusCoordinatorTests
{
    [Fact]
    public async Task Configured_idle_coordinator_does_not_admit_runtime_on_each_dispatch_poll()
    {
        string root = Path.Combine(Path.GetTempPath(), "las-corpus-idle-" + Guid.NewGuid().ToString("N"));
        var runtime = new CountingRuntime(); var clock = new DispatchClock();
        var services = new ServiceCollection().AddStudioProjects(new ProjectCatalogOptions(root));
        services.AddSingleton<ITtsCorpusRuntime>(runtime);
        services.AddSingleton<TimeProvider>(clock);
        await using var provider = services.BuildServiceProvider();
        using var coordinator = ActivatorUtilities.CreateInstance<TtsCorpusCoordinator>(provider);
        try
        {
            await coordinator.StartAsync(TestContext.Current.CancellationToken);
            for (int poll = 0; poll < 3; poll++)
            {
                var timer = await clock.Scheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
                Assert.Equal(TimeSpan.FromSeconds(1), timer.Due);
                Assert.Equal(0, runtime.Admissions);
                timer.Fire();
            }
            _ = await clock.Scheduled.Reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, runtime.Admissions);
        }
        finally
        {
            await coordinator.StopAsync(TestContext.Current.CancellationToken);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class CountingRuntime : ITtsCorpusRuntime
    {
        internal int Admissions;
        public Task<TtsCorpusReadinessDto> GetReadinessAsync(CancellationToken token = default)
        { Interlocked.Increment(ref Admissions); return Task.FromResult(new TtsCorpusReadinessDto(true, null)); }
        public Task<string> GetFingerprintAsync(CancellationToken token = default)
        { Interlocked.Increment(ref Admissions); return Task.FromResult(new string('a', 64)); }
    }

    private sealed class DispatchClock : TimeProvider
    {
        internal Channel<DispatchTimer> Scheduled { get; } = Channel.CreateUnbounded<DispatchTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DispatchTimer(callback, state, dueTime); Scheduled.Writer.TryWrite(timer); return timer;
        }
    }
    private sealed class DispatchTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        private bool disposed;
        internal TimeSpan Due { get; } = due;
        internal void Fire() { if (!disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
        public void Dispose() => disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

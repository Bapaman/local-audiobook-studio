using System.Threading.Channels;
using LocalAudiobookStudio.Application;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Web.VoxCpmTraining;
using Microsoft.Extensions.DependencyInjection;
using static LocalAudiobookStudio.Web.Tests.VoxCpmTrainingApiFixture;

namespace LocalAudiobookStudio.Web.Tests;
public sealed class VoxCpmTrainingCoordinatorTests
{
    [Fact]
    public async Task Idle_dispatch_does_not_rehash_runtime()
    {
        string root = Path.Combine(Path.GetTempPath(), "las-vox-idle-" + Guid.NewGuid().ToString("N")); var runtime = new Runtime(); var clock = new Clock();
        var services = new ServiceCollection().AddStudioProjects(new ProjectCatalogOptions(root)); services.AddSingleton<IVoxCpmTrainingRuntime>(runtime); services.AddSingleton<TimeProvider>(clock);
        await using var provider = services.BuildServiceProvider(); using var coordinator = ActivatorUtilities.CreateInstance<VoxCpmTrainingCoordinator>(provider);
        try
        {
            await coordinator.StartAsync(ApiTestHost.Token);
            for (int i = 0; i < 3; i++) { var timer = await clock.Scheduled.Reader.ReadAsync(ApiTestHost.Token); Assert.Equal(TimeSpan.FromSeconds(1), timer.Due); Assert.Equal(0, runtime.Admissions); timer.Fire(); }
            _ = await clock.Scheduled.Reader.ReadAsync(ApiTestHost.Token); Assert.Equal(0, runtime.Admissions);
        }
        finally { await coordinator.StopAsync(ApiTestHost.Token); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Startup_interrupts_durable_queue_without_native_or_runtime_admission()
    {
        await using var original = await HostAsync(); var p = await ApprovedAsync(original);
        var service = original.Services.GetRequiredService<IStudioVoxCpmTrainingService>(); var queued = await service.StartAsync(p.Id, long.Parse(p.ETag.Trim('"')), "start", p.VersionId, true, ApiTestHost.Token);
        await original.StopAsync(ApiTestHost.Token); var runtime = new Runtime(); var executor = new Executor();
        await using var reopened = await HostAsync(runtime, executor, hosted: true, root: original.Root);
        var interrupted = (await reopened.Services.GetRequiredService<IStudioVoxCpmTrainingService>().GetAsync(p.Id, ApiTestHost.Token))!;
        Assert.Equal(queued.Id, interrupted.Id); Assert.Equal("interrupted", interrupted.State); Assert.Equal(0, runtime.Admissions); Assert.Equal(0, executor.Calls);
    }
    [Fact]
    public async Task Accepted_HTTP_command_outlives_request_and_finishes_in_host()
    {
        await using var original = await HostAsync(); var p = await ApprovedAsync(original); await original.StopAsync(ApiTestHost.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Executor { Before = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(ApiTestHost.Token); } };
        await using var host = await HostAsync(executor: executor, hosted: true, root: original.Root);
        using (var response = await host.SendAsync(HttpMethod.Post, PathFor(p.Id), StartBody(p.VersionId), p.ETag, "start")) Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ApiTestHost.Token); } finally { release.TrySetResult(); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ApiTestHost.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var service = host.Services.GetRequiredService<IStudioVoxCpmTrainingService>();
        while ((await service.GetAsync(p.Id, deadline.Token))!.State != "completed") await Task.Delay(20, deadline.Token);
        var done = (await service.GetAsync(p.Id, deadline.Token))!; Assert.Equal(1, done.GlobalStep); Assert.Single(done.Models); Assert.False(done.QualityEstablished);
    }
    private sealed class Clock : TimeProvider
    {
        internal Channel<Timer> Scheduled { get; } = Channel.CreateUnbounded<Timer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(callback, state, dueTime); Scheduled.Writer.TryWrite(timer); return timer; }
    }
    private sealed class Timer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    { private bool disposed; internal TimeSpan Due => due; internal void Fire() { if (!disposed) callback(state); } public bool Change(TimeSpan d, TimeSpan p) => !disposed; public void Dispose() => disposed = true; public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; } }
}

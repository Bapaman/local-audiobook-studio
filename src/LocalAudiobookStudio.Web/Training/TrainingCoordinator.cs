using LocalAudiobookStudio.Application.Training;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Training;

public sealed class TrainingCoordinator(TrainingRunner runner, IStudioTrainingService training, TrainingRuntime runtime, TimeProvider time) : BackgroundService
{
    private readonly CancellationTokenSource idleStop = new();
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await runner.RecoverAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Configuration is immutable for this host. An unconfigured host has no training dispatcher or timer.
        if (runtime.Options == null) return;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, idleStop.Token);
        while (!idle.IsCancellationRequested)
        {
            try
            {
                if ((await training.GetReadinessAsync(idle.Token)).Ready && await runner.RunNextAsync(CancellationToken.None)) continue;
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested) { return; }
            catch (TrainingExecutionException e) when (!e.CleanupConfirmed) { return; }
            catch (Exception e) when (e is LocalAudiobookStudio.Application.Errors.StudioException or LocalAudiobookStudio.Core.Projects.ProjectException or IOException or UnauthorizedAccessException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), time, idle.Token); }
            catch (OperationCanceledException) when (idle.IsCancellationRequested) { return; }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await idleStop.CancelAsync();
        await runner.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
    public override void Dispose() { idleStop.Dispose(); base.Dispose(); }
}

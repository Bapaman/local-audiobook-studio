using LocalAudiobookStudio.Application.VoxCpmTraining;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.VoxCpmTraining;

public sealed class VoxCpmTrainingCoordinator(VoxCpmTrainingRunner runner, IVoxCpmTrainingRuntime runtime, TimeProvider time) : BackgroundService
{
    private readonly CancellationTokenSource idleStop = new();
    public override async Task StartAsync(CancellationToken cancellationToken)
    { await runner.RecoverAsync(cancellationToken); await base.StartAsync(cancellationToken); }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (runtime is UnavailableVoxCpmTrainingRuntime) return;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, idleStop.Token);
        while (!idle.IsCancellationRequested)
        {
            try
            {
                // Owned work outlives HTTP requests and idle cancellation. The
                // runner admits a runtime only for explicitly queued work.
                if (await runner.RunNextAsync(CancellationToken.None)) continue;
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested) { return; }
            catch (VoxCpmTrainingExecutionException e) when (!e.CleanupConfirmed) { return; }
            catch (Exception e) when (e is LocalAudiobookStudio.Application.Errors.StudioException
                or LocalAudiobookStudio.Core.Projects.ProjectException or VoxCpmTrainingExecutionException or IOException or UnauthorizedAccessException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), time, idle.Token); }
            catch (OperationCanceledException) when (idle.IsCancellationRequested) { return; }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    { await idleStop.CancelAsync(); await runner.StopAsync(cancellationToken); await base.StopAsync(cancellationToken); }
    public override void Dispose() { idleStop.Dispose(); base.Dispose(); }
}

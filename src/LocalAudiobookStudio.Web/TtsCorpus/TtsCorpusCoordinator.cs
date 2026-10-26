using LocalAudiobookStudio.Application.TtsCorpus;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.TtsCorpus;

public sealed class TtsCorpusCoordinator(TtsCorpusRunner runner, ITtsCorpusRuntime runtime, TimeProvider time) : BackgroundService
{
    private readonly CancellationTokenSource idleStop = new();
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await runner.RecoverAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The default runtime has no configuration; there is nothing to poll.
        if (runtime is UnavailableTtsCorpusRuntime) return;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, idleStop.Token);
        while (!idle.IsCancellationRequested)
        {
            try
            {
                // An accepted run belongs to the host/runner, independent of HTTP and idle cancellation.
                // Runner admits the runtime only for queued work under shared compute ownership.
                // Idle dispatch polling must not repeatedly hash the external runtime inventory.
                if (await runner.RunNextAsync(CancellationToken.None)) continue;
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested) { return; }
            catch (TtsCorpusExecutionException e) when (!e.CleanupConfirmed) { return; }
            catch (Exception e) when (e is LocalAudiobookStudio.Application.Errors.StudioException
                or LocalAudiobookStudio.Core.Projects.ProjectException or TtsCorpusExecutionException or IOException or UnauthorizedAccessException) { }
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

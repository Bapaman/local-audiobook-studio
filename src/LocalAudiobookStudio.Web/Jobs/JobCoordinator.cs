using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Projects;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Jobs;

/// <summary>The host, not a browser connection, owns durable recovery and serial dispatch.</summary>
public sealed class JobCoordinator(GenerationRunner runner, IRuntimeReadiness runtime, TimeProvider time) : BackgroundService
{
    private readonly CancellationTokenSource idleStop = new();
    private readonly object sync = new();
    private int stopping;
    private Task? stop;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, idleStop.Token);
        while (Volatile.Read(ref stopping) == 0 && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Recovery is permitted even without a model. Busy ownership is retried
                // on the next durable scan, not by a browser or a mandatory wakeup.
                await runner.RecoverAsync(stoppingToken);
                var readiness = await runtime.GetAsync(idle.Token);
                bool legacyReady = readiness.Ready && readiness.DiagnosticCode is null && readiness.ProfileId is "q8-f16-cpu" or "q8-f16-hybrid-16";
                if (Volatile.Read(ref stopping) == 0 && await runner.RunNextAsync(stoppingToken, legacyReady)) continue;
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested) { return; }
            catch (WorkerClientException e) when (!e.CleanupConfirmed)
            {
                // The runner retains actual identities. Never schedule again or turn
                // an unconfirmed native exit into a clean background-service result.
                Interlocked.Exchange(ref stopping, 1); return;
            }
            catch (Exception e) when (e is StudioException or ProjectException or WorkerClientException or IOException or UnauthorizedAccessException)
            { /* Durable state is authoritative; a transient scan error loses no wakeup. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(1), time, idle.Token); }
            catch (OperationCanceledException) when (idle.IsCancellationRequested) { return; }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (sync) return stop ??= StopCore(cancellationToken);
    }

    private async Task StopCore(CancellationToken token)
    {
        Interlocked.Exchange(ref stopping, 1); await idleStop.CancelAsync();
        // Runner.Stop commits interruption BEFORE requesting synthesis cancellation
        // and owns the single monotonic cleanup budget. Base cancellation comes last.
        await runner.StopAsync(token);
        await base.StopAsync(token);
    }

    public override void Dispose() { idleStop.Dispose(); base.Dispose(); }
}

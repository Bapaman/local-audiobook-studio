using LocalAudiobookStudio.Application.Workers;

namespace LocalAudiobookStudio.App.Lifetime;

/// <summary>Do not release the host/instance marker while exact inference cleanup is unknown.</summary>
internal static class VoxCpmInferenceShutdown
{
    internal static async Task WaitForCleanupAsync(Func<ValueTask> cleanup)
    {
        while (true)
        {
            try { await cleanup(); return; }
            catch (WorkerClientException e) when (e.CleanupConfirmed) { return; }
            catch (WorkerClientException e) when (!e.CleanupConfirmed)
            { await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None); }
        }
    }
}

using LocalAudiobookStudio.Application.VoxCpmTraining;

namespace LocalAudiobookStudio.App.Lifetime;

/// <summary>Keep Main, its single-instance marker and host services alive while
/// the exact VoxCPM native owner still cannot confirm Job-zero.</summary>
internal static class VoxCpmTrainingShutdown
{
    internal static async Task WaitForCleanupAsync(Func<ValueTask> cleanup)
    {
        while (true)
        {
            try { await cleanup(); return; }
            catch (VoxCpmTrainingExecutionException e) when (e.CleanupConfirmed)
            { return; } // Changed cache is unusable, but the owned tree is gone.
            catch (VoxCpmTrainingExecutionException e) when (!e.CleanupConfirmed)
            { await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None); }
        }
    }
}

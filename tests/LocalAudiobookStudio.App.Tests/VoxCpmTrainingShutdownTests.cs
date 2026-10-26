using LocalAudiobookStudio.App.Lifetime;
using LocalAudiobookStudio.Application.VoxCpmTraining;

namespace LocalAudiobookStudio.App.Tests;
public sealed class VoxCpmTrainingShutdownTests
{
    [Fact]
    public async Task Shutdown_does_not_return_or_release_host_owner_until_exact_cleanup_succeeds()
    {
        int calls = 0; bool hostReleased = false;
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var zero = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = HoldHost();
        await retry.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted); Assert.False(hostReleased); zero.SetResult(); await pending;
        Assert.True(hostReleased); Assert.Equal(2, calls);
        async Task HoldHost()
        { await VoxCpmTrainingShutdown.WaitForCleanupAsync(Cleanup); hostReleased = true; }
        async ValueTask Cleanup()
        {
            if (Interlocked.Increment(ref calls) == 1) throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false);
            retry.SetResult(); await zero.Task;
        }
    }
    [Fact]
    public async Task Known_zero_with_changed_cache_does_not_turn_into_endless_cleanup_retry()
    {
        int calls = 0;
        await VoxCpmTrainingShutdown.WaitForCleanupAsync(() =>
        { calls++; return ValueTask.FromException(new VoxCpmTrainingExecutionException("voxcpm_training.cache.changed")); });
        Assert.Equal(1, calls);
    }
}

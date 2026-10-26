using LocalAudiobookStudio.App.Lifetime;
using LocalAudiobookStudio.Application.Workers;
namespace LocalAudiobookStudio.App.Tests;
public sealed class VoxCpmInferenceShutdownTests
{
    [Fact]
    public async Task Host_is_retained_through_unknown_cleanup_until_exact_owner_proves_zero()
    {
        int calls=0;var retry=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var zero=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=VoxCpmInferenceShutdown.WaitForCleanupAsync(Cleanup);await retry.Task.WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);zero.SetResult();await pending;Assert.Equal(2,calls);
        async ValueTask Cleanup(){if(Interlocked.Increment(ref calls)==1)throw new WorkerClientException("worker.cleanup",false);retry.SetResult();await zero.Task;}
    }
    [Fact]
    public async Task Known_cleanup_failure_is_not_an_infinite_shutdown_retry()
    {int calls=0;await VoxCpmInferenceShutdown.WaitForCleanupAsync(()=>{calls++;return ValueTask.FromException(new WorkerClientException("worker.runtime.not_ready",true));});Assert.Equal(1,calls);}
}

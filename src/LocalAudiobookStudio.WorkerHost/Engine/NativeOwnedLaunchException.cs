namespace LocalAudiobookStudio.WorkerHost.Engine;

/// <summary>Cleanup owner for a process created successfully when launch construction failed.</summary>
public interface INativeLaunchCleanup : IDisposable { Task StopTreeAsync(); }

/// <summary>Deliberately not a WorkerException/IO exception: cleanup has not been proved.
/// Callers must retain this exact owner until StopTreeAsync proves Job-zero.</summary>
public sealed class NativeOwnedLaunchException(INativeLaunchCleanup ownership) : Exception("worker.launch.cleanup_unknown")
{
    public INativeLaunchCleanup Ownership { get; } = ownership;
}

internal sealed class NativeLaunchCleanupOwner(KillOnCloseJob job, KernelHandle? process) : INativeLaunchCleanup
{
    public async Task StopTreeAsync()
    {
        await job.StopTreeAsync();
        if (process != null && await Task.Run(() => Native.WaitForSingleObject(process, 5000)) != 0)
            throw new LocalAudiobookStudio.WorkerHost.Pipe.WorkerException("worker.launch");
    }
    public void Dispose() { try { job.Dispose(); } finally { process?.Dispose(); } }
}

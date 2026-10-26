namespace LocalAudiobookStudio.Application.Workers;

/// <summary>One host-wide native compute owner. An unconfirmed child exit retains ownership.</summary>
public sealed class ComputeGate
{
    private readonly SemaphoreSlim slot = new(1, 1);
    public Task<bool> WaitAsync(int milliseconds, CancellationToken token) => slot.WaitAsync(milliseconds, token);
    public void Release() => slot.Release();
}

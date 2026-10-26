using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Engine;

/// <summary>Bounds native parallel CPU execution without changing the library's thread count.</summary>
internal static class NativeCpuAffinity
{
    internal static nuint GetBoundedCurrentMask()
    {
        // Only inspect the current owned process; never widen its allowed affinity.
        if (!Native.GetProcessAffinityMask(Native.GetCurrentProcess(), out nuint allowed, out _))
            throw new WorkerException("worker.launch");
        return SelectAllowedMask(allowed);
    }

    internal static nuint SelectAllowedMask(nuint allowed)
    {
        if (allowed == 0) throw new WorkerException("worker.launch");
        nuint selected = 0;
        for (int count = 0; count < 2 && allowed != 0; count++)
        {
            nuint withoutLowestBit = allowed & (allowed - 1);
            selected |= allowed ^ withoutLowestBit;
            allowed = withoutLowestBit;
        }
        return selected;
    }
}

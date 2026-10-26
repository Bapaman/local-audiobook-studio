using System.Numerics;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

internal static class VoxCpmTrainingResourcePolicy
{
    internal const long MemoryCapBytes = 13958643712;
    internal const ulong ReserveBytes = 3221225472;
    internal static void CheckPreflight(ulong available)
    {
        if (available < (ulong)MemoryCapBytes + ReserveBytes) throw new VoxCpmTrainingExecutionException("voxcpm_training.memory.insufficient");
    }
    internal static string? Violation(ulong available, TimeSpan elapsed)
        => elapsed >= TimeSpan.FromSeconds(300) ? "voxcpm_training.window.deadline" : available < ReserveBytes ? "voxcpm_training.memory.reserve" : null;
    internal static void CheckLimits(NativeJobMemoryObservation readback)
    {
        if (readback.LimitFlags != 0x2210 || readback.AggregateJobMemoryLimitBytes != (ulong)MemoryCapBytes
            || BitOperations.PopCount((ulong)readback.AffinityMask) is < 1 or > 2 || readback.PeakJobMemoryUsedBytes > (ulong)MemoryCapBytes)
            throw new VoxCpmTrainingExecutionException("voxcpm_training.memory.policy");
    }
}

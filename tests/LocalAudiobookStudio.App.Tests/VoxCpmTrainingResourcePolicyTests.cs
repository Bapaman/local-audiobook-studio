using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmTrainingResourcePolicyTests
{
    [Fact]
    public void Insufficient_available_memory_fails_before_launch_and_deadline_is_bounded()
    {
        Assert.Throws<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingResourcePolicy.CheckPreflight(17179869183));
        VoxCpmTrainingResourcePolicy.CheckPreflight(17179869184);
        Assert.Null(VoxCpmTrainingResourcePolicy.Violation(3221225472, TimeSpan.FromMilliseconds(299999)));
        Assert.Equal("voxcpm_training.window.deadline", VoxCpmTrainingResourcePolicy.Violation(3221225472, TimeSpan.FromSeconds(300)));
        Assert.Equal("voxcpm_training.memory.reserve", VoxCpmTrainingResourcePolicy.Violation(3221225471, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Readback_must_prove_exact_job_limits_and_one_or_two_allowed_cores()
    {
        var valid = new NativeJobMemoryObservation(0x2210, 3, 13958643712, 13958643712);
        VoxCpmTrainingResourcePolicy.CheckLimits(valid);
        foreach (var bad in new[] { valid with { LimitFlags = 0x2010 }, valid with { AffinityMask = 0 }, valid with { AffinityMask = 7 },
                     valid with { AggregateJobMemoryLimitBytes = 0 }, valid with { PeakJobMemoryUsedBytes = 13958643713 } })
            Assert.Throws<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingResourcePolicy.CheckLimits(bad));
    }

    [Fact]
    public async Task Late_launch_cleanup_owner_is_retained_until_zero_is_proven()
    {
        var original = new Cleanup();
        using var owned = VoxCpmTrainingProcessLauncher.RetainFailure(original);
        await Assert.ThrowsAsync<IOException>(() => owned.StopTreeAsync());
        Assert.False(original.Disposed);
        original.CanStop = true;
        await owned.StopTreeAsync(); Assert.Equal(2, original.Attempts); Assert.False(original.Disposed);
    }
    private sealed class Cleanup : INativeLaunchCleanup
    {
        internal bool Disposed, CanStop; internal int Attempts;
        public Task StopTreeAsync() { Attempts++; return CanStop ? Task.CompletedTask : Task.FromException(new IOException()); }
        public void Dispose() => Disposed = true;
    }
}

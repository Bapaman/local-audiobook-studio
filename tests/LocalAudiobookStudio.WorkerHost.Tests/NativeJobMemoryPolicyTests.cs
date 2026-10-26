using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.WorkerHost.Tests;

public sealed class NativeJobMemoryPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Invalid_cap_is_rejected_without_allocating_native_resources(long bytes)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new NativeJobMemoryPolicy(bytes));

    [Fact]
    public void Aggregate_memory_limit_composes_with_existing_affinity_and_kill_on_close()
    {
        var limits = NativeJobLimitSettings.Create(0xa4, new NativeJobMemoryPolicy(13958643712));
        Assert.Equal(13958643712ul, (ulong)limits.AggregateJobMemoryLimitBytes);
        Assert.Equal(0x2210u, limits.LimitFlags);
        Assert.Equal((nuint)0x24, limits.AffinityMask);
    }

    [Fact]
    public void Unset_policy_preserves_existing_limits()
    {
        var limits = NativeJobLimitSettings.Create(0xa4);
        Assert.Equal((nuint)0, limits.AggregateJobMemoryLimitBytes);
        Assert.Equal(0x2010u, limits.LimitFlags);
        Assert.Equal((nuint)0x24, limits.AffinityMask);
    }
}

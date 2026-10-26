namespace LocalAudiobookStudio.WorkerHost.Engine;

public sealed record NativeJobMemoryPolicy
{
    public long AggregateJobMemoryLimitBytes { get; }
    public NativeJobMemoryPolicy(long aggregateJobMemoryLimitBytes)
    {
        if (aggregateJobMemoryLimitBytes <= 0 || (ulong)aggregateJobMemoryLimitBytes > (ulong)nuint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(aggregateJobMemoryLimitBytes));
        AggregateJobMemoryLimitBytes = aggregateJobMemoryLimitBytes;
    }
}

public sealed record NativeJobLimitSettings(uint LimitFlags, nuint AffinityMask, nuint AggregateJobMemoryLimitBytes)
{
    public static NativeJobLimitSettings Create(nuint allowedAffinityMask, NativeJobMemoryPolicy? memoryPolicy = null)
        => new(0x2000u | 0x10u | (memoryPolicy == null ? 0u : 0x200u), NativeCpuAffinity.SelectAllowedMask(allowedAffinityMask),
            memoryPolicy == null ? 0 : checked((nuint)memoryPolicy.AggregateJobMemoryLimitBytes));
}

/// <summary>Owned Job extended-limit readback. Peak is cumulative peak committed memory, not current usage.</summary>
public sealed record NativeJobMemoryObservation(uint LimitFlags, nuint AffinityMask, ulong AggregateJobMemoryLimitBytes, ulong PeakJobMemoryUsedBytes);

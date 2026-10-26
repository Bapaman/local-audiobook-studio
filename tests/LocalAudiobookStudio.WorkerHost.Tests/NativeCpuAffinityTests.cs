using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using LocalAudiobookStudio.WorkerHost.Engine;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Tests;

public class NativeCpuAffinityTests
{
    public static TheoryData<nuint, nuint> AllowedMasks
    {
        get
        {
            nuint highestBit = (nuint)1 << (nuint.Size * 8 - 1);
            return new()
            {
                { 0x0f, 0x03 },
                { 0xa4, 0x24 },
                { 0x20, 0x20 },
                { highestBit, highestBit },
                { highestBit | 0x20, highestBit | 0x20 },
                { highestBit | 0x24, 0x24 }
            };
        }
    }

    [Theory]
    [MemberData(nameof(AllowedMasks))]
    public void Selects_at_most_two_processors_from_the_allowed_mask(nuint allowed, nuint expected)
    {
        Assert.Equal(expected, NativeCpuAffinity.SelectAllowedMask(allowed));
    }

    [Fact]
    public void Empty_allowed_mask_fails_with_safe_launch_code()
    {
        var error = Assert.Throws<WorkerException>(() => NativeCpuAffinity.SelectAllowedMask(0));

        Assert.Equal("worker.launch", error.Code);
    }

    [Fact]
    public void Owned_job_bounds_cpu_affinity_and_preserves_kill_on_close()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        using var job = new KillOnCloseJob();
        Assert.True(QueryInformationJobObject(job.Handle, 9, out var limits,
            (uint)Marshal.SizeOf<Native.ExtendedLimits>(), out _));

        Assert.Equal(0x10u, limits.Basic.LimitFlags & 0x10u);
        Assert.Equal(0x2000u, limits.Basic.LimitFlags & 0x2000u);
        Assert.InRange(BitOperations.PopCount((ulong)limits.Basic.Affinity), 1, 2);
        using var current = Process.GetCurrentProcess();
        nuint currentAllowed = unchecked((nuint)current.ProcessorAffinity);
        Assert.Equal(limits.Basic.Affinity, limits.Basic.Affinity & currentAllowed);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(nint job, int type,
        out Native.ExtendedLimits info, uint length, out uint returnLength);
}

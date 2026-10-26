using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Engine;

internal sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public KernelHandle() : base(true) { }
    internal KernelHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => Native.CloseHandle(handle);
}

/// <summary>Uninheritable per-request Job. Closing it, including abrupt host exit, terminates the tree.</summary>
public sealed class KillOnCloseJob : IDisposable
{
    private readonly KernelHandle job;
    private readonly KernelHandle completion;
    private readonly SemaphoreSlim lifetime = new(1, 1);
    private bool disposed;
    internal nint Handle => job.DangerousGetHandle();
    public KillOnCloseJob(NativeJobMemoryPolicy? memoryPolicy = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new WorkerException("worker.unsupported.platform");
        job = Native.CreateJobObjectW(0, null);
        completion = Native.CreateIoCompletionPort(new nint(-1), 0, 0, 1);
        try
        {
            if (job.IsInvalid || completion.IsInvalid) throw new WorkerException("worker.launch");
            // Apply kill-on-close and the CPU bound before any child joins this Job.
            var settings = NativeJobLimitSettings.Create(NativeCpuAffinity.GetBoundedCurrentMask(), memoryPolicy);
            var limits = new Native.ExtendedLimits
            {
                Basic = new Native.BasicLimits
                {
                    LimitFlags = settings.LimitFlags,
                    Affinity = settings.AffinityMask
                },
                JobMemoryLimit = settings.AggregateJobMemoryLimitBytes
            };
            Set(9, limits);
            Set(7, new Native.CompletionPort { CompletionKey = new nint(1), Port = completion.DangerousGetHandle() });
        }
        catch { Dispose(); throw; }
    }
    private void Set<T>(int type, T value) where T : struct
    {
        nint buffer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try { Marshal.StructureToPtr(value, buffer, false); if (!Native.SetInformationJobObject(job, type, buffer, (uint)Marshal.SizeOf<T>())) throw new WorkerException("worker.launch"); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public async Task StopTreeAsync()
    {
        await lifetime.WaitAsync();
        try
        {
            if (disposed || !Native.TerminateJobObject(job, 1)) throw new WorkerException("worker.launch");
            // Poll this owned Job's accounting, rather than leave an infinite IOCP dequeue
            // running after a timeout. Root exit is never sufficient evidence of Job-zero.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (!Native.QueryJobAccounting(job, 1, out var accounting, (uint)Marshal.SizeOf<Native.BasicAccounting>(), out uint bytes)
                    || bytes != Marshal.SizeOf<Native.BasicAccounting>()) throw new WorkerException("worker.launch");
                if (accounting.ActiveProcesses == 0) break;
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(5)) throw new WorkerException("worker.launch");
                await Task.Delay(20);
            }
            WorkerLifetimeTrace.Report("job_zero_completion");
        }
        finally { lifetime.Release(); }
    }
    public NativeJobMemoryObservation ReadJobMemory()
    {
        lifetime.Wait();
        try
        {
            if (disposed || !Native.QueryJobLimits(job, 9, out var limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>(), out uint bytes)
                || bytes != Marshal.SizeOf<Native.ExtendedLimits>()) throw new WorkerException("worker.launch");
            return new(limits.Basic.LimitFlags, limits.Basic.Affinity, (ulong)limits.JobMemoryLimit, (ulong)limits.PeakJobMemoryUsed);
        }
        finally { lifetime.Release(); }
    }
    public void Dispose()
    {
        lifetime.Wait();
        try { if (disposed) return; disposed = true; job?.Dispose(); completion?.Dispose(); }
        finally { lifetime.Release(); }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicAccounting
    { public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime; public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)] internal struct CompletionPort { public nint CompletionKey, Port; }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public uint Length; public nint SecurityDescriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size; public nint Reserved2, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] internal static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessAffinityMask(nint process, out nuint processAffinity, out nuint systemAffinity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern KernelHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(KernelHandle job, int type, nint info, uint length);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryJobLimits(KernelHandle job, int type, out ExtendedLimits info, uint length, out uint returnLength);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryJobAccounting(KernelHandle job, int type, out BasicAccounting info, uint length, out uint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(KernelHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern KernelHandle CreateIoCompletionPort(nint file, nint existing, nuint key, uint concurrency);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetQueuedCompletionStatus(KernelHandle port, out uint bytes, out nuint key, out nint overlapped, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint bytes, nint previous, nint returnSize);
    [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string applicationName, System.Text.StringBuilder commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string workingDirectory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(KernelHandle process, out uint code);
}

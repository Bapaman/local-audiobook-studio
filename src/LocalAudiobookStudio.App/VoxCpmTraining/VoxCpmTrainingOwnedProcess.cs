using System.Diagnostics;
using System.Runtime.InteropServices;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

internal interface IVoxCpmTrainingOwnedProcess : IDisposable
{
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    NativeJobMemoryObservation ReadJobMemory();
    Task<int> WaitForExitAsync(CancellationToken token = default);
    Task StopTreeAsync();
}
internal interface IVoxCpmTrainingProcessLauncher
{
    IVoxCpmTrainingOwnedProcess Start(ProcessStartInfo start, NativeJobMemoryPolicy policy);
}
internal interface IVoxCpmTrainingMemoryReader { ulong AvailableBytes(); }

internal sealed class VoxCpmTrainingProcessLauncher : IVoxCpmTrainingProcessLauncher
{
    public IVoxCpmTrainingOwnedProcess Start(ProcessStartInfo start, NativeJobMemoryPolicy policy) => new Owned(WindowsNativeLauncher.Start(start, policy));
    internal static IVoxCpmTrainingOwnedProcess RetainFailure(INativeLaunchCleanup cleanup) => new Failed(cleanup);
    private sealed class Owned(NativeChild child) : IVoxCpmTrainingOwnedProcess
    {
        public StreamReader StandardOutput => child.StandardOutput;
        public StreamReader StandardError => child.StandardError;
        public NativeJobMemoryObservation ReadJobMemory() => child.ReadJobMemory();
        public Task<int> WaitForExitAsync(CancellationToken token = default) => child.WaitForExitAsync(token);
        public Task StopTreeAsync() => child.StopTreeAsync();
        public void Dispose() => child.Dispose();
    }
    private sealed class Failed(INativeLaunchCleanup cleanup) : IVoxCpmTrainingOwnedProcess
    {
        public StreamReader StandardOutput => throw new InvalidOperationException();
        public StreamReader StandardError => throw new InvalidOperationException();
        public NativeJobMemoryObservation ReadJobMemory() => throw new InvalidOperationException();
        public Task<int> WaitForExitAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public Task StopTreeAsync() => cleanup.StopTreeAsync();
        public void Dispose() => cleanup.Dispose();
    }
}
internal sealed class VoxCpmTrainingMemoryReader : IVoxCpmTrainingMemoryReader
{
    public ulong AvailableBytes()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new VoxCpmTrainingExecutionException("voxcpm_training.memory.unavailable");
        return status.AvailablePhysical;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

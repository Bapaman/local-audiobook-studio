using System.Diagnostics;
using System.Runtime.InteropServices;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.App.TtsCorpus;

internal sealed class TtsCorpusProcessLauncher : ITtsCorpusProcessLauncher
{
    public ITtsCorpusOwnedProcess Start(ProcessStartInfo start, NativeJobMemoryPolicy policy) => new Owned(WindowsNativeLauncher.Start(start, policy));
    internal static ITtsCorpusOwnedProcess RetainFailure(INativeLaunchCleanup cleanup) => new FailedLaunch(cleanup);
    private sealed class FailedLaunch(INativeLaunchCleanup cleanup) : ITtsCorpusOwnedProcess
    {
        public StreamReader StandardOutput => throw new InvalidOperationException();
        public StreamReader StandardError => throw new InvalidOperationException();
        public NativeJobMemoryObservation ReadJobMemory() => throw new InvalidOperationException();
        public Task<int> WaitForExitAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public Task StopTreeAsync() => cleanup.StopTreeAsync();
        public void Dispose() => cleanup.Dispose();
    }
    private sealed class Owned(NativeChild child) : ITtsCorpusOwnedProcess
    {
        public StreamReader StandardOutput => child.StandardOutput;
        public StreamReader StandardError => child.StandardError;
        public NativeJobMemoryObservation ReadJobMemory() => child.ReadJobMemory();
        public Task<int> WaitForExitAsync(CancellationToken token = default) => child.WaitForExitAsync(token);
        public Task StopTreeAsync() => child.StopTreeAsync();
        public void Dispose() => child.Dispose();
    }
}

internal sealed class TtsCorpusMemoryReader : ITtsCorpusMemoryReader
{
    public ulong AvailableBytes()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new TtsCorpusExecutionException("tts_corpus.memory.unavailable");
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

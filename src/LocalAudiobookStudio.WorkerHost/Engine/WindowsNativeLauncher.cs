using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Engine;

public static class WindowsArgumentSerializer
{
    // MSVC argv parsing: double backslashes preceding a quote or the closing delimiter.
    public static string Serialize(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));
    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new WorkerException("worker.arguments");
        var quoted = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); quoted.Append(c); slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }
}

public static class WindowsNativeLauncher
{
    public static NativeChild Start(ProcessStartInfo start, NativeJobMemoryPolicy? memoryPolicy = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new WorkerException("worker.unsupported.platform");
        if (!Path.IsPathFullyQualified(start.FileName) || !Path.IsPathFullyQualified(start.WorkingDirectory) || start.UseShellExecute || !start.CreateNoWindow || !start.RedirectStandardOutput || !start.RedirectStandardError || start.Arguments.Length != 0)
            throw new WorkerException("worker.arguments");
        var job = new KillOnCloseJob(memoryPolicy);
        SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null, inRead = null, inWrite = null;
        nint attributes = 0, inherited = 0, jobValue = 0, environment = 0; bool initialized = false;
        KernelHandle? process = null;
        bool created = false;
        try
        {
            Pipe(out outRead, out outWrite); Pipe(out errRead, out errWrite); Pipe(out inRead, out inWrite);
            if (!Native.SetHandleInformation(outRead, 1, 0) || !Native.SetHandleInformation(errRead, 1, 0) || !Native.SetHandleInformation(inWrite, 1, 0)) throw new WorkerException("worker.launch");
            nuint size = 0; Native.InitializeProcThreadAttributeList(0, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw new WorkerException("worker.launch");
            initialized = true;
            inherited = Marshal.AllocHGlobal(nint.Size * 3);
            Marshal.WriteIntPtr(inherited, inRead.DangerousGetHandle()); Marshal.WriteIntPtr(inherited, nint.Size, outWrite.DangerousGetHandle()); Marshal.WriteIntPtr(inherited, nint.Size * 2, errWrite.DangerousGetHandle());
            jobValue = Marshal.AllocHGlobal(nint.Size); Marshal.WriteIntPtr(jobValue, job.Handle);
            // Creation-time association eliminates any executable interval outside the kill-on-close Job.
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x20002, inherited, (nuint)(nint.Size * 3), 0, 0) ||
                !Native.UpdateProcThreadAttribute(attributes, 0, 0x2000D, jobValue, (nuint)nint.Size, 0, 0)) throw new WorkerException("worker.launch");
            var startup = new Native.StartupInfoEx
            {
                Attributes = attributes,
                Startup = new Native.StartupInfo { Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100, StandardInput = inRead.DangerousGetHandle(), StandardOutput = outWrite.DangerousGetHandle(), StandardError = errWrite.DangerousGetHandle() }
            };
            string block = string.Join('\0', start.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            string command = WindowsArgumentSerializer.Serialize(new[] { start.FileName }.Concat(start.ArgumentList));
            if (command.Length >= 32767) throw new WorkerException("worker.arguments");
            if (!Native.CreateProcessW(start.FileName, new StringBuilder(command), 0, 0, true, 0x00080000 | 0x08000000 | 0x00000400, environment, start.WorkingDirectory, ref startup, out var info)) throw new WorkerException("worker.launch");
            created = true;
            process = new KernelHandle(info.Process); using var thread = new KernelHandle(info.Thread);
            outWrite.Dispose(); errWrite.Dispose(); inRead.Dispose(); inWrite.Dispose();
            var child = new NativeChild(process, job, new FileStream(outRead, FileAccess.Read), new FileStream(errRead, FileAccess.Read), (int)info.ProcessId);
            process = null; outRead = null; errRead = null; return child;
        }
        catch
        {
            if (created)
            {
                var cleanup = new NativeLaunchCleanupOwner(job, process);
                try { cleanup.StopTreeAsync().WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult(); }
                catch
                {
                    // Keep the exact created native owner alive; closing a Job is not a
                    // substitute for confirmed zero and must not become known cleanup.
                    process = null;
                    throw new NativeOwnedLaunchException(cleanup);
                }
            }
            process?.Dispose(); job.Dispose(); throw;
        }
        finally
        {
            outRead?.Dispose(); outWrite?.Dispose(); errRead?.Dispose(); errWrite?.Dispose(); inRead?.Dispose(); inWrite?.Dispose();
            if (initialized) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes); if (inherited != 0) Marshal.FreeHGlobal(inherited);
            if (jobValue != 0) Marshal.FreeHGlobal(jobValue); if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }
    private static void Pipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        var security = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = 1 };
        if (!Native.CreatePipe(out read, out write, ref security, 0)) throw new WorkerException("worker.launch");
    }
}

public sealed class NativeChild : IDisposable
{
    private readonly KernelHandle process;
    private readonly KillOnCloseJob job;
    private readonly Task<int> exit;
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public int ProcessId { get; }
    private int disposed;
    internal NativeChild(KernelHandle process, KillOnCloseJob job, Stream output, Stream error, int pid)
    {
        this.process = process; this.job = job; ProcessId = pid;
        StandardOutput = new(output, new UTF8Encoding(false), false, 4096); StandardError = new(error, new UTF8Encoding(false), false, 4096);
        WorkerLifetimeTrace.Report("native_started", ProcessId);
        exit = Task.Run(() =>
        {
            if (Native.WaitForSingleObject(process, uint.MaxValue) != 0 || !Native.GetExitCodeProcess(process, out uint code)) throw new WorkerException("worker.launch");
            WorkerLifetimeTrace.Report("root_exit", ProcessId);
            return unchecked((int)code);
        });
    }
    public Task<int> WaitForExitAsync(CancellationToken token = default) => exit.WaitAsync(token);
    public NativeJobMemoryObservation ReadJobMemory() => job.ReadJobMemory();
    public async Task StopTreeAsync() { await job.StopTreeAsync(); await exit.WaitAsync(TimeSpan.FromSeconds(5)); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        job.Dispose();
        // Kill-on-close runs before wait/stream disposal, so inherited descendant pipe ends cannot hang readers.
        try { exit.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        StandardOutput.Dispose(); StandardError.Dispose(); process.Dispose();
        WorkerLifetimeTrace.Report("native_disposed", ProcessId);
    }
}

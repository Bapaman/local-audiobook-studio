using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.WorkerHost.Engine;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.App.Workers;

/// <summary>Owns only the process created in its Windows Job. No PID discovery or output logging.</summary>
public sealed class WorkerProcessSupervisor : IWorkerSessionLifetime
{
    private readonly NativeChild child;
    private readonly Task stdout, stderr;
    private readonly TimeProvider time;
    private readonly object sync = new();
    private Task<bool>? cleanup;
    internal int ProcessId => child.ProcessId;
    internal bool DrainsCompleted => stdout.IsCompletedSuccessfully && stderr.IsCompletedSuccessfully;
    internal bool NativeDisposed { get; private set; }
    internal string PipeName { get; }
    internal byte[] Secret { get; }
    public bool StopRequested { get { lock (sync) return cleanup != null; } }

    private WorkerProcessSupervisor(NativeChild child, string pipeName, byte[] secret, TimeProvider time)
    {
        this.child = child; PipeName = pipeName; Secret = secret; this.time = time;
        stdout = Drain(child.StandardOutput); stderr = Drain(child.StandardError);
    }
    internal static WorkerProcessSupervisor Start(string hostExecutable, string projectRoot, DeveloperWorkerOptions options, TimeProvider time)
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var parent = Process.GetCurrentProcess();
            string pipeName = "las-worker-" + Guid.NewGuid().ToString("N");
            var start = BuildStartInfo(hostExecutable, projectRoot, options, pipeName, secret, parent.Id, parent.StartTime.ToUniversalTime().Ticks);
            return new(WindowsNativeLauncher.Start(start), pipeName, secret, time);
        }
        catch (Exception e) when (e is WorkerException or IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new WorkerClientException("worker.launch", true);
        }
    }
    internal static ProcessStartInfo BuildStartInfo(string executable, string projectRoot, DeveloperWorkerOptions options, string pipeName, byte[] secret, int parentPid, long parentStartUtcTicks)
    {
        foreach (string root in new[] { executable, projectRoot, options.RuntimeRoot, options.ModelRoot, options.LockRoot })
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new WorkerException("worker.configuration");
        var start = new ProcessStartInfo(WorkerPaths.Root(executable))
        {
            WorkingDirectory = WorkerPaths.Root(projectRoot), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "--pipe-name", pipeName, "--project-root", WorkerPaths.Root(projectRoot),
            "--runtime-root", WorkerPaths.Root(options.RuntimeRoot), "--model-root", WorkerPaths.Root(options.ModelRoot),
            "--lock-root", WorkerPaths.Root(options.LockRoot), "--parent-pid", parentPid.ToString(CultureInfo.InvariantCulture),
            "--parent-start-utc-ticks", parentStartUtcTicks.ToString(CultureInfo.InvariantCulture), "--timeout-seconds", "1800", "--developer-mode"
        }) start.ArgumentList.Add(argument);
        start.Environment["LAS_WORKER_SESSION_SECRET"] = Convert.ToBase64String(secret);
        return start;
    }

    public async Task<bool> StopAsync(TimeSpan remaining, CancellationToken token)
    {
        Task<bool> stopped;
        lock (sync) stopped = cleanup ??= Task.Run(StopCore);
        // Starting StopCore is required even if the caller's budget has expired.
        // Its continuation must never turn an unconfirmed timeout into safe reuse.
        if (remaining <= TimeSpan.Zero) return stopped.IsCompletedSuccessfully && stopped.Result;
        try { return await stopped.WaitAsync(remaining, time, token); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { return false; }
    }
    private async Task<bool> StopCore()
    {
        bool confirmed = false;
        try
        {
            await child.StopTreeAsync(); // Proves Job active count zero AND root exit.
            await Task.WhenAll(stdout, stderr);
            confirmed = true;
        }
        catch { /* Safe false, never an inferred success from a pipe ACK/root alone. */ }
        finally
        {
            child.Dispose(); NativeDisposed = true;
            CryptographicOperations.ZeroMemory(Secret);
        }
        return confirmed && DrainsCompleted && NativeDisposed;
    }
    private static async Task Drain(StreamReader stream)
    {
        char[] buffer = new char[4096];
        while (await stream.ReadAsync(buffer.AsMemory()) != 0) { }
        Array.Clear(buffer);
    }

    public sealed class Factory(WorkerRuntimeReadiness runtime, TimeProvider time, WorkerClientOptions options) : IWorkerClientFactory
    {
        public async Task<IWorkerClient> OpenAsync(string projectRoot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Validate before launching: ConnectAsync cannot own a session when
            // configuration validation fails before construction.
            try { options.Validate(); }
            catch (WorkerClientException e) { throw new WorkerClientException(e.Code, true); }
            _ = await ((IRuntimeDescriptor)runtime).GetAsync(token);
            token.ThrowIfCancellationRequested();
            var owner = Start(runtime.HostExecutable, projectRoot, runtime.Options!, time);
            try
            {
                var client = await WorkerPipeClient.ConnectAsync(owner.PipeName, owner.Secret, owner, time, options, token);
                try { return new ProjectScopedWorkerClient(projectRoot, client); }
                catch { await client.DisposeAsync(); throw; }
            }
            finally { CryptographicOperations.ZeroMemory(owner.Secret); }
        }
    }
}

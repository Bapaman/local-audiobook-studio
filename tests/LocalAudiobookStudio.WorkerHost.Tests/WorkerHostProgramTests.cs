using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Contracts.Protocol;

namespace LocalAudiobookStudio.WorkerHost.Tests;

public class WorkerHostProgramTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string HostExe => Path.Combine(AppContext.BaseDirectory, "LocalAudiobookStudio.WorkerHost.exe");
    [Fact]
    public async Task Configuration_failure_is_nonzero_and_emits_no_private_arguments()
    {
        using var host = Process.Start(new ProcessStartInfo(HostExe) { UseShellExecute = false, RedirectStandardError = true, ArgumentList = { "--project-root", "PRIVATE-PATH-SENTINEL" } })!;
        string error = await host.StandardError.ReadToEndAsync(Token); await host.WaitForExitAsync(Token);
        Assert.Equal(2, host.ExitCode); Assert.Equal("worker.configuration" + Environment.NewLine, error);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Abrupt_host_kill_and_authenticated_disconnect_terminate_native_tree(bool killHost)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new VoxCpmProcessRunnerTests.RunnerFixture();
        string pipeName = "las-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(HostExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var parent = Process.GetCurrentProcess();
        string[] options = ["--pipe-name", pipeName, "--project-root", fixture.Root, "--runtime-root", fixture.Artifacts.Runtime, "--model-root", fixture.Artifacts.Model, "--lock-root", fixture.Artifacts.Locks,
            "--parent-pid", Environment.ProcessId.ToString(), "--parent-start-utc-ticks", parent.StartTime.ToUniversalTime().Ticks.ToString(), "--timeout-seconds", "30", "--developer-mode"];
        foreach (string option in options) start.ArgumentList.Add(option); start.Environment["LAS_WORKER_SESSION_SECRET"] = secret;
        using var host = Process.Start(start)!;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, Token); var codec = new ProtocolCodec(pipe);
            await codec.WriteAsync(new ProtocolEnvelope<HandshakeCommand>(1, "h", "handshake", "2026-10-06T12:00:00Z", new(secret)), Token);
            Assert.Equal("result", (await codec.ReadAsync(Token))!.Type);
            string eventName = "Local\\las-" + Guid.NewGuid().ToString("N"); using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            await codec.WriteAsync(new ProtocolEnvelope<SynthesizeSegmentCommand>(1, "s", "synthesizeSegment", "2026-10-06T12:00:00Z", fixture.Command("tree-" + eventName)), Token);
            Assert.True(await Task.Run(() => ready.WaitOne(5000), Token));
            string[] ids = File.ReadAllText(fixture.Part + ".pids").Split(',');
            using var main = Process.GetProcessById(int.Parse(ids[0])); using var child = Process.GetProcessById(int.Parse(ids[1])); using var grandchild = Process.GetProcessById(int.Parse(ids[2]));
            if (killHost) host.Kill(); else pipe.Dispose();
            await Task.WhenAll(host.WaitForExitAsync(Token), main.WaitForExitAsync(Token), child.WaitForExitAsync(Token), grandchild.WaitForExitAsync(Token)).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(main.HasExited); Assert.True(child.HasExited); Assert.True(grandchild.HasExited); Assert.False(File.Exists(fixture.Final));
            if (!killHost) Assert.False(File.Exists(fixture.Part));
            string errors = await host.StandardError.ReadToEndAsync(Token);
            Assert.DoesNotContain(secret, errors); Assert.DoesNotContain(fixture.Root, errors); Assert.DoesNotContain("tree-", errors);
        }
        finally { if (!host.HasExited) { host.Kill(); await host.WaitForExitAsync(Token); } }
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.App.TtsCorpus;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TtsCorpusNativeExecutorTests
{
    [Fact]
    public async Task Insufficient_ram_rejects_before_process_launch()
    {
        using var fixture = new CorpusProtocolFixture(); var launcher = new CorpusFakeLauncher(fixture);
        await using var executor = new TtsCorpusNativeExecutor(new(fixture.Options, new CorpusFakeInventory()), launcher, new CorpusFakeMemory(17179869183));
        var error = await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => executor.RunAsync(fixture.Execution, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal("tts_corpus.memory.insufficient", error.Code); Assert.Null(launcher.StartInfo);
    }

    [Fact]
    public async Task Runtime_mismatch_rejects_before_process_launch()
    {
        using var fixture = new CorpusProtocolFixture(); var launcher = new CorpusFakeLauncher(fixture);
        await using var executor = new TtsCorpusNativeExecutor(new(fixture.Options, new CorpusFakeInventory { Fingerprint = new string('d', 64) }), launcher, new CorpusFakeMemory());
        var error = await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => executor.RunAsync(fixture.Execution, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal("tts_corpus.runtime.changed", error.Code); Assert.Null(launcher.StartInfo);
    }

    [Fact]
    public async Task Ram_is_rechecked_after_inventory_pinning_immediately_before_launch()
    {
        using var fixture = new CorpusProtocolFixture(); var launcher = new CorpusFakeLauncher(fixture);
        await using var executor = new TtsCorpusNativeExecutor(new(fixture.Options, new CorpusFakeInventory()), launcher,
            new CorpusSequenceMemory(17179869184, 17179869183));
        var error = await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => executor.RunAsync(fixture.CreateSource(), _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal("tts_corpus.memory.insufficient", error.Code); Assert.Null(launcher.StartInfo);
    }

    [Fact]
    public async Task Reserve_violation_before_start_does_not_release_gate()
    {
        using var fixture = new CorpusProtocolFixture(); var launcher = new CorpusFakeLauncher(fixture);
        await using var executor = new TtsCorpusNativeExecutor(new(fixture.Options, new CorpusFakeInventory()), launcher,
            new CorpusSequenceMemory(17179869184, 17179869184, 3221225471));
        var error = await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => executor.RunAsync(fixture.CreateSource(), _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal("tts_corpus.memory.reserve", error.Code);
        Assert.True(launcher.Process.StopCalls >= 1); Assert.False(launcher.Process.SawExactStart);
        Assert.Empty(File.ReadAllBytes(launcher.Process.Gate));
    }

    [Fact]
    public async Task Successful_root_exit_still_stops_owned_tree_before_accepting_receipt()
    {
        using var fixture = new CorpusProtocolFixture(); var inventory = new CorpusFakeInventory(); var launcher = new CorpusFakeLauncher(fixture);
        await using var executor = new TtsCorpusNativeExecutor(new(fixture.Options, inventory), launcher, new CorpusFakeMemory());
        var result = await executor.RunAsync(fixture.CreateSource(), _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.True(result.Stopped); Assert.True(result.CleanupConfirmed);
        Assert.True(launcher.Process.StopCalls >= 1); Assert.True(launcher.Process.Disposed); Assert.True(inventory.Released);
        Assert.Equal(new[] { "-I", "-S", "-B", "-u" }, launcher.StartInfo!.ArgumentList.Take(4));
        Assert.Equal(13958643712, launcher.Policy!.AggregateJobMemoryLimitBytes);
        Assert.Equal("1", launcher.StartInfo.Environment["HF_HUB_OFFLINE"]);
        Assert.True(launcher.Process.SawExactStart);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("exit")]
    [InlineData("limit")]
    public async Task Invalid_owned_window_is_rejected_and_cleaned(string failure)
    {
        using var fixture = new CorpusProtocolFixture(); var launcher = new CorpusFakeLauncher(fixture) { Failure = failure };
        await using var executor = new TtsCorpusNativeExecutor(new(fixture.Options, new CorpusFakeInventory()), launcher, new CorpusFakeMemory());
        var error = await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => executor.RunAsync(fixture.CreateSource(), _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.True(error.CleanupConfirmed); Assert.True(launcher.Process.StopCalls >= 1); Assert.True(launcher.Process.Disposed);
    }

    [Fact]
    public async Task Unknown_job_zero_keeps_native_runtime_and_source_ownership()
    {
        using var fixture = new CorpusProtocolFixture(); var inventory = new CorpusFakeInventory(); var launcher = new CorpusFakeLauncher(fixture);
        var executor = new TtsCorpusNativeExecutor(new(fixture.Options, inventory), launcher, new CorpusFakeMemory());
        launcher.Process.StopFails = true;
        var error = await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => executor.RunAsync(fixture.CreateSource(), _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.False(error.CleanupConfirmed); Assert.False(launcher.Process.Disposed); Assert.False(inventory.Released);
        try
        {
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.Execution.SourcePath, "changed"));
            await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => executor.RunAsync(fixture.Execution, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        }
        finally { launcher.Process.StopFails = false; await executor.DisposeAsync(); }
        Assert.True(inventory.Released);
    }

    [Fact]
    public async Task Launch_construction_cleanup_unknown_also_retains_pins_and_exact_owner()
    {
        using var fixture = new CorpusProtocolFixture(); var inventory = new CorpusFakeInventory();
        var launcher = new CorpusFakeLauncher(fixture) { Failure = "launch-unknown" };
        var executor = new TtsCorpusNativeExecutor(new(fixture.Options, inventory), launcher, new CorpusFakeMemory());
        try
        {
            var error = await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => executor.RunAsync(fixture.CreateSource(), _ => Task.CompletedTask, TestContext.Current.CancellationToken));
            Assert.False(error.CleanupConfirmed); Assert.False(inventory.Released); Assert.False(launcher.Process.Disposed);
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.Execution.SourcePath, "changed"));
        }
        finally { await executor.DisposeAsync(); }
        Assert.True(inventory.Released); Assert.True(launcher.Process.Disposed);
    }
}

internal sealed class CorpusFakeMemory(ulong available = 17179869184) : ITtsCorpusMemoryReader { public ulong AvailableBytes() => available; }
internal sealed class CorpusSequenceMemory(params ulong[] values) : ITtsCorpusMemoryReader
{
    private int cursor;
    public ulong AvailableBytes() => values[Math.Min(Interlocked.Increment(ref cursor) - 1, values.Length - 1)];
}
internal sealed class CorpusFakeLauncher(CorpusProtocolFixture fixture) : ITtsCorpusProcessLauncher
{
    internal ProcessStartInfo? StartInfo { get; private set; }
    internal NativeJobMemoryPolicy? Policy { get; private set; }
    internal string? Failure { get; init; }
    internal CorpusFakeProcess Process { get; } = new();
    public ITtsCorpusOwnedProcess Start(ProcessStartInfo start, NativeJobMemoryPolicy policy)
    {
        StartInfo = start; Policy = policy;
        if (Failure == "launch-unknown") throw new NativeOwnedLaunchException(Process);
        string requestPath = start.ArgumentList[start.ArgumentList.IndexOf("--request") + 1];
        byte[] bytes = File.ReadAllBytes(requestPath); using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
        var request = new TtsCorpusInvocation(fixture.CreateSource(), root.GetProperty("windowId").GetString()!, root.GetProperty("requestId").GetString()!,
            root.GetProperty("outputRelativeRoot").GetString()!, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        fixture.WriteStopReceipt(request); Process.Gate = start.ArgumentList[start.ArgumentList.IndexOf("--gate") + 1];
        Process.Output = Failure == "stream" ? "private transcript\n" : "tts_corpus.worker.gated\ntts_corpus.worker.ready\n";
        Process.ExitCode = Failure == "exit" ? 1 : 0; Process.WrongLimits = Failure == "limit";
        return Process;
    }
}
internal sealed class CorpusFakeProcess : ITtsCorpusOwnedProcess, INativeLaunchCleanup
{
    internal string Output { set => StandardOutput = new(new MemoryStream(Encoding.UTF8.GetBytes(value))); }
    public StreamReader StandardOutput { get; private set; } = new(new MemoryStream());
    public StreamReader StandardError { get; } = new(new MemoryStream());
    internal string Gate { get; set; } = "";
    internal int ExitCode { get; set; }
    internal bool WrongLimits { get; set; }
    internal bool StopFails { get; set; }
    internal int StopCalls { get; private set; }
    internal bool Disposed { get; private set; }
    internal bool SawExactStart { get; private set; }
    public NativeJobMemoryObservation ReadJobMemory() => new(0x2210, 3, WrongLimits ? 0ul : 13958643712ul, 100);
    public async Task<int> WaitForExitAsync(CancellationToken token = default)
    {
        for (int i = 0; i < 100 && File.Exists(Gate) && new FileInfo(Gate).Length == 0; i++) await Task.Delay(5, token);
        if (File.Exists(Gate))
        {
            // Match Python's read sharing while the parent retains its gate writer.
            using var input = new FileStream(Gate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            SawExactStart = bytes.SequenceEqual("START\n"u8.ToArray());
        }
        return ExitCode;
    }
    public Task StopTreeAsync()
    {
        StopCalls++; if (StopFails) throw new IOException("unknown"); return Task.CompletedTask;
    }
    public void Dispose() { Disposed = true; StandardOutput.Dispose(); StandardError.Dispose(); }
}

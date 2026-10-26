using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmTrainingNativeExecutorTests
{
    [Fact]
    public async Task Already_completed_terminal_signal_is_never_filtered_out_of_wait_set()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Same(Task.CompletedTask, await VoxCpmTrainingNativeExecutor.NextSignal(Task.CompletedTask, pending.Task).WaitAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Deadline_rejects_before_start_and_cleans_owned_tree()
    {
        using var f = new VoxProtocolFixture(local: true); f.PrepareNative(); var inventory = new VoxNativeInventory(f); var launcher = new VoxNativeLauncher(f);
        await using var executor = new VoxCpmTrainingNativeExecutor(new(f.Options, inventory), launcher, new VoxNativeMemory(), new DeadlineClock());
        int acceptedCount = 0;
        var error = await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(async () =>
        { var result = await executor.RunAsync(f.Execution, _ => Task.CompletedTask, TestContext.Current.CancellationToken); if (result.Checkpoint != null) acceptedCount++; });
        Assert.Equal("voxcpm_training.window.deadline", error.Code); Assert.Equal(0, acceptedCount); Assert.False(launcher.Child.SawStart); Assert.True(launcher.Child.Zero);
    }
    [Fact]
    public async Task Insufficient_memory_never_admits_runtime_or_launches()
    {
        using var f = new VoxProtocolFixture(local: true); f.PrepareNative(); var inventory = new VoxNativeInventory(f); var launcher = new VoxNativeLauncher(f);
        await using var executor = new VoxCpmTrainingNativeExecutor(new(f.Options, inventory), launcher, new VoxNativeMemory(17179869183));
        var error = await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => executor.RunAsync(f.Execution, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal("voxcpm_training.memory.insufficient", error.Code); Assert.Equal(0, inventory.Opened); Assert.Null(launcher.StartInfo);
    }
    [Fact]
    public async Task Realistic_gated_success_checks_job_zero_before_acceptance_and_releases_pins()
    {
        using var f = new VoxProtocolFixture(local: true); f.PrepareNative(); var inventory = new VoxNativeInventory(f); var launcher = new VoxNativeLauncher(f);
        await using var executor = new VoxCpmTrainingNativeExecutor(new(f.Options, inventory), launcher, new VoxNativeMemory());
        var phases = new List<string>(); var result = await executor.RunAsync(f.Execution, p => { phases.Add(p.Phase); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.NotNull(result.Checkpoint); Assert.Equal(2, result.Checkpoint.Cursor.GlobalStep); Assert.True(launcher.Child.SawStart); Assert.True(launcher.Child.Zero);
        Assert.True(launcher.Child.Disposed); Assert.Equal(1, inventory.Released);
        Assert.Equal(new[] { "loading", "training", "checkpointing", "checkpointing" }, phases);
        Assert.Equal(new[] { "-I", "-S", "-B", "-u" }, launcher.StartInfo!.ArgumentList.Take(4));
        Assert.Equal(13958643712, launcher.Policy!.AggregateJobMemoryLimitBytes); Assert.Equal("1", launcher.StartInfo.Environment["HF_HUB_OFFLINE"]);
    }
    [Theory]
    [InlineData("limits")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    [InlineData("exit")]
    [InlineData("acceptance")]
    [InlineData("cache")]
    public async Task Failed_window_accepts_nothing_and_keeps_cache_until_zero(string fault)
    {
        using var f = new VoxProtocolFixture(local: true); f.PrepareNative(); var inventory = new VoxNativeInventory(f); var launcher = new VoxNativeLauncher(f) { Fault = fault };
        var runtime = new VoxCpmTrainingRuntime(f.Options, inventory);
        await using var executor = new VoxCpmTrainingNativeExecutor(runtime, launcher, new VoxNativeMemory()); int acceptedCount = 0;
        var error = await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(async () =>
        { var result = await executor.RunAsync(f.Execution, _ => Task.CompletedTask, TestContext.Current.CancellationToken); if (result.Checkpoint != null) acceptedCount++; });
        Assert.Equal(0, acceptedCount); Assert.True(error.CleanupConfirmed); Assert.True(launcher.Child.Zero); Assert.True(launcher.Child.Disposed); Assert.Equal(1, inventory.Released);
        if (fault == "limits") Assert.False(launcher.Child.SawStart);
        if (fault == "cache") { Assert.Equal("voxcpm_training.cache.changed", error.Code); Assert.False((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_cleanup_retains_every_owner_until_exact_child_later_proves_zero(bool launchFailure)
    {
        using var f = new VoxProtocolFixture(local: true); f.PrepareNative(); var inventory = new VoxNativeInventory(f);
        var launcher = new VoxNativeLauncher(f) { Fault = launchFailure ? "launch-unknown" : null }; launcher.Child.StopFails = true;
        var executor = new VoxCpmTrainingNativeExecutor(new(f.Options, inventory), launcher, new VoxNativeMemory()); int acceptedCount = 0;
        try
        {
            var error = await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(async () =>
            { var result = await executor.RunAsync(f.Execution, _ => Task.CompletedTask, TestContext.Current.CancellationToken); if (result.Checkpoint != null) acceptedCount++; });
            Assert.False(error.CleanupConfirmed); Assert.Equal(0, acceptedCount); Assert.Equal(0, inventory.Released); Assert.False(launcher.Child.Disposed);
            string audio = Path.Combine(f.Execution.ProjectRoot, f.Execution.Corpus.Clips[1].AudioRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Throws<IOException>(() => File.WriteAllText(audio, "changed"));
            await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingCacheLease.AcquireAsync(f.Options.CachePath, 4, f.Execution.Identity.CacheSha256, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => executor.RunAsync(f.Execution, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        }
        finally { launcher.Child.StopFails = false; await executor.DisposeAsync(); }
        Assert.True(launcher.Child.Zero); Assert.True(launcher.Child.Disposed); Assert.Equal(1, inventory.Released);
    }
    [Theory]
    [InlineData(17179869183ul, "voxcpm_training.memory.insufficient")]
    [InlineData(3221225471ul, "voxcpm_training.memory.reserve")]
    public async Task Memory_rechecked_after_admission_and_before_gate(ulong low, string expected)
    {
        using var f = new VoxProtocolFixture(local: true); f.PrepareNative(); var inventory = new VoxNativeInventory(f); var launcher = new VoxNativeLauncher(f);
        ulong[] readings = low == 17179869183 ? [17179869184, low] : [17179869184, 17179869184, low];
        await using var executor = new VoxCpmTrainingNativeExecutor(new(f.Options, inventory), launcher, new VoxNativeMemory(readings));
        var error = await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => executor.RunAsync(f.Execution, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal(expected, error.Code); Assert.False(launcher.Child.SawStart); Assert.Equal(1, inventory.Released);
    }
    [Fact]
    public async Task Root_exit_is_not_job_zero_and_does_not_release_or_accept_early()
    {
        using var f = new VoxProtocolFixture(local: true); f.PrepareNative(); var inventory = new VoxNativeInventory(f); var launcher = new VoxNativeLauncher(f);
        launcher.Child.ZeroGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new VoxCpmTrainingNativeExecutor(new(f.Options, inventory), launcher, new VoxNativeMemory());
        var running = executor.RunAsync(f.Execution, _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await launcher.Child.StopEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(running.IsCompleted); Assert.Equal(0, inventory.Released); launcher.Child.ZeroGate.SetResult();
        Assert.NotNull((await running).Checkpoint);
    }
}
internal sealed class DeadlineClock : TimeProvider
{
    private int calls;
    public override long TimestampFrequency => 1;
    public override long GetTimestamp() => Interlocked.Increment(ref calls) == 1 ? 0 : 300;
}
internal sealed class VoxNativeMemory(params ulong[] readings) : IVoxCpmTrainingMemoryReader
{
    private int cursor;
    public ulong AvailableBytes() => readings.Length == 0 ? 17179869184 : readings[Math.Min(Interlocked.Increment(ref cursor) - 1, readings.Length - 1)];
}
internal sealed class VoxNativeInventory(VoxProtocolFixture fixture) : IVoxCpmTrainingRuntimeInventory
{
    internal int Opened, Released;
    public Task<VoxCpmTrainingRuntimeLease> OpenAsync(VoxCpmTrainingRuntimeOptions options, CancellationToken token)
    {
        Opened++; var r = fixture.Runtime;
        return Task.FromResult(new VoxCpmTrainingRuntimeLease(r.Identity, r.ModelFiles, r.SourceFiles, r.AdapterLayout, r.RuntimeInventorySha256, new Release(this), 4));
    }
    private sealed class Release(VoxNativeInventory owner) : IDisposable { public void Dispose() => owner.Released++; }
}
internal sealed class VoxNativeLauncher(VoxProtocolFixture fixture) : IVoxCpmTrainingProcessLauncher
{
    internal string? Fault { get; init; }
    internal ProcessStartInfo? StartInfo; internal NativeJobMemoryPolicy? Policy;
    internal VoxNativeChild Child { get; } = new();
    public IVoxCpmTrainingOwnedProcess Start(ProcessStartInfo start, NativeJobMemoryPolicy policy)
    {
        StartInfo = start; Policy = policy;
        if (Fault == "launch-unknown") throw new NativeOwnedLaunchException(Child);
        Child.WrongLimits = Fault == "limits";
        string requestPath = start.ArgumentList[start.ArgumentList.IndexOf("--request") + 1]; byte[] request = File.ReadAllBytes(requestPath);
        using var doc = JsonDocument.Parse(request);
        var invocation = new VoxCpmTrainingInvocation(fixture.Execution, request, VoxCpmTrainingProtocol.Digest(request), doc.RootElement.GetProperty("requestId").GetString()!, doc.RootElement.GetProperty("outputRelativeRoot").GetString()!);
        string gate = start.ArgumentList[start.ArgumentList.IndexOf("--gate") + 1];
        Child.Start(gate, () => fixture.PublishBundle(invocation, Fault == "acceptance" ? "wrong-hash" : null), Fault,
            () => { using var file = new FileStream(fixture.Options.CachePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); file.WriteByte(9); file.Flush(true); });
        return Child;
    }
}
internal sealed class VoxNativeChild : IVoxCpmTrainingOwnedProcess, INativeLaunchCleanup
{
    private readonly VoxFeedStream output = new(), error = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? worker;
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    internal bool WrongLimits, StopFails, SawStart, Zero, Disposed;
    internal TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource? ZeroGate;
    internal VoxNativeChild() { StandardOutput = new(output); StandardError = new(error); }
    internal void Start(string gate, Action publish, string? fault, Action mutate)
    {
        output.Emit("voxcpm_training.worker.gated\n"); error.End();
        if (fault == "stderr") { error.Reset(); error.Emit(new string('x', 4097)); error.End(); }
        worker = Work();
        async Task Work()
        {
            try
            {
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    using var file = new FileStream(gate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    byte[] value = new byte[file.Length]; file.ReadExactly(value);
                    if (value.AsSpan().SequenceEqual("START\n"u8)) { SawStart = true; break; }
                    await Task.Delay(2, stop.Token);
                }
                if (fault == "stdout") output.Emit(new string('x', 4097));
                else
                {
                    output.Emit("voxcpm_training.worker.loading\nvoxcpm_training.worker.training\nvoxcpm_training.worker.checkpointing\nvoxcpm_training.worker.checkpointing\n");
                    publish(); if (fault == "cache") mutate(); output.Emit("voxcpm_training.worker.ready\n");
                }
                output.End(); exit.TrySetResult(fault is "exit" or "cache" ? 1 : 0);
            }
            catch (OperationCanceledException) { output.End(); exit.TrySetResult(1); }
            catch (Exception e) { output.End(); exit.TrySetException(e); }
        }
    }
    public NativeJobMemoryObservation ReadJobMemory() => new(WrongLimits ? 0u : 0x2210, 3, 13958643712, 100);
    public Task<int> WaitForExitAsync(CancellationToken token = default) => exit.Task.WaitAsync(token);
    public async Task StopTreeAsync()
    {
        StopEntered.TrySetResult(); if (StopFails) throw new IOException("unknown");
        if (ZeroGate != null) await ZeroGate.Task;
        stop.Cancel(); if (worker != null) await worker; Zero = true;
    }
    public void Dispose() { Disposed = true; StandardOutput.Dispose(); StandardError.Dispose(); stop.Dispose(); }
}
internal sealed class VoxFeedStream : Stream
{
    private Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>(); private byte[] current = []; private int cursor;
    internal void Reset() => chunks = Channel.CreateUnbounded<byte[]>();
    internal void Emit(string text) => Assert.True(chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text)));
    internal void End() => chunks.Writer.TryComplete();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (cursor == current.Length)
        { if (!await chunks.Reader.WaitToReadAsync(cancellationToken)) return 0; if (chunks.Reader.TryRead(out var value)) { current = value; cursor = 0; } }
        int count = Math.Min(buffer.Length, current.Length - cursor); current.AsMemory(cursor, count).CopyTo(buffer); cursor += count; return count;
    }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

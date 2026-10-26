using LocalAudiobookStudio.App.TtsCorpus;
using LocalAudiobookStudio.Application.TtsCorpus;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TtsCorpusRuntimeTests
{
    [Fact]
    public async Task Unconfigured_runtime_reports_not_ready_without_a_process()
    {
        var runtime = new TtsCorpusRuntime(null);
        var readiness = await runtime.GetReadinessAsync(TestContext.Current.CancellationToken);
        Assert.False(readiness.Ready); Assert.Equal("tts_corpus.runtime.not_ready", readiness.DiagnosticCode);
    }

    [Fact]
    public async Task Missing_explicit_runtime_stays_not_ready_and_creates_no_directories()
    {
        using var fixture = new CorpusProtocolFixture(); var runtime = new TtsCorpusRuntime(fixture.Options);
        var result = await runtime.GetReadinessAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Ready); Assert.Equal("tts_corpus.runtime.not_ready", result.DiagnosticCode);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "workers")));
        await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => runtime.GetFingerprintAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Runtime_fingerprint_comes_from_fresh_validated_inventory_each_time()
    {
        using var fixture = new CorpusProtocolFixture(); var inventory = new CorpusFakeInventory();
        var runtime = new TtsCorpusRuntime(fixture.Options, inventory);
        Assert.Equal(new string('b', 64), await runtime.GetFingerprintAsync(TestContext.Current.CancellationToken));
        inventory.Fingerprint = new string('c', 64);
        Assert.Equal(new string('c', 64), await runtime.GetFingerprintAsync(TestContext.Current.CancellationToken));
        inventory.Failure = new TtsCorpusExecutionException("tts_corpus.runtime.changed");
        Assert.False((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
    }

    [Fact]
    public async Task Positive_readiness_is_advisory_cached_for_at_most_five_minutes_without_retaining_pins()
    {
        using var fixture = new CorpusProtocolFixture(); var inventory = new CorpusFakeInventory(); var time = new CorpusManualTime();
        var runtime = new TtsCorpusRuntime(fixture.Options, inventory, time);
        Assert.True((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        Assert.True(inventory.Released);
        time.Advance(TimeSpan.FromMilliseconds(299999));
        Assert.True((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready); Assert.Equal(1, inventory.OpenCalls);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready); Assert.Equal(2, inventory.OpenCalls);
    }

    [Fact]
    public async Task Negative_readiness_is_never_cached_and_execution_admission_bypasses_positive_cache()
    {
        using var fixture = new CorpusProtocolFixture(); var inventory = new CorpusFakeInventory { Failure = new IOException() };
        var runtime = new TtsCorpusRuntime(fixture.Options, inventory, new CorpusManualTime());
        Assert.False((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        inventory.Failure = null;
        Assert.True((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready); Assert.Equal(2, inventory.OpenCalls);
        inventory.Failure = new IOException();
        Assert.True((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => runtime.GetFingerprintAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => runtime.AcquireAsync(TestContext.Current.CancellationToken));
        Assert.Equal(4, inventory.OpenCalls);
    }

    [Fact]
    public async Task Concurrent_readiness_requests_coalesce_one_serialized_admission()
    {
        using var fixture = new CorpusProtocolFixture(); var inventory = new CorpusBlockingInventory();
        var runtime = new TtsCorpusRuntime(fixture.Options, inventory, new CorpusManualTime());
        Task<TtsCorpusReadinessDto> first = runtime.GetReadinessAsync(TestContext.Current.CancellationToken);
        Task<TtsCorpusReadinessDto> second = runtime.GetReadinessAsync(TestContext.Current.CancellationToken);
        try { Assert.Equal(1, inventory.OpenCalls); }
        finally { inventory.Release.TrySetResult(); await Task.WhenAll(first, second); }
        Assert.Equal(1, inventory.OpenCalls);
    }
}

internal sealed class CorpusFakeInventory : ITtsCorpusRuntimeInventory
{
    internal string Fingerprint { get; set; } = new string('b', 64);
    internal Exception? Failure { get; set; }
    internal bool Released { get; private set; }
    internal int OpenCalls { get; private set; }
    public Task<TtsCorpusRuntimeLease> OpenAsync(TtsCorpusRuntimeOptions options, CancellationToken token)
    {
        OpenCalls++;
        if (Failure != null) throw Failure;
        return Task.FromResult(new TtsCorpusRuntimeLease(Fingerprint, [new("config.json", 1, new string('c', 64))], new Release(this)));
    }
    private sealed class Release(CorpusFakeInventory owner) : IDisposable { public void Dispose() => owner.Released = true; }
}

internal sealed class CorpusManualTime : TimeProvider
{
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    internal void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
}
internal sealed class CorpusBlockingInventory : ITtsCorpusRuntimeInventory
{
    internal int OpenCalls;
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<TtsCorpusRuntimeLease> OpenAsync(TtsCorpusRuntimeOptions options, CancellationToken token)
    {
        Interlocked.Increment(ref OpenCalls); await Release.Task.WaitAsync(token);
        return new(new string('b', 64), [], new Empty());
    }
    private sealed class Empty : IDisposable { public void Dispose() { } }
}

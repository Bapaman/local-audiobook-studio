using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.WorkerHost.Engine;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Tests;

/// <summary>In-process only: no native launcher or executable is invoked.</summary>
public sealed class OwnedLaunchFailureSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static SynthesizeSegmentCommand Command => new("Тест", "prompt.wav", "Речь", "output/clip.wav", "q8-f16-cpu", 70, 10, 2, 1, 42);

    [Fact]
    public async Task Vox_retains_exact_owner_prompt_and_artifacts_and_never_reuses_poisoned_engine()
    {
        using var fixture = new OwnedFailureFiles(); var cleanup = new CountingCleanup();
        var failure = new NativeOwnedLaunchException(cleanup); int launches = 0, resolutions = 0;
        var runner = new VoxCpmProcessRunner(fixture.Root, (_, _) =>
        {
            resolutions++;
            return Task.FromResult(new ResolvedArtifacts(fixture.Native, fixture.Model, fixture.Model, true, 0,
                [new(fixture.Native, FileMode.Open, FileAccess.Read, FileShare.Read), new(fixture.Model, FileMode.Open, FileAccess.Read, FileShare.Read)]));
        }, TimeSpan.FromSeconds(5), _ => { launches++; throw failure; });
        try
        {
            Assert.Same(failure, await Assert.ThrowsAsync<NativeOwnedLaunchException>(() => runner.SynthesizeAsync(Command, _ => { }, Token)));
            Assert.True(File.Exists(fixture.Part));
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.Prompt, "changed"));
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.Model, "changed"));
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.Native, "changed"));
            var again = await Assert.ThrowsAsync<NativeOwnedLaunchException>(() => runner.SynthesizeAsync(Command, _ => { }, Token));
            Assert.Same(cleanup, again.Ownership); Assert.Equal(1, launches); Assert.Equal(1, resolutions);
            Assert.False(cleanup.Disposed); Assert.Equal(0, cleanup.StopCalls);
        }
        finally { await runner.DisposeAsync(); }
        Assert.True(cleanup.Disposed); Assert.Equal(1, cleanup.StopCalls);
        File.WriteAllText(fixture.Prompt, "released"); File.WriteAllText(fixture.Model, "released");
    }

    [Fact]
    public async Task Pipe_closes_on_typed_unknown_cleanup_without_reusable_terminal_or_ready()
    {
        var engine = new UnknownCleanupEngine();
        string name = "las-owned-failure-" + Guid.NewGuid().ToString("N");
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var server = new WorkerPipeServer(name, secret, engine, TimeSpan.FromHours(1));
        Task running = server.RunAsync(stop.Token);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(5000, Token); var codec = new ProtocolCodec(client);
            await codec.WriteAsync(new ProtocolEnvelope<object>(1, "h", "handshake", "2026-10-17T00:00:00Z", new HandshakeCommand(secret)), Token);
            Assert.Equal("result", (await codec.ReadAsync(Token))!.Type);
            await codec.WriteAsync(new ProtocolEnvelope<object>(1, "s", "synthesizeSegment", "2026-10-17T00:00:00Z", Command), Token);
            await engine.Called.Task.WaitAsync(Token);
            await running.WaitAsync(TimeSpan.FromSeconds(2), Token);
            Assert.Equal("closed", server.State);
            var messages = new List<ProtocolEnvelope<JsonElement>>();
            while (await codec.ReadAsync(Token) is { } message) messages.Add(message);
            Assert.DoesNotContain(messages, m => m.RequestId == "s" && m.Type is "result" or "error");
            Assert.DoesNotContain(messages, m => m.Type == "heartbeat" && m.Payload.GetProperty("state").GetString() == "ready");
            Assert.False(engine.Cleanup.Disposed);
        }
        finally { stop.Cancel(); client.Dispose(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    [Fact]
    public async Task Failed_shutdown_retry_still_retains_legacy_prompt_artifacts_and_staged_file()
    {
        using var fixture = new OwnedFailureFiles(); var cleanup = new CountingCleanup();
        var failure = new NativeOwnedLaunchException(cleanup);
        var runner = new VoxCpmProcessRunner(fixture.Root, (_, _) => Task.FromResult(new ResolvedArtifacts(fixture.Native, fixture.Model, fixture.Model, true, 0,
            [new(fixture.Native, FileMode.Open, FileAccess.Read, FileShare.Read), new(fixture.Model, FileMode.Open, FileAccess.Read, FileShare.Read)])),
            TimeSpan.FromSeconds(5), _ => throw failure);
        try
        {
            await Assert.ThrowsAsync<NativeOwnedLaunchException>(() => runner.SynthesizeAsync(Command, _ => { }, Token));
            cleanup.Fail = true;
            Assert.Same(failure, await Assert.ThrowsAsync<NativeOwnedLaunchException>(() => runner.DisposeAsync().AsTask()));
            Assert.False(cleanup.Disposed); Assert.True(File.Exists(fixture.Part));
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.Prompt, "changed"));
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.Model, "changed"));
        }
        finally { cleanup.Fail = false; await runner.DisposeAsync(); }
    }

    [Fact]
    public async Task Ordinary_worker_failure_keeps_existing_terminal_and_ready_behavior()
    {
        string name = "las-known-failure-" + Guid.NewGuid().ToString("N");
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var server = new WorkerPipeServer(name, secret, new KnownFailureEngine(), TimeSpan.FromHours(1));
        Task running = server.RunAsync(stop.Token);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(5000, Token); var codec = new ProtocolCodec(client);
            await codec.WriteAsync(new ProtocolEnvelope<object>(1, "h", "handshake", "2026-10-17T00:00:00Z", new HandshakeCommand(secret)), Token);
            Assert.Equal("result", (await codec.ReadAsync(Token))!.Type);
            await codec.WriteAsync(new ProtocolEnvelope<object>(1, "s", "synthesizeSegment", "2026-10-17T00:00:00Z", Command), Token);
            var terminal = (await codec.ReadAsync(Token))!;
            Assert.Equal("error", terminal.Type); Assert.Equal("worker.arguments", terminal.Payload.GetProperty("code").GetString());
            Assert.Equal("ready", server.State);
            await codec.WriteAsync(new ProtocolEnvelope<object>(1, "bye", "shutdown", "2026-10-17T00:00:00Z", new ShutdownCommand()), Token);
            Assert.Equal("result", (await codec.ReadAsync(Token))!.Type);
            await running.WaitAsync(TimeSpan.FromSeconds(2), Token);
        }
        finally { stop.Cancel(); client.Dispose(); await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
    }

    private sealed class UnknownCleanupEngine : IWorkerEngine
    {
        internal CountingCleanup Cleanup { get; } = new();
        internal TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SynthesisResult> SynthesizeAsync(SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken cancellationToken)
        { Called.TrySetResult(); throw new NativeOwnedLaunchException(Cleanup); }
    }
    private sealed class CountingCleanup : INativeLaunchCleanup
    {
        internal int StopCalls; internal bool Disposed;
        internal bool Fail;
        public Task StopTreeAsync() { StopCalls++; if (Fail) throw new IOException("unknown"); return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }
    private sealed class KnownFailureEngine : IWorkerEngine
    {
        public Task<SynthesisResult> SynthesizeAsync(SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken cancellationToken)
            => throw new WorkerException("worker.arguments");
    }
    private sealed class OwnedFailureFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "las-owned-failure-" + Guid.NewGuid().ToString("N"));
        internal string Prompt => Path.Combine(Root, "prompt.wav");
        internal string Native => Path.Combine(Root, "native.bin");
        internal string Model => Path.Combine(Root, "model.bin");
        internal string Part => Path.Combine(Root, "output/clip.wav.part");
        internal OwnedFailureFiles()
        {
            Directory.CreateDirectory(Path.Combine(Root, "output")); File.WriteAllBytes(Native, [1]); File.WriteAllBytes(Model, [2]);
            using var file = File.Create(Prompt); using var writer = new BinaryWriter(file);
            writer.Write("RIFF"u8); writer.Write(236); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(200); writer.Write(new byte[200]);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}

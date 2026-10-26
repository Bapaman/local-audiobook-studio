using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Buffers.Binary;
using System.Reflection;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Tests;

public class WorkerPipeServerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("worker.output.limit", "worker.output.limit")]
    [InlineData("PRIVATE_UNKNOWN_DIAGNOSTIC", "worker.internal")]
    public void Output_limit_diagnostic_remains_safe_without_exposing_unknown_codes(string code, string expected)
    {
        var error = new WorkerException(code);
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("PRIVATE_UNKNOWN_DIAGNOSTIC", error.ToString());
    }

    [Fact]
    public void Error_and_warning_payloads_expose_explicit_camel_case_diagnostic_fields()
    {
        var diagnostic = new Diagnostic("worker.crash", "fatal", "worker.crash", "worker.restart", 3);
        var error = JsonSerializer.SerializeToElement(new ErrorEvent(diagnostic), ProtocolCodec.JsonOptions);
        var warning = JsonSerializer.SerializeToElement(new WarningEvent(diagnostic), ProtocolCodec.JsonOptions);
        Assert.Equal("worker.crash", error.GetProperty("code").GetString());
        Assert.Equal("worker.crash", warning.GetProperty("code").GetString());
        Assert.Equal(3, error.GetProperty("exitCode").GetInt32());
        Assert.Equal("worker.crash", JsonSerializer.Deserialize<ErrorEvent>(error.GetRawText(), ProtocolCodec.JsonOptions)!.Code);
        Assert.Equal("worker.crash", JsonSerializer.Deserialize<WarningEvent>(warning.GetRawText(), ProtocolCodec.JsonOptions)!.Code);
    }
    [Fact]
    public async Task Heartbeat_is_emitted_after_authentication_and_shutdown_cancels_active_synthesis()
    {
        var engine = new BlockingEngine(); await using var host = new Connection(engine); await host.Connect();
        await host.Send("h", "handshake", new HandshakeCommand(host.Secret)); await host.Terminal("h");
        var heartbeat = await host.Event("heartbeat"); Assert.Equal("ready", heartbeat.Payload.GetProperty("state").GetString());
        await host.Send("s", "synthesizeSegment", Command()); await engine.Started.Task.WaitAsync(Token);
        await host.Send("bye", "shutdown", new ShutdownCommand()); await host.Terminal("bye");
        Assert.Equal("worker.canceled", (await host.Terminal("s")).Payload.GetProperty("code").GetString());
        Assert.True(engine.Canceled.Task.IsCompleted); await host.Running.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }
    [Fact]
    public async Task Incompatible_version_gets_private_safe_terminal_before_close()
    {
        await using var host = new Connection(new BlockingEngine()); await host.Connect();
        byte[] json = Encoding.UTF8.GetBytes("{\"protocolVersion\":2,\"requestId\":\"bad\",\"type\":\"health\",\"timestamp\":\"2026-10-06T12:00:00Z\",\"payload\":{}}");
        byte[] bytes = new byte[json.Length + 4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, json.Length); json.CopyTo(bytes, 4);
        await host.Client.WriteAsync(bytes, Token);
        var terminal = await host.Terminal("protocol"); Assert.Equal("protocol.version", terminal.Payload.GetProperty("code").GetString());
        await host.Running.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }
    [Fact]
    public async Task Completed_duplicate_replays_terminal_without_rerunning_engine()
    {
        await using var host = new Connection(new BlockingEngine()); await host.Connect();
        await host.Send("h", "handshake", new HandshakeCommand(host.Secret)); var first = await host.Terminal("h");
        await host.Send("h", "handshake", new HandshakeCommand("PRIVATE-RETRANSMISSION")); var replay = await host.Terminal("h");
        Assert.Equal(first.Timestamp, replay.Timestamp); Assert.Equal(first.Payload.GetRawText(), replay.Payload.GetRawText());
    }
    [Fact]
    public async Task Authenticated_flow_remains_responsive_while_synthesis_is_active()
    {
        var engine = new BlockingEngine(); await using var host = new Connection(engine);
        await host.Connect();
        Assert.True((host.Server.Options & PipeOptions.CurrentUserOnly) != 0);
        await host.Send("h", "handshake", new HandshakeCommand(host.Secret));
        Assert.Equal("result", (await host.Terminal("h")).Type);
        await host.Send("caps", "capabilities", new CapabilitiesCommand());
        Assert.Equal("result", (await host.Terminal("caps")).Type);
        await host.Send("s", "synthesizeSegment", Command()); await engine.Started.Task.WaitAsync(Token);
        await host.Send("s", "synthesizeSegment", Command());
        await host.Send("health", "health", new HealthCommand());
        Assert.Equal("synthesizing", (await host.Terminal("health")).Payload.GetProperty("state").GetString());
        await host.Send("busy", "synthesizeSegment", Command());
        Assert.Equal("worker.busy", (await host.Terminal("busy")).Payload.GetProperty("code").GetString());
        await host.Send("c", "cancelRequest", new CancelRequestCommand("s"));
        Assert.Equal("result", (await host.Terminal("c")).Type);
        Assert.Equal("worker.canceled", (await host.Terminal("s")).Payload.GetProperty("code").GetString());
        Assert.Equal(1, engine.Runs);
        await host.Send("bye", "shutdown", new ShutdownCommand()); await host.Terminal("bye");
        await host.Running.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("closed", host.Server.State);
    }

    [Fact]
    public async Task Synthesis_slot_is_ready_when_terminal_becomes_observable()
    {
        var engine = new ControlledSuccessEngine();
        await using var host = new Connection(engine); await host.Connect();
        await host.Send("h", "handshake", new HandshakeCommand(host.Secret)); await host.Terminal("h");
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalPublishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(WorkerPipeServer).GetField("synthesisBoundaryObserver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host.Server, (Action<string>)(phase =>
            {
                if (phase == "cleanup") cleanupStarted.TrySetResult();
                if (phase == "terminal") terminalPublishing.TrySetResult();
                throw new InvalidOperationException("Test observer must not affect synthesis.");
            }));

        await host.Send("first", "synthesizeSegment", Command()); await engine.Started.Task.WaitAsync(Token);
        object requestGate = typeof(WorkerPipeServer).GetField("requestGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host.Server)!;
        using var held = new ManualResetEventSlim(); using var releaseGate = new ManualResetEventSlim();
        Task holder = Task.Run(() => { lock (requestGate) { held.Set(); releaseGate.Wait(Token); } }, Token);
        held.Wait(Token);
        Task<ProtocolEnvelope<JsonElement>> terminal = host.Terminal("first");
        try
        {
            engine.Release.TrySetResult(); await engine.Finishing.Task.WaitAsync(Token);
            await cleanupStarted.Task.WaitAsync(Token);
            Assert.False(terminalPublishing.Task.IsCompleted);
            Assert.False(terminal.IsCompleted);
        }
        finally { releaseGate.Set(); await holder; }
        await terminalPublishing.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("result", (await terminal).Type);
        Assert.Equal("ready", host.Server.State);

        await host.Send("second", "synthesizeSegment", Command());
        Assert.Equal("result", (await host.Terminal("second")).Type);
        Assert.Equal(2, engine.Runs);
    }

    [Theory]
    [InlineData("health")] [InlineData("handshake")]
    public async Task Unauthenticated_commands_close_connection_without_leaking_secret(string type)
    {
        await using var host = new Connection(new BlockingEngine()); await host.Connect();
        await host.Send("bad", type, type == "health" ? (object)new HealthCommand() : new HandshakeCommand(Convert.ToBase64String(new byte[32])));
        var terminal = await host.Terminal("bad"); Assert.Equal("error", terminal.Type);
        Assert.DoesNotContain(host.Secret, terminal.Payload.GetRawText());
        await host.Running.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task Disconnect_cancels_engine_and_closes_host()
    {
        var engine = new BlockingEngine(); await using var host = new Connection(engine); await host.Connect();
        await host.Send("h", "handshake", new HandshakeCommand(host.Secret)); await host.Terminal("h");
        await host.Send("s", "synthesizeSegment", Command()); await engine.Started.Task.WaitAsync(Token);
        host.Client.Dispose(); await host.Running.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(engine.Canceled.Task.IsCompleted); Assert.Equal("closed", host.Server.State);
    }

    [Fact]
    public async Task Required_payload_fields_are_not_silently_defaulted()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SynthesizeSegmentCommand>("{}", ProtocolCodec.JsonOptions));
        Assert.Equal("sessionSecret", JsonSerializer.SerializeToElement(new HandshakeCommand("x"), ProtocolCodec.JsonOptions).EnumerateObject().Single().Name);
        Assert.Equal("worker.crash", new Diagnostic("worker.crash", "fatal", "worker.crash", "worker.restart", 3).Code);
        await Task.CompletedTask;
    }

    private static SynthesizeSegmentCommand Command() => new("Тест", "voice/p.wav", "Точная речь\r\n", "segments/test.wav", "q8-f16-hybrid-16", 70, 10, 2, 1, 42);
    private sealed class BlockingEngine : IWorkerEngine
    {
        public int Runs; public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SynthesisResult> SynthesizeAsync(SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken token)
        {
            Interlocked.Increment(ref Runs); progress(new("generating")); Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
            throw new InvalidOperationException();
        }
    }
    private sealed class ControlledSuccessEngine : IWorkerEngine
    {
        public int Runs;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finishing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SynthesisResult> SynthesizeAsync(SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken token)
        {
            Interlocked.Increment(ref Runs);
            Started.TrySetResult(); await Release.Task.WaitAsync(token); Finishing.TrySetResult();
            return new SynthesisResult(command.FinalPath, 48, new string('a', 64), 1, 48000, 16, 0.001, command.ProfileId, "worker", "model");
        }
    }
    private sealed class Connection : IAsyncDisposable
    {
        public string Secret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public WorkerPipeServer Server { get; }
        public NamedPipeClientStream Client { get; }
        public Task Running { get; }
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        private ProtocolCodec codec = null!;
        private readonly List<ProtocolEnvelope<JsonElement>> buffered = [];
        public Connection(IWorkerEngine engine)
        {
            string name = "las-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            Server = new(name, Secret, engine, heartbeatInterval: TimeSpan.FromMilliseconds(20));
            Client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Running = Server.RunAsync(stop.Token);
        }
        public async Task Connect() { await Client.ConnectAsync(5000, Token); codec = new(Client); }
        public Task Send(string id, string type, object payload) => codec.WriteAsync(new ProtocolEnvelope<object>(1, id, type, "2026-10-06T12:00:00Z", payload), Token);
        public async Task<ProtocolEnvelope<JsonElement>> Terminal(string id)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var existing = buffered.FirstOrDefault(m => m.RequestId == id && m.Type is "result" or "error");
                if (existing != null) { buffered.Remove(existing); return existing; }
                buffered.Add((await codec.ReadAsync(deadline.Token))!);
            }
        }
        public async Task<ProtocolEnvelope<JsonElement>> Event(string type)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var message = await codec.ReadAsync(deadline.Token) ?? throw new EndOfStreamException();
                if (message.Type == type) return message;
                buffered.Add(message);
            }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); Client.Dispose(); await Running; stop.Dispose(); }
    }
}

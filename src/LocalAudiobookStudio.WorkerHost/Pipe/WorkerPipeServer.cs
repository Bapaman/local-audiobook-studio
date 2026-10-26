using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.WorkerHost.Pipe;

public interface IWorkerEngine
{
    Task<SynthesisResult> SynthesizeAsync(SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken cancellationToken);
}

public sealed class WorkerException(string code, int? exitCode = null) : Exception(SafeCode(code))
{
    public string Code { get; } = SafeCode(code);
    public int? ExitCode { get; } = exitCode;
    private static string SafeCode(string code) => code switch
    {
        "worker.canceled" or "worker.timeout" or "worker.crash" or "worker.cuda.oom" or "worker.output.invalid" or
        "worker.output.missing" or "worker.output.exists" or "worker.output.limit" or "worker.arguments" or "worker.path" or "worker.artifacts" or
        "worker.developer.required" or "worker.profile" or "worker.storage" or "worker.cleanup" or "worker.launch" or
        "worker.unsupported.platform" or "worker.configuration" => code,
        _ => "worker.internal"
    };
}

/// <summary>Single authenticated connection. Caller generates an unpredictable name and secret per launch.</summary>
public sealed class WorkerPipeServer
{
    public PipeOptions Options => PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    public string State => Volatile.Read(ref state);
    private string state = "awaitingHandshake";
    private readonly string name;
    private readonly byte[] secret;
    private readonly IWorkerEngine engine;
    private readonly TimeSpan heartbeatInterval;
    private int run;
    private readonly Channel<ProtocolEnvelope<object>> output = Channel.CreateBounded<ProtocolEnvelope<object>>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Dictionary<string, Request> requests = new(StringComparer.Ordinal);
    private readonly object requestGate = new();
    private Action<string>? synthesisBoundaryObserver = null;
    private Request? active;
    private sealed class Request(string id)
    {
        public string Id { get; } = id;
        public int Terminal;
        public ProtocolEnvelope<object>? Reply;
        public CancellationTokenSource? Stop;
        public Task? Work;
    }
    public WorkerPipeServer(string pipeName, string sessionSecret, IWorkerEngine engine, TimeSpan? heartbeatInterval = null)
    {
        if (pipeName.Length < 32 || pipeName.Length > 128 || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new WorkerException("worker.configuration");
        try { secret = Convert.FromBase64String(sessionSecret); } catch (FormatException) { throw new WorkerException("worker.configuration"); }
        if (secret.Length < 32 || secret.Length > 64) throw new WorkerException("worker.configuration");
        name = pipeName; this.engine = engine; this.heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(2);
        if (this.heartbeatInterval <= TimeSpan.Zero) throw new WorkerException("worker.configuration");
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref run, 1) != 0) throw new WorkerException("worker.configuration");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, Options);
        Task writer = Task.CompletedTask, heartbeat = Task.CompletedTask;
        bool orderly = false;
        try
        {
            await pipe.WaitForConnectionAsync(lifetime.Token);
            var codec = new ProtocolCodec(pipe);
            writer = WriteLoop(codec, lifetime); heartbeat = Heartbeats(lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                var message = await codec.ReadAsync(lifetime.Token);
                if (message == null) break;
                Request request;
                Request? duplicate;
                lock (requestGate)
                {
                    requests.TryGetValue(message.RequestId, out duplicate);
                    request = duplicate ?? new(message.RequestId);
                    if (duplicate == null) requests.Add(message.RequestId, request);
                }
                // Pending retransmissions coalesce into their original terminal. Completed ones replay it.
                if (duplicate != null)
                {
                    if (Volatile.Read(ref duplicate.Reply) is { } reply) await output.Writer.WriteAsync(reply, lifetime.Token);
                    continue;
                }
                if (State == "awaitingHandshake")
                {
                    bool valid = false;
                    if (message.Type == "handshake")
                    {
                        try
                        {
                            var payload = Payload<HandshakeCommand>(message);
                            byte[] supplied = Convert.FromBase64String(payload.SessionSecret);
                            valid = CryptographicOperations.FixedTimeEquals(secret, supplied);
                            CryptographicOperations.ZeroMemory(supplied);
                        }
                        catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { }
                    }
                    if (!valid) { await Terminal(request, "error", Diagnostic("worker.authentication"), lifetime.Token); orderly = true; break; }
                    state = "ready"; await Terminal(request, "result", new ResultEvent(State), lifetime.Token); continue;
                }
                try
                {
                    switch (message.Type)
                    {
                        case "health": Payload<HealthCommand>(message); await Terminal(request, "result", new ResultEvent(State), lifetime.Token); break;
                        case "capabilities": Payload<CapabilitiesCommand>(message); await Terminal(request, "result", new CapabilitiesResult(["q8-f16-hybrid-16", "q8-f16-cpu"], true, 1), lifetime.Token); break;
                        case "synthesizeSegment":
                            var command = Payload<SynthesizeSegmentCommand>(message);
                            lock (requestGate)
                            {
                                if (active == null) { active = request; request.Stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); state = "synthesizing"; }
                            }
                            if (request.Stop == null) await Terminal(request, "error", Diagnostic("worker.busy"), lifetime.Token);
                            else request.Work = Synthesize(request, command, lifetime);
                            break;
                        case "cancelRequest":
                            var cancel = Payload<CancelRequestCommand>(message);
                            lock (requestGate) { if (active?.Id == cancel.TargetRequestId) active.Stop!.Cancel(); }
                            await Terminal(request, "result", new ResultEvent(State), lifetime.Token); break;
                        case "shutdown":
                            Payload<ShutdownCommand>(message); state = "stopping";
                            Request? current; lock (requestGate) { current = active; current?.Stop!.Cancel(); }
                            if (current?.Work != null) await current.Work;
                            await Terminal(request, "result", new ResultEvent(State), lifetime.Token); orderly = true; break;
                        default: await Terminal(request, "error", Diagnostic("protocol.command"), lifetime.Token); break;
                    }
                }
                catch (JsonException) { await Terminal(request, "error", Diagnostic("protocol.payload"), lifetime.Token); }
                if (orderly) break;
                if (requests.Count >= 4096) { orderly = true; break; }
            }
        }
        catch (ProtocolException e)
        {
            if (!lifetime.IsCancellationRequested && e.Code is not ("protocol.frame.partial" or "protocol.connection.poisoned"))
            {
                await output.Writer.WriteAsync(Envelope("protocol", "error", Diagnostic(e.Code)), lifetime.Token);
                orderly = true;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
        finally
        {
            state = "stopping";
            Request? current; lock (requestGate) { current = active; current?.Stop?.Cancel(); }
            if (!orderly) lifetime.Cancel();
            if (current?.Work != null) { await current.Work; current.Stop?.Dispose(); }
            output.Writer.TryComplete();
            if (orderly)
            {
                try { await writer.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException) { }
            }
            lifetime.Cancel();
            try { await Task.WhenAll(writer, heartbeat); } catch (Exception e) when (e is OperationCanceledException or IOException) { }
            state = "closed"; CryptographicOperations.ZeroMemory(secret);
        }
    }
    private static T Payload<T>(ProtocolEnvelope<JsonElement> message) => message.Payload.Deserialize<T>(ProtocolCodec.JsonOptions) ?? throw new JsonException();
    private void ObserveSynthesisBoundary(string phase)
    {
        try { synthesisBoundaryObserver?.Invoke(phase); }
        catch (Exception) { }
    }
    private async Task Synthesize(Request request, SynthesizeSegmentCommand command, CancellationTokenSource lifetime)
    {
        CancellationToken token = lifetime.Token;
        bool cleanupUnknown = false;
        string terminalType;
        object terminalPayload;
        try
        {
            terminalPayload = await engine.SynthesizeAsync(command, progress => output.Writer.TryWrite(Envelope(request.Id, "progress", progress)), request.Stop!.Token);
            terminalType = "result";
        }
        catch (NativeOwnedLaunchException)
        {
            cleanupUnknown = true;
            lock (requestGate) state = "stopping";
            // A reusable terminal would let the caller release ownership while an
            // unconfirmed nested Job may still exist. Close this connection instead.
            lifetime.Cancel(); return;
        }
        catch (OperationCanceledException) { terminalType = "error"; terminalPayload = Diagnostic("worker.canceled"); }
        catch (WorkerException e) { terminalType = "error"; terminalPayload = Diagnostic(e.Code, e.ExitCode); }
        catch (Exception) { terminalType = "error"; terminalPayload = Diagnostic("worker.internal"); }
        finally
        {
            ObserveSynthesisBoundary("cleanup");
            if (!cleanupUnknown)
            {
                lock (requestGate) { if (ReferenceEquals(active, request)) active = null; if (State == "synthesizing") state = "ready"; }
                request.Stop!.Dispose();
            }
        }
        try { await Terminal(request, terminalType, terminalPayload, token); }
        catch (OperationCanceledException) { }
    }
    private async Task Terminal(Request request, string type, object payload, CancellationToken token)
    {
        ObserveSynthesisBoundary("terminal");
        if (Interlocked.CompareExchange(ref request.Terminal, 1, 0) == 0)
        {
            var reply = Envelope(request.Id, type, payload); Volatile.Write(ref request.Reply, reply);
            await output.Writer.WriteAsync(reply, token);
        }
    }
    private static ErrorEvent Diagnostic(string code, int? exit = null) => new(new Diagnostic(code,
        code is "worker.authentication" or "protocol.version" ? "fatal" : code is "worker.artifacts" or "worker.developer.required" or "worker.storage" or "worker.path" ? "user-action-required" : "recoverable",
        code, code is "worker.artifacts" or "worker.developer.required" ? "worker.configure" : "worker.retry", exit));
    private static ProtocolEnvelope<object> Envelope(string id, string type, object payload) => new(1, id, type, DateTimeOffset.UtcNow.ToString("O"), payload);
    private async Task WriteLoop(ProtocolCodec codec, CancellationTokenSource stop)
    {
        try { await foreach (var message in output.Reader.ReadAllAsync(stop.Token)) await codec.WriteAsync(message, stop.Token); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or ProtocolException) { stop.Cancel(); }
    }
    private async Task Heartbeats(CancellationToken token)
    {
        using var timer = new PeriodicTimer(heartbeatInterval);
        while (await timer.WaitForNextTickAsync(token)) if (State is "ready" or "synthesizing") output.Writer.TryWrite(Envelope("heartbeat", "heartbeat", new HeartbeatEvent(State)));
    }
}

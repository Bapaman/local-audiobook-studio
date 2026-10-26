using System.IO.Pipes;
using System.Text.Json;
using LocalAudiobookStudio.Contracts.Protocol;

namespace LocalAudiobookStudio.Application.Workers;

/// <summary>One authenticated session, one reader, serialized frames; no executable or filesystem ownership.</summary>
public sealed class WorkerPipeClient : IWorkerClient
{
    private readonly NamedPipeClientStream pipe;
    private readonly ProtocolCodec codec;
    private readonly IWorkerSessionLifetime owner;
    private readonly TimeProvider time;
    private readonly WorkerClientOptions options;
    private readonly CancellationTokenSource stopped = new();
    private readonly object sync = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> used = new(StringComparer.Ordinal);
    private long lastHeartbeat;
    private long? shutdownStarted;
    private string? failureCode;
    private Task<bool>? cleanup;
    private Task? disposal;
    private Task reader = Task.CompletedTask, heartbeat = Task.CompletedTask;
    private bool orderly;
    private int disposed;
    private sealed class Pending(string kind, SynthesizeSegmentCommand? command = null, Action<ProgressEvent>? progress = null)
    {
        public string Kind { get; } = kind;
        public SynthesizeSegmentCommand? Command { get; } = command;
        public Action<ProgressEvent>? Progress { get; } = progress;
        public TaskCompletionSource<JsonElement> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private WorkerPipeClient(NamedPipeClientStream pipe, IWorkerSessionLifetime owner, TimeProvider time, WorkerClientOptions options)
    { this.pipe = pipe; codec = new(pipe); this.owner = owner; this.time = time; this.options = options; }

    public static async Task<WorkerPipeClient> ConnectAsync(string pipeName, ReadOnlyMemory<byte> sessionSecret, IWorkerSessionLifetime owner, TimeProvider time, WorkerClientOptions options, CancellationToken token)
    {
        options.Validate();
        if (pipeName.Length is < 32 or > 128 || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || sessionSecret.Length is < 32 or > 64)
            throw new WorkerClientException("worker.configuration", false);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new WorkerPipeClient(pipe, owner, time, options);
        using var deadline = new CancellationTokenSource(options.ConnectAndHandshake, time);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, token);
        try
        {
            await pipe.ConnectAsync(connect.Token);
            string id = Guid.NewGuid().ToString("N"); client.used.Add(id);
            await client.codec.WriteAsync(Envelope(id, "handshake", new HandshakeCommand(Convert.ToBase64String(sessionSecret.Span))), connect.Token);
            while (true)
            {
                var reply = await client.codec.ReadAsync(connect.Token) ?? throw new WorkerClientException("worker.interrupted", false);
                if (reply.RequestId == "heartbeat" && reply.Type == "heartbeat") { ValidateHeartbeat(reply.Payload); continue; }
                if (reply.RequestId == "protocol" && reply.Type == "error") throw new WorkerClientException(ErrorCode(reply.Payload), false);
                if (reply.RequestId != id) throw new WorkerClientException("protocol.frame", false);
                if (reply.Type == "error") throw new WorkerClientException(ErrorCode(reply.Payload), false);
                if (reply.Type != "result" || State(reply.Payload) != "ready") throw new WorkerClientException("protocol.frame", false);
                break;
            }
            client.lastHeartbeat = time.GetTimestamp();
            client.reader = client.ReadLoop(); client.heartbeat = client.CheckHeartbeat();
            return client;
        }
        catch (Exception e)
        {
            string code = e is OperationCanceledException ? (token.IsCancellationRequested ? "worker.canceled" : "worker.timeout") : Map(e);
            await client.FailAsync(code);
            throw new WorkerClientException(code, await client.StopOwnedAsync());
        }
    }

    public async Task<SynthesisResult> SynthesizeAsync(string requestId, SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken token)
    {
        if (token.IsCancellationRequested) { await FailAsync("worker.canceled"); throw await Failure(); }
        var operation = Register(requestId, new("synthesizeSegment", command, progress));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = token.Register(() => _ = CancelAndObserveTerminal(requestId, operation, sent.Task));
        try
        {
            await Send(requestId, "synthesizeSegment", command, options.ConnectAndHandshake);
            sent.TrySetResult();
            var result = await operation.Terminal.Task.WaitAsync(options.Synthesis, time, CancellationToken.None);
            return result.Deserialize<SynthesisResult>(ProtocolCodec.JsonOptions)!;
        }
        catch (WorkerClientException) { throw; }
        catch (Exception e) { await FailAsync(e is TimeoutException ? "worker.timeout" : Map(e)); throw await Failure(); }
        finally { sent.TrySetCanceled(); }
    }

    public async Task CancelAsync(string targetRequestId, CancellationToken token)
    {
        Pending? target;
        lock (sync) pending.TryGetValue(targetRequestId, out target);
        await CancelCore(targetRequestId, time.GetTimestamp(), token);
        // Explicit cancellation has the same terminal deadline as token-driven
        // cancellation; returning its ACK must not leave a hung model unbounded.
        if (target != null) _ = ObserveCanceledTerminal(target);
    }

    private async Task CancelCore(string targetRequestId, long started, CancellationToken token)
    {
        lock (sync)
        {
            if (!pending.TryGetValue(targetRequestId, out var target) || target.Kind != "synthesizeSegment")
            {
                if (failureCode != null) throw new WorkerClientException(failureCode, cleanup?.IsCompletedSuccessfully == true && cleanup.Result);
                throw new WorkerClientException("worker.arguments", false);
            }
            shutdownStarted ??= time.GetTimestamp();
        }
        string id = Guid.NewGuid().ToString("N"); var operation = Register(id, new("cancelRequest"));
        try
        {
            TimeSpan budget = CancelRemaining(started);
            await Send(id, "cancelRequest", new CancelRequestCommand(targetRequestId), budget);
            await operation.Terminal.Task.WaitAsync(CancelRemaining(started), time, token);
        }
        catch (WorkerClientException) { throw; }
        catch (Exception e) { await FailAsync(e is TimeoutException ? "worker.timeout" : Map(e)); throw await Failure(); }
    }

    private async Task CancelAndObserveTerminal(string id, Pending target, Task sent)
    {
        try
        {
            long requested = time.GetTimestamp(); lock (sync) shutdownStarted ??= requested;
            // Never put cancellation ahead of the command it targets. The ACK
            // deadline includes time spent waiting for the initial frame to finish.
            await sent.WaitAsync(CancelRemaining(requested), time, CancellationToken.None);
            if (target.Terminal.Task.IsCompleted) return;
            await CancelCore(id, requested, CancellationToken.None);
            await ObserveCanceledTerminal(target);
        }
        catch (WorkerClientException) { }
        catch (Exception e) { await FailAsync(e is TimeoutException ? "worker.timeout" : Map(e)); }
    }
    private async Task ObserveCanceledTerminal(Pending target)
    {
        try { await target.Terminal.Task.WaitAsync(Remaining(), time, CancellationToken.None); }
        catch (WorkerClientException) { }
        catch (Exception e) { await FailAsync(e is TimeoutException ? "worker.timeout" : Map(e)); }
    }
    private TimeSpan CancelRemaining(long started)
    {
        var budget = Min(options.CancelAck - time.GetElapsedTime(started), Remaining());
        if (budget <= TimeSpan.Zero) throw new TimeoutException();
        return budget;
    }

    private Pending Register(string id, Pending operation, bool shutdown = false)
    {
        lock (sync)
        {
            if (failureCode != null) throw new WorkerClientException(failureCode, cleanup?.IsCompletedSuccessfully == true && cleanup.Result);
            if ((!shutdown && disposed != 0) || string.IsNullOrWhiteSpace(id) || id.Length > 128 || id is "heartbeat" or "protocol" || used.Count >= 4095 || !used.Add(id))
                throw new WorkerClientException("worker.arguments", false);
            pending.Add(id, operation); return operation;
        }
    }
    private async Task Send<T>(string id, string type, T payload, TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero) throw new TimeoutException();
        await codec.WriteAsync(Envelope(id, type, payload), stopped.Token).WaitAsync(budget, time, CancellationToken.None);
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                var frame = await codec.ReadAsync(stopped.Token);
                if (frame == null) { lock (sync) { if (orderly) return; } throw new WorkerClientException("worker.interrupted", false); }
                if (frame.RequestId == "heartbeat" && frame.Type == "heartbeat")
                { ValidateHeartbeat(frame.Payload); lock (sync) lastHeartbeat = time.GetTimestamp(); continue; }
                if (frame.RequestId == "protocol" && frame.Type == "error") { await FailAsync(ErrorCode(frame.Payload)); return; }
                Pending operation;
                lock (sync)
                {
                    if (!pending.TryGetValue(frame.RequestId, out operation!)) throw new WorkerClientException("protocol.frame", false);
                }
                if (frame.Type is "progress" or "warning")
                {
                    if (operation.Kind != "synthesizeSegment") throw new WorkerClientException("protocol.frame", false);
                    if (frame.Type == "warning") { _ = ErrorCode(frame.Payload); continue; }
                    RequireFields(frame.Payload, "phase"); string phase = frame.Payload.GetProperty("phase").GetString()!;
                    if (phase is not ("validatingInputs" or "generating" or "validatingOutput" or "publishing")) throw new WorkerClientException("protocol.frame", false);
                    try { operation.Progress?.Invoke(new(phase)); } catch { /* Progress is a hint, never completion. */ }
                    continue;
                }
                if (frame.Type == "error") { await FailAsync(ErrorCode(frame.Payload)); return; }
                if (frame.Type != "result") throw new WorkerClientException("protocol.frame", false);
                if (operation.Kind == "synthesizeSegment") ValidateResult(frame.Payload, operation.Command!);
                else
                {
                    string state = State(frame.Payload);
                    if (operation.Kind == "shutdown" ? state != "stopping" : state is not ("ready" or "synthesizing")) throw new WorkerClientException("protocol.frame", false);
                }
                lock (sync) { pending.Remove(frame.RequestId); if (operation.Kind == "shutdown") orderly = true; }
                operation.Terminal.TrySetResult(frame.Payload);
            }
        }
        catch (Exception e) when (e is OperationCanceledException && stopped.IsCancellationRequested) { }
        catch (Exception e) { await FailAsync(Map(e)); }
    }
    private async Task CheckHeartbeat()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                TimeSpan remaining;
                lock (sync) remaining = options.HeartbeatGap - time.GetElapsedTime(lastHeartbeat);
                if (remaining <= TimeSpan.Zero) { await FailAsync("worker.interrupted"); return; }
                await Task.Delay(remaining, time, stopped.Token);
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
    }

    private async Task FailAsync(string code)
    {
        lock (sync) { failureCode ??= code; shutdownStarted ??= time.GetTimestamp(); }
        bool confirmed = await StopOwnedAsync();
        lock (sync)
        {
            foreach (var operation in pending.Values) operation.Terminal.TrySetException(new WorkerClientException(failureCode!, confirmed));
            pending.Clear();
        }
    }
    private Task<bool> StopOwnedAsync()
    {
        lock (sync) { shutdownStarted ??= time.GetTimestamp(); return cleanup ??= StopCore(); }
    }
    private async Task<bool> StopCore()
    {
        try
        {
            var remaining = Remaining(); var exit = owner.StopAsync(remaining, CancellationToken.None);
            return remaining > TimeSpan.Zero ? await exit.WaitAsync(remaining, time, CancellationToken.None) : exit.IsCompletedSuccessfully && exit.Result;
        }
        catch { return false; }
        finally { stopped.Cancel(); pipe.Dispose(); }
    }
    private TimeSpan Remaining()
    {
        lock (sync)
        {
            var remaining = shutdownStarted is { } start ? options.Shutdown - time.GetElapsedTime(start) : options.Shutdown;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    private async Task<WorkerClientException> Failure() { bool confirmed = await StopOwnedAsync(); lock (sync) return new(failureCode ?? "worker.interrupted", confirmed); }

    public ValueTask DisposeAsync()
    {
        // Repeated/concurrent callers observe the same exit proof OR the same
        // failure. A no-op second dispose must not manufacture confirmed cleanup.
        lock (sync) return new ValueTask(disposal ??= DisposeCore());
    }

    private async Task DisposeCore()
    {
        Interlocked.Exchange(ref disposed, 1);
        string? code; lock (sync) { code = failureCode; shutdownStarted ??= time.GetTimestamp(); }
        if (code == null)
        {
            try
            {
                string id = Guid.NewGuid().ToString("N"); var operation = Register(id, new("shutdown"), shutdown: true);
                await Send(id, "shutdown", new ShutdownCommand(), Remaining());
                await operation.Terminal.Task.WaitAsync(Remaining(), time, CancellationToken.None);
            }
            catch (Exception e) { await FailAsync(e is TimeoutException ? "worker.timeout" : Map(e)); }
        }
        bool confirmed = await StopOwnedAsync();
        try { await Task.WhenAll(reader, heartbeat).WaitAsync(Remaining(), time, CancellationToken.None); }
        catch { confirmed = false; }
        lock (sync) code = failureCode;
        if (!confirmed) throw new WorkerClientException("worker.cleanup", false);
        if (code != null) throw new WorkerClientException(code, true);
        stopped.Dispose();
    }

    private static void ValidateResult(JsonElement payload, SynthesizeSegmentCommand command)
    {
        RequireFields(payload, "finalPath", "length", "sha256", "channels", "sampleRate", "bitsPerSample", "durationSeconds", "profileId", "workerVersion", "modelVersion");
        var value = payload.Deserialize<SynthesisResult>(ProtocolCodec.JsonOptions)!;
        if (value.FinalPath != command.FinalPath || value.ProfileId != command.ProfileId || value.Length is < 46 or > 1073741824 ||
            !Hash(value.Sha256, 64) || !Hash(value.WorkerVersion, 40) || !Hash(value.ModelVersion, 40) || value.Channels != 1 || value.SampleRate != 48000 || value.BitsPerSample != 16 || !double.IsFinite(value.DurationSeconds) || value.DurationSeconds <= 0)
            throw new WorkerClientException("protocol.frame", false);
    }
    private static bool Hash(string? value, int length) => value?.Length == length && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static string State(JsonElement payload) { RequireFields(payload, "state"); return payload.GetProperty("state").GetString()!; }
    private static void ValidateHeartbeat(JsonElement payload) { if (State(payload) is not ("ready" or "synthesizing")) throw new WorkerClientException("protocol.frame", false); }
    private static string ErrorCode(JsonElement payload)
    {
        var fields = payload.EnumerateObject().Select(x => x.Name).ToArray();
        if (fields.Length is < 4 or > 5 || !new[] { "code", "category", "messageKey", "actionKey" }.All(fields.Contains) || fields.Except(["code", "category", "messageKey", "actionKey", "exitCode"]).Any()) throw new WorkerClientException("protocol.frame", false);
        foreach (var field in new[] { "code", "category", "messageKey", "actionKey" }) if (payload.GetProperty(field).ValueKind != JsonValueKind.String) throw new WorkerClientException("protocol.frame", false);
        if (payload.TryGetProperty("exitCode", out var exit) && exit.ValueKind != JsonValueKind.Null && !exit.TryGetInt32(out _)) throw new WorkerClientException("protocol.frame", false);
        string code = payload.GetProperty("code").GetString()!;
        if (code.StartsWith("protocol.", StringComparison.Ordinal) && code is not ("protocol.version" or "protocol.command" or "protocol.payload")) return "protocol.frame";
        return new WorkerClientException(code, false).Code;
    }
    private static void RequireFields(JsonElement payload, params string[] names)
    { if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != names.Length || names.Any(name => !payload.TryGetProperty(name, out _))) throw new WorkerClientException("protocol.frame", false); }
    private static string Map(Exception error) => error switch
    { WorkerClientException worker => worker.Code, ProtocolException protocol when protocol.Code == "protocol.version" => "protocol.version", ProtocolException or JsonException or InvalidOperationException => "protocol.frame", _ => "worker.interrupted" };
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static ProtocolEnvelope<T> Envelope<T>(string id, string type, T payload) => new(1, id, type, DateTimeOffset.UtcNow.ToString("O"), payload);
}

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Contracts.Protocol;

namespace LocalAudiobookStudio.Application.Tests;

public class WorkerPipeClientTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly SynthesizeSegmentCommand Command = new("SYNTHETIC_PRIVATE_TEXT", "voice-snapshot/synthetic.wav", "SYNTHETIC_TRANSCRIPT", "segments/synthetic.wav", "q8-f16-cpu", 70, 10, 2, 1, 42);
    private static SynthesisResult Result => new(Command.FinalPath, 9646, new string('a', 64), 1, 48000, 16, .10002, Command.ProfileId, new string('1', 40), new string('2', 40));

    [Theory]
    [InlineData("worker.output.limit", "worker.output.limit")]
    [InlineData("PRIVATE_UNKNOWN_DIAGNOSTIC", "worker.internal")]
    public void Output_limit_diagnostic_remains_safe_in_client_without_exposing_unknown_codes(string remoteCode, string expected)
    {
        var error = new WorkerClientException(remoteCode, true);
        Assert.Equal(expected, error.Code);
        Assert.True(error.CleanupConfirmed);
        Assert.DoesNotContain("PRIVATE_UNKNOWN_DIAGNOSTIC", error.ToString());
    }

    [Fact]
    public async Task Authenticated_pipe_interleaves_heartbeat_progress_and_exact_result()
    {
        await using var peer = new Peer(async (codec, token) =>
        {
            var request = (await codec.ReadAsync(token))!;
            Assert.Equal("synthesizeSegment", request.Type);
            Assert.Equal(Command.Text, request.Payload.GetProperty("text").GetString());
            await Reply(codec, "heartbeat", "heartbeat", new HeartbeatEvent("synthesizing"), token);
            await Reply(codec, request.RequestId, "progress", new ProgressEvent("generating"), token);
            await Reply(codec, request.RequestId, "result", Result, token);
            await Shutdown(codec, token);
        });
        await using var client = await Open(peer);
        var phases = new List<string>();
        var result = await client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, x => phases.Add(x.Phase), Token);
        Assert.Equal(Result, result); Assert.Equal(["generating"], phases);
    }

    [Fact]
    public async Task Cancel_ACK_does_not_discard_publication_won_result()
    {
        using var stop = new CancellationTokenSource();
        var ackSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async (codec, token) =>
        {
            var synth = (await codec.ReadAsync(token))!;
            stop.Cancel();
            var cancel = (await codec.ReadAsync(token))!;
            Assert.Equal("cancelRequest", cancel.Type); Assert.Equal(synth.RequestId, cancel.Payload.GetProperty("targetRequestId").GetString());
            Assert.NotEqual(synth.RequestId, cancel.RequestId);
            await Reply(codec, cancel.RequestId, "result", new ResultEvent("synthesizing"), token); ackSent.SetResult();
            await allowResult.Task.WaitAsync(token);
            await Reply(codec, synth.RequestId, "result", Result, token);
            await Shutdown(codec, token);
        });
        await using var client = await Open(peer);
        var synthTask = client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, stop.Token);
        await ackSent.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); Assert.False(synthTask.IsCompleted);
        allowResult.SetResult(); Assert.Equal(Result, await synthTask.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Theory]
    [InlineData("unknown")] [InlineData("duplicate")] [InlineData("phase")]
    public async Task Invalid_correlated_frames_poison_session_and_keep_errors_private(string variant)
    {
        await using var peer = new Peer(async (codec, token) =>
        {
            var synth = (await codec.ReadAsync(token))!;
            if (variant == "duplicate")
            {
                await Reply(codec, synth.RequestId, "result", Result, token);
                await Reply(codec, synth.RequestId, "result", Result, token);
            }
            else await Reply(codec, variant == "unknown" ? "unknown" : synth.RequestId, "progress", new ProgressEvent(variant == "phase" ? Command.Text : "generating"), token);
            await peerClose(codec, token);
        });
        var client = await Open(peer);
        var operation = client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, Token);
        if (variant == "duplicate") Assert.Equal(Result, await operation);
        else Assert.Equal("protocol.frame", (await Assert.ThrowsAsync<WorkerClientException>(() => operation)).Code);
        var error = await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
        Assert.Equal("protocol.frame", error.Code); Assert.True(error.CleanupConfirmed);
        Assert.DoesNotContain(Command.Text, error.ToString()); Assert.DoesNotContain(Command.Transcript, error.ToString());
        Assert.DoesNotContain(Command.PromptWav, error.ToString()); Assert.DoesNotContain(Convert.ToBase64String(peer.Secret), error.ToString());
    }

    [Fact]
    public async Task EOF_before_terminal_reports_owned_cleanup_instead_of_raw_pipe_error()
    {
        await using var peer = new Peer(async (codec, token) => { Assert.Equal("synthesizeSegment", (await codec.ReadAsync(token))!.Type); });
        var client = await Open(peer);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, Token));
        Assert.Equal("worker.interrupted", error.Code); Assert.True(error.CleanupConfirmed);
        await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
    }

    [Fact]
    public async Task Wrong_secret_is_rejected_with_safe_authenticated_cleanup()
    {
        await using var peer = new Peer((_, _) => Task.CompletedTask, rejectHandshake: true);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => WorkerPipeClient.ConnectAsync(peer.Name, RandomNumberGenerator.GetBytes(32), peer.Owner, TimeProvider.System, new(), Token));
        Assert.Equal("worker.authentication", error.Code); Assert.True(error.CleanupConfirmed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task One_injected_connect_and_handshake_deadline_bounds_both_phases(bool stalledHandshake)
    {
        var clock = new ManualClock();
        await using var peer = new Peer((_, _) => Task.CompletedTask, stallHandshake: stalledHandshake);
        string name = stalledHandshake ? peer.Name : "las-no-server-" + Guid.NewGuid().ToString("N");
        var connect = WorkerPipeClient.ConnectAsync(name, peer.Secret, peer.Owner, clock, new() { ConnectAndHandshake = TimeSpan.FromSeconds(1) }, Token);
        if (stalledHandshake) await peer.HandshakeReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await clock.WaitForDelay(TimeSpan.FromSeconds(1), Token); clock.Advance(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => connect.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal("worker.timeout", error.Code); Assert.True(error.CleanupConfirmed);
    }

    [Fact]
    public async Task Already_canceled_synthesis_never_sends_a_model_request()
    {
        await using var peer = new Peer(async (codec, token) => Assert.Null(await codec.ReadAsync(token)));
        var client = await Open(peer);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, new CancellationToken(true)));
        Assert.Equal("worker.canceled", error.Code); Assert.True(error.CleanupConfirmed);
        await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
    }

    [Theory]
    [InlineData("protocol.version", "protocol.version")] [InlineData("protocol.utf8", "protocol.frame")]
    public async Task Literal_protocol_error_closes_session_with_safe_mapped_code(string remoteCode, string expected)
    {
        await using var peer = new Peer(async (codec, token) =>
        {
            Assert.Equal("synthesizeSegment", (await codec.ReadAsync(token))!.Type);
            await Reply(codec, "protocol", "error", new ErrorEvent(remoteCode, "fatal", remoteCode, "worker.retry"), token);
            await peerClose(codec, token);
        });
        var client = await Open(peer);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, Token));
        Assert.Equal(expected, error.Code); Assert.True(error.CleanupConfirmed);
        await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Partial_frame_EOF_never_returns_success(bool body)
    {
        await using var peer = new Peer((_, _) => Task.CompletedTask, rawAfterHandshake: async (codec, stream, token) =>
        {
            Assert.Equal("synthesizeSegment", (await codec.ReadAsync(token))!.Type);
            await stream.WriteAsync(body ? new byte[] { 20, 0, 0, 0, (byte)'{' } : new byte[] { 20 }, token);
            await stream.FlushAsync(token);
        });
        var client = await Open(peer);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, Token));
        Assert.Equal("protocol.frame", error.Code); Assert.True(error.CleanupConfirmed);
        await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
    }

    [Fact]
    public async Task Wrong_handshake_protocol_version_is_not_ready()
    {
        await using var peer = new Peer((_, _) => Task.CompletedTask, handshakeVersion: 2);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => Open(peer));
        Assert.Equal("protocol.version", error.Code); Assert.True(error.CleanupConfirmed);
    }

    [Fact]
    public async Task Partial_reply_during_owned_stop_reports_interruption()
    {
        var owner = new StoppingOwner();
        await using var peer = new Peer((_, _) => Task.CompletedTask, owner: owner, rawAfterHandshake: async (codec, stream, token) =>
        {
            Assert.Equal("synthesizeSegment", (await codec.ReadAsync(token))!.Type);
            await owner.StopAsync(TimeSpan.Zero, token);
            await stream.WriteAsync(new byte[] { 20, 0, 0, 0, (byte)'{' }, token);
            await stream.FlushAsync(token);
        });
        var client = await Open(peer);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, Token));
        Assert.True(error.CleanupConfirmed);
        var disposal = await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
        Assert.Equal("worker.interrupted", error.Code);
        Assert.Equal(error.Code, disposal.Code);
    }

    [Fact]
    public async Task Private_remote_diagnostic_is_never_exposed_as_exception_detail()
    {
        await using var peer = new Peer(async (codec, token) =>
        {
            var synth = (await codec.ReadAsync(token))!;
            await Reply(codec, synth.RequestId, "error", new ErrorEvent(Command.Text, Command.Transcript, Command.PromptWav, Command.FinalPath), token);
            await peerClose(codec, token);
        });
        var client = await Open(peer);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, Token));
        Assert.Equal("worker.internal", error.Code); Assert.True(error.CleanupConfirmed);
        foreach (string value in new[] { Command.Text, Command.Transcript, Command.PromptWav, Command.FinalPath }) Assert.DoesNotContain(value, error.ToString());
        await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Result_must_match_requested_profile_and_PCM_contract(bool profile)
    {
        await using var peer = new Peer(async (codec, token) =>
        {
            var synth = (await codec.ReadAsync(token))!;
            await Reply(codec, synth.RequestId, "result", profile ? Result with { ProfileId = "q8-f16-hybrid-16" } : Result with { SampleRate = 44100 }, token);
            await peerClose(codec, token);
        });
        var client = await Open(peer);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, Token));
        Assert.Equal("protocol.frame", error.Code);
        await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
    }

    [Theory]
    [InlineData(false, "worker.timeout")] [InlineData(true, "worker.interrupted")]
    public async Task Injected_clock_bounds_unit_and_missing_heartbeat_without_real_wait(bool heartbeatDeadline, string code)
    {
        var clock = new ManualClock();
        await using var peer = new Peer(async (codec, token) =>
        {
            Assert.Equal("synthesizeSegment", (await codec.ReadAsync(token))!.Type);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var options = heartbeatDeadline ? new WorkerClientOptions { HeartbeatGap = TimeSpan.FromSeconds(1), Synthesis = TimeSpan.FromSeconds(10) } : new WorkerClientOptions { Synthesis = TimeSpan.FromSeconds(2) };
        var client = await WorkerPipeClient.ConnectAsync(peer.Name, peer.Secret, peer.Owner, clock, options, Token);
        var synthesis = client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, Token);
        await clock.WaitForDelay(heartbeatDeadline ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(2), Token);
        clock.Advance(heartbeatDeadline ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => synthesis.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal(code, error.Code); Assert.True(error.CleanupConfirmed);
        await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
    }

    [Fact]
    public async Task Cancel_ACK_and_hung_cleanup_share_one_total_shutdown_budget()
    {
        var clock = new ManualClock(); var owner = new HungOwner(); using var stop = new CancellationTokenSource();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async (codec, token) =>
        {
            Assert.Equal("synthesizeSegment", (await codec.ReadAsync(token))!.Type);
            stop.Cancel();
            Assert.Equal("cancelRequest", (await codec.ReadAsync(token))!.Type); received.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, owner: owner);
        var options = new WorkerClientOptions { Shutdown = TimeSpan.FromSeconds(10) };
        var client = await WorkerPipeClient.ConnectAsync(peer.Name, peer.Secret, owner, clock, options, Token);
        var synthesis = client.SynthesizeAsync(Guid.NewGuid().ToString("N"), Command, _ => { }, stop.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await clock.WaitForDelay(TimeSpan.FromSeconds(5), Token); clock.Advance(TimeSpan.FromSeconds(5));
        await owner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(synthesis.IsCompleted);
        await clock.WaitForDelay(TimeSpan.FromSeconds(5), Token); clock.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => synthesis.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal("worker.timeout", error.Code); Assert.False(error.CleanupConfirmed);
        var dispose = await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
        Assert.False(dispose.CleanupConfirmed);
        var repeatedDispose = await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
        Assert.False(repeatedDispose.CleanupConfirmed);
    }

    [Fact]
    public async Task Explicit_cancel_ACK_also_bounds_missing_target_terminal()
    {
        var clock = new ManualClock();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async (codec, token) =>
        {
            Assert.Equal("synthesizeSegment", (await codec.ReadAsync(token))!.Type); received.SetResult();
            var cancel = (await codec.ReadAsync(token))!; Assert.Equal("cancelRequest", cancel.Type);
            await Reply(codec, cancel.RequestId, "result", new ResultEvent("synthesizing"), token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var client = await WorkerPipeClient.ConnectAsync(peer.Name, peer.Secret, peer.Owner, clock, new() { Shutdown = TimeSpan.FromSeconds(10) }, Token);
        string id = Guid.NewGuid().ToString("N");
        var synthesis = client.SynthesizeAsync(id, Command, _ => { }, Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await client.CancelAsync(id, Token);
        await clock.WaitForDelay(TimeSpan.FromSeconds(10), Token);
        clock.Advance(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => synthesis.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal("worker.timeout", error.Code); Assert.True(error.CleanupConfirmed);
        await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
    }

    [Theory]
    [InlineData("heartbeat")] [InlineData("protocol")]
    public async Task Reserved_request_ID_cannot_collide_with_unsolicited_control_frames(string id)
    {
        await using var peer = new Peer(Shutdown);
        await using var client = await Open(peer);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => client.SynthesizeAsync(id, Command, _ => { }, Token));
        Assert.Equal("worker.arguments", error.Code);
    }

    private sealed class HungOwner : IWorkerSessionLifetime
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> neverExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> StopAsync(TimeSpan remaining, CancellationToken token) { Started.TrySetResult(); return neverExited.Task; }
    }

    private sealed class StoppingOwner : IWorkerSessionLifetime
    {
        private int stopping;
        public bool StopRequested => Volatile.Read(ref stopping) != 0;
        public Task<bool> StopAsync(TimeSpan remaining, CancellationToken token)
        {
            Interlocked.Exchange(ref stopping, 1);
            return Task.FromResult(true);
        }
    }

    private static Task<WorkerPipeClient> Open(Peer peer) => WorkerPipeClient.ConnectAsync(peer.Name, peer.Secret, peer.Owner, TimeProvider.System, new(), Token);
    private static Task Reply<T>(ProtocolCodec codec, string id, string type, T value, CancellationToken token) => codec.WriteAsync(new ProtocolEnvelope<T>(1, id, type, DateTimeOffset.UtcNow.ToString("O"), value), token);
    private static async Task Shutdown(ProtocolCodec codec, CancellationToken token)
    {
        var request = (await codec.ReadAsync(token))!; Assert.Equal("shutdown", request.Type);
        await Reply(codec, request.RequestId, "result", new ResultEvent("stopping"), token);
    }
    private static async Task peerClose(ProtocolCodec codec, CancellationToken token)
    {
        try { await codec.ReadAsync(token); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    // A scripted, current-user OS pipe peer, not a product WorkerHost or real model.
    private sealed class Peer : IAsyncDisposable
    {
        public string Name { get; } = "las-client-test-" + Guid.NewGuid().ToString("N");
        public byte[] Secret { get; } = RandomNumberGenerator.GetBytes(32);
        public TaskCompletionSource HandshakeReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IWorkerSessionLifetime Owner { get; }
        private readonly NamedPipeServerStream pipe;
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        private readonly Task running;
        public Peer(Func<ProtocolCodec, CancellationToken, Task> script, bool rejectHandshake = false, IWorkerSessionLifetime? owner = null,
            int handshakeVersion = 1, Func<ProtocolCodec, Stream, CancellationToken, Task>? rawAfterHandshake = null, bool stallHandshake = false)
        {
            pipe = new(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Owner = owner ?? new PipeOwner(pipe);
            running = Run(script, rejectHandshake, handshakeVersion, rawAfterHandshake, stallHandshake);
        }
        private async Task Run(Func<ProtocolCodec, CancellationToken, Task> script, bool rejectHandshake, int handshakeVersion,
            Func<ProtocolCodec, Stream, CancellationToken, Task>? rawAfterHandshake, bool stallHandshake)
        {
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token); var codec = new ProtocolCodec(pipe);
                var handshake = (await codec.ReadAsync(stop.Token))!; Assert.Equal("handshake", handshake.Type);
                HandshakeReceived.TrySetResult();
                if (stallHandshake) { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); return; }
                if (!rejectHandshake) Assert.Equal(Convert.ToBase64String(Secret), handshake.Payload.GetProperty("sessionSecret").GetString());
                if (handshakeVersion != 1)
                {
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ProtocolEnvelope<ResultEvent>(handshakeVersion, handshake.RequestId, "result", DateTimeOffset.UtcNow.ToString("O"), new("ready")), ProtocolCodec.JsonOptions);
                    byte[] prefix = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
                    await pipe.WriteAsync(prefix, stop.Token); await pipe.WriteAsync(bytes, stop.Token); await pipe.FlushAsync(stop.Token); return;
                }
                await Reply(codec, handshake.RequestId, rejectHandshake ? "error" : "result", rejectHandshake ? (object)new ErrorEvent("worker.authentication", "fatal", "worker.authentication", "worker.retry") : new ResultEvent("ready"), stop.Token);
                if (!rejectHandshake)
                {
                    if (rawAfterHandshake != null) await rawAfterHandshake(codec, pipe, stop.Token);
                    else await script(codec, stop.Token);
                }
            }
            finally { pipe.Dispose(); }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); pipe.Dispose();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5), Token); }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
            finally { stop.Dispose(); CryptographicOperations.ZeroMemory(Secret); }
        }
        private sealed class PipeOwner(NamedPipeServerStream pipe) : IWorkerSessionLifetime
        {
            public Task<bool> StopAsync(TimeSpan remaining, CancellationToken token) { pipe.Dispose(); return Task.FromResult(true); }
        }
    }

    internal sealed class ManualClock : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ClockTimer> timers = [];
        private long ticks;
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (sync) return ticks; }
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (sync) { timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        public async Task WaitForDelay(TimeSpan delay, CancellationToken token)
        {
            while (true)
            {
                Task signal;
                lock (sync) { if (timers.Any(x => x.Due == ticks + delay.Ticks)) return; signal = changed.Task; }
                await signal.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        }
        public void Advance(TimeSpan amount)
        {
            List<ClockTimer> due;
            lock (sync)
            {
                ticks += amount.Ticks;
                due = timers.Where(x => x.Due is { } at && at <= ticks).ToList();
                foreach (var timer in due) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? null : ticks + timer.Period.Ticks;
                Pulse();
            }
            foreach (var timer in due) timer.Fire();
        }
        private void Pulse() { var previous = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); previous.TrySetResult(); }
        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal long? Due { get; set; }
            internal TimeSpan Period { get; private set; }
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.sync)
                {
                    if (disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.ticks + dueTime.Ticks;
                    Period = period; clock.Pulse(); return true;
                }
            }
            internal void Fire() { lock (clock.sync) { if (disposed) return; } callback(state); }
            public void Dispose() { lock (clock.sync) { disposed = true; clock.timers.Remove(this); clock.Pulse(); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

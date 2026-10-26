using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Web.Jobs;
using LocalAudiobookStudio.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class SseTests
{
    private const string Project = "11111111111111111111111111111111";
    private const string Path = "/api/projects/" + Project + "/events";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Kestrel must not wait for a browser's still-open EventSource before hub shutdown.
    [Fact]
    public async Task Host_shutdown_closes_live_stream_before_server_drain()
    {
        await using var host = await Host(new EventSource());
        using var response = await Connect(host); using var reader = await Reader(response);
        Assert.Equal("snapshot", (await Read(reader)).Event);
        await host.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.Null(await reader.ReadLineAsync(Token));
        Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.IsCancellationRequested);
    }

    [Fact]
    public async Task Initial_snapshot_is_atomic_safe_and_does_not_replay_obsolete_history()
    {
        var jobs = new EventSource(); jobs.Commit(1); jobs.Commit(2);
        await using var host = await Host(jobs);
        using var response = await Connect(host); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = await Reader(response); var frame = await Read(reader);
        Assert.Equal("snapshot", frame.Event); Assert.Equal("2", frame.Id);
        var payload = JsonDocument.Parse(frame.Data!).RootElement;
        Assert.Equal(2, payload.GetProperty("headSequence").GetInt64());
        Assert.Equal("running", Assert.Single(payload.GetProperty("jobs").EnumerateArray()).GetProperty("state").GetString());
        Assert.DoesNotContain(host.Root, frame.Data); Assert.DoesNotContain("PRIVATE_SYNTHETIC", frame.Data);
    }

    [Fact]
    public async Task Historical_replay_keeps_cursor_order_and_all_256_events_outside_live_buffer()
    {
        var jobs = new EventSource(); for (int i = 1; i <= 300; i++) jobs.Commit(i);
        await using var host = await Host(jobs);
        using var response = await Connect(host, "44"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = await Reader(response);
        var baseline = await Read(reader); Assert.Equal("snapshot", baseline.Event); Assert.Null(baseline.Id);
        for (int expected = 45; expected <= 300; expected++)
        {
            var frame = await Read(reader); Assert.Equal("job", frame.Event); Assert.Equal(expected.ToString(), frame.Id);
            Assert.Equal(expected, JsonDocument.Parse(frame.Data!).RootElement.GetProperty("jobRevision").GetInt64());
        }
    }

    [Theory] [InlineData("43")] [InlineData("301")]
    public async Task Expired_or_future_cursor_gets_reset_with_current_head(string cursor)
    {
        var jobs = new EventSource(); for (int i = 1; i <= 300; i++) jobs.Commit(i);
        await using var host = await Host(jobs);
        using var response = await Connect(host, cursor); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = await Reader(response); var frame = await Read(reader);
        Assert.Equal("reset", frame.Event); Assert.Equal("300", frame.Id);
    }

    [Theory] [InlineData("-1")] [InlineData("1.5")] [InlineData("9223372036854775808")] [InlineData("1,2")]
    public async Task Malformed_cursor_is_rejected_before_subscription(string cursor)
    {
        await using var host = await Host(new EventSource());
        using var response = await Connect(host, cursor); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("events.cursor.invalid", (await ApiTestHost.JsonAsync(response)).GetProperty("code").GetString());
    }

    [Theory] [InlineData("cookie", 401)] [InlineData("host", 400)] [InlineData("origin", 403)] [InlineData("null-origin", 403)]
    public async Task Stream_requires_local_host_session_and_any_present_origin_to_match(string invalid, int status)
    {
        await using var host = await Host(new EventSource()); using var request = host.Request(HttpMethod.Get, Path);
        if (invalid == "cookie") request.Headers.Remove("Cookie");
        if (invalid == "host") request.Headers.Host = "localhost:" + host.Client.BaseAddress!.Port;
        if (invalid is "origin" or "null-origin") { request.Headers.Remove("Origin"); request.Headers.Add("Origin", invalid == "origin" ? "https://example.invalid" : "null"); }
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task Native_eventsource_without_origin_or_csrf_is_allowed_and_empty_future_cursor_resets()
    {
        await using var host = await Host(new EventSource()); using var request = host.Request(HttpMethod.Get, Path);
        request.Headers.Remove("Origin"); request.Headers.Remove("X-CSRF-TOKEN"); request.Headers.Add("Last-Event-ID", "1");
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var reader = await Reader(response);
        var frame = await Read(reader); Assert.Equal("reset", frame.Event); Assert.Equal("0", frame.Id);
    }

    [Fact]
    public async Task Ninth_subscription_is_rejected_and_disconnect_releases_admission()
    {
        await using var host = await Host(new EventSource()); var open = new List<HttpResponseMessage>();
        try
        {
            for (int i = 0; i < 8; i++) { var response = await Connect(host); Assert.Equal(HttpStatusCode.OK, response.StatusCode); open.Add(response); }
            using var ninth = await Connect(host); Assert.Equal(HttpStatusCode.TooManyRequests, ninth.StatusCode);
            Assert.Equal("events.limit", (await ApiTestHost.JsonAsync(ninth)).GetProperty("code").GetString());
            open[0].Dispose(); open.RemoveAt(0);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                using var retried = await Connect(host);
                if (retried.StatusCode == HttpStatusCode.OK) break;
                Assert.Equal(HttpStatusCode.TooManyRequests, retried.StatusCode); await Task.Delay(25, deadline.Token);
            }
        }
        finally { foreach (var response in open) response.Dispose(); }
    }

    [Fact]
    public async Task Durable_scan_delivers_live_event_without_a_notification_and_heartbeat_has_no_cursor()
    {
        var jobs = new EventSource(); var clock = new JobCoordinatorTests.ManualClock();
        await using var host = await Host(jobs, clock); using var response = await Connect(host);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var reader = await Reader(response);
        Assert.Equal("0", (await Read(reader)).Id);
        await clock.WaitForDelay(TimeSpan.FromSeconds(1), 2);
        jobs.Commit(1); clock.Advance(TimeSpan.FromSeconds(1));
        var hint = await Read(reader); Assert.Equal("job", hint.Event); Assert.Equal("1", hint.Id);
        await clock.WaitForDelay(TimeSpan.FromSeconds(9));
        var pending = Read(reader); clock.Advance(TimeSpan.FromSeconds(8)); Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1)); var beat = await pending;
        Assert.Equal("heartbeat", beat.Comment); Assert.Null(beat.Event); Assert.Null(beat.Id); Assert.Null(beat.Data);
        Assert.Equal(1, (await jobs.ReadEventPageAsync(Project, null, Token)).HeadSequence);
    }

    [Fact]
    public async Task Live_retention_gap_resets_empty_waiting_subscriber_before_closing()
    {
        var jobs = new EventSource(); var clock = new JobCoordinatorTests.ManualClock();
        await using var host = await Host(jobs, clock); using var response = await Connect(host); using var reader = await Reader(response);
        Assert.Equal("0", (await Read(reader)).Id);
        // The adapter is actually waiting on its EMPTY channel, not writing old hints.
        await clock.WaitForDelay(TimeSpan.FromSeconds(10)); await clock.WaitForDelay(TimeSpan.FromSeconds(1), 2);
        for (int i = 1; i <= 300; i++) jobs.Commit(i);
        clock.Advance(TimeSpan.FromSeconds(1)); var frame = await Read(reader);
        Assert.Equal("reset", frame.Event); Assert.Equal("300", frame.Id);
        Assert.Equal(300, JsonDocument.Parse(frame.Data!).RootElement.GetProperty("headSequence").GetInt64());
        Assert.Null(await reader.ReadLineAsync(Token));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Subscription_buffers_changes_during_baseline_and_deduplicates_hints_already_in_it(bool refreshBaseline)
    {
        var jobs = new EventSource(); var clock = new JobCoordinatorTests.ManualClock();
        await using var host = await Host(jobs, clock); using var first = await Connect(host); using var firstReader = await Reader(first);
        await Read(firstReader); await clock.WaitForDelay(TimeSpan.FromSeconds(1), 2);
        jobs.GateBaseline = true; jobs.RefreshBaseline = refreshBaseline;
        var connecting = Connect(host); await jobs.BaselineCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        jobs.Commit(1); clock.Advance(TimeSpan.FromSeconds(1)); Assert.Equal("1", (await Read(firstReader)).Id);
        jobs.BaselineRelease.TrySetResult(); using var second = await connecting; using var secondReader = await Reader(second);
        Assert.Equal(refreshBaseline ? "1" : "0", (await Read(secondReader)).Id);
        if (!refreshBaseline) Assert.Equal("1", (await Read(secondReader)).Id);
        await clock.WaitForDelay(TimeSpan.FromSeconds(1), 2); jobs.Commit(2); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("2", (await Read(secondReader)).Id); Assert.Equal("2", (await Read(firstReader)).Id);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Concurrent_first_baselines_finishing_in_reverse_order_do_not_lose_the_older_subscribers_gap(bool scanInFlight)
    {
        var jobs = new EventSource { GateBaseline = true }; jobs.Commit(1); var clock = new JobCoordinatorTests.ManualClock();
        await using var host = await Host(jobs, clock);
        var olderConnection = Connect(host); await jobs.BaselineCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        jobs.Commit(2); using var newer = await Connect(host); using var newerReader = await Reader(newer);
        Assert.Equal("2", (await Read(newerReader)).Id);
        if (scanInFlight)
        {
            jobs.GateScan = true; await clock.WaitForDelay(TimeSpan.FromSeconds(1), 2); clock.Advance(TimeSpan.FromSeconds(1));
            await jobs.ScanCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        jobs.BaselineRelease.TrySetResult(); using var older = await olderConnection; using var olderReader = await Reader(older);
        Assert.Equal("1", (await Read(olderReader)).Id);
        jobs.ScanRelease.TrySetResult();
        await clock.WaitForDelay(TimeSpan.FromSeconds(1), 2); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("2", (await ReadJob(olderReader)).Id);
    }

    [Fact]
    public async Task Slow_subscriber_overflow_does_not_block_fast_ordered_delivery()
    {
        var jobs = new EventSource(); var clock = new JobCoordinatorTests.ManualClock();
        await using var host = await Host(jobs, clock);
        using var slow = host.Services.GetRequiredService<JobEventHub>().Subscribe(Project); slow.EstablishBaseline(0);
        using var response = await Connect(host); using var reader = await Reader(response); await Read(reader);
        for (int sequence = 1; sequence <= 33; sequence++)
        {
            await clock.WaitForDelay(TimeSpan.FromSeconds(1), 2); jobs.Commit(sequence); clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(sequence.ToString(), (await ReadJob(reader)).Id);
        }
        Assert.True(slow.ResetRequested);
        await clock.WaitForDelay(TimeSpan.FromSeconds(1), 2); jobs.Commit(34); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("34", (await ReadJob(reader)).Id);
    }

    [Fact]
    public async Task Blocked_adapter_writer_sends_atomic_reset_after_overflow_and_then_ends()
    {
        var jobs = new EventSource(); var clock = new JobCoordinatorTests.ManualClock();
        await using var host = await Host(jobs, clock); using var fast = await Connect(host); using var reader = await Reader(fast); await Read(reader);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token); using var body = new BlockedBody();
        var context = new DefaultHttpContext(); context.Response.Body = body; context.RequestAborted = cancel.Token;
        var method = typeof(ProjectEventEndpoints).GetMethod("StreamAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var handler = method.CreateDelegate<Func<HttpContext, string, IStudioJobService, JobEventHub, TimeProvider, Task>>();
        var operation = handler(context, Project, jobs, host.Services.GetRequiredService<JobEventHub>(), clock);
        try
        {
            await body.BaselineWritten.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await clock.WaitForDelay(TimeSpan.FromSeconds(1), 2); jobs.Commit(1); clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("1", (await ReadJob(reader)).Id); await body.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            for (int sequence = 2; sequence <= 34; sequence++)
            {
                await clock.WaitForDelay(TimeSpan.FromSeconds(1), 2); jobs.Commit(sequence); clock.Advance(TimeSpan.FromSeconds(1));
                Assert.Equal(sequence.ToString(), (await ReadJob(reader)).Id);
            }
            Assert.False(operation.IsCompleted); body.Release.TrySetResult(); await operation.WaitAsync(TimeSpan.FromSeconds(5), Token);
            using var captured = new StreamReader(new MemoryStream(body.Bytes));
            Assert.Equal("0", (await Read(captured)).Id); Assert.Equal("1", (await Read(captured)).Id);
            var reset = await Read(captured); Assert.Equal("reset", reset.Event); Assert.Equal("34", reset.Id);
            Assert.Equal(34, JsonDocument.Parse(reset.Data!).RootElement.GetProperty("headSequence").GetInt64());
            Assert.Null(await captured.ReadLineAsync(Token));
        }
        finally
        {
            body.Release.TrySetResult(); await cancel.CancelAsync();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Unknown_project_releases_subscription_and_multiple_headers_fail_closed()
    {
        await using var host = await Host(new EventSource());
        for (int i = 0; i < 9; i++)
        {
            using var unknown = await host.SendAsync(HttpMethod.Get, "/api/projects/33333333333333333333333333333333/events");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        using (var request = host.Request(HttpMethod.Get, Path))
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", new[] { "0", "1" });
            using var invalid = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var request = host.Request(HttpMethod.Get, Path))
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://example.invalid");
            using var invalid = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
            Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
        }
        using var response = await Connect(host, "0"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<ApiTestHost> Host(EventSource jobs, TimeProvider? time = null) => ApiTestHost.StartAsync(configureServices: services =>
    {
        services.AddSingleton<IStudioJobService>(jobs); if (time != null) services.AddSingleton(time);
    });
    private static async Task<HttpResponseMessage> Connect(ApiTestHost host, string? cursor = null)
    {
        using var request = host.Request(HttpMethod.Get, Path);
        if (cursor != null) request.Headers.Add("Last-Event-ID", cursor);
        return await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
    }
    private static async Task<StreamReader> Reader(HttpResponseMessage response) => new(await response.Content.ReadAsStreamAsync(Token));
    private sealed record Frame(string? Event, string? Id, string? Data, string? Comment);
    private static async Task<Frame> Read(StreamReader reader)
    {
        string? name = null, id = null, data = null, comment = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            string line = await reader.ReadLineAsync(deadline.Token) ?? throw new InvalidOperationException("SSE ended before frame");
            if (line == "") return new(name, id, data, comment);
            if (line.StartsWith(": ")) comment = line[2..];
            if (line.StartsWith("event: ")) name = line[7..];
            if (line.StartsWith("id: ")) id = line[4..];
            if (line.StartsWith("data: ")) data = line[6..];
        }
    }
    private static async Task<Frame> ReadJob(StreamReader reader)
    {
        while (true)
        {
            var frame = await Read(reader);
            if (frame.Event != null) return frame;
            Assert.Equal("heartbeat", frame.Comment); Assert.Null(frame.Id);
        }
    }

    private sealed class BlockedBody : Stream
    {
        private readonly MemoryStream captured = new();
        private int writes;
        public byte[] Bytes => captured.ToArray();
        public TaskCompletionSource BaselineWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await captured.WriteAsync(buffer, token);
            if (++writes == 1) BaselineWritten.TrySetResult();
            else if (writes == 2) { Blocked.TrySetResult(); await Release.Task.WaitAsync(token); }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) captured.Dispose(); base.Dispose(disposing); }
    }

    // Only atomic producer pages are scripted here; HTTP security, transport,
    // admission, baseline/replay adapter and background hub remain real.
    private sealed class EventSource : IStudioJobService
    {
        private readonly object sync = new();
        private readonly List<JobEventDto> events = [];
        private long head;
        public bool GateBaseline { get; set; }
        public bool RefreshBaseline { get; set; }
        public bool GateScan { get; set; }
        public TaskCompletionSource BaselineCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BaselineRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int gated;
        private int scanGated;
        public TaskCompletionSource ScanCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ScanRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Commit(long sequence)
        { lock (sync) { head = sequence; events.Add(new(sequence, "22222222222222222222222222222222", sequence, JobState.Running, JobRequestedState.Run, "generating", 0, 2, null, "q8-f16-cpu")); } }
        public async Task<JobEventPageDto> ReadEventPageAsync(string projectId, long? cursor, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (projectId != Project) throw new StudioException("project.unavailable");
            JobEventPageDto page; lock (sync) page = Page(cursor);
            if (cursor == null && GateBaseline && Interlocked.Exchange(ref gated, 1) == 0)
            {
                BaselineCaptured.TrySetResult(); await BaselineRelease.Task.WaitAsync(token);
                if (RefreshBaseline) lock (sync) page = Page(cursor);
            }
            if (cursor != null && GateScan && Interlocked.Exchange(ref scanGated, 1) == 0)
            { ScanCaptured.TrySetResult(); await ScanRelease.Task.WaitAsync(token); }
            return page;
        }
        private JobEventPageDto Page(long? cursor)
        {
            long first = head == 0 ? 0 : Math.Max(1, head - 255);
            bool reset = cursor != null && (cursor > head || cursor < first - 1);
            var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
            JobSnapshotDto[] jobs = head == 0 ? [] : [new("22222222222222222222222222222222", Project, JobKind.Generation, JobState.Running,
                JobRequestedState.Run, "generating", 0, 2, head, now, now, null, null, new string('A', 64), "q8-f16-cpu")];
            return new JobEventPageDto(head, first, jobs,
                cursor == null || reset ? [] : events.Where(x => x.Sequence > cursor && x.Sequence >= first).ToArray(), reset);
        }
        public Task<JobSnapshotDto> EnqueueAsync(string projectId, long revision, string key, GenerationRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobSnapshotDto>> ListAsync(string projectId, CancellationToken token) => throw new NotSupportedException();
        public Task<JobSnapshotDto> GetAsync(string projectId, string jobId, CancellationToken token) => throw new NotSupportedException();
        public Task<JobSnapshotDto> RequestStateAsync(string projectId, string jobId, long revision, string key, JobControlCommand command, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobEventDto>> ReadEventsAsync(string projectId, long cursor, CancellationToken token) => throw new NotSupportedException();
        public Task<long> GetEventSequenceAsync(string projectId, CancellationToken token) => throw new NotSupportedException();
    }
}

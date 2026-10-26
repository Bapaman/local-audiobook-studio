using System.Net;
using System.Net.Http.Json;
using System.Text;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class JobEndpointTests
{
    [Fact]
    public async Task Runtime_readiness_is_safe_false_without_configuration_and_cannot_disclose_arbitrary_provider_details()
    {
        await using (var host = await ApiTestHost.StartAsync())
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/runtime/readiness");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var readiness = await ApiTestHost.JsonAsync(response);
            Assert.False(readiness.GetProperty("ready").GetBoolean()); Assert.False(readiness.GetProperty("experimental").GetBoolean());
            Assert.Equal("worker.runtime.not_ready", readiness.GetProperty("diagnosticCode").GetString());
            Assert.Equal("q8-f16-cpu", readiness.GetProperty("profileId").GetString()); Assert.DoesNotContain(host.Root, readiness.ToString());
        }
        await using var malformed = await ApiTestHost.StartAsync(configureServices: services => services.AddSingleton<IRuntimeReadiness>(new UnsafeReadiness()));
        using var rejected = await malformed.SendAsync(HttpMethod.Get, "/api/runtime/readiness");
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode); var safe = await ApiTestHost.JsonAsync(rejected);
        Assert.False(safe.GetProperty("ready").GetBoolean()); Assert.False(safe.GetProperty("experimental").GetBoolean());
        Assert.Equal("worker.runtime.not_ready", safe.GetProperty("diagnosticCode").GetString());
        Assert.Equal("q8-f16-cpu", safe.GetProperty("profileId").GetString()); Assert.DoesNotContain("PRIVATE_SYNTHETIC", safe.ToString());
    }

    [Theory] [InlineData("q8-f16-cpu")] [InlineData("q8-f16-hybrid-16")]
    public async Task Readiness_reports_only_the_explicit_providers_valid_profile_without_starting_a_worker(string profile)
    {
        await using var host = await ApiTestHost.StartAsync(configureServices: services => services.AddSingleton<IRuntimeReadiness>(new ValidReadiness(profile)));
        using var response = await host.SendAsync(HttpMethod.Get, "/api/runtime/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var value = await ApiTestHost.JsonAsync(response);
        Assert.True(value.GetProperty("ready").GetBoolean()); Assert.True(value.GetProperty("experimental").GetBoolean());
        Assert.Equal(profile, value.GetProperty("profileId").GetString()); Assert.Equal(System.Text.Json.JsonValueKind.Null, value.GetProperty("diagnosticCode").ValueKind);
    }

    [Fact]
    public async Task Controls_commit_during_a_real_live_claim_without_waiting_for_exclusive_ownership()
    {
        await using var host = await ReadyHost(); var input = await Prepare(host);
        using var enqueue = await Enqueue(host, input, "enqueue"); Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        string id = (await ApiTestHost.JsonAsync(enqueue)).GetProperty("id").GetString()!;
        await using var db = await new ProjectService().OpenCurrentAsync(Path.Combine(host.Root, "projects", input.Id), ApiTestHost.Token);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: ApiTestHost.Token);
        var queued = await db.Jobs.GetAsync(id, ApiTestHost.Token);
        var claim = await lease.BeginGenerationUnitAsync(id, input.Segment, queued.Input.Preset, ApiTestHost.Token);
        Assert.NotNull(claim);
        long claimedProjectRevision = db.State.Revision;
        var running = await db.Jobs.GetAsync(id, ApiTestHost.Token);
        string path = $"/api/projects/{input.Id}/jobs/{id}";
        using var pause = await host.SendAsync(HttpMethod.Post, path + "/pause", revision: '"' + running.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + '"', key: "pause");
        Assert.Equal(HttpStatusCode.Accepted, pause.StatusCode); Assert.Equal("pause_requested", (await ApiTestHost.JsonAsync(pause)).GetProperty("state").GetString());
        using var cancel = await host.SendAsync(HttpMethod.Post, path + "/cancel", revision: pause.Headers.ETag!.Tag, key: "cancel");
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode); Assert.Equal("cancel_requested", (await ApiTestHost.JsonAsync(cancel)).GetProperty("state").GetString());
        var stored = await db.Jobs.GetAsync(id, ApiTestHost.Token);
        Assert.Equal(claim.Token, stored.ClaimToken); Assert.Equal(0, stored.CompletedUnits);
        Assert.Equal(claimedProjectRevision, (await db.ReadCurrentSnapshotAsync(ApiTestHost.Token)).State.Revision);
        await lease.InterruptGenerationUnitRetainingStagingAsync(claim, "worker.interrupted", ApiTestHost.Token);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Control_body_is_bounded_and_unknown_properties_never_change_the_job(bool oversized)
    {
        await using var host = await ReadyHost(); var input = await Prepare(host);
        using var enqueue = await Enqueue(host, input, "enqueue"); Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        string id = (await ApiTestHost.JsonAsync(enqueue)).GetProperty("id").GetString()!;
        string json = "{\"extra\":\"" + (oversized ? new string('x', 524288) : "PRIVATE_SYNTHETIC") + "\"}";
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{input.Id}/jobs/{id}/pause", new StringContent(json, Encoding.UTF8, "application/json"), "\"1\"", "pause");
        Assert.Equal(oversized ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest, response.StatusCode);
        using var get = await host.SendAsync(HttpMethod.Get, $"/api/projects/{input.Id}/jobs/{id}");
        Assert.Equal("queued", (await ApiTestHost.JsonAsync(get)).GetProperty("state").GetString()); Assert.Equal("\"1\"", get.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Enqueue_replays_before_stale_project_revision_and_returns_only_safe_string_enum_summary()
    {
        await using var host = await ReadyHost(); var input = await Prepare(host);
        using var response = await Enqueue(host, input, "first");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await ApiTestHost.JsonAsync(response); string jobId = job.GetProperty("id").GetString()!;
        Assert.Equal("generation", job.GetProperty("kind").GetString());
        Assert.Equal("queued", job.GetProperty("state").GetString());
        Assert.Equal("run", job.GetProperty("requestedState").GetString());
        Assert.Equal(0, job.GetProperty("completedUnits").GetInt32()); Assert.Equal(1, job.GetProperty("totalUnits").GetInt32());
        Assert.Equal("\"1\"", response.Headers.ETag!.Tag);
        Assert.Equal($"/api/projects/{input.Id}/jobs/{jobId}", response.Headers.Location!.OriginalString);
        Assert.DoesNotContain("PRIVATE_SYNTHETIC", job.ToString()); Assert.DoesNotContain(host.Root, job.ToString());
        Assert.False(job.TryGetProperty("input", out _)); Assert.False(job.TryGetProperty("claimToken", out _));
        using var replay = await Enqueue(host, input with { ETag = "\"0\"" }, "first");
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode); Assert.Equal(jobId, (await ApiTestHost.JsonAsync(replay)).GetProperty("id").GetString());
        using var list = await host.SendAsync(HttpMethod.Get, $"/api/projects/{input.Id}/jobs");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode); Assert.Single((await ApiTestHost.JsonAsync(list)).EnumerateArray());
        using var get = await host.SendAsync(HttpMethod.Get, $"/api/projects/{input.Id}/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode); Assert.Equal("\"1\"", get.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Controls_commit_and_replay_without_changing_project_revision_and_job_conflict_has_job_etag()
    {
        await using var host = await ReadyHost(); var input = await Prepare(host);
        using var enqueue = await Enqueue(host, input, "enqueue"); Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        string id = (await ApiTestHost.JsonAsync(enqueue)).GetProperty("id").GetString()!;
        string path = $"/api/projects/{input.Id}/jobs/{id}";
        using var pause = await host.SendAsync(HttpMethod.Post, path + "/pause", revision: "\"1\"", key: "pause");
        Assert.Equal(HttpStatusCode.Accepted, pause.StatusCode); Assert.Equal("paused", (await ApiTestHost.JsonAsync(pause)).GetProperty("state").GetString());
        string pausedTag = pause.Headers.ETag!.Tag;
        using var replay = await host.SendAsync(HttpMethod.Post, path + "/pause", revision: "\"0\"", key: "pause");
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode); Assert.Equal(pausedTag, replay.Headers.ETag!.Tag);
        using var stale = await host.SendAsync(HttpMethod.Post, path + "/resume", revision: "\"1\"", key: "stale");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var problem = await ApiTestHost.JsonAsync(stale);
        Assert.Equal("job.conflict", problem.GetProperty("code").GetString()); Assert.Equal("job", problem.GetProperty("conflictType").GetString());
        var current = problem.GetProperty("currentSnapshot"); Assert.Equal(id, current.GetProperty("id").GetString());
        Assert.Equal("paused", current.GetProperty("state").GetString()); Assert.False(current.TryGetProperty("segments", out _));
        Assert.Equal(pausedTag, stale.Headers.ETag!.Tag); Assert.DoesNotContain(host.Root, problem.ToString());
        using var resume = await host.SendAsync(HttpMethod.Post, path + "/resume", revision: pausedTag, key: "resume");
        Assert.Equal(HttpStatusCode.Accepted, resume.StatusCode); Assert.Equal("queued", (await ApiTestHost.JsonAsync(resume)).GetProperty("state").GetString());
        using var cancel = await host.SendAsync(HttpMethod.Post, path + "/cancel", revision: resume.Headers.ETag!.Tag, key: "cancel");
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode); Assert.Equal("canceled", (await ApiTestHost.JsonAsync(cancel)).GetProperty("state").GetString());
        using var project = await host.SendAsync(HttpMethod.Get, $"/api/projects/{input.Id}"); Assert.Equal(input.ETag, project.Headers.ETag!.Tag);
        using var invalid = await host.SendAsync(HttpMethod.Post, path + "/resume", revision: cancel.Headers.ETag!.Tag, key: "invalid");
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode); Assert.Equal("job.transition.invalid", (await ApiTestHost.JsonAsync(invalid)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false, null, 428)] [InlineData(true, null, 428)]
    [InlineData(false, "1", 400)] [InlineData(true, "1", 400)]
    [InlineData(false, "W/\"1\"", 400)] [InlineData(true, "W/\"1\"", 400)]
    [InlineData(false, "\"1\",\"2\"", 400)] [InlineData(true, "\"1\",\"2\"", 400)]
    public async Task Both_command_revision_scopes_require_one_strong_decimal_etag_before_service(bool control, string? revision, int status)
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        string path = $"/api/projects/{project.Id}/jobs" + (control ? "/" + Guid.NewGuid().ToString("N") + "/pause" : "");
        using var response = await host.SendAsync(HttpMethod.Post, path, control ? null : JsonContent.Create(new { kind = "generation", segmentIds = new[] { "segment" } }), revision, "key");
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("{\"kind\":0,\"segmentIds\":[]}")]
    [InlineData("{\"kind\":\"unknown\",\"segmentIds\":[]}")]
    [InlineData("{\"kind\":\"generation\",\"segmentIds\":[],\"privatePath\":\"PRIVATE_SYNTHETIC\"}")]
    [InlineData("{\"segmentIds\":[]}")]
    [InlineData("{\"kind\":\"generation\"}")]
    public async Task Job_body_rejects_numeric_unknown_extra_or_missing_properties_without_runtime_or_private_details(string json)
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/jobs", new StringContent(json, Encoding.UTF8, "application/json"), project.ETag, "key");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiTestHost.JsonAsync(response); Assert.Equal("request.invalid", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("PRIVATE_SYNTHETIC", problem.ToString()); Assert.DoesNotContain(host.Root, problem.ToString());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Job_body_limit_applies_to_known_and_chunked_lengths(bool chunked)
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        byte[] bytes = Encoding.UTF8.GetBytes("{\"kind\":\"generation\",\"segmentIds\":[\"" + new string('x', 524288) + "\"]}");
        using HttpContent content = chunked ? new StreamContent(new ForwardOnly(bytes)) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/jobs", content, project.ETag, "key");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request.too_large", (await ApiTestHost.JsonAsync(response)).GetProperty("code").GetString());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Exact_job_json_limit_is_allowed_before_runtime_validation(bool chunked)
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        string prefix = "{\"kind\":\"preview\",\"segmentIds\":[\"" + Guid.NewGuid().ToString("N") + "\"]}";
        byte[] bytes = Encoding.UTF8.GetBytes(prefix + new string(' ', 524288 - prefix.Length));
        using HttpContent content = chunked ? new StreamContent(new ForwardOnly(bytes)) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/jobs", content, project.ETag, "key");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("worker.runtime.not_ready", (await ApiTestHost.JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stale_new_enqueue_returns_project_conflict_not_job_snapshot_and_runtime_missing_is_503()
    {
        await using (var host = await ReadyHost())
        {
            var input = await Prepare(host);
            using var stale = await Enqueue(host, input with { ETag = "\"0\"" }, "new");
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            var problem = await ApiTestHost.JsonAsync(stale); Assert.Equal("project", problem.GetProperty("conflictType").GetString());
            Assert.Equal(input.Id, problem.GetProperty("currentSnapshot").GetProperty("id").GetString());
            Assert.True(problem.GetProperty("currentSnapshot").TryGetProperty("segments", out _)); Assert.Equal(input.ETag, stale.Headers.ETag!.Tag);
        }
        await using var unavailable = await ApiTestHost.StartAsync(); var project = await unavailable.CreateAsync();
        using var response = await unavailable.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/jobs", JsonContent.Create(new { kind = "preview", segmentIds = new[] { Guid.NewGuid().ToString("N") } }), project.ETag, "key");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal("worker.runtime.not_ready", (await ApiTestHost.JsonAsync(response)).GetProperty("code").GetString());
    }

    [Theory] [InlineData("session", 401)] [InlineData("origin", 403)] [InlineData("csrf", 400)] [InlineData("host", 400)]
    public async Task Commands_use_existing_local_security_before_any_durable_job(string invalid, int status)
    {
        await using var host = await ReadyHost(); var input = await Prepare(host);
        using var request = host.Request(HttpMethod.Post, $"/api/projects/{input.Id}/jobs", JsonContent.Create(new { kind = "generation", segmentIds = new[] { input.Segment } }), input.ETag, "key");
        if (invalid == "host") request.Headers.Host = "localhost:" + host.Client.BaseAddress!.Port;
        else request.Headers.Remove(invalid switch { "session" => "Cookie", "origin" => "Origin", _ => "X-CSRF-TOKEN" });
        using var response = await host.Client.SendAsync(request, ApiTestHost.Token); Assert.Equal(status, (int)response.StatusCode);
        await using var db = await new ProjectService().OpenCurrentAsync(Path.Combine(host.Root, "projects", input.Id), ApiTestHost.Token);
        Assert.Empty(await db.Jobs.ListAsync(ApiTestHost.Token));
    }

    [Fact]
    public async Task Unknown_job_is_a_fixed_404_and_key_reuse_with_changed_kind_is_409()
    {
        await using var host = await ReadyHost(); var input = await Prepare(host);
        using var missing = await host.SendAsync(HttpMethod.Get, $"/api/projects/{input.Id}/jobs/{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode); Assert.Equal("job.missing", (await ApiTestHost.JsonAsync(missing)).GetProperty("code").GetString());
        using var first = await Enqueue(host, input, "key"); Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var changed = await host.SendAsync(HttpMethod.Post, $"/api/projects/{input.Id}/jobs", JsonContent.Create(new { kind = "preview", segmentIds = new[] { input.Segment } }), input.ETag, "key");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode); Assert.Equal("job.idempotency.conflict", (await ApiTestHost.JsonAsync(changed)).GetProperty("code").GetString());
    }

    private static Task<ApiTestHost> ReadyHost() => ApiTestHost.StartAsync(configureServices: services => services.AddSingleton<IRuntimeDescriptor>(new SyntheticDescriptor()));
    private sealed record Input(string Id, string ETag, string Segment);
    private static Task<HttpResponseMessage> Enqueue(ApiTestHost host, Input input, string key) => host.SendAsync(HttpMethod.Post,
        $"/api/projects/{input.Id}/jobs", JsonContent.Create(new { kind = "generation", segmentIds = new[] { input.Segment } }), input.ETag, key);
    private static async Task<Input> Prepare(ApiTestHost host)
    {
        var project = await host.CreateAsync();
        using var upload = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart(ApiTestHost.Wav()), project.ETag);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var anchor = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/anchor", JsonContent.Create(new
        { startMilliseconds = 0, endMilliseconds = 10000, transcript = "PRIVATE_SYNTHETIC_TRANSCRIPT", pairListenedAndConfirmed = true }), upload.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, anchor.StatusCode);
        using var text = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/text/paste", JsonContent.Create(new { displayName = "Text", text = "PRIVATE_SYNTHETIC_TEXT." }), anchor.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        return new(project.Id, text.Headers.ETag!.Tag, (await ApiTestHost.JsonAsync(text)).GetProperty("segments")[0].GetProperty("id").GetString()!);
    }
    private sealed class SyntheticDescriptor : IRuntimeDescriptor
    {
        public Task<RuntimeDescriptor> GetAsync(CancellationToken token) => Task.FromResult(new RuntimeDescriptor(
            new("voxcpm2-local-v1", "q8-f16-cpu", 70, 10, 2, 1, 42, false), new("", new string('1', 40), new string('2', 40), "", "", "")));
    }
    private sealed class UnsafeReadiness : IRuntimeReadiness
    {
        public Task<RuntimeReadiness> GetAsync(CancellationToken token) => Task.FromResult(new RuntimeReadiness(true, true, "PRIVATE_SYNTHETIC_DIAGNOSTIC", "C:\\PRIVATE_SYNTHETIC_RUNTIME"));
    }
    private sealed class ValidReadiness(string profile) : IRuntimeReadiness
    {
        public Task<RuntimeReadiness> GetAsync(CancellationToken token) => Task.FromResult(new RuntimeReadiness(true, true, null, profile));
    }
    private sealed class ForwardOnly(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

using System.Net;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using Microsoft.Extensions.DependencyInjection;
using static LocalAudiobookStudio.Web.Tests.VoxCpmTrainingApiFixture;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class VoxCpmTrainingEndpointTests
{
    [Fact]
    public async Task Unconfigured_runtime_is_honestly_unavailable_and_history_is_nullable()
    {
        await using var host = await ApiTestHost.StartAsync(); var p = await host.CreateAsync();
        using var ready = await host.SendAsync(HttpMethod.Get, "/api/voxcpm-training/readiness"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var r = await ApiTestHost.JsonAsync(ready); Assert.False(r.GetProperty("ready").GetBoolean()); Assert.Equal("voxcpm_training.runtime.not_ready", r.GetProperty("diagnosticCode").GetString());
        using var empty = await host.SendAsync(HttpMethod.Get, PathFor(p.Id)); Assert.Equal("null", await empty.Content.ReadAsStringAsync(ApiTestHost.Token)); Assert.Null(empty.Headers.ETag);
        using var denied = await host.SendAsync(HttpMethod.Post, PathFor(p.Id), StartBody(new('a', 32)), p.ETag, "start");
        await Problem(denied, HttpStatusCode.ServiceUnavailable, "voxcpm_training.runtime.not_ready");
        using var noSession = await host.Client.GetAsync("/api/voxcpm-training/readiness", ApiTestHost.Token); Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
    }
    [Fact]
    public async Task Exact_start_stop_resume_routes_replay_before_CAS_and_conflicts_use_training_snapshot()
    {
        await using var host = await HostAsync(); var p = await ApprovedAsync(host); string path = PathFor(p.Id);
        using var started = await host.SendAsync(HttpMethod.Post, path, StartBody(p.VersionId), p.ETag, "start"); Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var s = await ApiTestHost.JsonAsync(started); string id = s.GetProperty("id").GetString()!;
        Assert.Equal(p.VersionId, s.GetProperty("corpusVersionId").GetString()); Assert.Equal("queued", s.GetProperty("state").GetString()); Assert.False(s.GetProperty("qualityEstablished").GetBoolean());
        Assert.DoesNotContain(host.Root, s.ToString()); Assert.False(s.TryGetProperty("identity", out _));
        using var replay = await host.SendAsync(HttpMethod.Post, path, StartBody(p.VersionId), "\"0\"", "start"); Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode); Assert.Equal(s.ToString(), (await ApiTestHost.JsonAsync(replay)).ToString());
        using var reused = await host.SendAsync(HttpMethod.Post, path, StartBody(p.VersionId, false), p.ETag, "start"); await Problem(reused, HttpStatusCode.Conflict, "voxcpm_training.idempotency.conflict");
        using var stopped = await host.SendAsync(HttpMethod.Post, path + "/" + id + "/stop", revision: started.Headers.ETag!.Tag, key: "stop"); Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode);
        Assert.Equal("paused", (await ApiTestHost.JsonAsync(stopped)).GetProperty("state").GetString());
        using var stale = await host.SendAsync(HttpMethod.Post, path + "/" + id + "/resume", revision: "\"0\"", key: "stale");
        var problem = await Problem(stale, HttpStatusCode.PreconditionFailed, "voxcpm_training.conflict");
        Assert.Equal("voxcpm_training", problem.GetProperty("conflictType").GetString()); Assert.Equal(id, problem.GetProperty("currentSnapshot").GetProperty("id").GetString()); Assert.Equal(stopped.Headers.ETag!.Tag, stale.Headers.ETag!.Tag);
        using var resumed = await host.SendAsync(HttpMethod.Post, path + "/" + id + "/resume", revision: stopped.Headers.ETag!.Tag, key: "resume"); Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.NotEqual(s.GetProperty("executionId").GetString(), (await ApiTestHost.JsonAsync(resumed)).GetProperty("executionId").GetString());
        using var get = await host.SendAsync(HttpMethod.Get, path); Assert.Equal(resumed.Headers.ETag!.Tag, get.Headers.ETag!.Tag);
    }
    [Fact]
    public async Task Start_project_conflict_remains_project_discriminator()
    {
        await using var host = await HostAsync(); var p = await ApprovedAsync(host);
        using var response = await host.SendAsync(HttpMethod.Post, PathFor(p.Id), StartBody(p.VersionId), "\"0\"", "stale-project");
        var problem = await Problem(response, HttpStatusCode.PreconditionFailed, "project.conflict"); Assert.Equal("project", problem.GetProperty("conflictType").GetString()); Assert.Equal(p.ETag, response.Headers.ETag!.Tag);
    }
    [Theory]
    [InlineData("{}", 400, "request.invalid")]
    [InlineData("{\"experimentalConfirmed\":true}", 400, "request.invalid")]
    [InlineData("{\"corpusVersionId\":\"a\"}", 400, "request.invalid")]
    [InlineData("{\"corpusVersionId\":\"a\",\"experimentalConfirmed\":false}", 403, "voxcpm_training.confirmation.required")]
    [InlineData("{\"corpusVersionId\":\"a\",\"experimentalConfirmed\":true,\"modelPath\":\"C:/private\"}", 400, "request.invalid")]
    public async Task Explicit_fields_consent_and_no_browser_paths_are_required(string body, int status, string code)
    {
        await using var host = await HostAsync(); var p = await host.CreateAsync();
        using var response = await host.SendAsync(HttpMethod.Post, PathFor(p.Id), Json(body), p.ETag, "invalid"); await Problem(response, (HttpStatusCode)status, code);
        Assert.Null(await host.Services.GetRequiredService<IStudioVoxCpmTrainingService>().GetAsync(p.Id, ApiTestHost.Token));
    }
    [Fact]
    public async Task Headers_body_bound_origin_and_CSRF_are_enforced()
    {
        await using var host = await HostAsync(); var p = await host.CreateAsync(); string path = PathFor(p.Id);
        using var noRev = await host.SendAsync(HttpMethod.Post, path, StartBody(new('a', 32)), key: "no-rev"); await Problem(noRev, (HttpStatusCode)428, "precondition.required");
        using var noKey = await host.SendAsync(HttpMethod.Post, path, StartBody(new('a', 32)), p.ETag); await Problem(noKey, HttpStatusCode.BadRequest, "voxcpm_training.input.invalid");
        using var large = await host.SendAsync(HttpMethod.Post, path, Json(new(' ', 16385)), p.ETag, "large"); await Problem(large, HttpStatusCode.RequestEntityTooLarge, "request.too_large");
        using var csrf = host.Request(HttpMethod.Post, path, StartBody(new('a', 32)), p.ETag, "csrf"); csrf.Headers.Remove("X-CSRF-TOKEN");
        using var denied = await host.Client.SendAsync(csrf, ApiTestHost.Token); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using var foreign = host.Request(HttpMethod.Post, path, StartBody(new('a', 32)), p.ETag, "foreign"); foreign.Headers.Remove("Origin"); foreign.Headers.Add("Origin", "http://foreign.invalid");
        using var origin = await host.Client.SendAsync(foreign, ApiTestHost.Token); Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
    }
    [Fact]
    public async Task Expired_or_invalid_key_and_control_browser_fields_never_mutate_training()
    {
        await using var host = await HostAsync(); var p = await ApprovedAsync(host); string path = PathFor(p.Id);
        string coldRoot = Path.Combine(host.Root, "projects", p.Id, "voxcpm-training", "expired-keys"); Directory.CreateDirectory(coldRoot);
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("expired")));
        File.WriteAllBytes(Path.Combine(coldRoot, hash + ".expired"), [1]);
        using var expired = await host.SendAsync(HttpMethod.Post, path, StartBody(p.VersionId), p.ETag, "expired"); await Problem(expired, HttpStatusCode.Conflict, "voxcpm_training.idempotency.expired");
        using var invalid = await host.SendAsync(HttpMethod.Post, path, StartBody(p.VersionId), p.ETag, new('x', 257)); await Problem(invalid, HttpStatusCode.BadRequest, "voxcpm_training.input.invalid");
        using var started = await host.SendAsync(HttpMethod.Post, path, StartBody(p.VersionId), p.ETag, "start"); var s = await ApiTestHost.JsonAsync(started);
        using var extra = await host.SendAsync(HttpMethod.Post, path + "/" + s.GetProperty("id").GetString() + "/stop", Json("{\"checkpointPath\":\"C:/private\"}"), started.Headers.ETag!.Tag, "extra");
        await Problem(extra, HttpStatusCode.BadRequest, "request.invalid");
        using var get = await host.SendAsync(HttpMethod.Get, path); Assert.Equal(started.Headers.ETag!.Tag, get.Headers.ETag!.Tag); Assert.Equal("queued", (await ApiTestHost.JsonAsync(get)).GetProperty("state").GetString());
    }
    [Theory]
    [InlineData("voxcpm_training.missing", 404, "voxcpm_training.missing")]
    [InlineData("voxcpm_training.runtime.changed", 503, "voxcpm_training.runtime.changed")]
    [InlineData("voxcpm_training.confirmation.required", 403, "voxcpm_training.confirmation.required")]
    [InlineData("voxcpm_training.input.invalid", 400, "voxcpm_training.input.invalid")]
    [InlineData("voxcpm_training.idempotency.expired", 409, "voxcpm_training.idempotency.expired")]
    [InlineData("voxcpm_training.checkpoint.invalid", 409, "voxcpm_training.checkpoint.invalid")]
    [InlineData("private C:/secret.wav", 409, "storage.unavailable")]
    public async Task Safe_error_mapping_never_leaks_private_diagnostics(string error, int status, string code)
    {
        await using var host = await HostAsync(new Runtime { ErrorCode = error }); using var response = await host.SendAsync(HttpMethod.Get, "/api/voxcpm-training/readiness");
        var problem = await Problem(response, (HttpStatusCode)status, code); Assert.DoesNotContain(host.Root, problem.ToString()); Assert.DoesNotContain("secret.wav", problem.ToString());
    }
}

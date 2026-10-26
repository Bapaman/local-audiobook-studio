using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.TtsCorpus;
using Microsoft.Extensions.DependencyInjection;
using static LocalAudiobookStudio.Web.Tests.TtsCorpusApiFixture;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class TtsCorpusEndpointTests
{
    [Theory]
    [InlineData("tts_corpus.missing", 404, "tts_corpus.missing")]
    [InlineData("tts_corpus.source.missing", 404, "tts_corpus.source.missing")]
    [InlineData("tts_corpus.runtime.changed", 503, "tts_corpus.runtime.changed")]
    [InlineData("tts_corpus.confirmation.required", 403, "tts_corpus.confirmation.required")]
    [InlineData("tts_corpus.text.invalid", 400, "tts_corpus.text.invalid")]
    [InlineData("tts_corpus.source.changed", 409, "tts_corpus.source.changed")]
    [InlineData("tts_corpus.payload.invalid", 409, "tts_corpus.payload.invalid")]
    [InlineData("tts_corpus.idempotency.expired", 409, "tts_corpus.idempotency.expired")]
    [InlineData("tts_corpus.state.invalid", 409, "tts_corpus.state.invalid")]
    [InlineData("tts_corpus.busy", 409, "storage.busy")]
    [InlineData("private transcript C:\\secret", 409, "storage.unavailable")]
    public async Task Service_errors_have_deliberate_safe_public_mappings(string error, int status, string code)
    {
        await using var host = await HostAsync(new Runtime { ErrorCode = error });
        using var response = await host.SendAsync(HttpMethod.Get, "/api/tts-corpus/readiness");
        var problem = await Problem(response, (HttpStatusCode)status, code); Assert.DoesNotContain(host.Root, problem.ToString());
    }

    [Fact]
    public async Task Host_recovery_interrupts_durable_queue_without_automatic_resume()
    {
        await using var original = await HostAsync(); var p = await ProjectAsync(original);
        var queued = await original.Services.GetRequiredService<IStudioTtsCorpusService>().StartAsync(p.Id, long.Parse(p.ETag.Trim('"')), "start", true, ApiTestHost.Token);
        await original.StopAsync(ApiTestHost.Token);
        await using var reopened = await HostAsync(hosted: true, root: original.Root);
        using var response = await reopened.SendAsync(HttpMethod.Get, PathFor(p.Id)); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await ApiTestHost.JsonAsync(response); Assert.Equal("interrupted", snapshot.GetProperty("state").GetString());
        Assert.Equal(queued.Id, snapshot.GetProperty("id").GetString()); Assert.True(snapshot.GetProperty("canResume").GetBoolean());
    }

    [Fact]
    public async Task Accepted_HTTP_start_survives_request_disposal_and_runs_in_host()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Executor { Before = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(ApiTestHost.Token); } };
        await using var host = await HostAsync(executor: executor, hosted: true); var p = await ProjectAsync(host);
        using (var response = await host.SendAsync(HttpMethod.Post, PathFor(p.Id), Json("{\"experimentalConfirmed\":true}"), p.ETag, "start"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ApiTestHost.Token); }
        finally { release.TrySetResult(); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ApiTestHost.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var service = host.Services.GetRequiredService<IStudioTtsCorpusService>();
        while ((await service.GetAsync(p.Id, deadline.Token))!.State != "completed") await Task.Delay(25, deadline.Token);
    }

    [Fact]
    public async Task Unconfigured_readiness_is_safe_and_empty_snapshot_is_nullable()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var ready = await host.SendAsync(HttpMethod.Get, "/api/tts-corpus/readiness");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode); var value = await ApiTestHost.JsonAsync(ready);
        Assert.False(value.GetProperty("ready").GetBoolean()); Assert.Equal(2, value.GetProperty("cpuThreads").GetInt32());
        Assert.Equal("tts_corpus.runtime.not_ready", value.GetProperty("diagnosticCode").GetString());
        Assert.DoesNotContain(host.Root, value.ToString());
        var p = await host.CreateAsync(); using var empty = await host.SendAsync(HttpMethod.Get, PathFor(p.Id));
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode); Assert.Null(empty.Headers.ETag);
        Assert.Equal(JsonValueKind.Null, (await ApiTestHost.JsonAsync(empty)).ValueKind);
        using var start = await host.SendAsync(HttpMethod.Post, PathFor(p.Id), JsonContent.Create(new { experimentalConfirmed = true }), p.ETag, "start");
        await Problem(start, HttpStatusCode.ServiceUnavailable, "tts_corpus.runtime.not_ready");
        using var unauthenticated = await host.Client.GetAsync("/api/tts-corpus/readiness", ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Theory]
    [InlineData("{}", 400, "request.invalid")]
    [InlineData("{\"experimentalConfirmed\":false}", 403, "tts_corpus.confirmation.required")]
    [InlineData("{\"experimentalConfirmed\":true,\"sourcePath\":\"private\"}", 400, "request.invalid")]
    public async Task Start_requires_explicit_confirmation_and_rejects_browser_paths(string body, int status, string code)
    {
        await using var host = await HostAsync(); var p = await ProjectAsync(host);
        using var result = await host.SendAsync(HttpMethod.Post, PathFor(p.Id), Json(body), p.ETag, "start");
        await Problem(result, (HttpStatusCode)status, code);
        Assert.Null(await host.Services.GetRequiredService<IStudioTtsCorpusService>().GetAsync(p.Id, ApiTestHost.Token));
    }

    [Fact]
    public async Task Commands_require_headers_bounded_JSON_and_same_origin_CSRF()
    {
        await using var host = await HostAsync(); var p = await ProjectAsync(host); string path = PathFor(p.Id);
        using var noRevision = await host.SendAsync(HttpMethod.Post, path, Json("{\"experimentalConfirmed\":true}"), key: "start");
        await Problem(noRevision, (HttpStatusCode)428, "precondition.required");
        using var noKey = await host.SendAsync(HttpMethod.Post, path, Json("{\"experimentalConfirmed\":true}"), p.ETag);
        await Problem(noKey, HttpStatusCode.BadRequest, "tts_corpus.input.invalid");
        using var large = await host.SendAsync(HttpMethod.Post, path, Json(new string(' ', 16385)), p.ETag, "large");
        await Problem(large, HttpStatusCode.RequestEntityTooLarge, "request.too_large");
        using var csrf = host.Request(HttpMethod.Post, path, Json("{\"experimentalConfirmed\":true}"), p.ETag, "csrf");
        csrf.Headers.Remove("X-CSRF-TOKEN"); using var rejected = await host.Client.SendAsync(csrf, ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var foreign = host.Request(HttpMethod.Post, path, Json("{\"experimentalConfirmed\":true}"), p.ETag, "origin");
        foreign.Headers.Remove("Origin"); foreign.Headers.Add("Origin", "http://foreign.invalid");
        using var denied = await host.Client.SendAsync(foreign, ApiTestHost.Token); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Start_stop_resume_replay_before_CAS_and_conflicts_include_current_snapshot()
    {
        await using var host = await HostAsync(); var p = await ProjectAsync(host); string path = PathFor(p.Id);
        using var queued = await host.SendAsync(HttpMethod.Post, path, Json("{\"experimentalConfirmed\":true}"), p.ETag, "start");
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode); var q = await ApiTestHost.JsonAsync(queued); string id = q.GetProperty("id").GetString()!;
        Assert.Equal("queued", q.GetProperty("state").GetString()); Assert.NotNull(q.GetProperty("executionId").GetString());
        Assert.DoesNotContain(host.Root, q.ToString()); Assert.False(q.TryGetProperty("outputRoot", out _));
        using var replay = await host.SendAsync(HttpMethod.Post, path, Json("{\"experimentalConfirmed\":true}"), "\"0\"", "start");
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode); Assert.Equal(q.ToString(), (await ApiTestHost.JsonAsync(replay)).ToString());
        using var reused = await host.SendAsync(HttpMethod.Post, path, Json("{\"experimentalConfirmed\":false}"), p.ETag, "start");
        await Problem(reused, HttpStatusCode.Conflict, "tts_corpus.idempotency.conflict");
        using var activeApproval = await host.SendAsync(HttpMethod.Post, path + "/" + id + "/approve", Json("{\"transcriptAndBoundariesConfirmed\":true}"), queued.Headers.ETag!.Tag, "active");
        await Problem(activeApproval, HttpStatusCode.Conflict, "tts_corpus.active");
        using var stopped = await host.SendAsync(HttpMethod.Post, path + "/" + id + "/stop", revision: queued.Headers.ETag!.Tag, key: "stop");
        Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode); Assert.Equal("paused", (await ApiTestHost.JsonAsync(stopped)).GetProperty("state").GetString());
        using var stopReplay = await host.SendAsync(HttpMethod.Post, path + "/" + id + "/stop", revision: "\"0\"", key: "stop");
        Assert.Equal(HttpStatusCode.Accepted, stopReplay.StatusCode); Assert.Equal(stopped.Headers.ETag!.Tag, stopReplay.Headers.ETag!.Tag);
        using var stale = await host.SendAsync(HttpMethod.Post, path + "/" + id + "/resume", revision: "\"0\"", key: "stale");
        var problem = await Problem(stale, HttpStatusCode.PreconditionFailed, "tts_corpus.conflict");
        Assert.Equal("tts_corpus", problem.GetProperty("conflictType").GetString()); Assert.Equal("paused", problem.GetProperty("currentSnapshot").GetProperty("state").GetString());
        Assert.Equal(stopped.Headers.ETag!.Tag, stale.Headers.ETag!.Tag);
        using var resumed = await host.SendAsync(HttpMethod.Post, path + "/" + id + "/resume", revision: stopped.Headers.ETag!.Tag, key: "resume");
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode); Assert.NotEqual(q.GetProperty("executionId").GetString(), (await ApiTestHost.JsonAsync(resumed)).GetProperty("executionId").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":\"Точный черновик.\",\"excluded\":false}")]
    [InlineData("{\"text\":\"Точный черновик.\",\"reviewed\":true}")]
    [InlineData("{\"excluded\":false,\"reviewed\":true}")]
    [InlineData("{\"text\":\"Точный черновик.\",\"excluded\":false,\"reviewed\":true,\"audioPath\":\"private\"}")]
    public async Task Clip_edit_requires_each_field_and_rejects_paths(string body)
    {
        await using var host = await HostAsync(); var p = await ProjectAsync(host); var draft = await DraftAsync(host, p.Id, p.ETag);
        using var response = await host.SendAsync(HttpMethod.Put, PathFor(p.Id, draft.Id) + "/clips/" + draft.Clips[0].Id, Json(body), ETag(draft), "edit");
        await Problem(response, HttpStatusCode.BadRequest, "request.invalid");
    }

    [Fact]
    public async Task Review_approval_and_later_edit_preserve_immutable_version_and_audio_range()
    {
        var executor = new Executor(); await using var host = await HostAsync(executor: executor); var p = await ProjectAsync(host);
        var draft = await DraftAsync(host, p.Id, p.ETag); string path = PathFor(p.Id, draft.Id), clip = draft.Clips[0].Id;
        using var get = await host.SendAsync(HttpMethod.Get, PathFor(p.Id)); Assert.Equal(ETag(draft), get.Headers.ETag!.Tag);
        var snapshot = await ApiTestHost.JsonAsync(get); Assert.Equal(10, snapshot.GetProperty("retainedDurationSeconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("clips")[0].GetProperty("words")[0].GetProperty("end").ValueKind);
        using var unreviewed = await host.SendAsync(HttpMethod.Post, path + "/approve", Json("{\"transcriptAndBoundariesConfirmed\":true}"), ETag(draft), "unreviewed");
        await Problem(unreviewed, HttpStatusCode.Conflict, "tts_corpus.review.required");
        using var missingConfirmation = await host.SendAsync(HttpMethod.Post, path + "/approve", Json("{}"), ETag(draft), "missing-confirmation");
        await Problem(missingConfirmation, HttpStatusCode.BadRequest, "request.invalid");
        using var reviewed = await host.SendAsync(HttpMethod.Put, path + "/clips/" + clip, JsonContent.Create(new { text = draft.Clips[0].Text, excluded = false, reviewed = true }), ETag(draft), "review");
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        using var approved = await host.SendAsync(HttpMethod.Post, path + "/approve", Json("{\"transcriptAndBoundariesConfirmed\":true}"), reviewed.Headers.ETag!.Tag, "approve");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode); var approvedValue = await ApiTestHost.JsonAsync(approved); var version = approvedValue.GetProperty("versions")[0];
        Assert.Equal(1, version.GetProperty("clipCount").GetInt32()); Assert.Equal(10, version.GetProperty("retainedDurationSeconds").GetDouble());
        using var edited = await host.SendAsync(HttpMethod.Put, path + "/clips/" + clip, JsonContent.Create(new { text = "Исправленный текст.", excluded = true, reviewed = true }), approved.Headers.ETag!.Tag, "edit");
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode); var editedValue = await ApiTestHost.JsonAsync(edited);
        Assert.False(editedValue.GetProperty("clips")[0].GetProperty("reviewed").GetBoolean()); Assert.Equal(0, editedValue.GetProperty("retainedDurationSeconds").GetDouble());
        Assert.Equal(version.ToString(), editedValue.GetProperty("versions")[0].ToString());
        using var replay = await host.SendAsync(HttpMethod.Post, path + "/approve", Json("{\"transcriptAndBoundariesConfirmed\":true}"), "\"0\"", "approve");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(approved.Headers.ETag!.Tag, replay.Headers.ETag!.Tag);
        using var empty = await host.SendAsync(HttpMethod.Post, path + "/approve", Json("{\"transcriptAndBoundariesConfirmed\":true}"), edited.Headers.ETag!.Tag, "empty");
        await Problem(empty, HttpStatusCode.Conflict, "tts_corpus.empty");
        using var request = host.Request(HttpMethod.Get, path + "/clips/" + clip + "/audio"); request.Headers.Range = new(44, 59);
        using var audio = await host.Client.SendAsync(request, ApiTestHost.Token); Assert.Equal(HttpStatusCode.PartialContent, audio.StatusCode);
        Assert.Equal($"bytes 44-59/{executor.AudioBytes!.Length}", audio.Content.Headers.ContentRange!.ToString());
        Assert.Equal(executor.AudioBytes[44..60], await audio.Content.ReadAsByteArrayAsync(ApiTestHost.Token));
        using var wrongClip = await host.SendAsync(HttpMethod.Get, path + "/clips/" + Guid.NewGuid().ToString("N") + "/audio");
        await Problem(wrongClip, HttpStatusCode.NotFound, "tts_corpus.clip.missing");
        byte[] changed = executor.AudioBytes.ToArray(); changed[^1] ^= 1; await File.WriteAllBytesAsync(executor.AudioPath!, changed, ApiTestHost.Token);
        using var modified = await host.SendAsync(HttpMethod.Get, path + "/clips/" + clip + "/audio");
        await Problem(modified, HttpStatusCode.Conflict, "tts_corpus.audio.invalid");
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode); var value = await ApiTestHost.JsonAsync(response);
        Assert.Equal(code, value.GetProperty("code").GetString()); Assert.False(value.TryGetProperty("detail", out _)); return value;
    }
}

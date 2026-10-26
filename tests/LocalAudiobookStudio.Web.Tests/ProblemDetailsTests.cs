using System.Net;
using System.Net.Http.Json;
using System.Text;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class ProblemDetailsTests
{
    [Theory]
    [InlineData("{broken-secret-transcript", "application/json", 400, "request.invalid")]
    [InlineData("{}", "text/plain", 415, "request.media_type")]
    public async Task Framework_parse_errors_are_generic_and_redacted(string body, string media, int status, string code)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var response = await host.SendAsync(HttpMethod.Post, "/api/projects", new StringContent(body, Encoding.UTF8, media), key: "safe-key");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await ApiTestHost.JsonAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.DoesNotContain("secret-transcript", problem.ToString()); Assert.DoesNotContain(host.Root, problem.ToString());
    }

    [Fact]
    public async Task Invalid_media_and_binary_text_return_stable_domain_codes()
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var voice = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart([1, 2, 3]), project.ETag);
        Assert.Equal(HttpStatusCode.BadRequest, voice.StatusCode);
        Assert.StartsWith("wav.", (await ApiTestHost.JsonAsync(voice)).GetProperty("code").GetString());
        using var text = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/text/paste", JsonContent.Create(new { displayName = "Text", text = "secret\u0000transcript" }), project.ETag);
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        Assert.Equal("text.binary", (await ApiTestHost.JsonAsync(text)).GetProperty("code").GetString()); host.AssertNoParts();
    }

    [Fact]
    public async Task Voice_upload_and_text_import_work_while_an_idle_current_session_is_open()
    {
        await using var host = await ApiTestHost.StartAsync();
        var project = await host.CreateAsync();
        // The coordinator and event readers use this same prepared, maintenance-free
        // connection. An idle reader must not make a registered project disappear.
        await using var current = await new ProjectService().OpenCurrentAsync(
            Path.Combine(host.Root, "projects", project.Id), ApiTestHost.Token);
        using var upload = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording",
            ApiTestHost.Multipart(ApiTestHost.Wav()), project.ETag);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var anchor = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/anchor", JsonContent.Create(new
        { startMilliseconds = 0, endMilliseconds = 10000, transcript = "Синтетическая запись.", pairListenedAndConfirmed = true }), upload.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, anchor.StatusCode);
        using var text = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/text/paste",
            JsonContent.Create(new { displayName = "Text", text = "Синтетический пример." }), anchor.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        var snapshot = await ApiTestHost.JsonAsync(text);
        Assert.Equal(4, snapshot.GetProperty("revision").GetInt64());
        Assert.Single(snapshot.GetProperty("segments").EnumerateArray());
        Assert.Equal("Синтетический пример.", (await current.ReadCurrentSnapshotAsync(ApiTestHost.Token)).State.Segments[0].SpokenText);
        host.AssertNoParts();
    }

    [Fact]
    public async Task Editing_a_paused_job_returns_a_path_free_active_job_problem()
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var upload = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart(ApiTestHost.Wav()), project.ETag);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var anchor = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/anchor", JsonContent.Create(new
        { startMilliseconds = 0, endMilliseconds = 10000, transcript = "Синтетическая запись.", pairListenedAndConfirmed = true }), upload.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, anchor.StatusCode);
        using var text = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/text/paste",
            JsonContent.Create(new { displayName = "Text", text = "Синтетический пример." }), anchor.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        string segmentId = (await ApiTestHost.JsonAsync(text)).GetProperty("segments")[0].GetProperty("id").GetString()!;
        string root = Path.Combine(host.Root, "projects", project.Id); JobRecord paused;
        await using (var db = await new ProjectService().OpenCurrentAsync(root, ApiTestHost.Token))
        await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: ApiTestHost.Token))
        {
            var input = new GenerationInput(db.State.Metadata.Id, db.State.Revision, db.State.Voice!,
                new("voxcpm2-local-v1", "q8-f16-cpu", 70, 10, 2, 1, 42, false), db.State.Segments.Select(x => new GenerationItemInput(
                    x.Id, x.Order, x.SpokenText, x.TextHash, x.PreviousSentenceContextHash, x.NextSentenceContextHash, x.BoundaryAfter,
                    x.Versions with { Worker = new string('1', 40), Model = new string('2', 40) })).ToArray());
            var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "synthetic-enqueue", JobKind.Generation, input, ApiTestHost.Token);
            paused = await db.Jobs.RequestStateAsync(job.Id, job.Revision, "pause", JobControlCommand.Pause, ApiTestHost.Token);
        }
        using var response = await host.SendAsync(HttpMethod.Patch, $"/api/projects/{project.Id}/segments/{segmentId}",
            JsonContent.Create(new { spokenText = "PRIVATE_SYNTHETIC_REPLACEMENT" }), text.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await ApiTestHost.JsonAsync(response);
        Assert.Equal("job.active", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("detail", out _)); Assert.False(problem.TryGetProperty("currentSnapshot", out _));
        Assert.DoesNotContain(host.Root, problem.ToString()); Assert.DoesNotContain("PRIVATE_SYNTHETIC", problem.ToString());
        await using var after = await new ProjectService().OpenCurrentAsync(root, ApiTestHost.Token);
        Assert.Equal(paused.Revision, (await after.Jobs.GetAsync(paused.Id, ApiTestHost.Token)).Revision);
        Assert.Equal(JobState.Paused, (await after.Jobs.GetAsync(paused.Id, ApiTestHost.Token)).State);
        Assert.Equal("Синтетический пример.", after.State.Segments[0].SpokenText);
        host.AssertNoParts();
    }
}

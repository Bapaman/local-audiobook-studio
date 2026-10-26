using System.Net;
using System.Net.Http.Json;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class ProjectApiTests
{
    [Fact]
    public async Task Legacy_core_content_is_reported_as_accepted_without_inventing_modern_metadata()
    {
        await using var host = await ApiTestHost.StartAsync();
        string id = Guid.NewGuid().ToString("N");
        await using (var project = await new LocalAudiobookStudio.Core.Projects.ProjectService().CreateAsync(Path.Combine(host.Root, "projects", id), "Legacy", id, ApiTestHost.Token))
        {
            var state = project.State; var now = DateTimeOffset.UtcNow;
            var chapter = new LocalAudiobookStudio.Core.Projects.ChapterRecord("chapter", 0, "Глава", "Старый исходник.", "Старый исходник.", "Старый исходник.", now);
            var segment = new LocalAudiobookStudio.Core.Projects.SegmentRecord("segment", "chapter", 0, "Старый исходник.", "Старый исходник.", "Старая правка.", "hash", "fingerprint", null, null, 1, state.Metadata.Versions, state.Metadata.Parameters, "pending", false, now, now, null);
            var voice = new LocalAudiobookStudio.Core.Projects.VoiceSnapshot("voice", "voice/legacy.wav", "Старый голос", "hash", "hash", "hash", true, now);
            await project.SaveAsync(state with { Voice = voice, Chapters = [chapter], Segments = [segment] }, ApiTestHost.Token);
        }
        using var response = await host.SendAsync(HttpMethod.Get, $"/api/projects/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await ApiTestHost.JsonAsync(response);
        Assert.True(snapshot.GetProperty("hasAcceptedText").GetBoolean());
        Assert.True(snapshot.GetProperty("hasAcceptedVoice").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, snapshot.GetProperty("textDocument").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, snapshot.GetProperty("voiceAnchor").ValueKind);
        Assert.Equal("Старая правка.", snapshot.GetProperty("segments")[0].GetProperty("spokenText").GetString());
        using var edit = await host.SendAsync(HttpMethod.Patch, $"/api/projects/{id}/segments/segment", JsonContent.Create(new { spokenText = "Изменение" }), response.Headers.ETag!.Tag);
        Assert.Equal("text.reimport_required", (await ApiTestHost.JsonAsync(edit)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Creation_replays_once_and_changed_payload_conflicts()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var first = await host.SendAsync(HttpMethod.Post, "/api/projects", JsonContent.Create(new { displayName = "Book" }), key: "same-key");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        string id = (await ApiTestHost.JsonAsync(first)).GetProperty("id").GetString()!;
        Assert.Equal("/api/projects/" + id, first.Headers.Location!.OriginalString);
        Assert.Equal("\"1\"", first.Headers.ETag!.Tag);
        using var replay = await host.SendAsync(HttpMethod.Post, "/api/projects", JsonContent.Create(new { displayName = "Book" }), key: "same-key");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(id, (await ApiTestHost.JsonAsync(replay)).GetProperty("id").GetString());
        using var changed = await host.SendAsync(HttpMethod.Post, "/api/projects", JsonContent.Create(new { displayName = "Changed" }), key: "same-key");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var list = await host.SendAsync(HttpMethod.Get, "/api/projects");
        Assert.Single((await ApiTestHost.JsonAsync(list)).EnumerateArray());
        using var opened = await host.SendAsync(HttpMethod.Post, "/api/projects/" + id + "/open", revision: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal("\"1\"", opened.Headers.ETag!.Tag);
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("0", 400)]
    [InlineData("W/\"0\"", 400)]
    [InlineData("*", 400)]
    [InlineData("\"0\",\"1\"", 400)]
    [InlineData("\"-1\"", 400)]
    [InlineData("\"9223372036854775808\"", 400)]
    public async Task Mutation_requires_one_strong_decimal_revision(string? revision, int status)
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var response = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/text/paste", JsonContent.Create(new { displayName = "Text", text = "Привет." }), revision);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task Text_edit_and_stale_precondition_return_authoritative_snapshot_and_etag()
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var paste = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/text/paste", JsonContent.Create(new { displayName = "Text", text = "Привет, мир." }), project.ETag);
        Assert.Equal(HttpStatusCode.OK, paste.StatusCode);
        Assert.Equal("\"2\"", paste.Headers.ETag!.Tag);
        string segment = (await ApiTestHost.JsonAsync(paste)).GetProperty("segments")[0].GetProperty("id").GetString()!;
        using var edit = await host.SendAsync(HttpMethod.Patch, $"/api/projects/{project.Id}/segments/{segment}", JsonContent.Create(new { spokenText = "Здравствуй, мир." }), "\"2\"");
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        using var stale = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/open", revision: "\"0\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var snapshot = (await ApiTestHost.JsonAsync(stale)).GetProperty("currentSnapshot");
        Assert.Equal(3, snapshot.GetProperty("revision").GetInt64());
        Assert.Equal("\"3\"", stale.Headers.ETag!.Tag);
        Assert.Equal("Здравствуй, мир.", snapshot.GetProperty("segments")[0].GetProperty("spokenText").GetString());
        Assert.DoesNotContain(host.Root, snapshot.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exit_acknowledgment_completes_before_shutdown_request()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var response = await host.SendAsync(HttpMethod.Post, "/api/app/exit");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await response.Content.ReadAsByteArrayAsync(ApiTestHost.Token);
        await host.Exit.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5), ApiTestHost.Token);
    }

    [Theory]
    [InlineData("session", 401)]
    [InlineData("origin", 403)]
    [InlineData("csrf", 400)]
    public async Task Exit_requires_session_origin_and_antiforgery_before_shutdown(string invalid, int status)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var request = host.Request(HttpMethod.Post, "/api/app/exit");
        request.Headers.Remove(invalid switch { "session" => "Cookie", "origin" => "Origin", _ => "X-CSRF-TOKEN" });
        using var response = await host.Client.SendAsync(request, ApiTestHost.Token);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.False(host.Exit.Requested.Task.IsCompleted);
    }

    [Fact]
    public async Task Duplicate_if_match_is_rejected_without_mutation()
    {
        await using var host = await ApiTestHost.StartAsync(); var project = await host.CreateAsync();
        using var request = host.Request(HttpMethod.Post, $"/api/projects/{project.Id}/text/paste", JsonContent.Create(new { displayName = "Text", text = "Привет." }));
        request.Headers.TryAddWithoutValidation("If-Match", ["\"1\"", "\"1\""]);
        using var response = await host.Client.SendAsync(request, ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var get = await host.SendAsync(HttpMethod.Get, $"/api/projects/{project.Id}");
        Assert.Equal(1, (await ApiTestHost.JsonAsync(get)).GetProperty("revision").GetInt64());
    }
}

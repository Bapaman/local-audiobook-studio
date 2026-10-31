using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class TextAppendEndpointTests
{
    [Fact]
    public async Task Authenticated_paste_and_file_append_replay_stale_revision_without_replacing_first_text()
    {
        await using var host=await ApiTestHost.StartAsync();var project=await host.CreateAsync();
        using var first=await host.SendAsync(HttpMethod.Post,$"/api/projects/{project.Id}/text/paste",JsonContent.Create(new{displayName="Первый",text="Первая книга."}),project.ETag);
        Assert.Equal(HttpStatusCode.OK,first.StatusCode);var before=await ApiTestHost.JsonAsync(first);Assert.False(before.TryGetProperty("textDocuments",out _));
        string id=Guid.NewGuid().ToString("N");var body=new{displayName="Второй",text="Вторая книга.",textId=id};string path=$"/api/projects/{project.Id}/text/append/paste";
        using var appended=await host.SendAsync(HttpMethod.Post,path,JsonContent.Create(body),first.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK,appended.StatusCode);var saved=await ApiTestHost.JsonAsync(appended);
        Assert.Equal(2,saved.GetProperty("textDocuments").GetArrayLength());Assert.Equal("Первый",saved.GetProperty("textDocument").GetProperty("displayName").GetString());
        Assert.Equal(id,saved.GetProperty("textDocuments")[1].GetProperty("id").GetString());
        using var replay=await host.SendAsync(HttpMethod.Post,path,JsonContent.Create(body),first.Headers.ETag.Tag);
        Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(appended.Headers.ETag!.Tag,replay.Headers.ETag!.Tag);
        using var changed=await host.SendAsync(HttpMethod.Post,path,JsonContent.Create(new{body.displayName,text="Изменено.",textId=id}),first.Headers.ETag.Tag);
        Assert.Equal(HttpStatusCode.Conflict,changed.StatusCode);Assert.Equal("text.idempotency.conflict",(await ApiTestHost.JsonAsync(changed)).GetProperty("code").GetString());
        string fileId=Guid.NewGuid().ToString("N"),metadata=System.Text.Json.JsonSerializer.Serialize(new{displayName="Третий.txt",textId=fileId});
        using var file=await host.SendAsync(HttpMethod.Post,$"/api/projects/{project.Id}/text/append/file",ApiTestHost.Multipart(Encoding.UTF8.GetBytes("Третья книга."),metadata),appended.Headers.ETag.Tag);
        Assert.Equal(HttpStatusCode.OK,file.StatusCode);Assert.Equal(3,(await ApiTestHost.JsonAsync(file)).GetProperty("textDocuments").GetArrayLength());
        using var fileReplay=await host.SendAsync(HttpMethod.Post,$"/api/projects/{project.Id}/text/append/file",ApiTestHost.Multipart(Encoding.UTF8.GetBytes("Третья книга."),metadata),appended.Headers.ETag.Tag);
        Assert.Equal(HttpStatusCode.OK,fileReplay.StatusCode);Assert.Equal(file.Headers.ETag!.Tag,fileReplay.Headers.ETag!.Tag);host.AssertNoParts();
    }
    [Fact]
    public async Task Append_retains_session_csrf_revision_and_id_validation()
    {
        await using var host=await ApiTestHost.StartAsync();var project=await host.CreateAsync();string path=$"/api/projects/{project.Id}/text/append/paste";
        var body=new{displayName="New",text="Текст.",textId=Guid.NewGuid().ToString("N")};
        using var unauthenticated=await host.Client.PostAsJsonAsync(path,body,ApiTestHost.Token);Assert.Equal(HttpStatusCode.Unauthorized,unauthenticated.StatusCode);
        using var request=host.Request(HttpMethod.Post,path,JsonContent.Create(body),project.ETag);request.Headers.Remove("X-CSRF-TOKEN");
        using var noCsrf=await host.Client.SendAsync(request,ApiTestHost.Token);Assert.Equal(HttpStatusCode.BadRequest,noCsrf.StatusCode);
        using var noRevision=await host.SendAsync(HttpMethod.Post,path,JsonContent.Create(body));Assert.Equal((HttpStatusCode)428,noRevision.StatusCode);
        using var invalid=await host.SendAsync(HttpMethod.Post,path,JsonContent.Create(new{body.displayName,body.text,textId="../bad"}),project.ETag);
        Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);Assert.Equal("text.id.invalid",(await ApiTestHost.JsonAsync(invalid)).GetProperty("code").GetString());
    }
}

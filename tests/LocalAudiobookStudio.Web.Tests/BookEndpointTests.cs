using System.Buffers.Binary;
using System.Net;
using LocalAudiobookStudio.Application.Artifacts;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class BookEndpointTests
{
    [Fact]
    public async Task Authenticated_download_is_one_exact_wav_and_not_a_path_or_unaccepted_output()
    {
        await using var host = await ApiTestHost.StartAsync(configureServices: services =>
        {
            services.AddSingleton<IStudioProjectService, Projects>(); services.AddSingleton<IProjectArtifactReader, Reader>();
            services.RemoveAll<IHostedService>();
        });
        using var response = await host.SendAsync(HttpMethod.Get,"/api/projects/book/book.wav?revision=1");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode); Assert.Equal("audio/wav",response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment",response.Content.Headers.ContentDisposition!.DispositionType);
        byte[] bytes=await response.Content.ReadAsByteArrayAsync(ApiTestHost.Token);
        Assert.Equal(54,bytes.Length); Assert.Equal(54,response.Content.Headers.ContentLength);
        Assert.Equal(10u,BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        using var missingAuth=await host.Client.GetAsync("/api/projects/book/book.wav?revision=1",ApiTestHost.Token);
        Assert.Equal(HttpStatusCode.Unauthorized,missingAuth.StatusCode);
        using var stale=await host.SendAsync(HttpMethod.Get,"/api/projects/book/book.wav?revision=2");
        Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
    }
    [Fact]
    public async Task Text_query_selects_only_one_source_and_invalid_scope_never_returns_combined_book()
    {
        await using var host=await ApiTestHost.StartAsync(configureServices:services=>{services.AddSingleton<IStudioProjectService,Projects>();services.AddSingleton<IProjectArtifactReader,Reader>();services.RemoveAll<IHostedService>();});
        using var response=await host.SendAsync(HttpMethod.Get,"/api/projects/book/book.wav?revision=1&textDocumentId=11111111111111111111111111111111");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal(48,(await response.Content.ReadAsByteArrayAsync(ApiTestHost.Token)).Length);
        using var missing=await host.SendAsync(HttpMethod.Get,"/api/projects/book/book.wav?revision=1&textDocumentId=33333333333333333333333333333333");
        Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);
    }
    private sealed class Projects : IStudioProjectService
    {
        public Task<ProjectSnapshotDto> GetAsync(string id,CancellationToken token=default) => Task.FromResult(new ProjectSnapshotDto(id,"Synthetic",1,null,null,null,
            [new("first",0,"one","one","one","hash","ready","a"),new("second",1,"two","two","two","hash","ready","b")],
            TextDocuments:[new("11111111111111111111111111111111",0,new("Pasted","One","utf-8","hash",3,"one","one"),["first"]),
                new("22222222222222222222222222222222",1,new("Pasted","Two","utf-8","hash",3,"two","two"),["second"])]));
        public Task<ProjectSnapshotDto> OpenAsync(string id,CancellationToken token=default) => GetAsync(id,token);
        public Task<IReadOnlyList<ProjectListItem>> ListAsync(CancellationToken token=default) => Task.FromResult<IReadOnlyList<ProjectListItem>>([]);
        public Task<CreateProjectResult> CreateAsync(CreateProjectCommand command,string key,CancellationToken token=default) => throw new NotSupportedException();
    }
    private sealed class Reader : IProjectArtifactReader
    {
        public Task<ArtifactReadHandle> OpenAsync(string projectId,string artifactId,CancellationToken token=default)
        {
            byte[] wav=ApiTestHost.Wav(artifactId=="a"?2:3);
            return Task.FromResult(new ArtifactReadHandle(new MemoryStream(wav),wav.Length,"audio/wav","audio.wav"));
        }
    }
}

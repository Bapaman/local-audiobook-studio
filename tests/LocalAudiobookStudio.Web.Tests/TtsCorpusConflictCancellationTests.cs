using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Web.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using static LocalAudiobookStudio.Web.Tests.TtsCorpusApiFixture;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class TtsCorpusConflictCancellationTests
{
    [Fact]
    public async Task Aborted_conflict_snapshot_fetch_aborts_instead_of_fabricating_missing_corpus()
    {
        await using var host = await HostAsync(); var p = await ProjectAsync(host); var draft = await DraftAsync(host, p.Id, p.ETag);
        using var recordLock = File.Open(Path.Combine(host.Root, "projects", p.Id, "tts-corpus", "record.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ApiTestHost.Token); cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        var context = new DefaultHttpContext { RequestServices = host.Services };
        var lifetime = new Lifetime { RequestAborted = cancellation.Token }; context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        using var output = new MemoryStream(); context.Response.Body = output;
        context.Request.RouteValues["id"] = p.Id; context.Request.RouteValues["corpusId"] = draft.Id;
        await new ProblemDetailsMapper(_ => throw new StudioException("tts_corpus.conflict")).InvokeAsync(context);
        Assert.True(lifetime.Aborted); Assert.Equal(0, output.Length); Assert.False(context.Response.Headers.ContainsKey("ETag"));
    }
    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborted = true;
    }
}

using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Web.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using static LocalAudiobookStudio.Web.Tests.VoxCpmTrainingApiFixture;

namespace LocalAudiobookStudio.Web.Tests;
public sealed class VoxCpmTrainingConflictCancellationTests
{
    [Fact]
    public async Task Aborted_conflict_fetch_aborts_without_fabricated_missing_training()
    {
        await using var host = await HostAsync(); var p = await ApprovedAsync(host);
        using var started = await host.SendAsync(HttpMethod.Post, PathFor(p.Id), StartBody(p.VersionId), p.ETag, "start"); var s = await ApiTestHost.JsonAsync(started);
        using var held = File.Open(Path.Combine(host.Root, "projects", p.Id, "voxcpm-training", "record.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ApiTestHost.Token); cancellation.CancelAfter(100);
        var context = new DefaultHttpContext { RequestServices = host.Services }; var lifetime = new Lifetime { RequestAborted = cancellation.Token };
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime); using var output = new MemoryStream(); context.Response.Body = output;
        context.Request.RouteValues["id"] = p.Id; context.Request.RouteValues["trainingId"] = s.GetProperty("id").GetString();
        await new ProblemDetailsMapper(_ => throw new StudioException("voxcpm_training.conflict")).InvokeAsync(context);
        Assert.True(lifetime.Aborted); Assert.Equal(0, output.Length); Assert.False(context.Response.Headers.ContainsKey("ETag"));
    }
    private sealed class Lifetime : IHttpRequestLifetimeFeature
    { public bool Aborted; public CancellationToken RequestAborted { get; set; } public void Abort() => Aborted = true; }
}

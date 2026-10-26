using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using static LocalAudiobookStudio.Web.Tests.TtsCorpusApiFixture;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class TtsCorpusAudioResponseTests
{
    // A wrapper that disposes before executing Results.File, or drops ancestry pins,
    // must fail while the response is still copying its accepted owned WAV.
    [Fact]
    public async Task Slow_audio_response_retains_actual_file_and_parent_until_abort_without_holding_project_lease()
    {
        var executor = new Executor(); await using var host = await HostAsync(executor: executor);
        var p = await ProjectAsync(host); var draft = await DraftAsync(host, p.Id, p.ETag);
        var endpoint = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/projects/{id}/tts-corpus/{corpusId}/clips/{clipId}/audio");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ApiTestHost.Token);
        await using var sink = new BlockedResponse();
        var context = new DefaultHttpContext { RequestServices = host.Services, RequestAborted = cancellation.Token };
        context.Request.Method = "GET"; context.Request.Path = PathFor(p.Id, draft.Id) + "/clips/" + draft.Clips[0].Id + "/audio";
        context.Request.RouteValues["id"] = p.Id; context.Request.RouteValues["corpusId"] = draft.Id; context.Request.RouteValues["clipId"] = draft.Clips[0].Id;
        context.Response.Body = sink; context.SetEndpoint(endpoint);
        Task copying = endpoint.RequestDelegate!(context);
        try
        {
            await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), ApiTestHost.Token);
            using (File.Open(Path.Combine(host.Root, "projects", p.Id, "artifact-ownership.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Assert.Throws<IOException>(() => { using var writer = File.Open(executor.AudioPath!, FileMode.Open, FileAccess.Write, FileShare.Read); });
            string parent = Path.GetDirectoryName(executor.AudioPath!)!;
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + ".moved"));
        }
        finally { cancellation.Cancel(); await copying; }
        using (File.Open(executor.AudioPath!, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
        string releasedParent = Path.GetDirectoryName(executor.AudioPath!)!;
        Directory.Move(releasedParent, releasedParent + ".moved"); Directory.Move(releasedParent + ".moved", releasedParent);
    }

    private sealed class BlockedResponse : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

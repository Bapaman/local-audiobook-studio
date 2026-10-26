using System.Text.Json.Serialization;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Web.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class TtsCorpusEndpoints
{
    private const long JsonLimit = 16 * 1024;
    public static IEndpointRouteBuilder MapTtsCorpusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tts-corpus/readiness", async (HttpContext context, IStudioTtsCorpusService corpus)
            => Results.Ok(await corpus.GetReadinessAsync(context.RequestAborted)));
        endpoints.MapGet("/api/projects/{id}/tts-corpus", async (HttpContext context, string id, IStudioTtsCorpusService corpus) =>
        {
            var snapshot = await corpus.GetAsync(id, context.RequestAborted);
            if (snapshot != null) ApiRequest.ETag(context, snapshot.Revision);
            return snapshot == null ? (IResult)Results.Text("null", "application/json") : Results.Json(snapshot);
        });
        endpoints.MapPost("/api/projects/{id}/tts-corpus", async (HttpContext context, string id, IStudioTtsCorpusService corpus) =>
        {
            long revision = ApiRequest.Revision(context); string key = Key(context);
            var request = await ApiRequest.JsonAsync<StartRequest>(context, JsonLimit);
            var snapshot = await corpus.StartAsync(id, revision, key, request.ExperimentalConfirmed, context.RequestAborted);
            ApiRequest.ETag(context, snapshot.Revision);
            return Results.Accepted($"/api/projects/{id}/tts-corpus", snapshot);
        });
        Control(endpoints, "stop"); Control(endpoints, "resume");
        endpoints.MapPut("/api/projects/{id}/tts-corpus/{corpusId}/clips/{clipId}",
            async (HttpContext context, string id, string corpusId, string clipId, IStudioTtsCorpusService corpus) =>
            {
                long revision = ApiRequest.Revision(context); string key = Key(context);
                var request = await ApiRequest.JsonAsync<EditRequest>(context, JsonLimit);
                var snapshot = await corpus.UpdateClipAsync(id, corpusId, clipId, revision, key,
                    new(request.Text, request.Excluded, request.Reviewed), context.RequestAborted);
                ApiRequest.ETag(context, snapshot.Revision); return Results.Ok(snapshot);
            });
        endpoints.MapPost("/api/projects/{id}/tts-corpus/{corpusId}/approve",
            async (HttpContext context, string id, string corpusId, IStudioTtsCorpusService corpus) =>
            {
                long revision = ApiRequest.Revision(context); string key = Key(context);
                var request = await ApiRequest.JsonAsync<ApproveRequest>(context, JsonLimit);
                var snapshot = await corpus.ApproveAsync(id, corpusId, revision, key, request.TranscriptAndBoundariesConfirmed, context.RequestAborted);
                ApiRequest.ETag(context, snapshot.Revision); return Results.Ok(snapshot);
            });
        endpoints.MapGet("/api/projects/{id}/tts-corpus/{corpusId}/clips/{clipId}/audio",
            async (HttpContext context, string id, string corpusId, string clipId, IStudioTtsCorpusService corpus)
                => new OwnedAudioResult(await corpus.OpenAudioAsync(id, corpusId, clipId, context.RequestAborted)));
        return endpoints;
    }

    private static void Control(IEndpointRouteBuilder endpoints, string command)
        => endpoints.MapPost("/api/projects/{id}/tts-corpus/{corpusId}/" + command,
            async (HttpContext context, string id, string corpusId, IStudioTtsCorpusService corpus) =>
            {
                long revision = ApiRequest.Revision(context); string key = Key(context);
                ApiRequest.Limit(context, JsonLimit);
                if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                    _ = await ApiRequest.JsonAsync<EmptyRequest>(context, JsonLimit);
                var snapshot = await corpus.ControlAsync(id, corpusId, revision, key, command, context.RequestAborted);
                ApiRequest.ETag(context, snapshot.Revision); return Results.Accepted(value: snapshot);
            });

    private static string Key(HttpContext context)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])) throw new ApiException(400, "tts_corpus.input.invalid");
        return values[0]!;
    }

    // The service transfers the actual open file and ancestry pins to the response.
    // Keep them until Range processing or an aborted copy has finished.
    private sealed class OwnedAudioResult(TtsCorpusAudioHandle handle) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            try { await Results.File(handle.Content, handle.ContentType, enableRangeProcessing: true).ExecuteAsync(context); }
            finally { await handle.DisposeAsync(); }
        }
    }
    private sealed record StartRequest([property: JsonRequired] bool ExperimentalConfirmed);
    private sealed record EditRequest([property: JsonRequired] string Text, [property: JsonRequired] bool Excluded, [property: JsonRequired] bool Reviewed);
    private sealed record ApproveRequest([property: JsonRequired] bool TranscriptAndBoundariesConfirmed);
    private sealed record EmptyRequest;
}

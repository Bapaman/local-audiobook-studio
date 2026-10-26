using System.Text.Json.Serialization;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Web.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class VoxCpmTrainingEndpoints
{
    private const long JsonLimit = 16 * 1024;
    public static IEndpointRouteBuilder MapVoxCpmTrainingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/voxcpm-training/readiness", async (HttpContext context, IStudioVoxCpmTrainingService training)
            => Results.Ok(await training.GetReadinessAsync(context.RequestAborted)));
        endpoints.MapGet("/api/projects/{id}/voxcpm-training", async (HttpContext context, string id, IStudioVoxCpmTrainingService training) =>
        {
            var snapshot = await training.GetAsync(id, context.RequestAborted);
            if (snapshot != null) ApiRequest.ETag(context, snapshot.Revision);
            return snapshot == null ? (IResult)Results.Text("null", "application/json") : Results.Json(snapshot);
        });
        endpoints.MapPost("/api/projects/{id}/voxcpm-training", async (HttpContext context, string id, IStudioVoxCpmTrainingService training) =>
        {
            long revision = ApiRequest.Revision(context); string key = Key(context);
            var request = await ApiRequest.JsonAsync<StartRequest>(context, JsonLimit);
            if (request.CorpusVersionId == null) throw new ApiException(400, "voxcpm_training.input.invalid");
            var snapshot = await training.StartAsync(id, revision, key, request.CorpusVersionId, request.ExperimentalConfirmed, context.RequestAborted);
            ApiRequest.ETag(context, snapshot.Revision); return Results.Accepted($"/api/projects/{id}/voxcpm-training", snapshot);
        });
        Control(endpoints, "stop"); Control(endpoints, "resume"); return endpoints;
    }
    private static void Control(IEndpointRouteBuilder endpoints, string command)
        => endpoints.MapPost("/api/projects/{id}/voxcpm-training/{trainingId}/" + command,
            async (HttpContext context, string id, string trainingId, IStudioVoxCpmTrainingService training) =>
            {
                long revision = ApiRequest.Revision(context); string key = Key(context); ApiRequest.Limit(context, JsonLimit);
                if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                    _ = await ApiRequest.JsonAsync<EmptyRequest>(context, JsonLimit);
                var snapshot = await training.ControlAsync(id, trainingId, revision, key, command, context.RequestAborted);
                ApiRequest.ETag(context, snapshot.Revision); return Results.Accepted(value: snapshot);
            });
    private static string Key(HttpContext context)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])) throw new ApiException(400, "voxcpm_training.input.invalid");
        return values[0]!;
    }
    private sealed record StartRequest([property: JsonRequired] string CorpusVersionId, [property: JsonRequired] bool ExperimentalConfirmed);
    private sealed record EmptyRequest;
}

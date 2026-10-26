using System.Text.Json.Serialization;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Web.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class TrainingEndpoints
{
    private const long JsonLimit = 16 * 1024;
    public static IEndpointRouteBuilder MapTrainingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/training/readiness", async (HttpContext context, IStudioTrainingService training)
            => Results.Ok(await training.GetReadinessAsync(context.RequestAborted)));
        endpoints.MapGet("/api/projects/{id}/training", async (HttpContext context, string id, IStudioTrainingService training) =>
        {
            var current = await training.GetAsync(id, context.RequestAborted);
            if (current != null) ApiRequest.ETag(context, current.Revision);
            return Results.Json(current);
        });
        endpoints.MapPost("/api/projects/{id}/training", async (HttpContext context, string id, IStudioTrainingService training) =>
        {
            long revision = ApiRequest.Revision(context); string key = Key(context);
            var command = await ApiRequest.JsonAsync<StartRequest>(context, JsonLimit);
            var current = await training.StartAsync(id, revision, key, command.ExperimentalConfirmed, context.RequestAborted);
            ApiRequest.ETag(context, current.Revision);
            return Results.Accepted($"/api/projects/{id}/training", current);
        });
        Control(endpoints, "stop"); Control(endpoints, "resume");
        return endpoints;
    }
    private static void Control(IEndpointRouteBuilder endpoints, string command)
        => endpoints.MapPost("/api/projects/{id}/training/{trainingId}/" + command,
            async (HttpContext context, string id, string trainingId, IStudioTrainingService training) =>
            {
                long revision = ApiRequest.Revision(context); string key = Key(context);
                ApiRequest.Limit(context, JsonLimit);
                if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                    _ = await ApiRequest.JsonAsync<EmptyCommand>(context, JsonLimit);
                var current = await training.ControlAsync(id, trainingId, revision, key, command, context.RequestAborted);
                ApiRequest.ETag(context, current.Revision); return Results.Accepted(value: current);
            });
    private static string Key(HttpContext context)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])) throw new ApiException(400, "training.input.invalid");
        return values[0]!;
    }
    private sealed record StartRequest([property: JsonRequired] bool ExperimentalConfirmed);
    private sealed record EmptyCommand;
}

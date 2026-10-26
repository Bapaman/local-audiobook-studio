using System.Text.Json.Serialization;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Web.Errors;
using LocalAudiobookStudio.Web.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class JobEndpoints
{
    private const long JsonLimit = 512 * 1024;
    private const long FramingAllowance = 64 * 1024;

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/runtime/readiness", async (HttpContext context, IRuntimeReadiness runtime) =>
        {
            var value = await runtime.GetAsync(context.RequestAborted);
            bool valid = value.ProfileId is "q8-f16-cpu" or "q8-f16-hybrid-16"
                && (value.Ready ? value.DiagnosticCode is null : value.DiagnosticCode == "worker.runtime.not_ready");
            return Results.Ok(valid ? value : new RuntimeReadiness(false, false, "worker.runtime.not_ready", "q8-f16-cpu"));
        });
        endpoints.MapPost("/api/projects/{id}/jobs", async (HttpContext context, string id, IStudioJobService jobs) =>
        {
            // Syntax only: the durable service must replay a receipt BEFORE current CAS/readiness.
            long revision = ApiRequest.Revision(context);
            string key = Key(context);
            var request = await ApiRequest.JsonAsync<EnqueueRequest>(context, JsonLimit, JobWireJson.RequestOptions, FramingAllowance);
            var job = await jobs.EnqueueAsync(id, revision, key, new(request.Kind, request.SegmentIds, request.SavedModelId, request.ExperimentalConfirmed, request.SavedVoxCpmModelId, request.VoxCpmOptions), context.RequestAborted);
            ApiRequest.ETag(context, job.Revision);
            return Results.Accepted($"/api/projects/{id}/jobs/{job.Id}", job);
        });
        endpoints.MapGet("/api/projects/{id}/jobs", async (HttpContext context, string id, IStudioJobService jobs)
            => Results.Ok(await jobs.ListAsync(id, context.RequestAborted)));
        endpoints.MapGet("/api/projects/{id}/jobs/{jobId}", async (HttpContext context, string id, string jobId, IStudioJobService jobs) =>
        {
            var job = await jobs.GetAsync(id, jobId, context.RequestAborted);
            ApiRequest.ETag(context, job.Revision);
            return Results.Ok(job);
        });
        Control(endpoints, "pause", JobControlCommand.Pause);
        Control(endpoints, "resume", JobControlCommand.Resume);
        Control(endpoints, "cancel", JobControlCommand.Cancel);
        return endpoints;
    }

    private static void Control(IEndpointRouteBuilder endpoints, string name, JobControlCommand command)
        => endpoints.MapPost("/api/projects/{id}/jobs/{jobId}/" + name,
            async (HttpContext context, string id, string jobId, IStudioJobService jobs) =>
            {
                long revision = ApiRequest.Revision(context);
                string key = Key(context);
                ApiRequest.Limit(context, JsonLimit);
                if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                    _ = await ApiRequest.JsonAsync<EmptyCommand>(context, JsonLimit, JobWireJson.RequestOptions, FramingAllowance);
                var job = await jobs.RequestStateAsync(id, jobId, revision, key, command, context.RequestAborted);
                ApiRequest.ETag(context, job.Revision);
                return Results.Accepted(value: job);
            });

    private static string Key(HttpContext context)
    {
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0])) throw new ApiException(400, "job.input.invalid");
        return keys[0]!;
    }
    private sealed record EnqueueRequest([property: JsonRequired] JobKind Kind,
        [property: JsonRequired] IReadOnlyList<string> SegmentIds, string? SavedModelId = null, bool ExperimentalConfirmed = false, string? SavedVoxCpmModelId = null, VoxCpmGenerationOptions? VoxCpmOptions = null);
    private sealed record EmptyCommand;
}

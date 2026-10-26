using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Web.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/projects", async (HttpContext context, IStudioProjectService projects)
            => Results.Ok(await projects.ListAsync(context.RequestAborted)));
        endpoints.MapPost("/api/projects", async (HttpContext context, IStudioProjectService projects) =>
        {
            var keys = context.Request.Headers["Idempotency-Key"];
            if (keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0])) throw new ApiException(400, "project.idempotency.invalid");
            var command = await ApiRequest.JsonAsync<CreateRequest>(context, 4096);
            var result = await projects.CreateAsync(new(command.DisplayName), keys[0]!, context.RequestAborted);
            ApiRequest.ETag(context, result.Snapshot.Revision);
            return result.Created ? Results.Created($"/api/projects/{result.Snapshot.Id}", result.Snapshot) : Results.Ok(result.Snapshot);
        });
        endpoints.MapGet("/api/projects/{id}", async (HttpContext context, string id, IStudioProjectService projects)
            => ApiRequest.Snapshot(context, await projects.GetAsync(id, context.RequestAborted)));
        endpoints.MapPost("/api/projects/{id}/open", async (HttpContext context, string id, StudioProjectService projects)
            => ApiRequest.Snapshot(context, await projects.OpenAsync(id, ApiRequest.Revision(context), context.RequestAborted)));
        return endpoints;
    }

    private sealed record CreateRequest(string DisplayName);
}

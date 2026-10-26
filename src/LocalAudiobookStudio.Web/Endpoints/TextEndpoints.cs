using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Text;
using LocalAudiobookStudio.Core.Text;
using LocalAudiobookStudio.Web.Uploads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class TextEndpoints
{
    public static IEndpointRouteBuilder MapTextEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/projects/{id}/text/file", async (HttpContext context, string id, IStudioProjectService projects, IStudioTextService text) =>
        {
            long revision = await ApiRequest.CheckRevisionAsync(context, id, projects);
            var upload = await MultipartUploadReader.ReadAsync<FileMetadata>(context, TextImportService.MaximumFileBytes);
            using (upload.Content)
                return ApiRequest.Snapshot(context, await text.ImportFileAsync(id, revision, upload.Metadata.DisplayName, upload.Content, context.RequestAborted));
        });
        endpoints.MapPost("/api/projects/{id}/text/paste", async (HttpContext context, string id, IStudioProjectService projects, IStudioTextService text) =>
        {
            long revision = await ApiRequest.CheckRevisionAsync(context, id, projects);
            var command = await ApiRequest.JsonAsync<ImportPastedText>(context);
            return ApiRequest.Snapshot(context, await text.ImportPastedAsync(id, revision, command, context.RequestAborted));
        });
        endpoints.MapPatch("/api/projects/{id}/segments/{segmentId}", async (HttpContext context, string id, string segmentId, IStudioProjectService projects, IStudioTextService text) =>
        {
            long revision = await ApiRequest.CheckRevisionAsync(context, id, projects);
            var command = await ApiRequest.JsonAsync<UpdateSpokenText>(context, 4096);
            return ApiRequest.Snapshot(context, await text.UpdateSpokenTextAsync(id, segmentId, revision, command, context.RequestAborted));
        });
        return endpoints;
    }

    private sealed record FileMetadata(string DisplayName);
}

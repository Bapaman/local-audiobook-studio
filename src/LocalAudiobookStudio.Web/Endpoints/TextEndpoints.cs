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
        endpoints.MapPost("/api/projects/{id}/text/append/file", async (HttpContext context, string id, IStudioTextService text) =>
        {
            long revision = ApiRequest.Revision(context);
            var upload = await MultipartUploadReader.ReadAsync<AppendFileMetadata>(context, TextImportService.MaximumFileBytes);
            using (upload.Content)
                return ApiRequest.Snapshot(context, await text.AppendFileAsync(id,revision,upload.Metadata.TextId,upload.Metadata.DisplayName,upload.Content,context.RequestAborted));
        });
        endpoints.MapPost("/api/projects/{id}/text/append/paste", async (HttpContext context, string id, IStudioTextService text) =>
        {
            long revision = ApiRequest.Revision(context);
            var command = await ApiRequest.JsonAsync<AppendPastedText>(context);
            return ApiRequest.Snapshot(context,await text.AppendPastedAsync(id,revision,command,context.RequestAborted));
        });
        endpoints.MapPost("/api/projects/{id}/text/file", async (HttpContext context, string id, IStudioProjectService projects, IStudioTextService text) =>
        {
            long revision = await ApiRequest.CheckRevisionAsync(context, id, projects);
            var upload = await MultipartUploadReader.ReadAsync<FileMetadata>(context, TextImportService.MaximumFileBytes);
            using (upload.Content)
            {
                var snapshot = upload.Metadata.SplitChapters
                    ? await text.ImportBookFileAsync(id, revision, upload.Metadata.DisplayName, upload.Content, context.RequestAborted)
                    : await text.ImportFileAsync(id, revision, upload.Metadata.DisplayName, upload.Content, context.RequestAborted);
                return ApiRequest.Snapshot(context, snapshot);
            }
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
        endpoints.MapPatch("/api/projects/{id}/segments/{segmentId}/review", async (HttpContext context, string id, string segmentId, IStudioTextService text) =>
        {
            long revision = ApiRequest.Revision(context);
            var command = await ApiRequest.JsonAsync<UpdateAudioReview>(context, 512);
            return ApiRequest.Snapshot(context, await text.UpdateAudioReviewAsync(id, segmentId, revision, command, context.RequestAborted));
        });
        endpoints.MapPatch("/api/projects/{id}/segments/{segmentId}/correction", async (HttpContext context, string id, string segmentId, IStudioTextService text) =>
        {
            long revision = ApiRequest.Revision(context);
            var command = await ApiRequest.JsonAsync<UpdateAudioCorrection>(context, 4096);
            return ApiRequest.Snapshot(context, await text.UpdateAudioCorrectionAsync(id, segmentId, revision, command, context.RequestAborted));
        });
        return endpoints;
    }

    private sealed record FileMetadata(string DisplayName, bool SplitChapters = false);
    private sealed record AppendFileMetadata(string DisplayName, string TextId);
}

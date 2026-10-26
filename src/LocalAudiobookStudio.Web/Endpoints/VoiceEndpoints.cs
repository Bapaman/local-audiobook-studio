using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Voices;
using LocalAudiobookStudio.Core.Voices;
using LocalAudiobookStudio.Web.Uploads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class VoiceEndpoints
{
    public static IEndpointRouteBuilder MapVoiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/projects/{id}/voice/recording", async (HttpContext context, string id, IStudioProjectService projects, IStudioVoiceService voice, VoiceRecordingPolicy policy) =>
        {
            long revision = await ApiRequest.CheckRevisionAsync(context, id, projects);
            var upload = await MultipartUploadReader.ReadAsync<UploadVoiceRecording>(context, policy.MaximumBytes);
            using (upload.Content)
                return ApiRequest.Snapshot(context, await voice.UploadRecordingAsync(id, revision, upload.Metadata, upload.Content, context.RequestAborted));
        });
        endpoints.MapPost("/api/projects/{id}/voice/anchor", async (HttpContext context, string id, IStudioProjectService projects, IStudioVoiceService voice) =>
        {
            long revision = await ApiRequest.CheckRevisionAsync(context, id, projects);
            var command = await ApiRequest.JsonAsync<ConfirmVoiceAnchor>(context, 1024 * 1024);
            return ApiRequest.Snapshot(context, await voice.ConfirmAnchorAsync(id, revision, command, context.RequestAborted));
        });
        return endpoints;
    }
}

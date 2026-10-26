using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmInference;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class VoxCpmGenerationEndpoints
{
    public static IEndpointRouteBuilder MapVoxCpmGenerationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/voxcpm-inference/readiness", async (HttpContext context, IVoxCpmInferenceRuntime runtime)
            => Results.Ok(await runtime.GetReadinessAsync(context.RequestAborted)));
        endpoints.MapGet("/api/projects/{id}/voxcpm-generation/models/{modelId}/references",
            async (HttpContext context, string id, string modelId, IStudioVoxCpmGenerationService generation)
                => Results.Ok(await generation.GetReferencesAsync(id, modelId, context.RequestAborted)));
        endpoints.MapGet("/api/projects/{id}/voxcpm-generation/models/{modelId}/references/{clipId}/audio",
            async (HttpContext context, string id, string modelId, string clipId, IStudioVoxCpmGenerationService generation)
                => new OwnedReferenceAudioResult(await generation.OpenAudioAsync(id, modelId, clipId, context.RequestAborted)));
        return endpoints;
    }
    private sealed class OwnedReferenceAudioResult(TtsCorpusAudioHandle handle) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            try { await Results.File(handle.Content, handle.ContentType, enableRangeProcessing: true).ExecuteAsync(context); }
            finally { await handle.DisposeAsync(); }
        }
    }
}

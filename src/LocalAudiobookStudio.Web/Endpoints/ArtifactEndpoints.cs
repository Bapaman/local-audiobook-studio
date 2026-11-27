using LocalAudiobookStudio.Application.Artifacts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class ArtifactEndpoints
{
    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/projects/{id}/artifacts/{artifactId}", async (HttpContext context, string id, string artifactId, IProjectArtifactReader reader)
            => new OwnedArtifactResult(await reader.OpenAsync(id, artifactId, context.RequestAborted)));
        endpoints.MapGet("/api/projects/{id}/book.wav", async (HttpContext context, string id, long revision, string? textDocumentId, BookDownloadService books)
            => new BookResult(await books.OpenAsync(id, revision, textDocumentId, context.RequestAborted)));
        endpoints.MapGet("/api/projects/{id}/chapters.zip", async (HttpContext context, string id, long revision, string? textDocumentId, ChapterArchiveDownloadService archives) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var archive = await archives.OpenAsync(id, revision, textDocumentId, context.RequestAborted);
            return Results.File(archive, "application/zip", "chapters.zip");
        });
        return endpoints;
    }

    private sealed class BookResult(BookReadHandle book) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            try
            {
                context.Response.ContentType = "audio/wav"; context.Response.ContentLength = book.Length;
                context.Response.Headers.ContentDisposition = "attachment; filename=\"audiobook.wav\"";
                context.Response.Headers.AcceptRanges = "none";
                await book.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or LocalAudiobookStudio.Audio.WavInspectionException)
            { context.Abort(); }
            finally
            {
                try { await book.DisposeAsync(); }
                catch { context.Abort(); }
            }
        }
    }

    private sealed class OwnedArtifactResult(ArtifactReadHandle handle) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            try { await Results.File(handle.Content, handle.ContentType, handle.DownloadName, enableRangeProcessing: true).ExecuteAsync(context); }
            finally { await handle.DisposeAsync(); }
        }
    }
}

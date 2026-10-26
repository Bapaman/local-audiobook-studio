using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Web.Errors;
using LocalAudiobookStudio.Web.Uploads;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace LocalAudiobookStudio.Web.Endpoints;

internal static class ApiRequest
{
    internal const long LargeJsonLimit = 48_000_000 + 65536;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static void Limit(HttpContext context, long bytes)
    {
        // Reject an advertised oversized body before lowering Kestrel's read cap:
        // the still-unconsumed rejected body belongs to its response drain.
        if (context.Request.ContentLength > bytes) throw new ApiException(413, "request.too_large");
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = bytes;
    }

    internal static async Task<T> JsonAsync<T>(HttpContext context, long bytes = LargeJsonLimit, JsonSerializerOptions? options = null,
        long framingAllowance = 0)
    {
        Limit(context, bytes);
        // Kestrel counts HTTP/1 chunk framing too. The decoded JSON remains
        // strictly bounded below; only its transport receives a finite allowance.
        if (framingAllowance > 0 && context.Request.ContentLength is null)
        {
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = checked(bytes + framingAllowance);
        }
        if (!context.Request.HasJsonContentType()) throw new ApiException(415, "request.media_type");
        using var body = new BoundedReadStream(context.Request.Body, bytes);
        return await JsonSerializer.DeserializeAsync<T>(body, options ?? JsonOptions, context.RequestAborted)
            ?? throw new ApiException(400, "request.invalid");
    }

    internal static long Revision(HttpContext context)
    {
        var values = context.Request.Headers.IfMatch;
        if (values.Count == 0) throw new ApiException(428, "precondition.required");
        string? value = values.Count == 1 ? values[0] : null;
        if (value == null || value.Length < 3 || value[0] != '"' || value[^1] != '"'
            || value.AsSpan(1, value.Length - 2).ContainsAnyExceptInRange('0', '9')
            || !long.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out long revision))
            throw new ApiException(400, "precondition.invalid");
        return revision;
    }

    internal static async Task<long> CheckRevisionAsync(HttpContext context, string id, IStudioProjectService projects)
    {
        long revision = Revision(context);
        var current = await projects.GetAsync(id, context.RequestAborted);
        if (current.Revision != revision) throw new StudioException("project.conflict");
        return revision;
    }

    internal static void ETag(HttpContext context, long revision) => context.Response.Headers.ETag = '"' + revision.ToString(CultureInfo.InvariantCulture) + '"';
    internal static IResult Snapshot(HttpContext context, ProjectSnapshotDto snapshot)
    { ETag(context, snapshot.Revision); return Results.Ok(snapshot); }
}

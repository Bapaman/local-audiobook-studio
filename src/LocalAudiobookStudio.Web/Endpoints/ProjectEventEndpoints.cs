using System.Globalization;
using System.Text.Json;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Web.Errors;
using LocalAudiobookStudio.Web.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class ProjectEventEndpoints
{
    public static IEndpointRouteBuilder MapProjectEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/projects/{id}/events", StreamAsync);
        return endpoints;
    }

    private static async Task StreamAsync(HttpContext context, string id, IStudioJobService jobs, JobEventHub hub, TimeProvider time)
    {
        var origins = context.Request.Headers.Origin;
        string origin = $"{context.Request.Scheme}://127.0.0.1:{context.Connection.LocalPort}";
        if (origins.Count != 0 && (origins.Count != 1 || origins[0] != origin)) throw new ApiException(403, "invalid_origin");
        long? cursor = Cursor(context);
        using var subscription = hub.Subscribe(id); // Buffer BEFORE capturing the one atomic baseline.
        var page = await jobs.ReadEventPageAsync(id, cursor, context.RequestAborted);
        subscription.EstablishBaseline(page.HeadSequence);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        bool replay = cursor != null && !page.Reset && page.Events.Count != 0;
        await Baseline(page.Reset ? "reset" : "snapshot", page, replay ? null : page.HeadSequence);
        long delivered = replay ? cursor!.Value : page.HeadSequence;
        foreach (var hint in page.Events) await Hint(hint);
        long heartbeat = time.GetTimestamp();
        while (!context.RequestAborted.IsCancellationRequested)
        {
            // A closed/full live buffer cannot silently lose its suffix. The client
            // receives one fresh atomic reset, then reconnects using that ID.
            if (subscription.ResetRequested)
            { await Baseline("reset", await jobs.ReadEventPageAsync(id, null, context.RequestAborted), null); return; }
            while (subscription.Reader.TryRead(out var hint))
            {
                if (hint.Sequence <= delivered) continue;
                if (hint.Sequence > delivered + 1)
                {
                    var gap = await jobs.ReadEventPageAsync(id, delivered, context.RequestAborted);
                    if (gap.Reset) { await Baseline("reset", gap, gap.HeadSequence); return; }
                    foreach (var missing in gap.Events) await Hint(missing);
                }
                await Hint(hint);
                if (subscription.ResetRequested) break;
            }
            if (subscription.ResetRequested) continue;
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var available = subscription.Reader.WaitToReadAsync(waiting.Token).AsTask();
            TimeSpan remaining = TimeSpan.FromSeconds(10) - time.GetElapsedTime(heartbeat);
            var pulse = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, time, waiting.Token);
            try
            {
                if (await Task.WhenAny(available, pulse) == pulse)
                {
                    await pulse;
                    await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    heartbeat = time.GetTimestamp();
                }
                else if (!await available && !subscription.ResetRequested) return;
            }
            finally { await waiting.CancelAsync(); }
        }

        async Task Baseline(string name, JobEventPageDto value, long? frameId)
        {
            await Write(name, frameId ?? (name == "reset" ? value.HeadSequence : null),
                new { value.Jobs, value.HeadSequence, value.FirstRetainedSequence });
        }
        async Task Hint(JobEventDto hint)
        {
            if (hint.Sequence <= delivered) return;
            await Write("job", hint.Sequence, hint); delivered = hint.Sequence;
        }
        async Task Write(string name, long? sequence, object value)
        {
            string prefix = sequence == null ? "" : "id: " + sequence.Value.ToString(CultureInfo.InvariantCulture) + "\n";
            await context.Response.WriteAsync(prefix + "event: " + name + "\ndata: " + JsonSerializer.Serialize(value, JobWireJson.RequestOptions) + "\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    private static long? Cursor(HttpContext context)
    {
        var values = context.Request.Headers["Last-Event-ID"];
        if (values.Count == 0) return null;
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]) || values[0]!.Any(x => x is < '0' or > '9')
            || !long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out long cursor))
            throw new ApiException(400, "events.cursor.invalid");
        return cursor;
    }
}

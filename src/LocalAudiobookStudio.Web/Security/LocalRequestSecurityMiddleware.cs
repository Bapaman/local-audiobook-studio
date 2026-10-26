using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace LocalAudiobookStudio.Web.Security;

public sealed class LocalRequestSecurityMiddleware(RequestDelegate next)
{
    private static readonly PathString HealthPath = new("/healthz");
    private static readonly PathString LaunchPath = new("/_session/launch");
    private static readonly PathString ApiPath = new("/api");

    public async Task InvokeAsync(
        HttpContext context,
        LocalSessionStore sessions,
        IAntiforgery antiforgery)
    {
        var expectedHost = $"127.0.0.1:{context.Connection.LocalPort}";
        if (!string.Equals(context.Request.Host.Value, expectedHost, StringComparison.Ordinal))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "invalid_host", "Invalid request host.");
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method)
            && (context.Request.Path == HealthPath || context.Request.Path == LaunchPath))
        {
            await next(context);
            return;
        }

        if (!context.Request.Cookies.TryGetValue("las_session", out var sessionToken)
            || !sessions.IsValid(sessionToken))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "session_required",
                "A local application session is required.");
            return;
        }

        if (context.Request.Path.StartsWithSegments(ApiPath)
            && IsUnsafe(context.Request.Method))
        {
            var expectedOrigin = $"{context.Request.Scheme}://{expectedHost}";
            var origins = context.Request.Headers.Origin;
            if (origins.Count != 1
                || string.IsNullOrWhiteSpace(origins[0])
                || !string.Equals(origins[0], expectedOrigin, StringComparison.Ordinal))
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "invalid_origin",
                    "The request origin is not allowed.");
                return;
            }

            var antiforgeryHeaders = context.Request.Headers[AntiforgeryExtensions.RequestHeaderName];
            if (antiforgeryHeaders.Count != 1
                || string.IsNullOrWhiteSpace(antiforgeryHeaders[0])
                || antiforgeryHeaders[0]!.Contains(',', StringComparison.Ordinal))
            {
                await WriteAntiforgeryProblemAsync(context);
                return;
            }

            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await WriteAntiforgeryProblemAsync(context);
                return;
            }
        }

        await next(context);
    }

    private static bool IsUnsafe(string method) =>
        !HttpMethods.IsGet(method)
        && !HttpMethods.IsHead(method)
        && !HttpMethods.IsOptions(method)
        && !HttpMethods.IsTrace(method);

    internal static Task WriteProblemAsync(HttpContext context, int status, string code, string title) =>
        Results.Problem(
                statusCode: status,
                title: title,
                extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(context);

    private static Task WriteAntiforgeryProblemAsync(HttpContext context) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status400BadRequest,
            "antiforgery_failed",
            "The antiforgery token is missing or invalid.");
}

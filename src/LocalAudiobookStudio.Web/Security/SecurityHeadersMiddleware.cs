using Microsoft.AspNetCore.Http;

namespace LocalAudiobookStudio.Web.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; "
        + "media-src 'self'; connect-src 'self'; object-src 'none'; frame-src 'none'; "
        + "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(
            static state =>
            {
                var response = (HttpResponse)state;
                response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
                response.Headers["Referrer-Policy"] = "no-referrer";
                response.Headers.XContentTypeOptions = "nosniff";
                response.Headers.XFrameOptions = "DENY";
                return Task.CompletedTask;
            },
            context.Response);
        await next(context);
    }
}

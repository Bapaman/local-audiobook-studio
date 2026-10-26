using LocalAudiobookStudio.Web.Lifetime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class AppControlEndpoints
{
    public static IEndpointRouteBuilder MapAppControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/app/exit", (HttpContext context) =>
        {
            var lifetime = context.RequestServices.GetRequiredService<IAppLifetimeControl>();
            context.Response.OnCompleted(() => { lifetime.RequestShutdown(); return Task.CompletedTask; });
            return Results.Accepted();
        });
        return endpoints;
    }
}

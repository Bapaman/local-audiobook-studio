using System.Reflection;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Web.Security;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Application.Voices;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Endpoints;

public static class BootstrapEndpoints
{
    public static IEndpointRouteBuilder MapBootstrapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/_session/launch",
            (HttpContext context, LaunchTokenStore launches) =>
            {
                if (!context.Request.Query.TryGetValue("token", out var token)
                    || token.Count != 1
                    || !launches.TryRedeem(token[0]!, out var session))
                {
                    return LocalRequestSecurityMiddleware.WriteProblemAsync(
                        context,
                        StatusCodes.Status401Unauthorized,
                        "invalid_launch_token",
                        "The launch token is invalid or expired.");
                }

                context.Response.Cookies.Append(
                    "las_session",
                    session.Plaintext,
                    new CookieOptions
                    {
                        HttpOnly = true,
                        IsEssential = true,
                        Path = "/",
                        SameSite = SameSiteMode.Strict,
                    });
                context.Response.Redirect("/");
                return Task.CompletedTask;
            });

        endpoints.MapGet(
            "/api/bootstrap",
            async (HttpContext context, IAntiforgery antiforgery, IStudioTrainingService training) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(context);
                return Results.Ok(
                    new BootstrapDto(
                        GetVersion(),
                        tokens.RequestToken!,
                        new BootstrapCapabilities(
                            Generation: false,
                            Training: (await training.GetReadinessAsync(context.RequestAborted)).Ready,
                            VoiceMessages: context.RequestServices.GetService<IVoiceRecordingDecoder>() is not null)));
            });

        return endpoints;
    }

    private static string GetVersion() =>
        typeof(BootstrapEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(BootstrapEndpoints).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}

public sealed record BootstrapDto(
    string Version,
    string AntiforgeryRequestToken,
    BootstrapCapabilities Capabilities);

public sealed record BootstrapCapabilities(bool Generation, bool Training,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool VoiceMessages = false);

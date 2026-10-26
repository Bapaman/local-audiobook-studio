using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Web.Security;

public static class AntiforgeryExtensions
{
    public const string RequestHeaderName = "X-CSRF-TOKEN";

    public static IServiceCollection AddLocalAntiforgery(this IServiceCollection services)
    {
        services.AddAntiforgery(options =>
        {
            options.HeaderName = RequestHeaderName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });
        return services;
    }
}

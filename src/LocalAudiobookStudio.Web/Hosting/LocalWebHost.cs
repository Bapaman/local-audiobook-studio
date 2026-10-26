using System.Net;
using LocalAudiobookStudio.Web.Endpoints;
using LocalAudiobookStudio.Web.Security;
using LocalAudiobookStudio.Application;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Voices;
using LocalAudiobookStudio.Web.Errors;
using LocalAudiobookStudio.Web.Jobs;
using LocalAudiobookStudio.Web.Training;
using LocalAudiobookStudio.Web.TtsCorpus;
using LocalAudiobookStudio.Web.VoxCpmTraining;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Web.Hosting;

public sealed record LocalWebHostOptions(string DataRoot, int Port = 0, string? WebRoot = null);

public static class LocalWebHost
{
    public static WebApplication Build(
        LocalWebHostOptions options,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.WebRoot is not null && !Path.IsPathFullyQualified(options.WebRoot))
            throw new ArgumentException("The UI root must be absolute.", nameof(options));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = options.WebRoot
        });
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, options.Port));
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Services.AddSingleton(options);
        builder.Services.AddStudioProjects(new ProjectCatalogOptions(options.DataRoot));
        builder.Services.TryAddSingleton<VoiceRecordingPolicy>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<LocalSessionStore>();
        builder.Services.AddSingleton<LaunchTokenStore>();
        builder.Services.AddLocalAntiforgery();
        builder.Services.ConfigureHttpJsonOptions(options => JobWireJson.Configure(options.SerializerOptions));
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddHostedService<JobCoordinator>();
        builder.Services.AddHostedService<TrainingCoordinator>();
        builder.Services.AddHostedService<TtsCorpusCoordinator>();
        builder.Services.AddHostedService<VoxCpmTrainingCoordinator>();
        builder.Services.AddSingleton<JobEventHub>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<JobEventHub>());
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<LocalRequestSecurityMiddleware>();
        app.UseMiddleware<ProblemDetailsMapper>();
        if (options.WebRoot is not null)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }
        app.MapHealthEndpoints();
        app.MapBootstrapEndpoints();
        app.MapProjectEndpoints();
        app.MapVoiceEndpoints();
        app.MapTextEndpoints();
        app.MapJobEndpoints();
        app.MapTrainingEndpoints();
        app.MapTtsCorpusEndpoints();
        app.MapVoxCpmTrainingEndpoints();
        app.MapVoxCpmGenerationEndpoints();
        app.MapProjectEventEndpoints();
        app.MapArtifactEndpoints();
        app.MapAppControlEndpoints();
        return app;
    }
}

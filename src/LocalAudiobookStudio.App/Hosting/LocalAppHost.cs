using LocalAudiobookStudio.App.Lifetime;
using LocalAudiobookStudio.App.Workers;
using LocalAudiobookStudio.App.TtsCorpus;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.App.VoxCpmInference;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Web.Hosting;
using LocalAudiobookStudio.Web.Lifetime;
using LocalAudiobookStudio.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAudiobookStudio.App.Hosting;

public static class LocalAppHost
{
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        _ = await RunAsync(args, LocalAppHostDependencies.CreateForArguments(args), cancellationToken);
    }

    internal static async Task<InstanceRole> RunAsync(
        string[] args,
        LocalAppHostDependencies dependencies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(dependencies);
        cancellationToken.ThrowIfCancellationRequested();
        StartupErrorSink? errors = null;
        var launchFactoryReady = new TaskCompletionSource<LaunchUriFactory>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var browserGate = new SemaphoreSlim(1, 1);

        async Task OpenFromSignalAsync(CancellationToken commandCancellation)
        {
            var factory = await launchFactoryReady.Task.WaitAsync(commandCancellation);
            await browserGate.WaitAsync(commandCancellation);
            try
            {
                await OpenBrowserAsync(factory, dependencies.Browser,
                    errors ?? throw new InvalidOperationException("Startup diagnostics are unavailable."),
                    commandCancellation);
            }
            finally
            {
                browserGate.Release();
            }
        }

        await using var coordinator = new SingleInstanceCoordinator(
            dependencies.InstanceName,
            dependencies.CoordinatorOptions);
        var role = await coordinator.StartAsync(OpenFromSignalAsync, cancellationToken);
        if (role == InstanceRole.Secondary)
        {
            return role;
        }

        dependencies.Paths.EnsureCreated();
        errors = new StartupErrorSink(dependencies.Paths.LogsRoot);

        WebApplication? app = null;
        try
        {
            app = dependencies.HostFactory(
                new LocalWebHostOptions(dependencies.Paths.Root, WebRoot: Path.Combine(AppContext.BaseDirectory, "wwwroot")),
                services =>
                {
                    services.AddSingleton<IAppLifetimeControl>(provider =>
                        new HostLifetimeControl(provider.GetRequiredService<IHostApplicationLifetime>()));
                    // No implicit model/runtime discovery. Explicit developer composition
                    // may supply options; the ordinary user launch stays honestly not-ready.
                    services.AddSingleton(new WorkerRuntimeReadiness(dependencies.WorkerOptions, AppContext.BaseDirectory));
                    services.AddSingleton<IRuntimeReadiness>(provider => provider.GetRequiredService<WorkerRuntimeReadiness>());
                    services.AddSingleton<IRuntimeDescriptor>(provider => provider.GetRequiredService<WorkerRuntimeReadiness>());
                    services.AddSingleton<IWorkerClientFactory>(provider => new WorkerProcessSupervisor.Factory(
                        provider.GetRequiredService<WorkerRuntimeReadiness>(), TimeProvider.System, new()));
                    if (dependencies.TrainingOptions is { } training) services.AddRvcTraining(training);
                    if (dependencies.TtsCorpusOptions is { } corpus)
                    {
                        services.AddSingleton(_ => new TtsCorpusRuntime(corpus));
                        services.AddSingleton<ITtsCorpusRuntime>(provider => provider.GetRequiredService<TtsCorpusRuntime>());
                        services.AddSingleton(provider => new TtsCorpusNativeExecutor(provider.GetRequiredService<TtsCorpusRuntime>()));
                        services.AddSingleton<ITtsCorpusExecutor>(provider => provider.GetRequiredService<TtsCorpusNativeExecutor>());
                    }
                    if (dependencies.VoxCpmTrainingOptions is { } vox)
                    {
                        services.AddSingleton(_ => new VoxCpmTrainingRuntime(vox));
                        services.AddSingleton<IVoxCpmTrainingRuntime>(provider => provider.GetRequiredService<VoxCpmTrainingRuntime>());
                        services.AddSingleton(provider => new VoxCpmTrainingNativeExecutor(provider.GetRequiredService<VoxCpmTrainingRuntime>()));
                        services.AddSingleton<IVoxCpmTrainingExecutor>(provider => provider.GetRequiredService<VoxCpmTrainingNativeExecutor>());
                        services.AddSingleton<IVoxCpmTrainingCheckpointValidator, VoxCpmTrainingCheckpointValidator>();
                        services.AddSingleton(provider => new VoxCpmInferenceRuntime(provider.GetRequiredService<VoxCpmTrainingRuntime>()));
                        services.AddSingleton<IVoxCpmInferenceRuntime>(provider => provider.GetRequiredService<VoxCpmInferenceRuntime>());
                        services.AddSingleton(provider => new VoxCpmInferenceNativeExecutor(provider.GetRequiredService<VoxCpmInferenceRuntime>()));
                        services.AddSingleton<IVoxCpmInferenceExecutor>(provider => provider.GetRequiredService<VoxCpmInferenceNativeExecutor>());
                        services.AddSingleton<IVoxCpmSavedVoiceResolver>(provider => new VoxCpmSavedVoiceResolver(
                            provider.GetRequiredService<IApprovedTtsCorpusResolver>(),
                            provider.GetRequiredService<IVoxCpmTrainingCheckpointValidator>(),
                            provider.GetRequiredService<IVoxCpmInferenceRuntime>(),useCorpusReference:true));
                    }
                });
            await app.StartAsync(cancellationToken);

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || addresses.Count != 1)
            {
                throw new InvalidOperationException("The localhost server did not publish one runtime address.");
            }

            var launchFactory = new LaunchUriFactory(
                new Uri(addresses.Single(), UriKind.Absolute),
                app.Services.GetRequiredService<LaunchTokenStore>());
            await OpenBrowserAsync(launchFactory, dependencies.Browser, errors, cancellationToken);
            launchFactoryReady.TrySetResult(launchFactory);

            try
            {
                await app.WaitForShutdownAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            return role;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return role;
        }
        catch (Exception exception)
        {
            errors.Record(StartupEventCode.HostStartFailed, exception);
            throw;
        }
        finally
        {
            launchFactoryReady.TrySetCanceled(cancellationToken);
            if (app is not null)
            {
                try
                {
                    await app.StopAsync(CancellationToken.None);
                }
                finally
                {
                    // StopAsync's bounded wait is not native-tree exit proof.
                    // Never unwind the instance marker or dispose host services
                    // while this exact training owner can still be alive.
                    if (app.Services.GetService<VoxCpmTrainingNativeExecutor>() is { } vox)
                        await VoxCpmTrainingShutdown.WaitForCleanupAsync(vox.DisposeAsync);
                    if (app.Services.GetService<VoxCpmInferenceNativeExecutor>() is { } inference)
                        await VoxCpmInferenceShutdown.WaitForCleanupAsync(inference.DisposeAsync);
                    await app.DisposeAsync();
                }
            }
        }
    }

    private static async Task OpenBrowserAsync(
        LaunchUriFactory launchFactory,
        IExternalBrowser browser,
        StartupErrorSink errors,
        CancellationToken cancellationToken)
    {
        try
        {
            await browser.OpenAsync(launchFactory.Create(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            errors.Record(StartupEventCode.BrowserOpenFailed, exception);
        }
    }
}

internal sealed record LocalAppHostDependencies(
    AppDataPaths Paths,
    string InstanceName,
    IExternalBrowser Browser,
    Func<LocalWebHostOptions, Action<IServiceCollection>?, WebApplication> HostFactory,
    SingleInstanceCoordinatorOptions CoordinatorOptions)
{
    public DeveloperWorkerOptions? WorkerOptions { get; init; }
    public RvcTrainingOptions? TrainingOptions { get; init; }
    public TtsCorpusRuntimeOptions? TtsCorpusOptions { get; init; }
    public VoxCpmTrainingRuntimeOptions? VoxCpmTrainingOptions { get; init; }

    public static LocalAppHostDependencies CreateForArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        DeveloperWorkerOptions? worker = null;
        RvcTrainingOptions? training = null;
        TtsCorpusRuntimeOptions? corpus = null;
        VoxCpmTrainingRuntimeOptions? vox = null;
        while (true)
        {
            if (args.Length >= 2 && args[^2] == "--voxcpm-training-runtime-config")
            {
                if (vox != null) throw new ArgumentException("Duplicate VoxCPM training options.");
                vox = VoxCpmTrainingRuntimeOptions.Read(args[^1]); args = args[..^2]; continue;
            }
            if (args.Length >= 2 && args[^2] == "--tts-corpus-runtime-config")
            {
                if (corpus != null) throw new ArgumentException("Duplicate corpus options.");
                corpus = TtsCorpusRuntimeOptions.Read(args[^1]);
                args = args[..^2]; continue;
            }
            if (args.Length >= 2 && args[^2] == "--training-runtime-config")
            {
                if (training != null) throw new ArgumentException("Duplicate training options.");
                training = ReadTrainingConfig(CanonicalLocalRoot(args[^1]));
                args = args[..^2]; continue;
            }
            if (args.Length >= 4 && args[^4] == "--developer-cpu-runtime")
            {
                if (worker != null) throw new ArgumentException("Duplicate worker options.");
                worker = new(CanonicalLocalRoot(args[^3]), CanonicalLocalRoot(args[^2]), CanonicalLocalRoot(args[^1]));
                args = args[..^4]; continue;
            }
            break;
        }
        if (args.Length == 0) return CreateDefault() with { WorkerOptions = worker, TrainingOptions = training, TtsCorpusOptions = corpus, VoxCpmTrainingOptions = vox };
        if ((args.Length != 2 && args.Length != 4) || args[0] != "--synthetic-data-root"
            || (args.Length == 4 && args[2] != "--synthetic-launch-pipe")) throw new ArgumentException("Invalid startup options.");
        string root = CanonicalLocalRoot(args[1]);
        var paths = AppDataPaths.ForRoot(root);
        if (string.Equals(paths.Root, AppDataPaths.ForCurrentUser().Root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid synthetic root.");
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..24];
        IExternalBrowser browser = args.Length == 4 ? new SyntheticLaunchBrowser(args[3]) : new ExternalBrowser();
        return new(paths, SingleInstanceCoordinator.DefaultInstanceName + ".Synthetic." + identity,
            browser, LocalWebHost.Build, SingleInstanceCoordinatorOptions.Default) { WorkerOptions = worker, TrainingOptions = training, TtsCorpusOptions = corpus, VoxCpmTrainingOptions = vox };
    }

    private static RvcTrainingOptions ReadTrainingConfig(string path)
    {
        try
        {
            // Trusted local configuration, never a path supplied by the browser.
            // Deny replacement while reading; reject links and duplicate fields.
            for (string? current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Invalid training configuration.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > 32768) throw new ArgumentException("Invalid training configuration.");
            byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes);
            string[] names = ["pythonPath", "powerShellPath", "scriptsRoot", "decoderRoot", "sourceRoot", "baseAssetRoot", "featureAssetRoot"];
            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != names.Length || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != names.Length
                || properties.Any(p => !names.Contains(p.Name, StringComparer.Ordinal) || p.Value.ValueKind != JsonValueKind.String))
                throw new ArgumentException("Invalid training configuration.");
            string Value(string name) => CanonicalLocalRoot(document.RootElement.GetProperty(name).GetString()!);
            return new(Value("pythonPath"), Value("powerShellPath"), Value("scriptsRoot"), Value("decoderRoot"),
                Value("sourceRoot"), Value("baseAssetRoot"), Value("featureAssetRoot"));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { throw new ArgumentException("Invalid training configuration."); }
    }

    internal static string CanonicalLocalRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !IsDriveAbsolute(root) || !Path.IsPathFullyQualified(root)
            || root.Skip(2).Contains(':')
            || root.Any(char.IsControl)) throw new ArgumentException("Invalid synthetic root.");
        foreach (string part in root[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") continue;
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part.EndsWith('.') || part.EndsWith(' ') || part.Contains('~')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || stem is "CON" or "PRN" or "AUX" or "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
                throw new ArgumentException("Invalid synthetic root.");
        }
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? canonicalRoot = Path.GetPathRoot(canonical);
        if (canonicalRoot is null || !IsDriveAbsolute(canonicalRoot) || !IsDriveAbsolute(canonical)
            || string.Equals(canonical, Path.TrimEndingDirectorySeparator(canonicalRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid synthetic root.");
        return canonical;
    }

    private static bool IsDriveAbsolute(string path) => path.Length >= 3
        && path[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
        && path[1] == ':' && path[2] is '\\' or '/';

    public static LocalAppHostDependencies CreateDefault() =>
        new(
            AppDataPaths.ForCurrentUser(),
            SingleInstanceCoordinator.DefaultInstanceName,
            new ExternalBrowser(),
            LocalWebHost.Build,
            SingleInstanceCoordinatorOptions.Default);
}

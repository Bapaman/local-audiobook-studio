using System.Collections.Concurrent;
using System.Net;
using LocalAudiobookStudio.App.Hosting;
using LocalAudiobookStudio.App.Lifetime;
using LocalAudiobookStudio.Web.Hosting;
using LocalAudiobookStudio.Web.Lifetime;
using LocalAudiobookStudio.App.Workers;
using LocalAudiobookStudio.Application.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.App.Tests;

[CollectionDefinition("ConsoleCapture", DisableParallelization = true)]
public sealed class ConsoleCaptureCollection;

[Collection("ConsoleCapture")]
public sealed class LocalAppHostTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Explicit_CPU_runtime_arguments_reach_the_real_host_without_fake_readiness()
    {
        using var temporary = new TemporaryDirectory();
        string runtime = Path.Combine(temporary.Path, "runtime"), model = Path.Combine(temporary.Path, "model"), locks = Path.Combine(temporary.Path, "locks");
        var browser = new SequencedBrowser(blockFirstOpen: false);
        WebApplication? host = null;
        var dependencies = LocalAppHostDependencies.CreateForArguments([
            "--synthetic-data-root", Path.Combine(temporary.Path, "studio"),
            "--developer-cpu-runtime", runtime, model, locks]) with
        {
            Browser = browser,
            HostFactory = (options, configure) => host = LocalWebHost.Build(options, configure)
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = LocalAppHost.RunAsync([], dependencies, cancellation.Token);
        try
        {
            await browser.FirstOpenStarted.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            var readiness = host!.Services.GetRequiredService<WorkerRuntimeReadiness>();
            Assert.Equal(new DeveloperWorkerOptions(runtime, model, locks, "q8-f16-cpu", false), readiness.Options);
            Assert.Equal(new RuntimeReadiness(false, true, "worker.runtime.not_ready", "q8-f16-cpu"),
                await readiness.GetAsync(TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(runtime)); Assert.False(Directory.Exists(model)); Assert.False(Directory.Exists(locks));
        }
        finally
        {
            cancellation.Cancel();
            Assert.Equal(InstanceRole.Primary, await running.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Default_host_logger_does_not_print_start_failure_details()
    {
        using var temporary = new TemporaryDirectory();
        const string sentinel = "private-startup-content-7f4a";
        var name = $"LocalAudiobookStudio.Tests.{Guid.NewGuid():N}";
        var dependencies = Dependencies(
            AppDataPaths.ForRoot(temporary.Path), name, new RejectingBrowser(),
            (options, configure) => LocalWebHost.Build(options, services =>
            {
                configure?.Invoke(services);
                services.AddSingleton<IHostedService>(new FailingStartService(sentinel));
            }));
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => LocalAppHost.RunAsync([], dependencies, TestContext.Current.CancellationToken));
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.DoesNotContain(sentinel, captured.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Normal_host_start_does_not_print_content_root_paths()
    {
        using var temporary = new TemporaryDirectory();
        var name = $"LocalAudiobookStudio.Tests.{Guid.NewGuid():N}";
        var browser = new SequencedBrowser(blockFirstOpen: false);
        var dependencies = Dependencies(AppDataPaths.ForRoot(temporary.Path), name,
            browser, LocalWebHost.Build);
        using var cancellation = new CancellationTokenSource();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            var running = LocalAppHost.RunAsync([], dependencies, cancellation.Token);
            await browser.FirstOpenStarted.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            cancellation.Cancel();
            Assert.Equal(InstanceRole.Primary,
                await running.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.DoesNotContain("Content root path:", captured.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secondary_does_not_create_application_data_directories()
    {
        using var primaryTemporary = new TemporaryDirectory();
        using var secondaryTemporary = new TemporaryDirectory();
        var name = $"LocalAudiobookStudio.Tests.{Guid.NewGuid():N}";
        var browser = new SequencedBrowser(blockFirstOpen: false);
        using var primaryCancellation = new CancellationTokenSource();
        var primaryTask = LocalAppHost.RunAsync(
            [],
            Dependencies(AppDataPaths.ForRoot(Path.Combine(primaryTemporary.Path, "primary")),
                name, browser, LocalWebHost.Build),
            primaryCancellation.Token);
        await browser.FirstOpenStarted.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        var secondaryRoot = Path.Combine(secondaryTemporary.Path, "must-not-exist");
        var role = await LocalAppHost.RunAsync(
            [],
            Dependencies(AppDataPaths.ForRoot(secondaryRoot), name,
                new RejectingBrowser(),
                (_, _) => throw new Xunit.Sdk.XunitException("A secondary must not build a host.")),
            TestContext.Current.CancellationToken);

        Assert.Equal(InstanceRole.Secondary, role);
        Assert.False(Directory.Exists(secondaryRoot));
        primaryCancellation.Cancel();
        Assert.Equal(InstanceRole.Primary,
            await primaryTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Real_host_is_owned_only_by_primary_and_repeat_launch_waits_for_initial_browser_open()
    {
        using var temporary = new TemporaryDirectory();
        var name = $"LocalAudiobookStudio.Tests.{Guid.NewGuid():N}";
        var paths = AppDataPaths.ForRoot(temporary.Path);
        var browser = new SequencedBrowser(blockFirstOpen: true);
        var hostBuildCount = 0;
        WebApplication? runningApp = null;
        var primaryDependencies = Dependencies(
            paths,
            name,
            browser,
            (options, configure) =>
            {
                Interlocked.Increment(ref hostBuildCount);
                runningApp = LocalWebHost.Build(options, configure);
                return runningApp;
            });
        using var primaryCancellation = new CancellationTokenSource();
        var primaryTask = LocalAppHost.RunAsync([], primaryDependencies, primaryCancellation.Token);

        await browser.FirstOpenStarted.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref hostBuildCount));

        var secondaryHostBuildCount = 0;
        var secondaryBrowser = new RejectingBrowser();
        var secondaryDependencies = Dependencies(
            paths,
            name,
            secondaryBrowser,
            (options, configure) =>
            {
                Interlocked.Increment(ref secondaryHostBuildCount);
                throw new Xunit.Sdk.XunitException("A secondary must not build a host.");
            });
        var secondaryRole = await LocalAppHost.RunAsync(
            [],
            secondaryDependencies,
            TestContext.Current.CancellationToken);

        Assert.Equal(InstanceRole.Secondary, secondaryRole);
        Assert.Equal(0, secondaryHostBuildCount);
        Assert.Equal(0, secondaryBrowser.OpenCount);
        Assert.Single(browser.OpenedUris);

        browser.ReleaseFirstOpen();
        var uris = await browser.WaitForCountAsync(2, TestTimeout);
        Assert.Equal(2, uris.Count);
        Assert.NotEqual(uris[0].Query, uris[1].Query);
        Assert.Equal(uris[0].GetLeftPart(UriPartial.Path), uris[1].GetLeftPart(UriPartial.Path));

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        foreach (var uri in uris)
        {
            using var launch = await client.GetAsync(uri, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, launch.StatusCode);
            Assert.Equal("/", launch.Headers.Location?.OriginalString);
        }

        var healthUri = new Uri(uris[0], "/healthz");
        using var health = await client.GetAsync(healthUri, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        Assert.NotNull(runningApp);
        runningApp.Services.GetRequiredService<IAppLifetimeControl>().RequestShutdown();
        Assert.Equal(
            InstanceRole.Primary,
            await primaryTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        await using var replacement = new SingleInstanceCoordinator(name, FastCoordinatorOptions());
        Assert.Equal(
            InstanceRole.Primary,
            await replacement.StartAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Browser_failure_is_privately_logged_and_host_stays_available_for_repeat_launch()
    {
        using var temporary = new TemporaryDirectory();
        var name = $"LocalAudiobookStudio.Tests.{Guid.NewGuid():N}";
        var paths = AppDataPaths.ForRoot(temporary.Path);
        const string sentinel = "secret-launch-url-and-book-text";
        var browser = new FailFirstBrowser(sentinel);
        var dependencies = Dependencies(paths, name, browser, LocalWebHost.Build);
        using var primaryCancellation = new CancellationTokenSource();
        var primaryTask = LocalAppHost.RunAsync([], dependencies, primaryCancellation.Token);

        await browser.FirstFailureObserved.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await using var secondary = new SingleInstanceCoordinator(name, FastCoordinatorOptions());
        Assert.Equal(
            InstanceRole.Secondary,
            await secondary.StartAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken));

        var reopened = await browser.SuccessfulOpen.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var launch = await client.GetAsync(reopened, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, launch.StatusCode);

        var log = await File.ReadAllTextAsync(
            Path.Combine(paths.LogsRoot, "startup.log"),
            TestContext.Current.CancellationToken);
        Assert.Contains("browser_open_failed", log, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, log, StringComparison.Ordinal);
        Assert.DoesNotContain(reopened.ToString(), log, StringComparison.Ordinal);

        primaryCancellation.Cancel();
        Assert.Equal(
            InstanceRole.Primary,
            await primaryTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
    }

    private static LocalAppHostDependencies Dependencies(
        AppDataPaths paths,
        string instanceName,
        IExternalBrowser browser,
        Func<LocalWebHostOptions, Action<IServiceCollection>?, WebApplication> hostFactory) =>
        new(
            paths,
            instanceName,
            browser,
            hostFactory,
            FastCoordinatorOptions());

    private static SingleInstanceCoordinatorOptions FastCoordinatorOptions() =>
        new(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(300));

    private sealed class SequencedBrowser(bool blockFirstOpen) : IExternalBrowser
    {
        private readonly TaskCompletionSource _firstOpenStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstOpen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<Uri> _openedUris = new();
        private readonly SemaphoreSlim _changed = new(0);
        private int _count;

        public Task FirstOpenStarted => _firstOpenStarted.Task;

        public IReadOnlyList<Uri> OpenedUris => _openedUris.ToArray();

        public async Task OpenAsync(Uri uri, CancellationToken cancellationToken)
        {
            _openedUris.Enqueue(uri);
            var count = Interlocked.Increment(ref _count);
            _changed.Release();
            if (count == 1)
            {
                _firstOpenStarted.TrySetResult();
                if (blockFirstOpen)
                {
                    await _releaseFirstOpen.Task.WaitAsync(cancellationToken);
                }
            }
        }

        public void ReleaseFirstOpen() => _releaseFirstOpen.TrySetResult();

        public async Task<IReadOnlyList<Uri>> WaitForCountAsync(int expected, TimeSpan timeout)
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            while (Volatile.Read(ref _count) < expected)
            {
                await _changed.WaitAsync(timeoutCancellation.Token);
            }

            return OpenedUris;
        }
    }

    private sealed class RejectingBrowser : IExternalBrowser
    {
        public int OpenCount { get; private set; }

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken)
        {
            OpenCount++;
            throw new Xunit.Sdk.XunitException("A secondary must not open a browser locally.");
        }
    }

    private sealed class FailFirstBrowser(string sentinel) : IExternalBrowser
    {
        private readonly TaskCompletionSource _firstFailureObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Uri> _successfulOpen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task FirstFailureObserved => _firstFailureObserved.Task;

        public Task<Uri> SuccessfulOpen => _successfulOpen.Task;

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                _firstFailureObserved.TrySetResult();
                throw new InvalidOperationException(sentinel);
            }

            _successfulOpen.TrySetResult(uri);
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalAudiobookStudio.App.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FailingStartService(string message) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

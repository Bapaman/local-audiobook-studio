using LocalAudiobookStudio.App.Hosting;
using LocalAudiobookStudio.App.Lifetime;
using LocalAudiobookStudio.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.App.Tests;

[Collection("ConsoleCapture")]
public sealed class ProgramStartupTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Forward_slash_UNC_is_rejected_lexically_with_only_fixed_startup_failure()
    {
        using var temporary = new TemporaryDirectory();
        string[] args = ["--synthetic-data-root", "//synthetic-server/synthetic-share/studio"];
        bool dependenciesCreated = false;
        var originalError = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.Equal(1, await Program.RunAsync(args, () =>
            {
                _ = LocalAppHostDependencies.CreateForArguments(args);
                dependenciesCreated = true;
                // Never run a host or touch any share even against the pre-fix parser.
                throw new InvalidOperationException("synthetic-parser-reached-dependencies");
            }, () => new StartupErrorSink(temporary.Path), TestContext.Current.CancellationToken));
        }
        finally { Console.SetError(originalError); }
        Assert.False(dependenciesCreated);
        Assert.Equal("app_start_failed", captured.ToString().Trim());
        var log = await File.ReadAllTextAsync(new StartupErrorSink(temporary.Path).LogPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("synthetic-server", log, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-share", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_synthetic_arguments_return_only_fixed_startup_failure()
    {
        using var temporary = new TemporaryDirectory();
        string[] args = ["--synthetic-launch-pipe", "private-argument-sentinel"];
        var originalError = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Assert.Equal(1, await Program.RunAsync(args, () => LocalAppHostDependencies.CreateForArguments(args),
                () => new StartupErrorSink(temporary.Path), TestContext.Current.CancellationToken));
        }
        finally { Console.SetError(originalError); }
        Assert.Equal("app_start_failed", captured.ToString().Trim());
        var log = await File.ReadAllTextAsync(new StartupErrorSink(temporary.Path).LogPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("private-argument-sentinel", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dependency_construction_failure_returns_nonzero_with_only_fixed_diagnostics()
    {
        using var temporary = new TemporaryDirectory();
        const string sentinel = "private-dependency-content-79e2";
        var sink = new StartupErrorSink(temporary.Path);
        var originalError = Console.Error;
        using var capturedError = new StringWriter();
        Console.SetError(capturedError);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync([], () => throw new InvalidOperationException(sentinel),
                () => sink, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal(1, exitCode);
        Assert.Equal("app_start_failed", capturedError.ToString().Trim());
        var log = await File.ReadAllTextAsync(sink.LogPath, TestContext.Current.CancellationToken);
        Assert.Contains("app_start_failed", log, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Election_failure_returns_nonzero_without_leaking_pipe_details()
    {
        using var temporary = new TemporaryDirectory();
        var name = UniqueName();
        using var marker = new Mutex(false, name,
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true },
            out var createdNew);
        Assert.True(createdNew);
        var sink = new StartupErrorSink(temporary.Path);
        var dependencies = Dependencies(temporary.Path, name,
            (_, _) => throw new Xunit.Sdk.XunitException("Election should prevent host construction."),
            new SingleInstanceCoordinatorOptions(TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)));

        var exitCode = await Program.RunAsync([], () => dependencies, () => sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        var log = await File.ReadAllTextAsync(sink.LogPath, TestContext.Current.CancellationToken);
        Assert.Contains("app_start_failed", log, StringComparison.Ordinal);
        Assert.DoesNotContain(name, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Directory_creation_failure_returns_nonzero_and_releases_election_marker()
    {
        using var temporary = new TemporaryDirectory();
        var fileRoot = Path.Combine(temporary.Path, "occupied-root");
        await File.WriteAllTextAsync(fileRoot, "fixture", TestContext.Current.CancellationToken);
        var name = UniqueName();
        var sink = new StartupErrorSink(temporary.Path);
        var dependencies = Dependencies(fileRoot, name,
            (_, _) => throw new Xunit.Sdk.XunitException("Directory failure should prevent host construction."));

        var exitCode = await Program.RunAsync([], () => dependencies, () => sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("app_start_failed", await File.ReadAllTextAsync(sink.LogPath,
            TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await AssertMarkerReleasedAsync(name);
    }

    [Fact]
    public async Task Host_construction_failure_returns_nonzero_and_releases_election_marker()
    {
        using var temporary = new TemporaryDirectory();
        var name = UniqueName();
        const string sentinel = "private-host-content-484f";
        var sink = new StartupErrorSink(temporary.Path);
        var dependencies = Dependencies(temporary.Path, name,
            (_, _) => throw new InvalidOperationException(sentinel));

        var exitCode = await Program.RunAsync([], () => dependencies, () => sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        var log = await File.ReadAllTextAsync(sink.LogPath, TestContext.Current.CancellationToken);
        Assert.Contains("app_start_failed", log, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, log, StringComparison.Ordinal);
        await AssertMarkerReleasedAsync(name);
    }

    [Fact]
    public async Task Cleanup_failure_returns_nonzero_and_releases_election_marker()
    {
        using var temporary = new TemporaryDirectory();
        var name = UniqueName();
        const string sentinel = "private-cleanup-content-3341";
        var sink = new StartupErrorSink(temporary.Path);
        var browser = new SignalingBrowser();
        var dependencies = Dependencies(temporary.Path, name,
            (options, configure) => LocalWebHost.Build(options, services =>
            {
                configure?.Invoke(services);
                services.AddSingleton<IHostedService>(new FailingStopService(sentinel));
            }), browser: browser);
        using var cancellation = new CancellationTokenSource();

        var running = Program.RunAsync([], () => dependencies, () => sink, cancellation.Token);
        await browser.Opened.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var exitCode = await running.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        var log = await File.ReadAllTextAsync(sink.LogPath, TestContext.Current.CancellationToken);
        Assert.Contains("app_start_failed", log, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, log, StringComparison.Ordinal);
        await AssertMarkerReleasedAsync(name);
    }

    private static LocalAppHostDependencies Dependencies(
        string root,
        string name,
        Func<LocalWebHostOptions, Action<IServiceCollection>?, WebApplication> hostFactory,
        SingleInstanceCoordinatorOptions? options = null,
        IExternalBrowser? browser = null) =>
        new(AppDataPaths.ForRoot(root), name, browser ?? new SignalingBrowser(), hostFactory,
            options ?? new SingleInstanceCoordinatorOptions(TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(300)));

    private static async Task AssertMarkerReleasedAsync(string name)
    {
        await using var replacement = new SingleInstanceCoordinator(name);
        Assert.Equal(InstanceRole.Primary, await replacement.StartAsync(_ => Task.CompletedTask,
            TestContext.Current.CancellationToken));
    }

    private static string UniqueName() => $"LocalAudiobookStudio.Tests.{Guid.NewGuid():N}";

    private sealed class SignalingBrowser : IExternalBrowser
    {
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Opened => _opened.Task;

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken)
        {
            _opened.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class FailingStopService(string message) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"LocalAudiobookStudio.Program.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

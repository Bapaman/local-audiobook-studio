using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using LocalAudiobookStudio.App.Hosting;
using LocalAudiobookStudio.App.Lifetime;
using LocalAudiobookStudio.Web.Security;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Primary_listener_is_ready_secondary_signals_once_and_release_allows_a_new_primary()
    {
        var name = UniqueName();
        var options = FastOptions();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;

        await using (var primary = new SingleInstanceCoordinator(name, options))
        {
            var role = await primary.StartAsync(
                _ =>
                {
                    if (Interlocked.Increment(ref openCount) == 1)
                    {
                        opened.TrySetResult();
                    }

                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(InstanceRole.Primary, role);

            await using var secondary = new SingleInstanceCoordinator(name, options);
            Assert.Equal(
                InstanceRole.Secondary,
                await secondary.StartAsync(
                    _ => throw new Xunit.Sdk.XunitException("A secondary must not install an open handler."),
                    TestContext.Current.CancellationToken));

            await opened.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref openCount));
        }

        await using var replacement = new SingleInstanceCoordinator(name, options);
        Assert.Equal(
            InstanceRole.Primary,
            await replacement.StartAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Secondary_retry_exhaustion_and_cancellation_never_promote_to_primary()
    {
        var name = UniqueName();
        var mutexOptions = new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = true,
        };
        using var marker = new Mutex(false, name, mutexOptions, out var createdNew);
        Assert.True(createdNew);

        await using var timedOut = new SingleInstanceCoordinator(
            name,
            new SingleInstanceCoordinatorOptions(
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(150)));
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => timedOut.StartAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TestTimeout);

        await using var canceled = new SingleInstanceCoordinator(name, FastOptions());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.StartAsync(_ => Task.CompletedTask, cancellation.Token));
    }

    [Fact]
    public async Task Malformed_oversized_and_stalled_clients_do_not_block_a_later_open_command()
    {
        var name = UniqueName();
        var options = FastOptions();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var primary = new SingleInstanceCoordinator(name, options);
        Assert.Equal(
            InstanceRole.Primary,
            await primary.StartAsync(
                _ =>
                {
                    opened.TrySetResult();
                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken));

        await SendRawAsync(primary.PipeName, "not-a-command\n");
        await SendRawAsync(primary.PipeName, new string('x', 128));
        await using var stalled = await ConnectRawAsync(primary.PipeName);

        await using var secondary = new SingleInstanceCoordinator(name, options);
        Assert.Equal(
            InstanceRole.Secondary,
            await secondary.StartAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken));
        await opened.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Command_waits_for_eof_and_rejects_trailing_bytes()
    {
        var name = UniqueName();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        await using var primary = new SingleInstanceCoordinator(name, FastOptions());
        Assert.Equal(InstanceRole.Primary,
            await primary.StartAsync(_ =>
            {
                Interlocked.Increment(ref openCount);
                opened.TrySetResult();
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken));

        await using (var client = await ConnectRawAsync(primary.PipeName))
        {
            await client.WriteAsync(Encoding.ASCII.GetBytes("LAS/1 open\n"),
                TestContext.Current.CancellationToken);
            await client.FlushAsync(TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref openCount));
            await client.WriteAsync(Encoding.ASCII.GetBytes("trailing"),
                TestContext.Current.CancellationToken);
        }

        await using var secondary = new SingleInstanceCoordinator(name, FastOptions());
        Assert.Equal(InstanceRole.Secondary,
            await secondary.StartAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken));
        await opened.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref openCount));
    }

    [Fact]
    public void App_data_paths_are_exact_and_all_directories_stay_below_the_injected_root()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.GetFullPath(Path.Combine(temporary.Path, "данные приложения"));

        var paths = AppDataPaths.ForRoot(root);
        paths.EnsureCreated();

        Assert.Equal(root, paths.Root);
        Assert.Equal(Path.Combine(root, "catalog.sqlite"), paths.CatalogPath);
        Assert.Equal(Path.Combine(root, "projects"), paths.ProjectsRoot);
        Assert.Equal(Path.Combine(root, "logs"), paths.LogsRoot);
        Assert.Equal(Path.Combine(root, "transient"), paths.TransientRoot);
        Assert.All(
            new[] { paths.ProjectsRoot, paths.LogsRoot, paths.TransientRoot },
            directory => Assert.True(Directory.Exists(directory)));
    }

    [Fact]
    public void Launch_uri_factory_issues_fresh_sixty_second_loopback_tickets()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        var launches = new LaunchTokenStore(time, new LocalSessionStore());
        var factory = new LaunchUriFactory(new Uri("http://127.0.0.1:43123"), launches);

        var first = factory.Create();
        var second = factory.Create();

        Assert.Equal("http://127.0.0.1:43123/_session/launch", first.GetLeftPart(UriPartial.Path));
        Assert.Equal(first.GetLeftPart(UriPartial.Path), second.GetLeftPart(UriPartial.Path));
        Assert.NotEqual(first.Query, second.Query);

        time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(launches.TryRedeem(TokenFrom(first), out _));
        Assert.False(launches.TryRedeem(TokenFrom(second), out _));
        Assert.Throws<ArgumentException>(
            () => new LaunchUriFactory(new Uri("http://localhost:43123"), launches));
        Assert.Throws<ArgumentException>(
            () => new LaunchUriFactory(new Uri("https://127.0.0.1:43123"), launches));
    }

    [Fact]
    public async Task External_browser_accepts_successful_shell_dispatch_without_a_process_handle()
    {
        var launches = new LaunchTokenStore(TimeProvider.System, new LocalSessionStore());
        var uri = new LaunchUriFactory(new Uri("http://127.0.0.1:43123"), launches).Create();
        ProcessStartInfo? request = null;
        var browser = new ExternalBrowser(info =>
        {
            request = info;
            return null;
        });

        await browser.OpenAsync(uri, TestContext.Current.CancellationToken);

        Assert.NotNull(request);
        Assert.Equal(uri.AbsoluteUri, request.FileName);
        Assert.True(request.UseShellExecute);
    }

    [Fact]
    public void Startup_error_log_is_bounded_allowlisted_and_never_contains_exception_details()
    {
        using var temporary = new TemporaryDirectory();
        var sentinel = "private-token-book-text-C:/private/audio.wav";
        var sink = new StartupErrorSink(temporary.Path);

        for (var index = 0; index < 4_000; index++)
        {
            sink.Record(
                StartupEventCode.BrowserOpenFailed,
                new InvalidOperationException($"{sentinel}-{index}"));
        }

        sink.Record((StartupEventCode)int.MaxValue, new InvalidOperationException(sentinel));
        var bytes = File.ReadAllBytes(sink.LogPath);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.InRange(bytes.Length, 1, StartupErrorSink.MaximumBytes);
        Assert.Contains("browser_open_failed", text, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_lifetime_control_requests_shutdown_only_once()
    {
        var lifetime = new CountingHostLifetime();
        var control = new HostLifetimeControl(lifetime);

        control.RequestShutdown();
        control.RequestShutdown();

        Assert.Equal(1, lifetime.StopCount);
    }

    private static SingleInstanceCoordinatorOptions FastOptions() =>
        new(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(300));

    private static string UniqueName() => $"LocalAudiobookStudio.Tests.{Guid.NewGuid():N}";

    private static async Task SendRawAsync(string pipeName, string command)
    {
        await using var client = await ConnectRawAsync(pipeName);
        try
        {
            await client.WriteAsync(Encoding.ASCII.GetBytes(command), TestContext.Current.CancellationToken);
            await client.FlushAsync(TestContext.Current.CancellationToken);
        }
        catch (IOException)
        {
            // A strict server may close an invalid or oversized frame before the client finishes writing it.
        }
    }

    private static async Task<NamedPipeClientStream> ConnectRawAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(2_000, TestContext.Current.CancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static string TokenFrom(Uri uri)
    {
        const string prefix = "?token=";
        Assert.StartsWith(prefix, uri.Query, StringComparison.Ordinal);
        return Uri.UnescapeDataString(uri.Query[prefix.Length..]);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class CountingHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public int StopCount { get; private set; }

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopCount++;
            _stopping.Cancel();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalAudiobookStudio.Tests.{Guid.NewGuid():N}");
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
}

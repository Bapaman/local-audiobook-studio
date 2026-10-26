using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace LocalAudiobookStudio.App.Lifetime;

public enum InstanceRole
{
    Primary,
    Secondary,
}

public sealed record SingleInstanceCoordinatorOptions(
    TimeSpan ConnectTimeout,
    TimeSpan RetryDelay,
    TimeSpan CommandReadTimeout)
{
    public static SingleInstanceCoordinatorOptions Default { get; } =
        new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2));
}

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    public const string DefaultInstanceName = "LocalAudiobookStudio.App.v1";
    private readonly string _instanceName;
    private readonly SingleInstanceCoordinatorOptions _options;
    private Mutex? _marker;
    private InstanceCommandServer? _server;
    private int _started;

    public SingleInstanceCoordinator(
        string instanceName,
        SingleInstanceCoordinatorOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        _instanceName = instanceName;
        _options = options ?? SingleInstanceCoordinatorOptions.Default;
        ValidateOptions(_options);
        PipeName = CreatePipeName(instanceName);
    }

    internal string PipeName { get; }

    public async Task<InstanceRole> StartAsync(
        Func<CancellationToken, Task> onOpen,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onOpen);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The instance coordinator has already started.");
        }

        var markerOptions = new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = true,
        };
        var marker = new Mutex(false, _instanceName, markerOptions, out var createdNew);
        if (!createdNew)
        {
            marker.Dispose();
            await SendOpenAsync(cancellationToken);
            return InstanceRole.Secondary;
        }

        _marker = marker;
        _server = new InstanceCommandServer(PipeName, _options.CommandReadTimeout);
        try
        {
            await _server.StartAsync(onOpen, cancellationToken);
            return InstanceRole.Primary;
        }
        catch
        {
            await _server.DisposeAsync();
            _server = null;
            _marker.Dispose();
            _marker = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }

        _marker?.Dispose();
        _marker = null;
    }

    private async Task SendOpenAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (stopwatch.Elapsed < _options.ConnectTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = _options.ConnectTimeout - stopwatch.Elapsed;
            var attempt = remaining < TimeSpan.FromMilliseconds(100)
                ? remaining
                : TimeSpan.FromMilliseconds(100);
            if (attempt <= TimeSpan.Zero)
            {
                break;
            }

            await using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await client.ConnectAsync(
                    Math.Max(1, (int)Math.Ceiling(attempt.TotalMilliseconds)),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
            {
                lastFailure = exception;
                remaining = _options.ConnectTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var retryDelay = remaining < _options.RetryDelay ? remaining : _options.RetryDelay;
                await Task.Delay(retryDelay, cancellationToken);
                continue;
            }

            remaining = _options.ConnectTimeout - stopwatch.Elapsed;
            using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeCancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
            await client.WriteAsync(InstanceCommandServer.OpenCommand, writeCancellation.Token);
            await client.FlushAsync(writeCancellation.Token);
            return;
        }

        throw new TimeoutException("The primary application instance did not accept the open command.", lastFailure);
    }

    private static string CreatePipeName(string instanceName)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(instanceName));
        var suffix = Convert.ToHexString(digest.AsSpan(0, 12));
        return $"LocalAudiobookStudio.{Process.GetCurrentProcess().SessionId}.{suffix}";
    }

    private static void ValidateOptions(SingleInstanceCoordinatorOptions options)
    {
        if (options.ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Connect timeout must be positive.");
        }

        if (options.RetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retry delay must be positive.");
        }

        if (options.CommandReadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Command read timeout must be positive.");
        }
    }
}

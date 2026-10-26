using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace LocalAudiobookStudio.App.Lifetime;

internal sealed class InstanceCommandServer : IAsyncDisposable
{
    internal static readonly byte[] OpenCommand = Encoding.ASCII.GetBytes("LAS/1 open\n");
    private const int MaximumCommandBytes = 32;
    private readonly string _pipeName;
    private readonly TimeSpan _readTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, byte> _clients = new();
    private Task? _acceptLoop;

    public InstanceCommandServer(string pipeName, TimeSpan readTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (readTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readTimeout));
        }

        _pipeName = pipeName;
        _readTimeout = readTimeout;
    }

    public async Task StartAsync(
        Func<CancellationToken, Task> onOpen,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onOpen);
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The command server has already started.");
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _acceptLoop = AcceptLoopAsync(onOpen, ready, _stopping.Token);
        try
        {
            await ready.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            _stopping.Cancel();
            await ObserveAsync(_acceptLoop);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (_acceptLoop is not null)
        {
            await ObserveAsync(_acceptLoop);
        }

        var clients = _clients.Keys.ToArray();
        await Task.WhenAll(clients.Select(ObserveAsync));
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync(
        Func<CancellationToken, Task> onOpen,
        TaskCompletionSource ready,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                ready.TrySetResult();
                await server.WaitForConnectionAsync(cancellationToken);

                var client = HandleClientAsync(server, onOpen, cancellationToken);
                server = null;
                _clients.TryAdd(client, 0);
                _ = client.ContinueWith(
                    completed => _clients.TryRemove(completed, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                server?.Dispose();
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                server?.Dispose();
                if (!ready.Task.IsCompleted)
                {
                    ready.TrySetException(new IOException("The local instance command pipe could not start."));
                    return;
                }
            }
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream server,
        Func<CancellationToken, Task> onOpen,
        CancellationToken stoppingToken)
    {
        await using (server)
        {
            using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            readCancellation.CancelAfter(_readTimeout);
            var buffer = new byte[MaximumCommandBytes + 1];
            var count = 0;
            try
            {
                while (count < buffer.Length)
                {
                    var read = await server.ReadAsync(
                        buffer.AsMemory(count, buffer.Length - count),
                        readCancellation.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    count += read;
                }

                if (count == OpenCommand.Length
                    && buffer.AsSpan(0, count).SequenceEqual(OpenCommand))
                {
                    await onOpen(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            catch (Exception)
            {
                // Browser callback failures must not fault a detached client task.
            }
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }
}

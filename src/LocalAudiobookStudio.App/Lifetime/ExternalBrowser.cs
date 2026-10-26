using System.Diagnostics;

namespace LocalAudiobookStudio.App.Lifetime;

internal sealed class ExternalBrowser : IExternalBrowser
{
    private readonly Func<ProcessStartInfo, Process?> _start;

    public ExternalBrowser() : this(Process.Start)
    {
    }

    internal ExternalBrowser(Func<ProcessStartInfo, Process?> start)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public Task OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        LaunchUriFactory.ValidateLaunchUri(uri);
        cancellationToken.ThrowIfCancellationRequested();
        var process = _start(
            new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
        process?.Dispose();
        return Task.CompletedTask;
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace LocalAudiobookStudio.App.Lifetime;

/// <summary>Explicit synthetic-root acceptance channel; normal launch always uses ExternalBrowser.</summary>
internal sealed class SyntheticLaunchBrowser : IExternalBrowser
{
    private readonly string pipeName;

    public SyntheticLaunchBrowser(string pipeName)
    {
        string prefix = $"LAS.SyntheticLaunch.{Process.GetCurrentProcess().SessionId}.";
        if (!pipeName.StartsWith(prefix, StringComparison.Ordinal) || pipeName.Length != prefix.Length + 32
            || !pipeName.AsSpan(prefix.Length).ToArray().All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new ArgumentException("Invalid synthetic launch channel.");
        this.pipeName = pipeName;
    }

    public async Task OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        LaunchUriFactory.ValidateLaunchUri(uri);
        byte[] payload = Encoding.ASCII.GetBytes(uri.AbsoluteUri + "\n");
        if (payload.Length > 2048) throw new ArgumentException("Invalid synthetic launch message.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(payload, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
    }
}

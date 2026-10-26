using System.Text;

namespace LocalAudiobookStudio.App.Lifetime;

internal enum StartupEventCode
{
    AppStartFailed,
    BrowserOpenFailed,
    HostStartFailed,
}

internal sealed class StartupErrorSink
{
    internal const int MaximumBytes = 64 * 1024;
    private static readonly IReadOnlyDictionary<StartupEventCode, string> EventNames =
        new Dictionary<StartupEventCode, string>
        {
            [StartupEventCode.AppStartFailed] = "app_start_failed",
            [StartupEventCode.BrowserOpenFailed] = "browser_open_failed",
            [StartupEventCode.HostStartFailed] = "host_start_failed",
        };
    private readonly Lock _gate = new();

    public StartupErrorSink(string logsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        LogPath = Path.Combine(Path.GetFullPath(logsRoot), "startup.log");
    }

    internal string LogPath { get; }

    public void Record(StartupEventCode code, Exception? ignoredException = null)
    {
        _ = ignoredException;
        if (!EventNames.TryGetValue(code, out var eventName))
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var line = Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O} {eventName}{Environment.NewLine}");
                var existingLength = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
                var mode = existingLength + line.Length <= MaximumBytes
                    ? FileMode.Append
                    : FileMode.Create;
                using var stream = new FileStream(
                    LogPath,
                    mode,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4_096,
                    FileOptions.WriteThrough);
                stream.Write(line);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Startup diagnostics are best effort and intentionally never echo exception details.
        }
    }
}

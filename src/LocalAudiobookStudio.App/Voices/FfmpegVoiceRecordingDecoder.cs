using System.Diagnostics;
using System.Globalization;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Voices;
using LocalAudiobookStudio.Core.Voices;
using LocalAudiobookStudio.WorkerHost.Engine;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.App.Voices;

internal sealed class FfmpegVoiceRecordingDecoder(string executable, string stagingDirectory, VoiceRecordingPolicy policy) : IVoiceRecordingDecoder
{
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly Lazy<string> staging = new(() => PrepareStaging(stagingDirectory));
    private bool stopping;

    public async Task<Stream> DecodeAsync(Stream content, VoiceMessageFormat format, CancellationToken cancellationToken)
    {
        if (!await admission.WaitAsync(0, cancellationToken)) throw new StudioException("voice.decoder.busy");
        string? input = null;
        string? output = null;
        bool outputOpened = false;
        try
        {
            if (stopping) throw new StudioException("voice.decoder.unavailable");
            input = Path.Combine(staging.Value, $"recording-{Guid.NewGuid():N}.tmp");
            await using (var upload = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await content.CopyToAsync(upload, cancellationToken);

            output = Path.Combine(staging.Value, $"recording-{Guid.NewGuid():N}.tmp");
            await ConvertAsync(input, output, format, cancellationToken);
            var wav = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            outputOpened = true;
            return wav;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new StudioException("voice.decoder.unavailable");
        }
        finally
        {
            if (input is not null) DeleteTemporaryFile(input);
            if (!outputOpened && output is not null) DeleteTemporaryFile(output);
            admission.Release();
        }
    }

    internal async Task FinishAsync()
    {
        await admission.WaitAsync();
        try { stopping = true; }
        finally { admission.Release(); }
    }

    private async Task ConvertAsync(string input, string output, VoiceMessageFormat format, CancellationToken cancellationToken)
    {
        var container = format switch
        {
            VoiceMessageFormat.Ogg => "ogg",
            VoiceMessageFormat.M4a => "mov",
            VoiceMessageFormat.Mp3 => "mp3",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(input)!,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-xerror", "-max_alloc", "67108864", "-y",
                "-protocol_whitelist", "file", "-f", container,
            },
        };
        if (format == VoiceMessageFormat.M4a)
            foreach (string argument in new[] { "-enable_drefs", "0", "-use_absolute_path", "0" })
                start.ArgumentList.Add(argument);

        // Keep one extra second so an over-limit recording fails the normal WAV duration check.
        string limit = (policy.MaximumSamples / 48_000d + 1).ToString("0.000000", CultureInfo.InvariantCulture);
        foreach (string argument in new[]
        {
            "-i", input, "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
            "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", "-t", limit, "-f", "wav", output,
        })
            start.ArgumentList.Add(argument);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        deadline.Token.ThrowIfCancellationRequested();
        try
        {
            using var child = WindowsNativeLauncher.Start(start, new NativeJobMemoryPolicy(512L << 20));
            var exit = child.WaitForExitAsync();
            try
            {
                await Task.WhenAll(exit, child.StandardOutput.BaseStream.CopyToAsync(Stream.Null),
                    child.StandardError.BaseStream.CopyToAsync(Stream.Null)).WaitAsync(deadline.Token);
                int code = await exit;
                if ((unchecked((uint)code) & 0xFFFF0000) == 0xC0000000)
                    throw new StudioException("voice.decoder.unavailable");
                if (code != 0) throw new StudioException("voice.decoder.invalid");
            }
            finally
            {
                await StopOwnedProcessAsync(child.StopTreeAsync);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StudioException("voice.decoder.timeout");
        }
        catch (NativeOwnedLaunchException error)
        {
            using var owner = error.Ownership;
            await StopOwnedProcessAsync(owner.StopTreeAsync);
            throw new StudioException("voice.decoder.unavailable");
        }
        catch (Exception error) when (error is WorkerException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            throw new StudioException("voice.decoder.unavailable");
        }
    }

    private static async Task StopOwnedProcessAsync(Func<Task> stop)
    {
        while (true)
        {
            try { await stop(); return; }
            catch (Exception error) when (error is WorkerException or TimeoutException)
            { await Task.Delay(TimeSpan.FromMilliseconds(250)); }
        }
    }

    private static string PrepareStaging(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new StudioException("voice.decoder.unavailable");
        // The Windows host is single-instance for this data directory. Only recover names
        // generated by the decoder after a previous instance exited.
        foreach (string file in Directory.EnumerateFiles(path, "recording-*.tmp"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("recording-", StringComparison.Ordinal) && Guid.TryParseExact(name[10..], "N", out _))
                DeleteTemporaryFile(file);
        }
        return path;
    }

    private static void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

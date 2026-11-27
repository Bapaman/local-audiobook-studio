using LocalAudiobookStudio.App.Voices;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Voices;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.App.Tests;

public sealed class FfmpegVoiceRecordingDecoderTests
{
    [Fact]
    public async Task Upload_owns_admission_until_cancellation_removes_staging_and_shutdown_can_finish()
    {
        var token = TestContext.Current.CancellationToken;
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(token);
        string staging = Directory.CreateTempSubdirectory("las-voice-import-").FullName;
        string recovered = Path.Combine(staging, $"recording-{Guid.NewGuid():N}.tmp");
        string unrelated = Path.Combine(staging, "unrelated.tmp");
        File.WriteAllText(recovered, "recover");
        File.WriteAllText(unrelated, "preserve");
        var decoder = new FfmpegVoiceRecordingDecoder(Path.Combine(staging, "unused.exe"), staging, new VoiceRecordingPolicy());
        using var upload = new BlockedUpload();
        var first = decoder.DecodeAsync(upload, VoiceMessageFormat.Ogg, abort.Token);
        try
        {
            await upload.Started.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(File.Exists(recovered));
            Assert.True(File.Exists(unrelated));
            Assert.Single(Directory.GetFiles(staging, "recording-*.tmp"));

            var other = await Assert.ThrowsAsync<StudioException>(() => decoder.DecodeAsync(Stream.Null, VoiceMessageFormat.Mp3, token));
            Assert.Equal("voice.decoder.busy", other.Code);
            var shutdown = decoder.FinishAsync();
            Assert.False(shutdown.IsCompleted);

            abort.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Empty(Directory.GetFiles(staging, "recording-*.tmp"));
            var closed = await Assert.ThrowsAsync<StudioException>(() => decoder.DecodeAsync(Stream.Null, VoiceMessageFormat.Ogg, token));
            Assert.Equal("voice.decoder.unavailable", closed.Code);
        }
        finally
        {
            abort.Cancel();
            try { await first; }
            catch (OperationCanceledException) { }
            File.Delete(recovered);
            File.Delete(unrelated);
            Directory.Delete(staging);
        }
    }

    private sealed class BlockedUpload : MemoryStream
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => started.Task;

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}

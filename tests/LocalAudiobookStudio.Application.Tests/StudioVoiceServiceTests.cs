using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Voices;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class StudioVoiceServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static StudioVoiceService Service(CatalogSandbox box) => new(box.Service(), new VoiceRecordingService(new(48000, 1440000)));

    [Fact]
    public async Task Guarded_upload_confirmation_and_restart_map_recording_and_range_without_paths()
    {
        using var box = new CatalogSandbox(); var created = await box.Service().CreateAsync(new("Synthetic"), "key", Token);
        using var stream = Wav();
        var uploaded = await Service(box).UploadRecordingAsync(created.Snapshot.Id, created.Snapshot.Revision, new("synthetic.wav", true), stream, Token);
        Assert.NotNull(uploaded.VoiceRecording); Assert.Null(uploaded.VoiceAnchor); Assert.Equal(2880044, uploaded.VoiceRecording.ByteLength); Assert.Equal(30000, uploaded.VoiceRecording.DurationMilliseconds);
        var recordingOnly = Assert.Single(await box.Service().ListAsync(Token)); Assert.True(recordingOnly.HasRecording); Assert.False(recordingOnly.HasAnchor);
        const string transcript = "  Синтетическая строка\r\n";
        var accepted = await Service(box).ConfirmAnchorAsync(uploaded.Id, uploaded.Revision, new(1000, 11000, transcript, true), Token);
        Assert.Equal(1000, accepted.VoiceAnchor!.StartMilliseconds); Assert.Equal(11000, accepted.VoiceAnchor.EndMilliseconds); Assert.Equal(transcript, accepted.VoiceAnchor.Transcript);
        var restarted = await box.Service().GetAsync(uploaded.Id, Token);
        Assert.Equal(accepted.VoiceRecording, restarted.VoiceRecording); Assert.Equal(accepted.VoiceAnchor, restarted.VoiceAnchor);
        var listed = Assert.Single(await box.Service().ListAsync(Token)); Assert.True(listed.HasRecording); Assert.True(listed.HasAnchor);
        Assert.DoesNotContain("RelativePath", JsonSerializer.Serialize(restarted)); Assert.DoesNotContain(box.Root, JsonSerializer.Serialize(restarted));
        Assert.Equal("voice.exists", (await Assert.ThrowsAsync<StudioException>(() => Service(box).ConfirmAnchorAsync(uploaded.Id, accepted.Revision, new(0, 10000, "second", true), Token))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guarded_v1_list_get_open_or_upload_upgrade_preserve_voice_and_text(bool upload)
    {
        using var box = new CatalogSandbox(); var created = await box.Service().CreateAsync(new("Synthetic"), "key", Token);
        await using (var project = await new ProjectService().OpenAsync(box.ProjectRoot(created.Snapshot.Id), Token))
        {
            var now = DateTimeOffset.UtcNow;
            await project.SaveAsync(project.State with { Voice = new("old", "voice-snapshot/old.wav", "synthetic", "old", "old", "old", true, now), Chapters = [new("chapter", 1, "Synthetic", "original", "normalized", "spoken", now)] }, Token);
        }
        CatalogSandbox.Sql(box.ProjectRoot(created.Snapshot.Id), "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs; DROP TABLE IF EXISTS voice_recording; PRAGMA user_version=1");
        string database = Path.Combine(box.ProjectRoot(created.Snapshot.Id), "project.sqlite"); var before = File.ReadAllBytes(database);
        var old = await box.Service().GetAsync(created.Snapshot.Id, Token); Assert.Null(old.VoiceRecording); Assert.Null(old.VoiceAnchor);
        Assert.Single(await box.Service().ListAsync(Token)); Assert.Equal(before, File.ReadAllBytes(database));
        using var stream = Wav();
        var upgraded = upload ? await Service(box).UploadRecordingAsync(old.Id, old.Revision, new("source.wav", true), stream, Token) : await box.Service().OpenAsync(old.Id, Token);
        Assert.Equal(upload, upgraded.VoiceRecording != null);
        var state = await new ProjectService().ProbeAsync(box.ProjectRoot(old.Id), Token); Assert.Equal("synthetic", state.Voice!.Transcript); Assert.Equal("original", Assert.Single(state.Chapters).OriginalText);
    }

    [Fact]
    public async Task Invalid_id_and_stale_revision_do_not_consume_content()
    {
        using var box = new CatalogSandbox(); using var content = Wav(); var service = Service(box);
        Assert.Equal("project.id.invalid", (await Assert.ThrowsAsync<StudioException>(() => service.UploadRecordingAsync("../bad", 1, new("source.wav", true), content, Token))).Code);
        var created = await box.Service().CreateAsync(new("Synthetic"), "key", Token);
        Assert.Equal("project.conflict", (await Assert.ThrowsAsync<StudioException>(() => service.UploadRecordingAsync(created.Snapshot.Id, created.Snapshot.Revision - 1, new("source.wav", true), content, Token))).Code);
        Assert.Equal(0, content.Position); Assert.Null((await box.Service().GetAsync(created.Snapshot.Id, Token)).VoiceRecording);
    }

    [Fact]
    public async Task Duplicate_upload_is_guarded_and_original_recording_remains_accepted()
    {
        using var box = new CatalogSandbox(); var created = await box.Service().CreateAsync(new("Synthetic"), "key", Token);
        using var first = Wav(); var uploaded = await Service(box).UploadRecordingAsync(created.Snapshot.Id, created.Snapshot.Revision, new("source.wav", true), first, Token);
        using var duplicate = Wav();
        Assert.Equal("voice.recording.exists", (await Assert.ThrowsAsync<StudioException>(() => Service(box).UploadRecordingAsync(uploaded.Id, uploaded.Revision, new("second.wav", true), duplicate, Token))).Code);
        Assert.Equal(0, duplicate.Position); Assert.Equal(uploaded.VoiceRecording, (await box.Service().GetAsync(uploaded.Id, Token)).VoiceRecording);
    }

    private static MemoryStream Wav()
    {
        var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("RIFF"u8); writer.Write(2880036u); writer.Write("WAVEfmt "u8); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write(48000u); writer.Write(96000u); writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(2880000u);
            for (int i = 0; i < 1440000; i++) writer.Write((short)1000);
        }
        stream.Position = 0; return stream;
    }
}

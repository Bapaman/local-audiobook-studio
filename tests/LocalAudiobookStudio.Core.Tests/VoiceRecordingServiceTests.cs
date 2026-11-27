using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Voices;
using Microsoft.Data.Sqlite;

namespace LocalAudiobookStudio.Core.Tests;

public sealed class VoiceRecordingServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static VoiceRecordingService Service(long bytes = 1L << 30) => new(new(48000, 1440000, bytes));

    [Theory]
    [InlineData(239_999, "voice.recording.too_short")]
    [InlineData(240_000, null)]
    [InlineData(2_879_999, null)]
    [InlineData(14_400_000, null)]
    [InlineData(57600000, null)]
    [InlineData(57600001, "voice.recording.too_long")]
    public void Default_policy_uses_inclusive_integer_sample_boundaries(long samples, string? code)
    {
        var inspection = new WavInspection(1, 48000, 16, 2, 44, samples * 2, samples, TimeSpan.Zero, 0, false, "");
        if (code == null) new VoiceRecordingPolicy().Validate(inspection);
        else Assert.Equal(code, Assert.Throws<ProjectException>(() => new VoiceRecordingPolicy().Validate(inspection)).Code);
    }

    [Theory]
    [InlineData(5000, 0, 5000, null)]
    [InlineData(8267, 0, 8267, null)]
    [InlineData(8267, 1000, 8267, "voice.anchor.range")]
    [InlineData(8267, 0, 8000, "voice.anchor.range")]
    public async Task Short_recording_requires_its_full_range_for_the_voice_sample(int duration, int start, int end, string? code)
    {
        using var box = new ProjectSandbox();
        var projects = new ProjectService();
        await using var project = await projects.CreateAsync(box.ProjectRoot, "Synthetic", Token);
        using var stream = File.OpenRead(VoiceProfileServiceTests.WriteWav(box.Root, duration * 48));
        var service = new VoiceRecordingService();
        await service.ImportAsync(project, new("short.wav", true), stream, Token);
        var command = new ConfirmVoiceAnchor(start, end, "Пять секунд речи.", true);
        if (code != null)
        {
            Assert.Equal(code, (await Assert.ThrowsAsync<ProjectException>(() => service.ConfirmAnchorAsync(project, command, Token))).Code);
            Assert.Null(project.State.Voice);
            return;
        }

        var anchor = await service.ConfirmAnchorAsync(project, command, Token);
        await using var reopened = await projects.OpenAsync(box.ProjectRoot, Token);
        Assert.Equal(anchor, reopened.State.Voice);
        Assert.Equal(duration * 48, (await WavInspector.InspectAsync(project.ResolveOwnedPath(anchor.WavRelativePath), Token)).SampleCount);
    }

    [Fact]
    public async Task Clipped_recording_is_preserved_reopened_protected_from_recovery_and_stale_saves()
    {
        using var box = new ProjectSandbox(); var core = new ProjectService();
        await using var project = await core.CreateAsync(box.ProjectRoot, "Synthetic", Token);
        await using var stale = await core.OpenAsync(box.ProjectRoot, Token);
        string source = VoiceProfileServiceTests.WriteWav(box.Root, 48000, short.MaxValue);
        byte[] bytes = File.ReadAllBytes(source); using var content = new MemoryStream(bytes);
        var recording = await Service().ImportAsync(project, new("renamed.mp3", true), content, Token);
        Assert.True(recording.HasClippingWarning); Assert.Equal(bytes.Length, recording.ByteLength); Assert.Equal(48000, recording.SampleCount);
        Assert.Equal(bytes, File.ReadAllBytes(source)); Assert.Equal(bytes, File.ReadAllBytes(project.ResolveOwnedPath(recording.WavRelativePath)));
        Assert.Equal("voice.recording.exists", (await Assert.ThrowsAsync<ProjectException>(() => stale.SaveAsync(stale.State, Token))).Code);
        using var duplicate = new MemoryStream(bytes);
        Assert.Equal("voice.recording.exists", (await Assert.ThrowsAsync<ProjectException>(() => Service().ImportAsync(stale, new("again.wav", true), duplicate, Token))).Code);
        await project.RegisterPendingArtifactAsync(recording.WavRelativePath + ".part", recording.WavRelativePath, Token);
        await core.RecoverInterruptedAsync(project, Token);
        await using var reopened = await core.OpenAsync(box.ProjectRoot, Token);
        Assert.Equal(recording, reopened.State.VoiceRecording); Assert.Equal(bytes, File.ReadAllBytes(project.ResolveOwnedPath(recording.WavRelativePath)));
        Assert.Empty(Directory.GetFiles(box.ProjectRoot, "*.part", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("m4a", "wav.format")]
    [InlineData("mp3", "wav.format")]
    [InlineData("stereo", "wav.format")]
    [InlineData("rate", "wav.format")]
    [InlineData("float", "wav.format")]
    [InlineData("truncated", "wav.truncated")]
    [InlineData("growing", "wav.truncated")]
    [InlineData("limit", "voice.recording.too_large")]
    public async Task Rejects_invalid_streams_and_cleans_registered_artifacts(string kind, string code)
    {
        using var box = new ProjectSandbox(); await using var project = await new ProjectService().CreateAsync(box.ProjectRoot, "Synthetic", Token);
        byte[] bytes = File.ReadAllBytes(VoiceProfileServiceTests.WriteWav(box.Root, 48000, 1000, (ushort)(kind == "stereo" ? 2 : 1), kind == "rate" ? 44100u : 48000u));
        if (kind is "mp3" or "m4a") bytes = System.Text.Encoding.ASCII.GetBytes(kind == "mp3" ? "ID3 synthetic mp3 bytes" : "synthetic ftyp M4A bytes");
        if (kind == "float") bytes[20] = 3;
        if (kind == "truncated") bytes = bytes[..^1];
        if (kind == "growing") bytes = [.. bytes, 0, 0];
        using var stream = new MemoryStream(bytes);
        Assert.Equal(code, (await Assert.ThrowsAsync<ProjectException>(() => Service(kind == "limit" ? 100 : 1L << 30).ImportAsync(project, new("looks-valid.wav", true), stream, Token))).Code);
        Assert.Null(project.State.VoiceRecording); Assert.Empty(Directory.GetFiles(box.ProjectRoot, "*.wav*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false, 100000)]
    [InlineData(true, 100000)]
    [InlineData(false, 100)]
    public async Task Nonseekable_short_reads_are_bounded_and_growing_stream_is_rejected(bool grow, long limit)
    {
        using var box = new ProjectSandbox(); await using var project = await new ProjectService().CreateAsync(box.ProjectRoot, "Synthetic", Token);
        byte[] bytes = File.ReadAllBytes(VoiceProfileServiceTests.WriteWav(box.Root, 48000));
        using var stream = new NonseekableStream(bytes, grow);
        if (!grow && limit == 100000)
            Assert.Equal(bytes.Length, (await Service(limit).ImportAsync(project, new("renamed.m4a", true), stream, Token)).ByteLength);
        else
        {
            Assert.Equal(grow ? "wav.truncated" : "voice.recording.too_large", (await Assert.ThrowsAsync<ProjectException>(() => Service(limit).ImportAsync(project, new("source.wav", true), stream, Token))).Code);
            Assert.Null(project.State.VoiceRecording); Assert.Empty(Directory.GetFiles(box.ProjectRoot, "*.wav*", SearchOption.AllDirectories));
        }
        Assert.True(stream.BytesRead <= limit + 1);
    }

    [Fact]
    public async Task Cancellation_during_streaming_cannot_publish()
    {
        using var box = new ProjectSandbox(); await using var project = await new ProjectService().CreateAsync(box.ProjectRoot, "Synthetic", Token);
        var bytes = File.ReadAllBytes(VoiceProfileServiceTests.WriteWav(box.Root, 48000));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var stream = new CallbackStream(bytes, () => canceled.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ImportAsync(project, new("source.wav", true), stream, canceled.Token));
        Assert.Null(project.State.VoiceRecording); Assert.Empty(Directory.GetFiles(box.ProjectRoot, "*.wav*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Revision_is_rechecked_after_streaming_before_atomic_acceptance()
    {
        using var box = new ProjectSandbox(); await using var project = await new ProjectService().CreateAsync(box.ProjectRoot, "Synthetic", Token);
        long expected = project.State.Revision;
        using var stream = new CallbackStream(File.ReadAllBytes(VoiceProfileServiceTests.WriteWav(box.Root, 48000)), () => Sql(box.ProjectRoot, "UPDATE project SET revision=revision+1"));
        Assert.Equal("project.conflict", (await Assert.ThrowsAsync<ProjectException>(() => Service().ImportAsync(project, new("source.wav", true), stream, expected, Token))).Code);
        Assert.Null((await new ProjectService().ProbeAsync(box.ProjectRoot, Token)).VoiceRecording);
        Assert.Empty(Directory.GetFiles(box.ProjectRoot, "*.wav*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Concurrent_connections_accept_one_recording_and_cannot_replace_any_metadata()
    {
        using var box = new ProjectSandbox(); var core = new ProjectService();
        await using var first = await core.CreateAsync(box.ProjectRoot, "Synthetic", Token); await using var second = await core.OpenAsync(box.ProjectRoot, Token);
        byte[] bytes = File.ReadAllBytes(VoiceProfileServiceTests.WriteWav(box.Root, 48000));
        using var a = new MemoryStream(bytes); using var b = new MemoryStream(bytes);
        var accepting = Service().ImportAsync(first, new("first.wav", true), a, Token);
        var rejecting = Service().ImportAsync(second, new("second.wav", true), b, Token);
        var accepted = await accepting;
        Assert.Equal("voice.recording.exists", (await Assert.ThrowsAsync<ProjectException>(() => rejecting)).Code);
        Assert.Equal("voice.recording.exists", (await Assert.ThrowsAsync<ProjectException>(() => first.SaveAsync(first.State with { VoiceRecording = accepted with { HasClippingWarning = true } }, Token))).Code);
        Assert.Single(Directory.GetFiles(box.ProjectRoot, "*.wav", SearchOption.AllDirectories));
        Assert.Equal(accepted, (await core.ProbeAsync(box.ProjectRoot, Token)).VoiceRecording);
    }

    [Fact]
    public async Task Clipping_outside_selection_warns_but_unclipped_anchor_is_accepted()
    {
        using var box = new ProjectSandbox(); await using var project = await new ProjectService().CreateAsync(box.ProjectRoot, "Synthetic", Token);
        string source = VoiceProfileServiceTests.WriteWav(box.Root, 960000);
        using (var file = File.OpenWrite(source)) { file.Position = 44 + 720000 * 2; file.Write(BitConverter.GetBytes(short.MaxValue)); }
        using var content = File.OpenRead(source); var service = Service();
        var recording = await service.ImportAsync(project, new("source.wav", true), content, Token);
        Assert.True(recording.HasClippingWarning);
        var anchor = await service.ConfirmAnchorAsync(project, new(0, 10000, "synthetic", true), Token);
        Assert.False((await WavInspector.InspectAsync(project.ResolveOwnedPath(anchor.WavRelativePath), Token)).IsClipped);
    }

    [Fact]
    public async Task Changed_accepted_source_is_detected_before_anchor_publication()
    {
        using var box = new ProjectSandbox(); await using var project = await new ProjectService().CreateAsync(box.ProjectRoot, "Synthetic", Token);
        using var content = File.OpenRead(VoiceProfileServiceTests.WriteWav(box.Root, 480000)); var service = Service();
        var recording = await service.ImportAsync(project, new("source.wav", true), content, Token);
        using (var file = File.OpenWrite(project.ResolveOwnedPath(recording.WavRelativePath))) { file.Position = 44; file.Write(BitConverter.GetBytes((short)42)); }
        Assert.Equal("voice.recording.changed", (await Assert.ThrowsAsync<ProjectException>(() => service.ConfirmAnchorAsync(project, new(0, 10000, "synthetic", true), Token))).Code);
        Assert.Null(project.State.Voice); Assert.Empty(Directory.GetFiles(box.ProjectRoot, "*.part", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("hash", "project.schema.invalid")]
    [InlineData("timestamp", "project.timestamp")]
    [InlineData("path", "project.schema.invalid")]
    [InlineData("samples", "project.schema.invalid")]
    public async Task V2_read_probe_rejects_corrupt_recording_metadata_without_migration(string field, string code)
    {
        using var box = new ProjectSandbox(); var core = new ProjectService();
        await using (var project = await core.CreateAsync(box.ProjectRoot, "Synthetic", Token))
        {
            using var content = File.OpenRead(VoiceProfileServiceTests.WriteWav(box.Root, 48000)); await Service().ImportAsync(project, new("source.wav", true), content, Token);
        }
        string update = field switch
        {
            "hash" => "json_set(payload,'$.Sha256','bad')",
            "timestamp" => "json_set(payload,'$.ImportedUtc','2026-10-08T10:00:00+03:00')",
            "path" => "json_set(payload,'$.WavRelativePath','other/synthetic.wav')",
            _ => "json_set(payload,'$.SampleCount',0)"
        };
        Sql(box.ProjectRoot, "UPDATE voice_recording SET payload=" + update);
        byte[] before = File.ReadAllBytes(Path.Combine(box.ProjectRoot, "project.sqlite"));
        Assert.Equal(code, (await Assert.ThrowsAsync<ProjectException>(() => core.ProbeAsync(box.ProjectRoot, Token))).Code);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(box.ProjectRoot, "project.sqlite")));
    }

    [Theory]
    [InlineData(10000, null)]
    [InlineData(20000, null)]
    [InlineData(9999, "voice.anchor.range")]
    [InlineData(20001, "voice.anchor.range")]
    public async Task Anchor_boundaries_preserve_exact_transcript_ranges_and_source(int duration, string? code)
    {
        using var box = new ProjectSandbox(); var core = new ProjectService(); await using var project = await core.CreateAsync(box.ProjectRoot, "Synthetic", Token);
        byte[] bytes = File.ReadAllBytes(VoiceProfileServiceTests.WriteWav(box.Root, 1440000)); using var stream = new MemoryStream(bytes);
        var service = Service(); var recording = await service.ImportAsync(project, new("source.wav", true), stream, Token);
        const string transcript = "  Синтетический текст!\r\n\t";
        if (code != null) Assert.Equal(code, (await Assert.ThrowsAsync<ProjectException>(() => service.ConfirmAnchorAsync(project, new(1000, 1000 + duration, transcript, true), Token))).Code);
        else
        {
            var voice = await service.ConfirmAnchorAsync(project, new(1000, 1000 + duration, transcript, true), Token);
            Assert.Equal(transcript, voice.Transcript); Assert.Equal(1000, voice.StartMilliseconds); Assert.Equal(1000 + duration, voice.EndMilliseconds);
            Assert.Equal(duration * 48L, (await WavInspector.InspectAsync(project.ResolveOwnedPath(voice.WavRelativePath), Token)).SampleCount);
            Assert.Equal("voice.exists", (await Assert.ThrowsAsync<ProjectException>(() => service.ConfirmAnchorAsync(project, new(0, 10000, "other", true), Token))).Code);
            await using var reopened = await core.OpenAsync(box.ProjectRoot, Token); Assert.Equal(voice, reopened.State.Voice);
        }
        Assert.Equal(bytes, File.ReadAllBytes(project.ResolveOwnedPath(recording.WavRelativePath)));
        Assert.Empty(Directory.GetFiles(box.ProjectRoot, "*.part", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false, "text", "voice.anchor.confirmation")]
    [InlineData(true, " \r\n", "voice.transcript")]
    [InlineData(true, "text", "voice.clipped")]
    public async Task Requires_confirmed_pair_nonblank_transcript_and_unclipped_selection(bool confirmed, string transcript, string code)
    {
        using var box = new ProjectSandbox(); await using var project = await new ProjectService().CreateAsync(box.ProjectRoot, "Synthetic", Token);
        using var stream = File.OpenRead(VoiceProfileServiceTests.WriteWav(box.Root, 480000, short.MaxValue));
        var service = Service(); var recording = await service.ImportAsync(project, new("source.wav", true), stream, Token);
        Assert.Equal(code, (await Assert.ThrowsAsync<ProjectException>(() => service.ConfirmAnchorAsync(project, new(0, 10000, transcript, confirmed), Token))).Code);
        Assert.True(File.Exists(project.ResolveOwnedPath(recording.WavRelativePath))); Assert.Null(project.State.Voice);
        Assert.Empty(Directory.GetFiles(box.ProjectRoot, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task V1_probe_is_read_only_and_open_upgrades_without_losing_voice_or_text()
    {
        using var box = new ProjectSandbox(); var core = new ProjectService(); ProjectState original;
        await using (var project = await core.CreateAsync(box.ProjectRoot, "Synthetic", Token))
        {
            var now = DateTimeOffset.UtcNow;
            await project.SaveAsync(project.State with { Voice = new("v1", "voice-snapshot/old.wav", "synthetic", "old", "old", "old", true, now), Chapters = [new("old", 1, "Synthetic", "original", "normalized", "spoken", now)] }, Token);
            original = project.State;
        }
        Sql(box.ProjectRoot, "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs; DROP TABLE IF EXISTS voice_recording; PRAGMA user_version=1");
        byte[] before = File.ReadAllBytes(Path.Combine(box.ProjectRoot, "project.sqlite"));
        Assert.Equal(original.Voice, (await core.ProbeAsync(box.ProjectRoot, Token)).Voice);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(box.ProjectRoot, "project.sqlite")));
        await using var opened = await core.OpenAsync(box.ProjectRoot, Token);
        Assert.Equal(original.Voice, opened.State.Voice); Assert.Equal(original.Chapters, opened.State.Chapters);
        Assert.Equal(3L, Sql(box.ProjectRoot, "PRAGMA user_version"));
    }

    internal static object? Sql(string root, string sql)
    {
        string path = Path.Combine(root, "project.sqlite");
        if (OperatingSystem.IsWindows()) path = "\\\\?\\" + path;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open();
        using var query = db.CreateCommand(); query.CommandText = sql; return query.ExecuteScalar();
    }

    private sealed class CallbackStream(byte[] bytes, Action action) : MemoryStream(bytes)
    {
        private bool called;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { if (!called) { called = true; action(); } return base.ReadAsync(buffer, token); }
    }

    private sealed class NonseekableStream(byte[] bytes, bool grow) : Stream
    {
        private int position; private bool extra;
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); int count = Math.Min(Math.Min(buffer.Length, 701), bytes.Length - position);
            if (count > 0) { bytes.AsMemory(position, count).CopyTo(buffer); position += count; }
            else if (grow && !extra) { extra = true; count = Math.Min(2, buffer.Length); buffer.Span[..count].Clear(); }
            BytesRead += count; return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

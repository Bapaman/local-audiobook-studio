using System.Security.Cryptography;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Core.Tests;

public class VoiceProfileServiceTests
{
    [Fact]
    public async Task Import_preserves_external_source_and_exact_transcript_and_accepted_snapshot()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        string source = WriteWav(sandbox.Root);
        byte[] before = File.ReadAllBytes(source);
        const string transcript = "Привет, ёж!  \r\n\t";
        var voice = await new VoiceProfileService().ImportPromptAsync(project, new(source, transcript, true), TestContext.Current.CancellationToken);
        Assert.Equal(transcript, voice.Transcript);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(before)), voice.WavHash);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal(before, File.ReadAllBytes(project.ResolveOwnedPath(voice.WavRelativePath)));
        Assert.False(Path.IsPathRooted(voice.WavRelativePath));
        await using var reopened = await new ProjectService().OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Equal(voice, reopened.State.Voice);
        Assert.Empty(Directory.GetFiles(sandbox.ProjectRoot, "*.part", SearchOption.AllDirectories));
        await Assert.ThrowsAsync<ProjectException>(() => new VoiceProfileService().ImportPromptAsync(project, new(source, "other", true), TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllBytes(project.ResolveOwnedPath(voice.WavRelativePath)));
    }

    [Theory]
    [InlineData(false, "text", 144000, 1000, 1, 48000, "voice.rights")]
    [InlineData(true, " \r\n", 144000, 1000, 1, 48000, "voice.transcript")]
    [InlineData(true, "text", 143999, 1000, 1, 48000, "voice.too_short")]
    [InlineData(true, "text", 144000, 32767, 1, 48000, "voice.clipped")]
    [InlineData(true, "text", 144000, -32768, 1, 48000, "voice.clipped")]
    [InlineData(true, "text", 144000, 1000, 2, 48000, "wav.format")]
    [InlineData(true, "text", 144000, 1000, 1, 44100, "wav.format")]
    public async Task Invalid_import_has_stable_code_and_leaves_no_snapshot_or_staging(bool rights, string transcript, int samples, short sample, ushort channels, uint rate, string code)
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        string source = WriteWav(sandbox.Root, samples, sample, channels, rate);
        byte[] before = File.ReadAllBytes(source);
        var error = await Assert.ThrowsAsync<ProjectException>(() => new VoiceProfileService().ImportPromptAsync(project, new(source, transcript, rights), TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code); Assert.Equal(code, error.Message);
        Assert.Null(project.State.Voice); Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(sandbox.ProjectRoot, "*.wav*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cancellation_cleans_staging_and_preserves_source()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        string source = WriteWav(sandbox.Root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VoiceProfileService().ImportPromptAsync(project, new(source, "text", true), new CancellationToken(true)));
        Assert.True(File.Exists(source)); Assert.Null(project.State.Voice);
        Assert.Empty(Directory.GetFiles(sandbox.ProjectRoot, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Snapshot_directory_collision_reports_only_stable_code()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(sandbox.ProjectRoot, "voice-snapshot"), "synthetic");
        string source = WriteWav(sandbox.Root);
        var error = await Assert.ThrowsAsync<ProjectException>(() => new VoiceProfileService().ImportPromptAsync(project, new(source, "text", true), TestContext.Current.CancellationToken));
        Assert.Equal("voice.io", error.Code); Assert.Equal(error.Code, error.Message);
        Assert.Null(project.State.Voice); Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Cancellation_after_staging_creation_cleans_partial_copy_without_publishing()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        string source = WriteWav(sandbox.Root, 48000 * 120);
        string voiceDirectory = Path.Combine(sandbox.ProjectRoot, "voice-snapshot"); Directory.CreateDirectory(voiceDirectory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var watcher = new FileSystemWatcher(voiceDirectory, "*.part");
        watcher.Created += (_, _) => cancellation.Cancel(); watcher.EnableRaisingEvents = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VoiceProfileService().ImportPromptAsync(project, new(source, "text", true), cancellation.Token));
        Assert.Null(project.State.Voice); Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(voiceDirectory));
    }

    [Fact]
    public async Task Truncated_import_is_rejected_and_source_is_unchanged()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        string source = WriteWav(sandbox.Root);
        using (var stream = File.OpenWrite(source)) stream.SetLength(stream.Length - 1);
        byte[] before = File.ReadAllBytes(source);
        Assert.Equal("wav.truncated", (await Assert.ThrowsAsync<ProjectException>(() => new VoiceProfileService().ImportPromptAsync(project, new(source, "text", true), TestContext.Current.CancellationToken))).Code);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(sandbox.ProjectRoot, "*.wav*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Active_source_writer_is_rejected_without_publishing_or_mutating_source()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        string source = WriteWav(sandbox.Root);
        byte[] before = File.ReadAllBytes(source);
        using (var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            Assert.Equal("voice.io", (await Assert.ThrowsAsync<ProjectException>(() => new VoiceProfileService().ImportPromptAsync(project, new(source, "text", true), TestContext.Current.CancellationToken))).Code);
        Assert.Equal(before, File.ReadAllBytes(source)); Assert.Null(project.State.Voice);
        Assert.Empty(Directory.GetFiles(sandbox.ProjectRoot, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Concurrent_imports_accept_exactly_one_snapshot_and_leave_its_file_intact()
    {
        using var sandbox = new ProjectSandbox();
        var projects = new ProjectService();
        await using var first = await projects.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await using var second = await projects.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        string source = WriteWav(sandbox.Root); var voices = new VoiceProfileService();
        Task<LocalAudiobookStudio.Core.Projects.VoiceSnapshot> acceptedTask = voices.ImportPromptAsync(first, new(source, "first", true), TestContext.Current.CancellationToken);
        Task<LocalAudiobookStudio.Core.Projects.VoiceSnapshot> rejectedTask = voices.ImportPromptAsync(second, new(source, "second", true), TestContext.Current.CancellationToken);
        var accepted = await acceptedTask;
        Assert.Equal("voice.exists", (await Assert.ThrowsAsync<ProjectException>(() => rejectedTask)).Code);
        Assert.True(File.Exists(first.ResolveOwnedPath(accepted.WavRelativePath)));
        Assert.Single(Directory.GetFiles(sandbox.ProjectRoot, "*.wav", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(sandbox.ProjectRoot, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Concurrent_connections_cannot_replace_an_accepted_voice_snapshot()
    {
        using var sandbox = new ProjectSandbox();
        var projects = new ProjectService();
        await using var first = await projects.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await using var second = await projects.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        string source = WriteWav(sandbox.Root);
        var service = new VoiceProfileService();
        var accepted = await service.ImportPromptAsync(first, new(source, "first", true), TestContext.Current.CancellationToken);
        Assert.Equal("voice.exists", (await Assert.ThrowsAsync<ProjectException>(() => service.ImportPromptAsync(second, new(source, "second", true), TestContext.Current.CancellationToken))).Code);
        await using var reopened = await projects.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Equal(accepted, reopened.State.Voice);
        Assert.Single(Directory.GetFiles(sandbox.ProjectRoot, "*.wav", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Transcript_bytes_change_snapshot_identity_and_long_prompt_has_no_maximum()
    {
        using var sandbox = new ProjectSandbox();
        string source = WriteWav(sandbox.Root, 48000 * 31);
        await using var first = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "first", TestContext.Current.CancellationToken);
        await using var second = await new ProjectService().CreateAsync(Path.Combine(sandbox.Root, "second"), "second", TestContext.Current.CancellationToken);
        var service = new VoiceProfileService();
        var a = await service.ImportPromptAsync(first, new(source, "text", true), TestContext.Current.CancellationToken);
        var b = await service.ImportPromptAsync(second, new(source, "text ", true), TestContext.Current.CancellationToken);
        Assert.NotEqual(a.IdentityHash, b.IdentityHash); Assert.Equal(a.WavHash, b.WavHash);
    }

    internal static string WriteWav(string root, int samples = 144000, short sample = 1000, ushort channels = 1, uint rate = 48000)
    {
        string path = Path.Combine(root, Guid.NewGuid() + ".wav");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write((uint)(36 + samples * 2)); writer.Write("WAVEfmt "u8);
        writer.Write(16u); writer.Write((ushort)1); writer.Write(channels); writer.Write(rate); writer.Write(rate * channels * 2); writer.Write((ushort)(channels * 2)); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write((uint)(samples * 2)); for (int i = 0; i < samples; i++) writer.Write(sample);
        return path;
    }
}

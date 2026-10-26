using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmInference;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class VoxCpmReferenceCatalogTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static StudioVoxCpmGenerationService Service(VoxCpmTrainingFixture fixture)
        => new(new ProjectJobAccess(new(fixture.Corpus.Box.Root), fixture.Corpus.Box.Catalog(), new()), new ApprovedTtsCorpusResolver());

    [Fact]
    public async Task Catalog_uses_models_approved_version_after_draft_replacement_and_filters_whole_clip_bounds()
    {
        using var fixture = new VoxCpmTrainingFixture();
        await fixture.Corpus.SeedAsync(Token, [80000, 79999, 192000, 256000], []);
        await fixture.StartAsync(Token); var trained = await fixture.AddStepsAsync(1, "paused", Token);
        await fixture.Corpus.ReplaceDraftAsync(Token); fixture.RuntimeProvider.RejectAll = true;

        var catalog = await Service(fixture).GetReferencesAsync(fixture.Id, trained.Models[0].Id, Token);

        Assert.Equal(trained.Models[0].Id, catalog.ModelId);
        Assert.Equal(fixture.Corpus.VersionId, catalog.CorpusVersionId);
        Assert.Equal(fixture.Corpus.ClipIds[2], catalog.DefaultClipId);
        Assert.Equal(new[] { fixture.Corpus.ClipIds[0], fixture.Corpus.ClipIds[2], fixture.Corpus.ClipIds[3] }, catalog.Clips.Select(c => c.Id));
        Assert.Equal(new[] { 0, 2, 3 }, catalog.Clips.Select(c => c.TrainingOrdinal));
        Assert.Equal(new[] { 5.0, 12.0, 16.0 }, catalog.Clips.Select(c => c.Seconds));
        Assert.Equal("исправлено", catalog.Clips[0].Text);
        string json = JsonSerializer.Serialize(catalog);
        Assert.DoesNotContain(fixture.Root, json);
        Assert.DoesNotContain("AudioRelativePath", json);
    }

    [Fact]
    public async Task Catalog_without_eligible_reference_returns_empty_choices_without_admitting_runtime()
    {
        using var fixture = new VoxCpmTrainingFixture(); await fixture.Corpus.SeedAsync(Token, [79999, 70000], []);
        await fixture.StartAsync(Token); var trained = await fixture.AddStepsAsync(1, "paused", Token);
        fixture.RuntimeProvider.RejectAll = true;

        var catalog = await Service(fixture).GetReferencesAsync(fixture.Id, trained.Models[0].Id, Token);

        Assert.Null(catalog.DefaultClipId); Assert.Empty(catalog.Clips);
        Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<StudioException>(async () =>
        { await using var unexpected = await Service(fixture).OpenAudioAsync(fixture.Id, trained.Models[0].Id, fixture.Corpus.ClipIds[0], Token); })).Code);
    }

    [Fact]
    public async Task Missing_and_foreign_model_or_clip_ids_never_open_an_unrelated_reference()
    {
        using var fixture = new VoxCpmTrainingFixture(); await fixture.Corpus.SeedAsync(Token, [160000], []);
        await fixture.StartAsync(Token); var trained = await fixture.AddStepsAsync(1, "paused", Token);
        using var foreign = new VoxCpmTrainingFixture(); await foreign.Corpus.SeedAsync(Token, [160000], []);
        await foreign.StartAsync(Token); var other = await foreign.AddStepsAsync(1, "paused", Token);
        fixture.RuntimeProvider.RejectAll = true; foreign.RuntimeProvider.RejectAll = true;
        var service = Service(fixture);

        foreach (string model in new[] { new string('e', 32), other.Models[0].Id })
            Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<StudioException>(() =>
                service.GetReferencesAsync(fixture.Id, model, Token))).Code);
        foreach (string clip in new[] { new string('e', 32), foreign.Corpus.ClipIds[0] })
            Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<StudioException>(async () =>
            { await using var unexpected = await service.OpenAudioAsync(fixture.Id, trained.Models[0].Id, clip, Token); })).Code);
    }

    [Fact]
    public async Task Drifted_approved_audio_is_rejected_and_failed_resolution_releases_its_pins()
    {
        using var fixture = new VoxCpmTrainingFixture(); await fixture.Corpus.SeedAsync(Token, [160000], []);
        await fixture.StartAsync(Token); var trained = await fixture.AddStepsAsync(1, "paused", Token);
        fixture.RuntimeProvider.RejectAll = true;
        string path = Path.Combine(fixture.Root, fixture.Corpus.AudioPaths[0]);
        byte[] audio = await File.ReadAllBytesAsync(path, Token); audio[44] ^= 1;
        await File.WriteAllBytesAsync(path, audio, Token);

        Assert.Equal("job.input_changed", (await Assert.ThrowsAsync<StudioException>(async () =>
        { await using var unexpected = await Service(fixture).OpenAudioAsync(fixture.Id, trained.Models[0].Id, fixture.Corpus.ClipIds[0], Token); })).Code);

        using var released = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        Assert.True(released.CanWrite);
    }

    [Fact]
    public async Task Audio_handle_retains_source_corpus_file_and_ancestry_pins_until_disposed_without_project_lease()
    {
        using var fixture = new VoxCpmTrainingFixture(); await fixture.Corpus.SeedAsync(Token, [160000], []);
        await fixture.StartAsync(Token); var trained = await fixture.AddStepsAsync(1, "paused", Token);
        fixture.RuntimeProvider.RejectAll = true;
        string audioPath = Path.Combine(fixture.Root, fixture.Corpus.AudioPaths[0]);
        string sourcePath = Path.Combine(fixture.Root, fixture.Corpus.SourceRelativePath);
        string parent = Path.GetDirectoryName(audioPath)!;
        await using (var audio = await Service(fixture).OpenAudioAsync(fixture.Id, trained.Models[0].Id, fixture.Corpus.ClipIds[0], Token))
        {
            using (File.Open(Path.Combine(fixture.Root, "artifact-ownership.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            byte[] magic = new byte[4]; await audio.Content.ReadExactlyAsync(magic, Token);
            Assert.Equal("RIFF"u8.ToArray(), magic); Assert.Equal(320044, audio.Length);
            Assert.Throws<IOException>(() => { using var write = File.Open(audioPath, FileMode.Open, FileAccess.Write, FileShare.Read); });
            Assert.Throws<IOException>(() => { using var write = File.Open(sourcePath, FileMode.Open, FileAccess.Write, FileShare.Read); });
            Assert.Throws<IOException>(() => { using var write = File.Open(fixture.Corpus.ManifestPath, FileMode.Open, FileAccess.Write, FileShare.Read); });
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + ".moved"));
        }
        using (File.Open(audioPath, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
        using (File.Open(sourcePath, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
        Directory.Move(parent, parent + ".moved"); Directory.Move(parent + ".moved", parent);
    }
}

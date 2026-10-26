using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class VoxCpmCorpusReferenceResolverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Profile = "voxcpm2-saved-adapter-reference-cpu-v1";
    private sealed class Runtime(VoxCpmTrainingFixture fixture) : IVoxCpmInferenceRuntime
    {
        internal int Calls;
        internal bool Reject;
        public Task<RuntimeReadiness> GetReadinessAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<VoxCpmInferenceIdentity> GetIdentityAsync(CancellationToken token)
        {
            Calls++; if (Reject) throw new InvalidOperationException("Captured execution must not admit runtime");
            return Task.FromResult(new VoxCpmInferenceIdentity(fixture.RuntimeProvider.Identity, new('5', 64)));
        }
    }
    private static VoxCpmSavedVoiceResolver Resolver(VoxCpmTrainingFixture f, Runtime runtime, bool reference)
        => new(new ApprovedTtsCorpusResolver(), f.Checkpoints, runtime, useCorpusReference: reference);
    private static JsonElement Reference(VoxCpmSavedVoiceBinding binding)
    {
        var json = JsonSerializer.SerializeToElement(binding);
        Assert.True(json.TryGetProperty("Reference", out var reference));
        Assert.Equal(JsonValueKind.Object, reference.ValueKind); return reference;
    }
    private static VoxCpmSavedVoiceBinding Capture(VoxCpmSavedVoiceBinding binding, ApprovedTtsCorpusClip clip)
    {
        var json = JsonSerializer.SerializeToNode(binding)!.AsObject(); json["ProfileVersion"] = Profile;
        json["Reference"] = JsonSerializer.SerializeToNode(new { clip.Id, clip.TrainingOrdinal, clip.AudioRelativePath,
            clip.AudioSha256, SampleCount = clip.EndSample - clip.StartSample });
        return JsonSerializer.Deserialize<VoxCpmSavedVoiceBinding>(json.ToJsonString())!;
    }

    [Fact]
    public void Selector_uses_ordinal_id_tiebreak_and_inclusive_whole_clip_bounds()
    {
        static ApprovedTtsCorpusClip Clip(char id, int ordinal, long samples) => new(ordinal, ordinal, new(id, 32),
            0, samples, "not used as a prompt", new('a', 64), "not opened.wav", new('b', 64), new('c', 64));
        static ApprovedTtsCorpusClip Select(params ApprovedTtsCorpusClip[] clips) => VoxCpmCorpusReferenceSelector.Select(clips);
        Assert.Equal(new string('a', 32), Select(Clip('b', 2, 180000), Clip('a', 2, 204000)).Id);
        Assert.Equal(80000, Select(Clip('b', 0, 79999), Clip('a', 1, 80000)).EndSample);
        Assert.Equal(256000, Select(Clip('b', 0, 256001), Clip('a', 1, 256000)).EndSample);
    }

    [Fact]
    public async Task New_selection_uses_whole_historical_approved_clip_nearest_twelve_seconds()
    {
        using var f = new VoxCpmTrainingFixture(); await f.Corpus.SeedAsync(Token, [256000, 201680, 192000], []);
        await f.StartAsync(Token); var trained = await f.AddStepsAsync(1, "paused", Token);
        await f.Corpus.ReplaceDraftAsync(Token);
        await using var db = await f.Corpus.OpenAsync(Token); var runtime = new Runtime(f);
        using var voice = await Resolver(f, runtime, true).ResolveAsync(db, trained.Models[0].Id, Token);
        var reference = Reference(voice.Binding);
        Assert.Equal(f.Corpus.ClipIds[2], reference.GetProperty("Id").GetString());
        Assert.Equal(2, reference.GetProperty("TrainingOrdinal").GetInt32());
        Assert.Equal(f.Corpus.AudioPaths[2], reference.GetProperty("AudioRelativePath").GetString());
        Assert.Equal(192000, reference.GetProperty("SampleCount").GetInt64());
        Assert.Equal(Profile, voice.Binding.ProfileVersion);
        Assert.Equal(voice.Corpus.Clips[2], voice.ReferenceClip);
        var descriptor = VoxCpmInferenceProfile.Descriptor(voice.Binding, db.State.Metadata.Versions);
        Assert.Equal(Profile, descriptor.Preset.ProfileId); Assert.Equal("voxcpm2-saved-adapter-reference-v1", descriptor.Preset.Id);
        Assert.Equal(voice.Binding.ManifestSha256[..40], descriptor.Versions.Model);
        Assert.False(descriptor.Preset.AllowCpuFallback);
        string path = db.ResolveOwnedPath(f.Corpus.AudioPaths[2]);
        Assert.Throws<IOException>(() => { using var denied = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); });
    }

    [Fact]
    public async Task Equal_distance_prefers_lower_retained_ordinal()
    {
        using var f = new VoxCpmTrainingFixture(); await f.Corpus.SeedAsync(Token, [180000, 204000], []);
        await f.StartAsync(Token); var trained = await f.AddStepsAsync(1, "paused", Token);
        await using var db = await f.Corpus.OpenAsync(Token);
        using var voice = await Resolver(f, new Runtime(f), true).ResolveAsync(db, trained.Models[0].Id, Token);
        Assert.Equal(f.Corpus.ClipIds[0], Reference(voice.Binding).GetProperty("Id").GetString());
    }

    [Fact]
    public async Task No_eligible_whole_reference_fails_and_releases_corpus_pins()
    {
        using var f = new VoxCpmTrainingFixture(); await f.Corpus.SeedAsync(Token, [79999, 70000], []);
        await f.StartAsync(Token); var trained = await f.AddStepsAsync(1, "paused", Token);
        await using var db = await f.Corpus.OpenAsync(Token);
        var error = await Assert.ThrowsAsync<StudioException>(async () =>
        { using var unexpected = await Resolver(f, new Runtime(f), true).ResolveAsync(db, trained.Models[0].Id, Token); });
        Assert.Equal("job.input_changed", error.Code);
        using var released = new FileStream(db.ResolveOwnedPath(f.Corpus.AudioPaths[0]), FileMode.Open, FileAccess.Write, FileShare.Read);
        Assert.True(released.CanWrite);
    }

    [Fact]
    public async Task Captured_reference_is_checked_without_reselection_or_current_mode_preference()
    {
        using var f = new VoxCpmTrainingFixture(); await f.Corpus.SeedAsync(Token, [160000, 192000], []);
        await f.StartAsync(Token); var trained = await f.AddStepsAsync(1, "paused", Token);
        await using var db = await f.Corpus.OpenAsync(Token); var runtime = new Runtime(f);
        using var legacy = await Resolver(f, runtime, false).ResolveAsync(db, trained.Models[0].Id, Token);
        var captured = Capture(legacy.Binding, legacy.Corpus.Clips[0]); // Valid historical choice, deliberately not today's winner.
        runtime.Reject = true;
        using var resolved = await Resolver(f, runtime, false).ResolveForExecutionAsync(db, captured, Token);
        Assert.Equal(captured, resolved.Binding); Assert.Equal(f.Corpus.ClipIds[0], Reference(resolved.Binding).GetProperty("Id").GetString());
        Assert.Equal(legacy.Corpus.Clips[0], resolved.ReferenceClip);
        using var old = await Resolver(f, runtime, true).ResolveForExecutionAsync(db, legacy.Binding, Token);
        Assert.Equal(legacy.Binding, old.Binding); Assert.Null(old.ReferenceClip); Assert.Equal(1, runtime.Calls);
    }

    [Theory]
    [InlineData("Id")] [InlineData("TrainingOrdinal")] [InlineData("AudioRelativePath")]
    [InlineData("AudioSha256")] [InlineData("SampleCount")] [InlineData("missing")]
    [InlineData("legacy-with-reference")]
    public async Task Captured_reference_drift_or_foreign_clip_never_reselects(string field)
    {
        using var f = new VoxCpmTrainingFixture(); await f.Corpus.SeedAsync(Token, [160000, 192000], []);
        await f.StartAsync(Token); var trained = await f.AddStepsAsync(1, "paused", Token);
        await using var db = await f.Corpus.OpenAsync(Token); var runtime = new Runtime(f);
        using var legacy = await Resolver(f, runtime, false).ResolveAsync(db, trained.Models[0].Id, Token);
        var captured = Capture(legacy.Binding, legacy.Corpus.Clips[0]);
        var json = JsonSerializer.SerializeToNode(captured)!.AsObject();
        json["Reference"] = JsonSerializer.SerializeToNode(new { legacy.Corpus.Clips[0].Id, TrainingOrdinal = 0,
            legacy.Corpus.Clips[0].AudioRelativePath, legacy.Corpus.Clips[0].AudioSha256, SampleCount = 160000 });
        if (field == "missing") json["Reference"] = null;
        else if (field == "legacy-with-reference") json["ProfileVersion"] = VoxCpmInferenceProfile.Id;
        else json["Reference"]![field] = field switch
        {
            "Id" => JsonValue.Create(new string('e', 32)), "TrainingOrdinal" => JsonValue.Create(1),
            "SampleCount" => JsonValue.Create(160001), "AudioSha256" => JsonValue.Create(new string('e', 64)),
            _ => JsonValue.Create(legacy.Corpus.Clips[1].AudioRelativePath)
        };
        captured = JsonSerializer.Deserialize<VoxCpmSavedVoiceBinding>(json.ToJsonString())!;
        Assert.Equal("job.input_changed", (await Assert.ThrowsAsync<StudioException>(async () =>
        { using var unexpected = await Resolver(f, runtime, true).ResolveForExecutionAsync(db, captured, Token); })).Code);
    }
}

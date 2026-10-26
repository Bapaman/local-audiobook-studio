using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class VoxCpmConfiguredResolverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("auto")] [InlineData("explicit")] [InlineData("off")]
    public async Task Configured_selection_captures_exact_settings_and_execution_never_reselects(string mode)
    {
        using var f = new VoxCpmTrainingFixture(); await f.Corpus.SeedAsync(Token, [160000, 192000], []);
        await f.StartAsync(Token); var trained = await f.AddStepsAsync(1, "paused", Token);
        await f.Corpus.ReplaceDraftAsync(Token);
        await using var db = await f.Corpus.OpenAsync(Token); var runtime = new Runtime(f);
        var resolver = new VoxCpmSavedVoiceResolver(new ApprovedTtsCorpusResolver(), f.Checkpoints, runtime);
        var options = new VoxCpmGenerationOptions(879, 16, 1.25, 85, mode != "off", mode == "explicit" ? f.Corpus.ClipIds[0] : null);
        using var voice = await resolver.ResolveConfiguredAsync(db, trained.Models[0].Id, options, Token);
        Assert.Equal(options, voice.Binding.Options); Assert.Equal(VoxCpmInferenceProfile.ControlsId, voice.Binding.ProfileVersion);
        Assert.Equal(mode == "off" ? null : f.Corpus.ClipIds[mode == "auto" ? 1 : 0], voice.Binding.Reference?.Id);
        var preset = VoxCpmInferenceProfile.Descriptor(voice.Binding, db.State.Metadata.Versions).Preset;
        Assert.Equal(879, preset.Seed); Assert.Equal(16, preset.Timesteps); Assert.Equal(1.25, preset.Cfg);
        runtime.Reject = true;
        using var captured = await resolver.ResolveForExecutionAsync(db, voice.Binding, Token);
        Assert.Equal(voice.Binding, captured.Binding);
    }

    [Fact]
    public async Task Explicit_foreign_clip_and_invalid_options_fail_without_runtime_admission()
    {
        using var f = new VoxCpmTrainingFixture(); await f.Corpus.SeedAsync(Token, [160000], []);
        await f.StartAsync(Token); var trained = await f.AddStepsAsync(1, "paused", Token);
        await using var db = await f.Corpus.OpenAsync(Token); var runtime = new Runtime(f) { Reject = true };
        var resolver = new VoxCpmSavedVoiceResolver(new ApprovedTtsCorpusResolver(), f.Checkpoints, runtime);
        foreach (var options in new[] { new VoxCpmGenerationOptions(ReferenceClipId: new('e', 32)), new(TempoPercent: 121), new(UseReference: false, ReferenceClipId: f.Corpus.ClipIds[0]) })
            Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<StudioException>(async () =>
            { using var unexpected = await resolver.ResolveConfiguredAsync(db, trained.Models[0].Id, options, Token); })).Code);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Execution_rejects_missing_reference_or_changed_captured_clip_without_fallback()
    {
        using var f = new VoxCpmTrainingFixture(); await f.Corpus.SeedAsync(Token, [160000, 192000], []);
        await f.StartAsync(Token); var trained = await f.AddStepsAsync(1, "paused", Token);
        await using var db = await f.Corpus.OpenAsync(Token); var runtime = new Runtime(f);
        var resolver = new VoxCpmSavedVoiceResolver(new ApprovedTtsCorpusResolver(), f.Checkpoints, runtime);
        using var selected = await resolver.ResolveConfiguredAsync(db, trained.Models[0].Id, new(), Token);
        runtime.Reject = true;
        foreach (var binding in new[] { selected.Binding with { Reference = null }, selected.Binding with {
            Reference = selected.Binding.Reference! with { AudioSha256 = new('e', 64) } } })
            Assert.Equal("job.input_changed", (await Assert.ThrowsAsync<StudioException>(async () =>
            { using var unexpected = await resolver.ResolveForExecutionAsync(db, binding, Token); })).Code);
    }

    private sealed class Runtime(VoxCpmTrainingFixture fixture) : IVoxCpmInferenceRuntime
    {
        internal int Calls; internal bool Reject;
        public Task<RuntimeReadiness> GetReadinessAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public Task<VoxCpmInferenceIdentity> GetIdentityAsync(CancellationToken token)
        {
            Calls++; if (Reject) throw new InvalidOperationException("No runtime identity admission");
            return Task.FromResult(new VoxCpmInferenceIdentity(fixture.RuntimeProvider.Identity, new('5', 64)));
        }
    }
}

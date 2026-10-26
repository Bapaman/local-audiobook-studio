using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class VoxCpmGenerationControlsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Controls_without_vox_model_are_rejected_before_provider_admission()
    {
        using var box = new CatalogSandbox(); var state = await StudioJobServiceTests.Prepare(box);
        var request = JsonSerializer.Deserialize<GenerationRequest>(JsonSerializer.Serialize(new { Kind = JobKind.Preview,
            SegmentIds = new[] { state.Segments[0].Id }, VoxCpmOptions = new { TempoPercent = 90 } }))!;
        var service = new StudioJobService(new(new(box.Root), box.Catalog(), new()), new NeverLegacyDescriptor());
        Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<StudioException>(() =>
            service.EnqueueAsync(state.Metadata.Id, state.Revision, "not-vox", request, Token))).Code);
    }

    [Fact]
    public async Task Historical_legacy_settings_come_from_captured_job_without_current_runtime()
    {
        using var box = new CatalogSandbox(); var state = await StudioJobServiceTests.Prepare(box);
        var service = new StudioJobService(new(new(box.Root), box.Catalog(), new()), new NeverLegacyDescriptor(), voxSavedVoices: new VoxProductResolver());
        var job = await service.EnqueueAsync(state.Metadata.Id, state.Revision, "legacy-info", new(JobKind.Preview,
            [state.Segments[0].Id], ExperimentalConfirmed: true, SavedVoxCpmModelId: new('4', 32)), Token);
        var json = JsonSerializer.SerializeToElement(job);
        Assert.True(json.TryGetProperty("VoxCpmGeneration", out var info));
        Assert.Equal(29, info.GetProperty("GlobalStep").GetInt64());
        Assert.False(info.GetProperty("Options").GetProperty("UseReference").GetBoolean());
        Assert.Equal(100, info.GetProperty("Options").GetProperty("TempoPercent").GetInt32());
    }

    [Fact]
    public async Task Configured_enqueue_replays_before_resolver_and_every_changed_requested_setting_conflicts()
    {
        using var box = new CatalogSandbox(); var state = await StudioJobServiceTests.Prepare(box);
        var resolver = new ConfiguredResolver(); var service = new StudioJobService(new(new(box.Root), box.Catalog(), new()),
            new NeverLegacyDescriptor(), voxSavedVoices: resolver);
        var options = new VoxCpmGenerationOptions(Seed: 99, Timesteps: 14, Cfg: 1.5, TempoPercent: 90, UseReference: false);
        var request = new GenerationRequest(JobKind.Preview, [state.Segments[0].Id], ExperimentalConfirmed: true,
            SavedVoxCpmModelId: new('4', 32), VoxCpmOptions: options);
        var job = await service.EnqueueAsync(state.Metadata.Id, state.Revision, "configured", request, Token);
        Assert.Equal(options, job.VoxCpmGeneration!.Options); Assert.Equal(VoxCpmInferenceProfile.ControlsId, job.EffectiveProfileId);
        resolver.Reject = true;
        Assert.Equal(job, await service.EnqueueAsync(state.Metadata.Id, -1, "configured", request, Token));
        foreach (var changed in new[] { options with { Seed = 100 }, options with { Timesteps = 15 }, options with { Cfg = 2 },
            options with { TempoPercent = 91 }, options with { UseReference = true }, options with { UseReference = true, ReferenceClipId = new('a', 32) } })
            Assert.Equal("job.idempotency.conflict", (await Assert.ThrowsAsync<StudioException>(() =>
                service.EnqueueAsync(state.Metadata.Id, -1, "configured", request with { VoxCpmOptions = changed }, Token))).Code);
        Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<StudioException>(() => service.EnqueueAsync(state.Metadata.Id, -1,
            "configured", request with { SavedVoxCpmModelId = null }, Token))).Code);
        Assert.Equal(options, Assert.Single(await service.ListAsync(state.Metadata.Id, Token)).VoxCpmGeneration!.Options);
        Assert.Equal(options, Assert.Single((await service.ReadEventPageAsync(state.Metadata.Id, null, Token)).Jobs).VoxCpmGeneration!.Options);
    }

    private sealed class ConfiguredResolver : IVoxCpmSavedVoiceResolver
    {
        internal bool Reject;
        public Task<ResolvedVoxCpmSavedVoice> ResolveAsync(ProjectDatabase db, string id, CancellationToken token)
            => throw new InvalidOperationException("Configured options must use configured resolver");
        public async Task<ResolvedVoxCpmSavedVoice> ResolveConfiguredAsync(ProjectDatabase db, string id, VoxCpmGenerationOptions options, CancellationToken token)
        {
            if (Reject) throw new InvalidOperationException("Replay must precede resolver");
            var old = await new VoxProductResolver().ResolveAsync(db, id, token);
            return new(old.Binding with { ProfileVersion = VoxCpmInferenceProfile.ControlsId, Options = options }, old.Checkpoint,
                old.Corpus, old.Identity, old);
        }
        public Task<ResolvedVoxCpmSavedVoice> ResolveForExecutionAsync(ProjectDatabase db, VoxCpmSavedVoiceBinding binding, CancellationToken token)
            => throw new NotSupportedException();
    }
}

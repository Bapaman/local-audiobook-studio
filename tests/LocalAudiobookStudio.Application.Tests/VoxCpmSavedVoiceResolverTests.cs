using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Application.Workers;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class VoxCpmSavedVoiceResolverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Runtime(VoxCpmTrainingFixture fixture) : IVoxCpmInferenceRuntime
    {
        internal VoxCpmTrainingIdentity? Changed;
        internal bool Unavailable;
        internal int Calls;
        public Task<RuntimeReadiness> GetReadinessAsync(CancellationToken token = default)
            => Task.FromResult(new RuntimeReadiness(!Unavailable, true, Unavailable ? "worker.runtime.not_ready" : null, VoxCpmInferenceProfile.Id));
        public Task<VoxCpmInferenceIdentity> GetIdentityAsync(CancellationToken token)
        {
            Calls++; if (Unavailable) throw new WorkerClientException("worker.runtime.not_ready", true);
            return Task.FromResult(new VoxCpmInferenceIdentity(Changed ?? fixture.RuntimeProvider.Identity, new('5', 64)));
        }
    }
    private sealed class Validator : IVoxCpmTrainingCheckpointValidator
    {
        internal readonly List<string> Models = [];
        internal bool Reject;
        public Task VerifyAsync(string projectRoot, VoxCpmCheckpointReference checkpoint, ApprovedTtsCorpusBinding corpus,
            VoxCpmTrainingIdentity identity, CancellationToken token)
        {
            Models.Add(checkpoint.Id);
            if (Reject) throw new VoxCpmTrainingExecutionException("voxcpm_training.checkpoint.invalid");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Resolves_exact_historical_model_instead_of_current_head_and_captures_real_pins()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); var first = await f.StartAsync(Token);
        var finished = await f.AddStepsAsync(2, "completed", Token); await f.StartAsync(Token, "new-line");
        var runtime = new Runtime(f); var validator = new Validator();
        var resolver = new VoxCpmSavedVoiceResolver(new ApprovedTtsCorpusResolver(), validator, runtime);
        await using var db = await f.Corpus.OpenAsync(Token);
        using var resolved = await resolver.ResolveAsync(db, finished.Models[0].Id, Token);
        Assert.Equal(finished.Models[0].Id, resolved.Binding.ModelId);
        Assert.Equal(first.Id, resolved.Binding.TrainingId);
        Assert.Equal(1, resolved.Binding.GlobalStep);
        Assert.Equal(f.Corpus.VersionId, resolved.Binding.CorpusVersionId);
        Assert.Equal(resolved.Corpus.BindingSha256, resolved.Binding.CorpusBindingSha256);
        Assert.Equal(resolved.Checkpoint.ManifestSha256, resolved.Binding.ManifestSha256);
        Assert.Equal(new string('5', 64), resolved.Binding.InferenceImplementationSha256);
        Assert.Equal([finished.Models[0].Id], validator.Models);
        var descriptor = VoxCpmInferenceProfile.Descriptor(resolved.Binding, db.State.Metadata.Versions);
        Assert.Equal("voxcpm2-saved-adapter-cpu-v1", descriptor.Preset.ProfileId);
        Assert.Equal(256, descriptor.Preset.Steps); Assert.False(descriptor.Preset.AllowCpuFallback);
        Assert.Equal(new string('5', 40), descriptor.Versions.Worker);
        Assert.Equal(resolved.Binding.ManifestSha256[..40], descriptor.Versions.Model);
        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task Execution_resolution_rechecks_captured_binding_without_re_admitting_runtime()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var trained = await f.AddStepsAsync(1, "paused", Token); var runtime = new Runtime(f);
        var resolver = new VoxCpmSavedVoiceResolver(new ApprovedTtsCorpusResolver(), new Validator(), runtime);
        await using var db = await f.Corpus.OpenAsync(Token);
        using var captured = await resolver.ResolveAsync(db, trained.Models[0].Id, Token);
        runtime.Unavailable = true;
        using var current = await resolver.ResolveForExecutionAsync(db, captured.Binding, Token);
        Assert.Equal(captured.Binding, current.Binding); Assert.Equal(1, runtime.Calls);
        foreach (var changed in new[] { captured.Binding with { ManifestSha256 = new('0', 64) },
                     captured.Binding with { RuntimeIdentitySha256 = new('0', 64) }, captured.Binding with { GlobalStep = 2 } })
            await Error("job.input_changed", () => resolver.ResolveForExecutionAsync(db, changed, Token));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public async Task Every_training_identity_field_is_bound_even_with_an_unchanged_fingerprint(int field)
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var trained = await f.AddStepsAsync(1, "paused", Token); var original = f.RuntimeProvider.Identity;
        var changed = field switch { 0 => original with { Fingerprint = new('6', 64) },
            1 => original with { SourceCommit = new('6', 40) }, 2 => original with { BaseRevision = new('6', 40) },
            3 => original with { SourceLockSha256 = new('6', 64) }, 4 => original with { ModelLockSha256 = new('6', 64) },
            5 => original with { CacheSha256 = new('6', 64) }, 6 => original with { CacheLayoutSha256 = new('6', 64) },
            7 => original with { LoaderSha256 = new('6', 64) }, 8 => original with { RecipeSha256 = new('6', 64) },
            _ => original with { AdapterLayoutSha256 = new('6', 64) } };
        Assert.NotEqual(VoxCpmInferenceProfile.IdentityHash(original), VoxCpmInferenceProfile.IdentityHash(changed));
        var runtime = new Runtime(f) { Changed = changed };
        var resolver = new VoxCpmSavedVoiceResolver(new ApprovedTtsCorpusResolver(), new Validator(), runtime);
        await using var db = await f.Corpus.OpenAsync(Token);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => resolver.ResolveAsync(db, trained.Models[0].Id, Token));
        Assert.Equal("worker.runtime.not_ready", error.Code);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unknown_model_or_changed_source_corpus_or_checkpoint_is_never_adopted(bool changeSource)
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var trained = await f.AddStepsAsync(1, "paused", Token); var validator = new Validator();
        var resolver = new VoxCpmSavedVoiceResolver(new ApprovedTtsCorpusResolver(), validator, new Runtime(f));
        await using var db = await f.Corpus.OpenAsync(Token);
        await Error("job.input.invalid", () => resolver.ResolveAsync(db, Guid.NewGuid().ToString("N"), Token));
        validator.Reject = true;
        await Error("job.input_changed", () => resolver.ResolveAsync(db, trained.Models[0].Id, Token));
        validator.Reject = false;
        if (changeSource)
        {
            await using var source = new FileStream(db.ResolveOwnedPath(f.Corpus.SourceRelativePath), FileMode.Open, FileAccess.Write, FileShare.Read);
            source.Position = 44; await source.WriteAsync(new byte[] { 7, 0 }, Token);
        }
        else await File.AppendAllTextAsync(f.Corpus.ManifestPath, " ", Token);
        await Error("job.input_changed", () => resolver.ResolveAsync(db, trained.Models[0].Id, Token));
    }

    [Fact]
    public async Task Resolved_context_retains_real_source_pin_until_disposed()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var trained = await f.AddStepsAsync(1, "paused", Token);
        var resolver = new VoxCpmSavedVoiceResolver(new ApprovedTtsCorpusResolver(), new Validator(), new Runtime(f));
        await using var db = await f.Corpus.OpenAsync(Token);
        var resolved = await resolver.ResolveAsync(db, trained.Models[0].Id, Token);
        string path = db.ResolveOwnedPath(f.Corpus.SourceRelativePath);
        try { Assert.Throws<IOException>(() => { using var denied = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); }); }
        finally { resolved.Dispose(); }
        using var released = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        Assert.True(released.CanWrite); // No bytes are modified.
    }

    private static async Task Error(string code, Func<Task> action)
        => Assert.Equal(code, (await Assert.ThrowsAsync<StudioException>(action)).Code);
}

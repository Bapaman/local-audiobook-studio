using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.VoxCpmTraining;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class VoxCpmTrainingServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Missing_read_is_inert_and_unavailable_runtime_does_not_start()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token);
        Assert.Null(await f.Service.GetAsync(f.Id, Token)); Assert.False(Directory.Exists(Path.Combine(f.Root, "voxcpm-training")));
        f.RuntimeProvider.Ready = false;
        await Error("voxcpm_training.runtime.not_ready", () => f.StartAsync(Token));
        Assert.Null(await f.Service.GetAsync(f.Id, Token));
    }
    [Fact]
    public async Task Start_requires_confirmation_CAS_and_committed_version()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token);
        await Error("voxcpm_training.confirmation.required", () => f.Service.StartAsync(f.Id, f.Corpus.ProjectRevision, "a", f.Corpus.VersionId, false, Token));
        await Error("project.conflict", () => f.Service.StartAsync(f.Id, 0, "b", f.Corpus.VersionId, true, Token));
        await Error("voxcpm_training.corpus.invalid", () => f.Service.StartAsync(f.Id, f.Corpus.ProjectRevision, "c", Guid.NewGuid().ToString("N"), true, Token));
        var s = await f.StartAsync(Token); Assert.Equal(f.Corpus.VersionId, s.CorpusVersionId); Assert.Equal(2, s.CorpusClipCount);
        Assert.Equal(10, s.RetainedDurationSeconds); Assert.False(s.QualityEstablished); Assert.Equal(0, s.GlobalStep); Assert.Equal(1, s.TargetCompletedEpochs);
    }
    [Fact]
    public async Task Replay_precedes_CAS_readiness_and_artifact_ownership_and_key_reuse_is_denied()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); var start = await f.StartAsync(Token);
        f.RuntimeProvider.Ready = false;
        await using var db = await f.Corpus.OpenAsync(Token); await using var held = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        Assert.Equivalent(start, await f.Service.StartAsync(f.Id, 0, "train", f.Corpus.VersionId, true, Token));
        await Error("voxcpm_training.idempotency.conflict", () => f.Service.StartAsync(f.Id, 0, "train", Guid.NewGuid().ToString("N"), true, Token));
        var stopped = await f.Service.ControlAsync(f.Id, start.Id, start.Revision, "stop", "stop", Token);
        Assert.Equal("paused", stopped.State);
        Assert.Equivalent(stopped, await f.Service.ControlAsync(f.Id, start.Id, 0, "stop", "stop", Token));
        await Error("voxcpm_training.conflict", () => f.Service.ControlAsync(f.Id, start.Id, 1, "fresh-stop", "stop", Token));
    }
    [Fact]
    public async Task Resume_preserves_cursor_identity_parent_and_requests_next_epoch_after_completion()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); var start = await f.StartAsync(Token);
        var paused = await f.AddStepsAsync(1, "paused", Token);
        var resumed = await f.Service.ControlAsync(f.Id, start.Id, paused.Revision, "resume", "resume", Token);
        Assert.Equal(start.Id, resumed.Id); Assert.NotEqual(start.ExecutionId, resumed.ExecutionId);
        Assert.Equal(1, resumed.GlobalStep); Assert.Equal(1, resumed.NextRetainedOrdinal); Assert.Equal(1, resumed.TargetCompletedEpochs);
        var completed = await f.AddStepsAsync(1, "completed", Token);
        var next = await f.Service.ControlAsync(f.Id, start.Id, completed.Revision, "next", "resume", Token);
        Assert.Equal(2, next.GlobalStep); Assert.Equal(1, next.CompletedEpochs); Assert.Equal(0, next.NextRetainedOrdinal); Assert.Equal(2, next.TargetCompletedEpochs);
        Assert.Equal(2, next.Models.Count);
    }
    [Fact]
    public async Task Resume_rejects_changed_runtime_corpus_and_invalid_head_without_losing_models()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var paused = await f.AddStepsAsync(1, "paused", Token); var identity = f.RuntimeProvider.Identity;
        f.RuntimeProvider.Identity = identity with { Fingerprint = new('5', 64) };
        await Error("voxcpm_training.runtime.changed", () => f.Service.ControlAsync(f.Id, paused.Id, paused.Revision, "runtime", "resume", Token));
        f.RuntimeProvider.Identity = identity; f.Checkpoints.Reject = true;
        await Error("voxcpm_training.checkpoint.invalid", () => f.Service.ControlAsync(f.Id, paused.Id, paused.Revision, "bad-head", "resume", Token));
        f.Checkpoints.Reject = false;
        await File.AppendAllTextAsync(f.Corpus.ManifestPath, " ", Token);
        await Error("voxcpm_training.corpus.invalid", () => f.Service.ControlAsync(f.Id, paused.Id, paused.Revision, "bad-corpus", "resume", Token));
        Assert.Equivalent(paused, await f.Service.GetAsync(f.Id, Token));
    }
    [Fact]
    public async Task New_lineage_preserves_prior_model_bindings_and_history_reads_without_runtime()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); var first = await f.StartAsync(Token);
        await f.AddStepsAsync(2, "completed", Token); var next = await f.StartAsync(Token, "new-lineage");
        Assert.NotEqual(first.Id, next.Id); Assert.Equal(0, next.GlobalStep); Assert.Equal(2, next.Models.Count);
        Assert.All(next.Models, model => Assert.Equal(first.Id, model.TrainingId));
        f.RuntimeProvider.Ready = false; Assert.Equivalent(next, await f.Service.GetAsync(f.Id, Token));
    }
    [Fact]
    public async Task Stop_at_the_completed_epoch_boundary_does_not_strand_the_target()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var boundary = await f.AddStepsAsync(2, "checkpointing", Token);
        var stopped = await f.Service.ControlAsync(f.Id, boundary.Id, boundary.Revision, "stop-last", "stop", Token);
        Assert.Equal("completed", stopped.State); Assert.Equal(2, stopped.GlobalStep); Assert.Equal(1, stopped.CompletedEpochs);
        var next = await f.Service.ControlAsync(f.Id, boundary.Id, stopped.Revision, "next-pass", "resume", Token);
        Assert.Equal(2, next.TargetCompletedEpochs); Assert.Equal(2, next.GlobalStep);
    }
    private static async Task Error(string code, Func<Task> action)
        => Assert.Equal(code, (await Assert.ThrowsAsync<StudioException>(action)).Code);
}

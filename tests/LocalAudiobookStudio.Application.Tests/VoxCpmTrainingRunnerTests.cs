using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class VoxCpmTrainingRunnerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task One_requested_epoch_runs_each_retained_clip_once_automatically()
    {
        using var f = new VoxCpmTrainingFixture(); await f.Corpus.SeedAsync(Token, [80000, 80000, 80000, 80000, 80000, 80000], [1, 3, 4]); await f.StartAsync(Token);
        var executor = new Executor();
        Assert.True(await Runner(f, executor).RunNextAsync(Token)); var s = (await f.Service.GetAsync(f.Id, Token))!;
        Assert.Equal(new[] { 0, 2, 5 }, executor.Visited.Select(e => e.Corpus.Clips[e.Cursor.NextRetainedOrdinal].SourceIndex));
        Assert.Equal(new[] { f.Corpus.ClipIds[0], f.Corpus.ClipIds[2], f.Corpus.ClipIds[5] }, executor.Visited.Select(e => e.Cursor.NextClipId));
        Assert.Equal(3, s.GlobalStep); Assert.Equal(1, s.CompletedEpochs); Assert.Equal(0, s.NextRetainedOrdinal); Assert.Equal("completed", s.State);
        Assert.Equal(3, s.Models.Count); Assert.False(s.QualityEstablished); Assert.Equal(3, executor.Visited.Select(e => e.WindowId).Distinct().Count());
    }
    [Fact]
    public async Task Fresh_process_resume_uses_next_clip_and_same_parent_head()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var first = new Executor { StopWithCheckpoint = true }; await Runner(f, first).RunNextAsync(Token);
        var paused = (await f.Service.GetAsync(f.Id, Token))!; Assert.Equal("paused", paused.State); Assert.Equal(1, paused.GlobalStep);
        var queued = await f.Service.ControlAsync(f.Id, paused.Id, paused.Revision, "resume", "resume", Token);
        var second = new Executor(); await Runner(f, second).RunNextAsync(Token);
        Assert.Single(second.Visited); Assert.Equal(f.Corpus.ClipIds[2], second.Visited[0].Cursor.NextClipId);
        Assert.Equal(first.Returned[0], second.Visited[0].ParentCheckpoint); Assert.Equal(queued.ExecutionId, second.Visited[0].ExecutionId);
        Assert.Equal("completed", (await f.Service.GetAsync(f.Id, Token))!.State);
    }
    [Fact]
    public async Task STOP_on_last_update_is_completed_not_stranded_paused()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token); await f.AddStepsAsync(1, "queued", Token);
        var executor = new Executor { StopWithCheckpoint = true, Before = async (_, _) =>
        {
            var s = (await f.Service.GetAsync(f.Id, Token))!; await f.Service.ControlAsync(f.Id, s.Id, s.Revision, "stop-last", "stop", Token);
        } };
        await Runner(f, executor).RunNextAsync(Token); var done = (await f.Service.GetAsync(f.Id, Token))!;
        Assert.Equal("completed", done.State); Assert.Equal(2, done.GlobalStep); Assert.Equal(1, done.CompletedEpochs);
    }
    [Fact]
    public async Task Recovery_is_metadata_only_and_never_admits_runtime_or_dispatches()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var other = (await f.Corpus.Box.Service().CreateAsync(new("No training"), "other", Token)).Snapshot;
        f.RuntimeProvider.Calls = 0; f.RuntimeProvider.RejectAll = true; File.Delete(f.Corpus.ManifestPath);
        var executor = new Executor(); var runner = Runner(f, executor); await runner.RecoverAsync(Token);
        var s = (await f.Service.GetAsync(f.Id, Token))!; Assert.Equal("interrupted", s.State); Assert.Equal(0, f.RuntimeProvider.Calls);
        Assert.Empty(executor.Visited); Assert.False(await runner.RunNextAsync(Token)); Assert.True(s.CanResume);
        Assert.False(Directory.Exists(Path.Combine(f.Corpus.Box.ProjectRoot(other.Id), "voxcpm-training")));
        await runner.RecoverAsync(Token); Assert.Equal(s.Revision, (await f.Service.GetAsync(f.Id, Token))!.Revision);
    }
    [Fact]
    public async Task READY_without_atomic_state_commit_is_not_adopted_after_restart()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        FileStream? replacementBlock = null;
        var executor = new Executor { Before = (e, _) =>
        {
            if (e.Cursor.GlobalStep == 1) replacementBlock = File.Open(Path.Combine(f.Root, "voxcpm-training", "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Directory.CreateDirectory(e.OutputRoot); File.WriteAllText(Path.Combine(e.OutputRoot, "READY"), "uncommitted synthetic output"); return Task.CompletedTask;
        } };
        try { await Runner(f, executor).RunNextAsync(Token); }
        finally { replacementBlock?.Dispose(); }
        var before = (await f.Service.GetAsync(f.Id, Token))!; Assert.Equal(1, before.GlobalStep); Assert.Single(before.Models);
        await Runner(f, new()).RecoverAsync(Token); var recovered = (await f.Service.GetAsync(f.Id, Token))!;
        Assert.Equal("interrupted", recovered.State); Assert.Equal(1, recovered.GlobalStep); Assert.Single(recovered.Models); Assert.Equal(executor.Returned[0].Id, recovered.Models[0].Id);
    }
    [Fact]
    public async Task Shared_gate_excludes_other_compute_until_successful_pass_finishes()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token); var gate = new ComputeGate(); var executor = new Executor(); var runner = Runner(f, executor, gate);
        Assert.True(await gate.WaitAsync(0, Token)); Assert.False(await runner.RunNextAsync(Token)); Assert.Empty(executor.Visited); gate.Release();
        Assert.True(await runner.RunNextAsync(Token)); Assert.True(await gate.WaitAsync(0, Token)); gate.Release();
    }
    [Fact]
    public async Task Delayed_progress_from_previous_window_cannot_regress_current_phase()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        Func<VoxCpmTrainingProgress, Task>? old = null;
        var executor = new Executor { Before = async (e, progress) =>
        {
            if (e.Cursor.GlobalStep == 0) old = progress;
            else
            {
                await old!(new("loading")); var s = (await f.Service.GetAsync(f.Id, Token))!;
                Assert.Equal("checkpointing", s.Phase); Assert.Equal(1, s.GlobalStep);
            }
        } };
        await Runner(f, executor).RunNextAsync(Token); Assert.Equal(2, (await f.Service.GetAsync(f.Id, Token))!.GlobalStep);
    }
    [Fact]
    public async Task Old_execution_progress_and_checkpoint_cannot_replace_new_execution()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        string replacement = Guid.NewGuid().ToString("N");
        var executor = new Executor { Before = async (_, progress) =>
        {
            await f.Store.TransactionAsync(e =>
            {
                var r = e.Current!; var next = r with { OutputRelativeRoot = $"voxcpm-training/actions/{r.ActionId}/executions/{replacement}",
                    Snapshot = r.Snapshot with { ExecutionId = replacement, Revision = r.Snapshot.Revision + 1, State = "queued", Phase = "queued" } };
                return (e with { Current = next }, true);
            }, Token);
            await progress(new("loading"));
        } };
        await Runner(f, executor).RunNextAsync(Token); var snapshot = (await f.Service.GetAsync(f.Id, Token))!;
        Assert.Equal(replacement, snapshot.ExecutionId); Assert.Equal("queued", snapshot.State); Assert.Equal("queued", snapshot.Phase); Assert.Equal(0, snapshot.GlobalStep); Assert.Empty(snapshot.Models);
    }
    [Theory]
    [InlineData("wrong-window")]
    [InlineData("wrong-cursor")]
    public async Task Invalid_candidate_never_advances_accepted_cursor(string fault)
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        await Runner(f, new() { Fault = fault }).RunNextAsync(Token); var s = (await f.Service.GetAsync(f.Id, Token))!;
        Assert.Equal(0, s.GlobalStep); Assert.Empty(s.Models); Assert.Equal("failed", s.State);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("unexpected")]
    public async Task Cleanup_unknown_retains_project_corpus_and_compute_owners(string fault)
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token); var gate = new ComputeGate(); var runner = Runner(f, new() { Fault = fault }, gate);
        try
        {
            var error = await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => runner.RunNextAsync(Token)); Assert.False(error.CleanupConfirmed);
            var s = (await f.Service.GetAsync(f.Id, Token))!; Assert.Empty(s.Models); Assert.False(s.CanResume); Assert.Equal(0, s.GlobalStep);
            Assert.False(await gate.WaitAsync(0, Token)); Assert.False(await runner.RunNextAsync(Token));
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(f.Root, f.Corpus.AudioPaths[0]), "changed"));
            await using var db = await f.Corpus.OpenAsync(Token);
            await Assert.ThrowsAsync<LocalAudiobookStudio.Core.Projects.ProjectException>(async () => { await using var ownership = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(40), Token); });
            await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => runner.StopAsync(Token));
        }
        finally { await ReleaseFakeOwnership(runner); }
    }
    [Fact]
    public async Task Late_STOP_before_execution_directory_exists_is_observed_without_dispatch()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var executor = new Executor(); var resolver = new StopDuringResolve(f);
        await Runner(f, executor, resolver: resolver).RunNextAsync(Token);
        Assert.Empty(executor.Visited); Assert.Equal("paused", (await f.Service.GetAsync(f.Id, Token))!.State);
    }
    [Fact]
    public async Task Shutdown_signals_execution_STOP_and_waits_for_owned_work_without_cancelling_it()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Executor { EmptyStop = true, Before = async (e, _) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(Token);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(e.OutputRoot)!)!, "STOP")));
        } };
        var runner = Runner(f, executor); var running = runner.RunNextAsync(Token); await entered.Task.WaitAsync(Token);
        var shutdown = runner.StopAsync(Token); Assert.False(shutdown.IsCompleted); release.SetResult(); await shutdown;
        Assert.True(await running); Assert.Equal("paused", (await f.Service.GetAsync(f.Id, Token))!.State);
    }
    [Fact]
    public void Capacity_policy_rejects_before_allocating_a_new_window()
    {
        Assert.Equal("voxcpm_training.disk.insufficient", Assert.Throws<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingRunner.EnsureCapacity(1, 64 * 1048576 - 1)).Code);
        Assert.Equal("voxcpm_training.state.limit", Assert.Throws<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingRunner.EnsureCapacity(512, long.MaxValue)).Code);
        VoxCpmTrainingRunner.EnsureCapacity(511, 64 * 1048576);
    }
    private static VoxCpmTrainingRunner Runner(VoxCpmTrainingFixture f, Executor executor, ComputeGate? gate = null, IApprovedTtsCorpusResolver? resolver = null)
        => new(new ProjectJobAccess(new(f.Corpus.Box.Root), f.Corpus.Box.Catalog(), new()), gate ?? new(), resolver ?? new ApprovedTtsCorpusResolver(), f.RuntimeProvider, f.Checkpoints, executor);
    private static async Task ReleaseFakeOwnership(VoxCpmTrainingRunner runner)
    {
        // No native child exists in this fixture. Product has no unsafe release hook.
        var owned = (System.Runtime.CompilerServices.ITuple?)typeof(VoxCpmTrainingRunner).GetField("retained", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runner);
        if (owned == null) return;
        for (int i = owned.Length - 1; i >= 0; i--)
        { if (owned[i] is IAsyncDisposable asyncOwned) await asyncOwned.DisposeAsync(); else if (owned[i] is IDisposable disposable) disposable.Dispose(); }
    }
    private sealed class StopDuringResolve(VoxCpmTrainingFixture f) : IApprovedTtsCorpusResolver
    {
        public async Task<ApprovedTtsCorpusLease> ResolveAsync(ProjectDatabase project, string versionId, string? expectedManifestSha256, CancellationToken token)
        {
            var source = await new ApprovedTtsCorpusResolver().ResolveAsync(project, versionId, expectedManifestSha256, token);
            var s = (await f.Service.GetAsync(f.Id, token))!;
            await f.Service.ControlAsync(f.Id, s.Id, s.Revision, "late-stop", "stop", token); return source;
        }
    }
    private sealed class Executor : IVoxCpmTrainingExecutor
    {
        internal bool StopWithCheckpoint, EmptyStop; internal string? Fault;
        internal Func<VoxCpmTrainingExecution, Func<VoxCpmTrainingProgress, Task>, Task>? Before;
        internal List<VoxCpmTrainingExecution> Visited { get; } = [];
        internal List<VoxCpmCheckpointReference> Returned { get; } = [];
        public async Task<VoxCpmTrainingWindowResult> RunAsync(VoxCpmTrainingExecution e, Func<VoxCpmTrainingProgress, Task> progress, CancellationToken token)
        {
            Assert.False(token.CanBeCanceled); Visited.Add(e); await progress(new("checkpointing")); if (Before != null) await Before(e, progress);
            if (Fault == "unknown") throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false);
            if (Fault == "null") return null!;
            if (Fault == "unexpected") throw new IOException("unreported native cleanup");
            if (EmptyStop) return new(null, true);
            string relative = Path.GetRelativePath(e.ProjectRoot, e.OutputRoot).Replace('\\', '/') + $"/checkpoints/{e.CheckpointId}/manifest.json";
            var candidate = new VoxCpmCheckpointReference(e.CheckpointId, e.TrainingId, e.ActionId, e.ExecutionId, e.WindowId,
                relative, new('f', 64), e.Cursor.Advance(e.Corpus.Clips.Select(c => c.Id).ToArray()), DateTimeOffset.UtcNow);
            if (Fault == "wrong-window") candidate = candidate with { WindowId = Guid.NewGuid().ToString("N") };
            if (Fault == "wrong-cursor") candidate = candidate with { Cursor = candidate.Cursor.Advance(e.Corpus.Clips.Select(c => c.Id).ToArray()) };
            Returned.Add(candidate); return new(candidate, StopWithCheckpoint);
        }
    }
}

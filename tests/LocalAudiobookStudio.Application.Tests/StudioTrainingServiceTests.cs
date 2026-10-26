using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Text;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class StudioTrainingServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Execution_identity_is_stable_during_progress_but_changes_on_resume()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var service = f.Service();
        var queued = await service.StartAsync(input.Id, input.Revision, "start", true, Token);
        Assert.NotNull(queued.ExecutionId);
        await f.Runner(new CompletingExecutor()).RunNextAsync(Token);
        var done = (await service.GetAsync(input.Id, Token))!;
        Assert.Equal(queued.ExecutionId, done.ExecutionId);
        var resumed = await service.ControlAsync(input.Id, done.Id, done.Revision, "resume", "resume", Token);
        Assert.NotEqual(done.ExecutionId, resumed.ExecutionId);
    }
    [Fact]
    public async Task Identical_filtered_progress_does_not_churn_the_control_ETag()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var service = f.Service();
        await service.StartAsync(input.Id, input.Revision, "start", true, Token);
        var versions = new List<long>();
        await f.Runner(new RepeatedProgressExecutor(async () => versions.Add((await service.GetAsync(input.Id, Token))!.Revision))).RunNextAsync(Token);
        Assert.Equal(20, versions.Count); Assert.Single(versions.Distinct());
    }
    [Theory]
    [InlineData("time_budget", "paused", "training.time_budget", 161, 0)]
    [InlineData("checkpoint_budget", "paused", "training.checkpoint_budget", 161, 0)]
    [InlineData("target_epoch", "completed", null, 200, 1)]
    public async Task Budget_stop_preserves_incomplete_epoch_as_resumable_pause(string reason, string state, string? diagnostic, long step, long epoch)
    {
        using var f = new Fixture(); var input = await f.Prepare(); var service = f.Service();
        await service.StartAsync(input.Id, input.Revision, "start", true, Token);
        await f.Runner(new CompletingExecutor(stopReason: reason, step: step, epoch: epoch, epochSize: 200)).RunNextAsync(Token);
        var ended = (await service.GetAsync(input.Id, Token))!;
        Assert.Equal(state, ended.State); Assert.Equal(diagnostic, ended.DiagnosticCode); Assert.True(ended.CanResume);
        Assert.Equal(step, ended.GlobalStep); Assert.Equal(epoch, ended.CompletedEpoch);
    }
    [Fact]
    public async Task Artifact_contention_preserves_the_observed_queue_and_stop_restart_can_replace_it()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var service = f.Service();
        var original = await service.StartAsync(input.Id, input.Revision, "start", true, Token);
        await using var db = await new ProjectService().OpenCurrentAsync(f.Box.ProjectRoot(input.Id), Token);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var gate = new ComputeGate(); var runner = f.Runner(new CompletingExecutor(), gate);
        Assert.False(await runner.RunNextAsync(Token));
        Assert.Equal("queued", (await service.GetAsync(input.Id, Token))!.State);
        var oldWait = runner.RunNextAsync(Token);
        while (await gate.WaitAsync(0, Token)) { gate.Release(); await Task.Delay(10, Token); }
        await service.ControlAsync(input.Id, original.Id, original.Revision, "stop", "stop", Token);
        var replacementTask = service.StartAsync(input.Id, input.Revision, "replacement", true, Token);
        await lease.DisposeAsync();
        var replacement = await replacementTask; await oldWait;
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.Equal("queued", (await service.GetAsync(input.Id, Token))!.State);
    }
    [Fact]
    public async Task Generation_enqueued_after_training_acceptance_defers_training_without_failure()
    {
        using var f = new Fixture(); var input = await f.PrepareGeneration(); var service = f.Service();
        await service.StartAsync(input.Id, input.Revision, "start", true, Token);
        await f.EnqueueGeneration(input.Id, input.Revision);
        Assert.False(await f.Runner(new CompletingExecutor()).RunNextAsync(Token));
        Assert.Equal("queued", (await service.GetAsync(input.Id, Token))!.State);
    }
    [Fact]
    public async Task Reading_missing_training_does_not_create_training_namespace()
    {
        using var f = new Fixture(); var input = await f.Prepare();
        Assert.Null(await f.Service().GetAsync(input.Id, Token));
        Assert.False(Directory.Exists(Path.Combine(f.Box.ProjectRoot(input.Id), "training")));
        await f.Runner(new CompletingExecutor()).RecoverAsync(Token);
        Assert.False(Directory.Exists(Path.Combine(f.Box.ProjectRoot(input.Id), "training")));
    }
    [Fact]
    public async Task Receipts_replay_before_project_CAS_and_stop_CAS_even_with_artifact_ownership_held()
    {
        using var f = new Fixture(); var input = await f.Prepare();
        var service = f.Service(); var queued = await service.StartAsync(input.Id, input.Revision, "start", true, Token);
        Assert.Equivalent(queued, await service.StartAsync(input.Id, 0, "start", true, Token));
        Assert.Equal("training.idempotency.conflict", (await Assert.ThrowsAsync<StudioException>(() => service.StartAsync(input.Id, input.Revision, "start", false, Token))).Code);
        await using var db = await new ProjectService().OpenCurrentAsync(f.Box.ProjectRoot(input.Id), Token);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var paused = await service.ControlAsync(input.Id, queued.Id, queued.Revision, "stop", "stop", Token);
        Assert.Equal("paused", paused.State); Assert.False(paused.CanResume);
        Assert.Equivalent(paused, await service.ControlAsync(input.Id, queued.Id, 0, "stop", "stop", Token));
        Assert.Equal("training.conflict", (await Assert.ThrowsAsync<StudioException>(() => service.ControlAsync(input.Id, queued.Id, 1, "stale", "stop", Token))).Code);
        Assert.Equal(input.Revision, (await db.ReadCurrentSnapshotAsync(Token)).State.Revision);
    }
    [Fact]
    public async Task Explicit_start_after_early_stop_restarts_preparation_with_new_identity()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var service = f.Service();
        var first = await service.StartAsync(input.Id, input.Revision, "first", true, Token);
        await service.ControlAsync(input.Id, first.Id, first.Revision, "stop", "stop", Token);
        var second = await service.StartAsync(input.Id, input.Revision, "restart", true, Token);
        Assert.NotEqual(first.Id, second.Id); Assert.Equal("queued", second.State); Assert.False(second.CanResume);
        Assert.Equivalent(first, await service.StartAsync(input.Id, 0, "first", true, Token));
    }
    [Fact]
    public async Task Stop_during_work_is_durable_without_waiting_for_artifact_lease()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var service = f.Service();
        await service.StartAsync(input.Id, input.Revision, "start", true, Token);
        var executor = new HoldingExecutor(); var runner = f.Runner(executor); var running = runner.RunNextAsync(Token);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var current = (await service.GetAsync(input.Id, Token))!;
        var stopped = await service.ControlAsync(input.Id, current.Id, current.Revision, "stop", "stop", Token).WaitAsync(TimeSpan.FromSeconds(2), Token);
        Assert.Equal("stop_requested", stopped.State);
        Assert.True(File.Exists(Path.Combine(executor.Execution!.OutputRoot, "STOP")));
        executor.Finish.TrySetResult(); await running;
        var end = (await service.GetAsync(input.Id, Token))!; Assert.Equal("paused", end.State); Assert.False(end.CanResume); Assert.Empty(end.Models);
    }
    [Fact]
    public async Task Restart_marks_queued_interrupted_and_shared_compute_defers_work()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var service = f.Service();
        await service.StartAsync(input.Id, input.Revision, "start", true, Token);
        var compute = new ComputeGate(); Assert.True(await compute.WaitAsync(0, Token));
        var runner = f.Runner(new CompletingExecutor(), compute);
        Assert.False(await runner.RunNextAsync(Token)); Assert.Equal("queued", (await service.GetAsync(input.Id, Token))!.State);
        compute.Release(); await runner.RecoverAsync(Token);
        Assert.Equal("interrupted", (await service.GetAsync(input.Id, Token))!.State);
        Assert.False(await runner.RunNextAsync(Token));
    }
    [Fact]
    public async Task Accepted_checkpoint_survives_later_failure_and_changed_recording_cannot_resume()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var service = f.Service();
        await service.StartAsync(input.Id, input.Revision, "start", true, Token);
        await f.Runner(new CompletingExecutor(failAfterCheckpoint: true)).RunNextAsync(Token);
        var failed = (await service.GetAsync(input.Id, Token))!;
        Assert.Equal("failed", failed.State); Assert.True(failed.CanResume); Assert.Single(failed.Models); Assert.Equal(1, failed.GlobalStep);
        await using var db = await new ProjectService().OpenCurrentAsync(f.Box.ProjectRoot(input.Id), Token);
        string recording = db.ResolveOwnedPath(db.State.VoiceRecording!.WavRelativePath);
        await File.AppendAllTextAsync(recording, "tamper", Token);
        var error = await Assert.ThrowsAsync<StudioException>(() => service.ControlAsync(input.Id, failed.Id, failed.Revision, "resume", "resume", Token));
        Assert.Equal("training.source.changed", error.Code); Assert.Equal("failed", (await service.GetAsync(input.Id, Token))!.State);
    }

    private sealed class Fixture : IDisposable
    {
        public CatalogSandbox Box { get; } = new();
        private readonly TrainingRuntime runtime;
        public Fixture()
        {
            string python = Path.Combine(Box.Root, "python.exe"), shell = Path.Combine(Box.Root, "pwsh.exe");
            File.WriteAllText(python, "inert-python"); File.WriteAllText(shell, "inert-shell");
            File.WriteAllText(Path.Combine(Box.Root, "Run-RvcWorkflow.py"), "inert-workflow");
            runtime = new(new(python, shell, Box.Root, Box.Root, Box.Root, Box.Root, Box.Root), new TrainingRuntimeTests.InertInspector(python));
        }
        public StudioTrainingService Service() => new(Access(), runtime);
        private ProjectJobAccess Access() => new(new(Box.Root), Box.Catalog(), new ProjectService());
        public TrainingRunner Runner(ITrainingWorkflowExecutor executor, ComputeGate? gate = null) => new(Access(), gate ?? new(), executor);
        public async Task<(string Id, long Revision)> PrepareGeneration()
        {
            var input = await Prepare();
            await using (var db = await new ProjectService().OpenCurrentAsync(Box.ProjectRoot(input.Id), Token))
                await new VoiceRecordingService(new(48000)).ConfirmAnchorAsync(db, new(0, 10000, "Synthetic recording.", true), Token);
            var project = await Box.Service().GetAsync(input.Id, Token);
            var text = await new StudioTextService(Box.Service()).ImportPastedAsync(input.Id, project.Revision, new("Synthetic", "Синтетический текст."), Token);
            return (input.Id, text.Revision);
        }
        public async Task EnqueueGeneration(string id, long revision)
        {
            var snapshot = await Box.Service().GetAsync(id, Token);
            await new StudioJobService(Access(), new StudioJobServiceTests.Runtime()).EnqueueAsync(id, revision, "generation", new(LocalAudiobookStudio.Core.Jobs.JobKind.Preview, [snapshot.Segments[0].Id]), Token);
        }
        public async Task<(string Id, long Revision)> Prepare()
        {
            var project = (await Box.Service().CreateAsync(new("Synthetic"), "project", Token)).Snapshot;
            await using var db = await new ProjectService().OpenAsync(Box.ProjectRoot(project.Id), Token, retainSidecars: true);
            byte[] wav = new byte[960044]; Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 48000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 96000);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
            Encoding.ASCII.GetBytes("data").CopyTo(wav, 36); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), 960000);
            await new VoiceRecordingService(new(48000)).ImportAsync(db, new("recording.wav", true), new MemoryStream(wav), Token);
            return (project.Id, db.State.Revision);
        }
        public void Dispose() => Box.Dispose();
    }
    private sealed class HoldingExecutor : ITrainingWorkflowExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TrainingExecution? Execution { get; private set; }
        public async Task<WorkflowReference?> RunAsync(TrainingExecution execution, Func<TrainingProgress, Task> progress, Func<WorkflowReference, Task> checkpoint, CancellationToken token)
        { Execution = execution; await progress(new("session_preparing", 0, 0, 0)); Started.TrySetResult(); await Finish.Task.WaitAsync(token); return null; }
    }
    private sealed class RepeatedProgressExecutor(Func<Task> observe) : ITrainingWorkflowExecutor
    {
        public async Task<WorkflowReference?> RunAsync(TrainingExecution execution, Func<TrainingProgress, Task> progress, Func<WorkflowReference, Task> checkpoint, CancellationToken token)
        {
            for (int i = 0; i < 20; i++) { await progress(new("session_batch_ready", 1, 0, 2)); await observe(); }
            return null;
        }
    }
    private sealed class CompletingExecutor(bool failAfterCheckpoint = false, string stopReason = "initial_update", long step = 1, long epoch = 0, int epochSize = 2) : ITrainingWorkflowExecutor
    {
        public async Task<WorkflowReference?> RunAsync(TrainingExecution execution, Func<TrainingProgress, Task> progress, Func<WorkflowReference, Task> checkpoint, CancellationToken token)
        {
            var point = new WorkflowReference(Path.Combine(execution.OutputRoot, "initial-state", "workflow_state.json"), new string('a', 64), step, epoch, epochSize, stopReason);
            await checkpoint(point);
            if (failAfterCheckpoint) throw new TrainingExecutionException("training.session.failed");
            return point;
        }
    }
}

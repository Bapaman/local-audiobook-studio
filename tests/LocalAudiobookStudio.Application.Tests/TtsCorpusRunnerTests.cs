using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Projects;
using Xunit;
using static LocalAudiobookStudio.Application.Tests.TtsCorpusServiceTests;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class TtsCorpusRunnerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Recovery_interrupts_known_work_and_does_not_create_missing_corpus_directories()
    {
        using var f = new Fixture(); var p = await f.Prepare();
        var other = (await f.Box.Service().CreateAsync(new("No corpus"), "other-project", Token)).Snapshot;
        await f.Service().StartAsync(p.Id, p.Revision, "start", true, Token);
        var runner = Runner(f, new()); await Recover(runner);
        var s = await f.Service().GetAsync(p.Id, Token);
        Assert.Equal("interrupted", s!.State); Assert.True(s.CanResume);
        Assert.False(await runner.RunNextAsync(Token));
        Assert.False(Directory.Exists(Path.Combine(f.Root(other.Id), "tts-corpus")));
    }
    [Theory]
    [InlineData("runtime")]
    [InlineData("source")]
    [InlineData("payload")]
    public async Task Recovery_disables_resume_for_changed_dependencies(string change)
    {
        using var f = new Fixture(); var p = await f.Prepare(); await f.Draft(p, "transcribing", acceptedCount: 1);
        if (change == "runtime") f.Runtime.Fingerprint = new string('b', 64);
        else if (change == "source") await File.AppendAllTextAsync(Path.Combine(f.Root(p.Id), "voice-recording", (await f.Service().GetAsync(p.Id, Token))!.SourceRecordingId + ".wav"), "bad", Token);
        else await File.AppendAllTextAsync(await f.Audio(p.Id), "bad", Token);
        await Recover(Runner(f, new()));
        var s = await f.Service().GetAsync(p.Id, Token); Assert.Equal("interrupted", s!.State); Assert.False(s.CanResume);
    }
    [Fact]
    public async Task Recovery_refreshes_terminal_eligibility_when_exact_runtime_returns_without_auto_dispatch()
    {
        using var f = new Fixture(); var p = await f.Prepare(); await f.Draft(p, "transcribing", acceptedCount: 1);
        f.Runtime.Fingerprint = new string('b', 64);
        await Recover(Runner(f, new()));
        var unavailable = (await f.Service().GetAsync(p.Id, Token))!; Assert.False(unavailable.CanResume);
        f.Runtime.Fingerprint = new string('a', 64);
        var runner = Runner(f, new()); await Recover(runner);
        var restored = (await f.Service().GetAsync(p.Id, Token))!;
        Assert.True(restored.CanResume); Assert.Equal("interrupted", restored.State); Assert.Equal(1, restored.CompletedClipCount);
        Assert.Equal(unavailable.ExecutionId, restored.ExecutionId); Assert.False(await runner.RunNextAsync(Token));
        await Recover(runner); Assert.Equal(restored.Revision, (await f.Service().GetAsync(p.Id, Token))!.Revision);
    }

    private static async Task Recover(TtsCorpusRunner runner)
        => await runner.VerifyResumeOnStartupAsync(await runner.InterruptOnStartupAsync(Token), Token);
    [Fact]
    public async Task Shared_compute_excludes_dispatch_and_releases_after_clean_success()
    {
        using var f = new Fixture(); var p = await f.Prepare(); await f.Service().StartAsync(p.Id, p.Revision, "start", true, Token);
        var compute = new ComputeGate(); var runner = Runner(f, new(), compute);
        Assert.True(await compute.WaitAsync(0, Token)); Assert.False(await runner.RunNextAsync(Token));
        Assert.Equal("queued", (await f.Service().GetAsync(p.Id, Token))!.State); compute.Release();
        Assert.True(await runner.RunNextAsync(Token)); Assert.Equal("completed", (await f.Service().GetAsync(p.Id, Token))!.State);
        Assert.True(await compute.WaitAsync(0, Token)); compute.Release();
    }
    [Fact]
    public async Task Preparation_then_exact_eight_clip_windows_advance_only_accepted_cursor()
    {
        using var f = new Fixture(); var p = await f.Prepare(); await f.Service().StartAsync(p.Id, p.Revision, "start", true, Token);
        var native = new Executor { ClipCount = 17 };
        native.Before = async (e, progress) =>
        {
            await progress(new("transcribing"));
            Assert.Equal(e.NextClipIndex, (await f.Service().GetAsync(p.Id, Token))!.CompletedClipCount);
        };
        Assert.True(await Runner(f, native).RunNextAsync(Token));
        var s = await f.Service().GetAsync(p.Id, Token); Assert.Equal("completed", s!.State); Assert.Equal(17, s.CompletedClipCount);
        Assert.Equal(new[] { 0, 0, 8, 16 }, native.Cursors); Assert.All(s.Clips, c => Assert.False(c.Reviewed));
    }
    [Fact]
    public async Task Stop_retains_accepted_prefix_and_resume_rotates_execution_without_repeating_it()
    {
        using var f = new Fixture(); var p = await f.Prepare(); var draft = await f.Draft(p, "paused", acceptedCount: 1);
        var queued = await f.Service().ControlAsync(p.Id, draft.Id, draft.Revision, "resume", "resume", Token);
        var native = new Executor { Stop = true };
        Assert.True(await Runner(f, native).RunNextAsync(Token));
        var paused = await f.Service().GetAsync(p.Id, Token); Assert.Equal("paused", paused!.State); Assert.Equal(1, paused.CompletedClipCount);
        Assert.Equal(new[] { 1 }, native.Cursors); Assert.NotEqual(draft.ExecutionId, queued.ExecutionId);
        var resumed = await f.Service().ControlAsync(p.Id, paused.Id, paused.Revision, "resume2", "resume", Token);
        Assert.True(await Runner(f, new()).RunNextAsync(Token));
        var done = await f.Service().GetAsync(p.Id, Token); Assert.Equal("completed", done!.State); Assert.Equivalent(draft.Clips[0], done.Clips[0]); Assert.Equal(resumed.ExecutionId, done.ExecutionId);
    }
    [Theory]
    [InlineData("reverse")]
    [InlineData("short")]
    [InlineData("empty")]
    [InlineData("failed")]
    [InlineData("tamper")]
    public async Task Invalid_or_failed_window_never_adopts_any_of_its_outputs(string mode)
    {
        using var f = new Fixture(); var p = await f.Prepare(); await f.Service().StartAsync(p.Id, p.Revision, "start", true, Token);
        var native = new Executor { Mode = mode };
        Assert.True(await Runner(f, native).RunNextAsync(Token));
        var s = await f.Service().GetAsync(p.Id, Token); Assert.Equal("failed", s!.State); Assert.Empty(s.Clips); Assert.Equal(0, s.CompletedClipCount);
        Assert.StartsWith("tts_corpus.", s.DiagnosticCode); Assert.Equal(2, native.Cursors.Count);
    }
    [Theory]
    [InlineData("unknown-result")]
    [InlineData("unknown-throw")]
    [InlineData("null-result")]
    [InlineData("cancelled")]
    public async Task Cleanup_unknown_rejects_outputs_and_retains_source_project_compute_ownership(string mode)
    {
        using var f = new Fixture(); var p = await f.Prepare(); await f.Service().StartAsync(p.Id, p.Revision, "start", true, Token);
        var compute = new ComputeGate(); var runner = Runner(f, new() { Mode = mode }, compute);
        try
        {
            var error = await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => runner.RunNextAsync(Token)); Assert.False(error.CleanupConfirmed);
            var s = await f.Service().GetAsync(p.Id, Token); Assert.Empty(s!.Clips); Assert.Null(s.TotalClipCount);
            Assert.False(await compute.WaitAsync(0, Token)); Assert.False(await runner.RunNextAsync(Token));
            await Assert.ThrowsAsync<IOException>(() => File.AppendAllTextAsync(Path.Combine(f.Root(p.Id), "voice-recording", s.SourceRecordingId + ".wav"), "bad", Token));
            await using var db = await new ProjectService().OpenCurrentAsync(f.Root(p.Id), Token);
            await Assert.ThrowsAnyAsync<Exception>(async () => { await using var lease = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(40), Token); });
            await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => runner.StopAsync(Token));
        }
        finally { await ReleaseRetainedTestOwnership(runner); }
    }
    [Theory]
    [InlineData("runtime")]
    [InlineData("source")]
    public async Task Claim_revalidates_source_and_runtime_before_native_dispatch(string change)
    {
        using var f = new Fixture(); var p = await f.Prepare(); var q = await f.Service().StartAsync(p.Id, p.Revision, "start", true, Token);
        if (change == "runtime") f.Runtime.Fingerprint = new string('b', 64);
        else await File.AppendAllTextAsync(Path.Combine(f.Root(p.Id), "voice-recording", q.SourceRecordingId + ".wav"), "bad", Token);
        var native = new Executor(); await Runner(f, native).RunNextAsync(Token);
        var s = await f.Service().GetAsync(p.Id, Token); Assert.Equal("failed", s!.State); Assert.Equal("tts_corpus." + change + ".changed", s.DiagnosticCode); Assert.Empty(native.Cursors);
    }
    [Fact]
    public async Task Stop_after_output_creation_signals_owned_execution_and_shutdown_waits_for_cleanup()
    {
        using var f = new Fixture(); var p = await f.Prepare(); await f.Service().StartAsync(p.Id, p.Revision, "start", true, Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new Executor { Stop = true, Before = async (e, _) =>
        {
            Assert.True(Directory.Exists(e.OutputRoot)); entered.SetResult(); await release.Task.WaitAsync(Token);
            Assert.True(File.Exists(Path.Combine(e.OutputRoot, "STOP")));
        } };
        var runner = Runner(f, native); var run = runner.RunNextAsync(Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var shutdown = runner.StopAsync(Token); Assert.False(shutdown.IsCompleted); release.SetResult(); await shutdown;
        Assert.True(await run); Assert.Equal("paused", (await f.Service().GetAsync(p.Id, Token))!.State); Assert.False(await runner.RunNextAsync(Token));
    }
    [Fact]
    public async Task Successful_stopped_window_accepts_only_its_exact_short_prefix_then_resumes_after_it()
    {
        using var f = new Fixture(); var p = await f.Prepare(480001);
        await f.Service().StartAsync(p.Id, p.Revision, "start", true, Token);
        await Runner(f, new() { ClipCount = 17, Mode = "stop-prefix" }).RunNextAsync(Token);
        var paused = (await f.Service().GetAsync(p.Id, Token))!;
        Assert.Equal("paused", paused.State); Assert.Equal(3, paused.CompletedClipCount); Assert.True(paused.CanResume);
        await f.Service().ControlAsync(p.Id, paused.Id, paused.Revision, "resume", "resume", Token);
        var native = new Executor(); await Runner(f, native).RunNextAsync(Token);
        var done = (await f.Service().GetAsync(p.Id, Token))!;
        Assert.Equal("completed", done.State); Assert.Equal(17, done.CompletedClipCount); Assert.Equal(160001, done.Clips[^1].EndSample);
        Assert.Equal(new[] { 3, 11 }, native.Cursors);
    }
    [Fact]
    public async Task Old_execution_callbacks_and_results_cannot_mutate_replacement_execution()
    {
        using var f = new Fixture(); var p = await f.Prepare();
        await f.Service().StartAsync(p.Id, p.Revision, "start", true, Token);
        string replacement = Guid.NewGuid().ToString("N");
        var native = new Executor { Before = async (_, progress) =>
        {
            await using var db = await new ProjectService().OpenCurrentAsync(f.Root(p.Id), Token);
            await StudioTtsCorpusService.Store(db).TransactionAsync(e =>
            {
                var r = e.Current!;
                return (e with { Current = r with { OutputRelativeRoot = $"tts-corpus/actions/{r.ActionId}/executions/{replacement}",
                    Snapshot = r.Snapshot with { ExecutionId = replacement, State = "queued", Phase = "queued", Revision = r.Snapshot.Revision + 1 } } }, true);
            }, Token);
            await progress(new("transcribing"));
        } };
        await Runner(f, native).RunNextAsync(Token);
        var s = (await f.Service().GetAsync(p.Id, Token))!;
        Assert.Equal(replacement, s.ExecutionId); Assert.Equal("queued", s.State); Assert.Equal("queued", s.Phase); Assert.Null(s.DiagnosticCode); Assert.Null(s.TotalClipCount); Assert.Empty(s.Clips);
    }
    private static TtsCorpusRunner Runner(Fixture f, Executor native, ComputeGate? compute = null) => new(f.Access(), compute ?? new(), f.Runtime, native);
    private static async Task ReleaseRetainedTestOwnership(TtsCorpusRunner runner)
    {
        // Production deliberately has no unsafe release hook. The fake launched no child;
        // dispose its retained handles here solely so the test sandbox can be removed.
        var owned = (System.Runtime.CompilerServices.ITuple?)typeof(TtsCorpusRunner)
            .GetField("retained", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runner);
        if (owned == null) return;
        for (int i = owned.Length - 1; i >= 0; i--)
        {
            if (owned[i] is IAsyncDisposable asyncOwned) await asyncOwned.DisposeAsync();
            else if (owned[i] is IDisposable disposable) disposable.Dispose();
        }
    }
    private sealed class Executor : ITtsCorpusExecutor
    {
        public int ClipCount = 2; public string? Mode; public bool Stop;
        public List<int> Cursors { get; } = [];
        public Func<TtsCorpusExecution, Func<TtsCorpusProgress, Task>, Task>? Before;
        public async Task<TtsCorpusWindowResult> RunAsync(TtsCorpusExecution e, Func<TtsCorpusProgress, Task> progress, CancellationToken token)
        {
            Cursors.Add(e.NextClipIndex); if (Before != null) await Before(e, progress);
            if (Mode == "unknown-throw") throw new TtsCorpusExecutionException("tts_corpus.native.cleanup", false);
            if (Mode == "null-result") return null!;
            if (Mode == "cancelled") throw new OperationCanceledException();
            if (Stop) return new(null, [], Stopped: true);
            if (e.PreparedPlan == null)
            {
                string relative = Path.GetRelativePath(e.ProjectRoot, e.OutputRoot).Replace('\\', '/');
                long total = (e.SourceSampleCount + 2) / 3; var clips = new List<TtsCorpusPlannedClip>();
                for (int i = 0; i < ClipCount; i++)
                {
                    long start = i * total / ClipCount, end = (i + 1) * total / ClipCount; string path = relative + "/" + i + ".wav";
                    byte[] wav = Wav(16000, (int)(end - start)); await File.WriteAllBytesAsync(Path.Combine(e.ProjectRoot, path), wav, token);
                    clips.Add(new(Guid.NewGuid().ToString("N"), i, start, end, path, Hash(wav)));
                }
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(clips); string planPath = relative + "/plan.json"; await File.WriteAllBytesAsync(Path.Combine(e.ProjectRoot, planPath), bytes, token);
                return new(new(planPath, Hash(bytes), e.SourceSha256, e.RuntimeFingerprint, total, clips), [], CleanupConfirmed: Mode != "unknown-result");
            }
            var accepted = new List<TtsCorpusAcceptedClip>();
            foreach (var clip in e.PreparedPlan.Clips.Skip(e.NextClipIndex).Take(8))
            {
                string path = Path.GetRelativePath(e.ProjectRoot, e.OutputRoot).Replace('\\', '/') + "/text-" + clip.Id + ".json";
                byte[] text = Encoding.UTF8.GetBytes("draft " + clip.Index); await File.WriteAllBytesAsync(Path.Combine(e.ProjectRoot, path), text, token);
                accepted.Add(new(clip, path, Hash(text), "draft " + clip.Index, [], []));
            }
            if (Mode == "failed") throw new TtsCorpusExecutionException("tts_corpus.native.failed");
            if (Mode == "reverse") accepted.Reverse();
            if (Mode == "short") accepted.RemoveAt(accepted.Count - 1);
            if (Mode == "empty") accepted.Clear();
            if (Mode == "tamper") await File.AppendAllTextAsync(Path.Combine(e.ProjectRoot, accepted[0].TextRelativePath), "tamper", token);
            if (Mode == "stop-prefix") return new(null, accepted.Take(3).ToArray(), Stopped: true);
            return new(null, accepted);
        }
    }
}

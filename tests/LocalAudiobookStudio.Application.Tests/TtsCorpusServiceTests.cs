using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class TtsCorpusServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Missing_read_does_not_create_namespace_and_default_runtime_cannot_start()
    {
        using var f = new Fixture(); var input = await f.Prepare();
        var service = new StudioTtsCorpusService(f.Access(), new UnavailableTtsCorpusRuntime());
        Assert.Null(await service.GetAsync(input.Id, Token));
        Assert.False(Directory.Exists(Path.Combine(f.Root(input.Id), "tts-corpus")));
        var ready = await service.GetReadinessAsync(Token);
        Assert.False(ready.Ready); Assert.Equal("whisper-large-v3-turbo", ready.Provider); Assert.Equal(2, ready.CpuThreads);
        await Error("tts_corpus.runtime.not_ready", () => service.StartAsync(input.Id, input.Revision, "start", true, Token));
    }
    [Fact]
    public async Task Start_requires_confirmation_project_CAS_and_valid_source()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var s = f.Service();
        await Error("tts_corpus.confirmation.required", () => s.StartAsync(input.Id, input.Revision, "a", false, Token));
        await Error("project.conflict", () => s.StartAsync(input.Id, 0, "b", true, Token));
        await using var db = await new ProjectService().OpenCurrentAsync(f.Root(input.Id), Token);
        await File.AppendAllTextAsync(db.ResolveOwnedPath(db.State.VoiceRecording!.WavRelativePath), "bad", Token);
        await Error("tts_corpus.source.changed", () => s.StartAsync(input.Id, input.Revision, "c", true, Token));
    }
    [Fact]
    public async Task Receipt_replays_before_CAS_and_key_reuse_fails_without_waiting_for_ownership()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var s = f.Service();
        var first = await s.StartAsync(input.Id, input.Revision, "start", true, Token);
        await using var db = await new ProjectService().OpenCurrentAsync(f.Root(input.Id), Token);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        Assert.Equivalent(first, await s.StartAsync(input.Id, 0, "start", true, Token));
        await Error("tts_corpus.idempotency.conflict", () => s.StartAsync(input.Id, 0, "start", false, Token));
        var stopped = await s.ControlAsync(input.Id, first.Id, first.Revision, "stop", "stop", Token);
        Assert.Equal("paused", stopped.State);
        Assert.Equivalent(stopped, await s.ControlAsync(input.Id, first.Id, 0, "stop", "stop", Token));
        await Error("tts_corpus.conflict", () => s.ControlAsync(input.Id, first.Id, 1, "new", "stop", Token));
    }
    [Fact]
    public async Task Editing_text_or_inclusion_requires_a_separate_review_request_and_replay_is_durable()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var s = f.Service(); var draft = await f.Draft(input);
        var clip = draft.Clips[0];
        var changed = await s.UpdateClipAsync(input.Id, draft.Id, clip.Id, draft.Revision, "edit", new("  corrected\n text  ", false, true), Token);
        Assert.Equal("corrected text", changed.Clips[0].Text); Assert.False(changed.Clips[0].Reviewed);
        var reviewed = await s.UpdateClipAsync(input.Id, draft.Id, clip.Id, changed.Revision, "review", new("corrected text", false, true), Token);
        Assert.True(reviewed.Clips[0].Reviewed);
        Assert.Equivalent(changed, await f.Service().UpdateClipAsync(input.Id, draft.Id, clip.Id, 0, "edit", new("  corrected\n text  ", false, true), Token));
        await Error("tts_corpus.idempotency.conflict", () => s.UpdateClipAsync(input.Id, draft.Id, clip.Id, reviewed.Revision, "edit", new("other", false, false), Token));
        var excluded = await s.UpdateClipAsync(input.Id, draft.Id, clip.Id, reviewed.Revision, "exclude", new("corrected text", true, true), Token);
        Assert.False(excluded.Clips[0].Reviewed); Assert.Equal(5, excluded.RetainedDurationSeconds);
    }
    [Theory]
    [InlineData("")]
    [InlineData(" \t\n ")]
    [InlineData("bad\0text")]
    public async Task Included_invalid_text_is_rejected(string text)
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input);
        await Error("tts_corpus.text.invalid", () => f.Service().UpdateClipAsync(input.Id, d.Id, d.Clips[0].Id, d.Revision, "edit", new(text, false, false), Token));
    }
    [Fact]
    public async Task Text_limit_is_applied_after_whitespace_normalization()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input); var s = f.Service();
        await Error("tts_corpus.text.invalid", () => s.UpdateClipAsync(input.Id, d.Id, d.Clips[0].Id, d.Revision, "long", new(new string('x', 4001), false, false), Token));
        var valid = await s.UpdateClipAsync(input.Id, d.Id, d.Clips[0].Id, d.Revision, "ok", new(" " + new string('x', 4000) + " ", false, false), Token);
        Assert.Equal(4000, valid.Clips[0].Text.Length);
    }
    [Fact]
    public async Task Approval_is_immutable_excludes_audio_and_preserves_versions_on_new_start()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input); var s = f.Service();
        d = await s.UpdateClipAsync(input.Id, d.Id, d.Clips[1].Id, d.Revision, "exclude", new("draft two", true, false), Token);
        d = await s.UpdateClipAsync(input.Id, d.Id, d.Clips[0].Id, d.Revision, "review", new("draft one", false, true), Token);
        var approved = await s.ApproveAsync(input.Id, d.Id, d.Revision, "approve", true, Token);
        var version = Assert.Single(approved.Versions); Assert.Equal(1, version.ClipCount); Assert.Equal(5, version.RetainedDurationSeconds);
        var manifestPath = await f.Manifest(input.Id, version.Id); byte[] before = await File.ReadAllBytesAsync(manifestPath, Token);
        using var json = JsonDocument.Parse(before); var clips = json.RootElement.GetProperty("Clips"); Assert.Equal(1, clips.GetArrayLength());
        Assert.Equal("draft one", clips[0].GetProperty("Text").GetString());
        var edited = await s.UpdateClipAsync(input.Id, d.Id, d.Clips[0].Id, approved.Revision, "after", new("new words", false, true), Token);
        Assert.False(edited.Clips[0].Reviewed); Assert.Equal(before, await File.ReadAllBytesAsync(manifestPath, Token));
        var replacement = await s.StartAsync(input.Id, input.Revision, "replacement", true, Token);
        Assert.NotEqual(d.Id, replacement.Id); Assert.Single(replacement.Versions);
        Assert.Equal(before, await File.ReadAllBytesAsync(manifestPath, Token));
        Assert.Equivalent(approved, await s.ApproveAsync(input.Id, d.Id, 0, "approve", true, Token));
    }
    [Theory]
    [InlineData("completed", false, false, "tts_corpus.confirmation.required")]
    [InlineData("completed", true, false, "tts_corpus.review.required")]
    [InlineData("preparing", true, false, "tts_corpus.active")]
    [InlineData("completed", true, true, "tts_corpus.empty")]
    public async Task Approval_refuses_unconfirmed_unreviewed_active_or_empty(string state, bool confirmation, bool excludeAll, string code)
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input, state, excludeAll);
        await Error(code, () => f.Service().ApproveAsync(input.Id, d.Id, d.Revision, "approve", confirmation, Token));
    }
    [Fact]
    public async Task Resume_keeps_identity_and_rejects_changed_runtime_source_and_full_draft()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input, "paused", acceptedCount: 1); var s = f.Service();
        Assert.True(d.CanResume); f.Runtime.Fingerprint = new string('b', 64);
        await Error("tts_corpus.runtime.changed", () => s.ControlAsync(input.Id, d.Id, d.Revision, "runtime", "resume", Token));
        f.Runtime.Fingerprint = new string('a', 64);
        var resumed = await s.ControlAsync(input.Id, d.Id, d.Revision, "resume", "resume", Token);
        Assert.Equal(d.Id, resumed.Id); Assert.NotEqual(d.ExecutionId, resumed.ExecutionId); Assert.Equal(1, resumed.CompletedClipCount); Assert.Equal("queued", resumed.State);
        var full = await f.Draft(input);
        await Error("tts_corpus.transition.invalid", () => s.ControlAsync(input.Id, full.Id, full.Revision, "full", "resume", Token));
        var partial = await f.Draft(input, "paused", acceptedCount: 1);
        await using var db = await new ProjectService().OpenCurrentAsync(f.Root(input.Id), Token);
        await File.AppendAllTextAsync(db.ResolveOwnedPath(db.State.VoiceRecording!.WavRelativePath), "tamper", Token);
        await Error("tts_corpus.source.changed", () => s.ControlAsync(input.Id, partial.Id, partial.Revision, "source", "resume", Token));
    }
    [Fact]
    public async Task Audio_is_seekable_hash_checked_and_playback_allows_project_ownership()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input); var s = f.Service();
        await using var handle = await s.OpenAudioAsync(input.Id, d.Id, d.Clips[0].Id, Token);
        Assert.True(handle.Content.CanSeek); Assert.Equal(160044, handle.Length);
        await using var db = await new ProjectService().OpenCurrentAsync(f.Root(input.Id), Token);
        await using (var lease = await db.AcquireInputMutationLeaseAsync(Token)) { }
        string audio = await f.Audio(input.Id);
        await Assert.ThrowsAsync<IOException>(() => File.AppendAllTextAsync(audio, "bad", Token));
        await handle.DisposeAsync();
        await File.AppendAllTextAsync(audio, "bad", Token);
        await Error("tts_corpus.audio.invalid", () => s.OpenAudioAsync(input.Id, d.Id, d.Clips[0].Id, Token));
    }
    [Fact]
    public async Task Unknown_corrupt_state_fails_closed()
    {
        using var f = new Fixture(); var input = await f.Prepare(); await f.Draft(input);
        string state = Path.Combine(f.Root(input.Id), "tts-corpus", "state.json");
        var text = await File.ReadAllTextAsync(state, Token); await File.WriteAllTextAsync(state, text.Replace("\"Format\":1", "\"Format\":2"), Token);
        await Error("tts_corpus.state.invalid", () => f.Service().GetAsync(input.Id, Token));
    }
    [Theory]
    [InlineData("control")]
    [InlineData("duration")]
    [InlineData("source-path")]
    public async Task Store_rejects_internally_inconsistent_state_before_replacement(string mutation)
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input);
        await using var db = await new ProjectService().OpenCurrentAsync(f.Root(input.Id), Token);
        await Error("tts_corpus.state.invalid", () => StudioTtsCorpusService.Store(db).TransactionAsync(e =>
        {
            var r = e.Current!;
            r = mutation switch
            {
                "control" => r with { Snapshot = r.Snapshot with { Clips = [r.Snapshot.Clips[0] with { Text = "bad\0text" }, r.Snapshot.Clips[1]] } },
                "duration" => r with { Snapshot = r.Snapshot with { SourceDurationSeconds = 99 } },
                _ => r with { Recording = r.Recording with { WavRelativePath = "voice-recording/unbound.wav" } }
            };
            return (e with { Current = r }, true);
        }, Token));
        Assert.Equivalent(d, await f.Service().GetAsync(input.Id, Token));
    }
    [Fact]
    public async Task Stop_of_active_work_commits_and_signals_without_artifact_ownership()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input, "transcribing", acceptedCount: 1);
        await using var db = await new ProjectService().OpenCurrentAsync(f.Root(input.Id), Token);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var stopped = await f.Service().ControlAsync(input.Id, d.Id, d.Revision, "stop", "stop", Token).WaitAsync(TimeSpan.FromSeconds(2), Token);
        Assert.Equal("stop_requested", stopped.State);
        bool exists = await StudioTtsCorpusService.Store(db).TransactionAsync(e => ((TtsCorpusEnvelope?)null, File.Exists(db.ResolveOwnedPath(e.Current!.OutputRelativeRoot + "/STOP"))), Token);
        Assert.True(exists);
    }
    [Fact]
    public async Task Clip_and_approval_writes_reject_stale_revision_wrong_identity_and_active_edit()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input); var s = f.Service();
        await Error("tts_corpus.conflict", () => s.UpdateClipAsync(input.Id, d.Id, d.Clips[0].Id, 0, "stale", new("draft one", false, true), Token));
        await Error("tts_corpus.conflict", () => s.ApproveAsync(input.Id, d.Id, 0, "stale-approval", true, Token));
        await Error("tts_corpus.missing", () => s.UpdateClipAsync(input.Id, Guid.NewGuid().ToString("N"), d.Clips[0].Id, d.Revision, "wrong", new("draft one", false, true), Token));
        await Error("tts_corpus.clip.missing", () => s.UpdateClipAsync(input.Id, d.Id, Guid.NewGuid().ToString("N"), d.Revision, "wrong-clip", new("draft one", false, true), Token));
        d = await f.Draft(input, "preparing");
        await Error("tts_corpus.active", () => s.UpdateClipAsync(input.Id, d.Id, d.Clips[0].Id, d.Revision, "active", new("draft one", false, true), Token));
    }
    [Fact]
    public async Task Tampered_payload_blocks_next_approval_and_preserves_prior_manifest_and_revision()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input); var s = f.Service();
        foreach (var clip in d.Clips) d = await s.UpdateClipAsync(input.Id, d.Id, clip.Id, d.Revision, "review-" + clip.Id, new(clip.Text, false, true), Token);
        d = await s.ApproveAsync(input.Id, d.Id, d.Revision, "first-approval", true, Token);
        string manifest = await f.Manifest(input.Id, d.Versions[0].Id); byte[] before = await File.ReadAllBytesAsync(manifest, Token);
        string audio = await f.Audio(input.Id); byte[] wav = await File.ReadAllBytesAsync(audio, Token); wav[^1] = 1; await File.WriteAllBytesAsync(audio, wav, Token);
        await Error("tts_corpus.audio.invalid", () => s.ApproveAsync(input.Id, d.Id, d.Revision, "second-approval", true, Token));
        Assert.Equivalent(d, await s.GetAsync(input.Id, Token)); Assert.Equal(before, await File.ReadAllBytesAsync(manifest, Token));
    }
    [Fact]
    public async Task Nondivisible_source_count_keeps_original_duration_and_accepts_ceil_resampling_plan()
    {
        using var f = new Fixture(); var input = await f.Prepare(480001); var d = await f.Draft(input);
        Assert.Equal(480001 / 48000.0, d.SourceDurationSeconds); Assert.Equal(160001, d.Clips[^1].EndSample);
    }
    [Fact]
    public async Task Queued_stop_can_resume_preparation_with_same_corpus_action_and_new_execution()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var s = f.Service();
        var queued = await s.StartAsync(input.Id, input.Revision, "start", true, Token);
        var paused = await s.ControlAsync(input.Id, queued.Id, queued.Revision, "stop", "stop", Token);
        Assert.True(paused.CanResume);
        f.Runtime.Fingerprint = new string('b', 64);
        await Error("tts_corpus.runtime.changed", () => s.ControlAsync(input.Id, paused.Id, paused.Revision, "changed", "resume", Token));
        f.Runtime.Fingerprint = new string('a', 64);
        await using var db = await new ProjectService().OpenCurrentAsync(f.Root(input.Id), Token);
        string action = await StudioTtsCorpusService.Store(db).TransactionAsync(e => ((TtsCorpusEnvelope?)null, e.Current!.ActionId), Token);
        var resumed = await s.ControlAsync(input.Id, paused.Id, paused.Revision, "resume", "resume", Token);
        Assert.Equal(paused.Id, resumed.Id); Assert.NotEqual(paused.ExecutionId, resumed.ExecutionId);
        Assert.Equal("queued", resumed.State); Assert.Null(resumed.TotalClipCount); Assert.Empty(resumed.Clips);
        var final = await StudioTtsCorpusService.Store(db).TransactionAsync(e => ((TtsCorpusEnvelope?)null, e.Current!), Token);
        Assert.Equal(action, final.ActionId); Assert.Null(final.Plan); Assert.Empty(final.Accepted);
    }
    [Fact]
    public async Task More_than_256_mutations_evict_oldest_replays_without_reexecuting_expired_start()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input); var s = f.Service();
        TtsCorpusUpdateClipRequest? last = null;
        for (int i = 0; i < 260; i++)
        {
            last = new("correction " + i, false, false);
            d = await s.UpdateClipAsync(input.Id, d.Id, d.Clips[0].Id, d.Revision, "edit-" + i, last, Token);
        }
        Assert.Equivalent(d, await f.Service().UpdateClipAsync(input.Id, d.Id, d.Clips[0].Id, 0, "edit-259", last!, Token));
        await Error("tts_corpus.idempotency.expired", () => f.Service().StartAsync(input.Id, input.Revision, "start", true, Token));
        await Error("tts_corpus.idempotency.expired", () => f.Service().StartAsync(input.Id, input.Revision, "start", false, Token));
        Assert.Equivalent(d, await s.GetAsync(input.Id, Token));
        await using var db = await new ProjectService().OpenCurrentAsync(f.Root(input.Id), Token);
        Assert.Equal(256, await StudioTtsCorpusService.Store(db).TransactionAsync(e => ((TtsCorpusEnvelope?)null, e.Receipts.Count), Token));
    }
    [Fact]
    public async Task Byte_budget_evicts_oldest_hot_receipts_below_count_limit()
    {
        using var f = new Fixture(); var input = await f.Prepare(); var d = await f.Draft(input);
        await using var db = await new ProjectService().OpenCurrentAsync(f.Root(input.Id), Token);
        string request = JsonSerializer.Serialize(new object[] { "start", true });
        var words = Enumerable.Range(0, 50).Select(_ => new TtsCorpusWordDto(new string('x', 4000), 0, null)).ToArray();
        var large = d with { Clips = [d.Clips[0] with { Words = words }, d.Clips[1]] };
        await StudioTtsCorpusService.Store(db).TransactionAsync(e =>
        {
            var receipts = Enumerable.Range(0, 60).Select(i => new TtsCorpusReceipt(TtsCorpusRecordStore.Hash("history-" + i), TtsCorpusRecordStore.Hash(request), large));
            return (e with { Receipts = [.. e.Receipts, .. receipts] }, true);
        }, Token);
        int count = await StudioTtsCorpusService.Store(db).TransactionAsync(e => ((TtsCorpusEnvelope?)null, e.Receipts.Count), Token);
        Assert.InRange(count, 1, 59); Assert.True(new FileInfo(Path.Combine(f.Root(input.Id), "tts-corpus/state.json")).Length <= 8 * 1024 * 1024);
        Assert.Equivalent(large, await f.Service().StartAsync(input.Id, 0, "history-59", true, Token));
        await Error("tts_corpus.idempotency.expired", () => f.Service().StartAsync(input.Id, input.Revision, "start", true, Token));
        Assert.Equivalent(d, await f.Service().GetAsync(input.Id, Token));
    }
    private static async Task Error(string code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<StudioException>(action)).Code);
    internal sealed class Runtime : ITtsCorpusRuntime
    {
        public string Fingerprint { get; set; } = new string('a', 64);
        public Task<TtsCorpusReadinessDto> GetReadinessAsync(CancellationToken token = default) => Task.FromResult(new TtsCorpusReadinessDto(true, null));
        public Task<string> GetFingerprintAsync(CancellationToken token = default) => Task.FromResult(Fingerprint);
    }
    internal sealed class Fixture : IDisposable
    {
        public CatalogSandbox Box { get; } = new();
        public Runtime Runtime { get; } = new();
        public string Root(string id) => Box.ProjectRoot(id);
        public ProjectJobAccess Access() => new(new(Box.Root), Box.Catalog(), new ProjectService());
        public StudioTtsCorpusService Service() => new(Access(), Runtime);
        public async Task<(string Id, long Revision)> Prepare(int sourceSamples = 480000)
        {
            var p = (await Box.Service().CreateAsync(new("Synthetic corpus"), "project", Token)).Snapshot;
            await using var db = await new ProjectService().OpenAsync(Root(p.Id), Token, retainSidecars: true);
            await new VoiceRecordingService(new(48000)).ImportAsync(db, new("source.wav", true), new MemoryStream(Wav(48000, sourceSamples)), Token);
            return (p.Id, db.State.Revision);
        }
        public async Task<TtsCorpusSnapshotDto> Draft((string Id, long Revision) input, string state = "completed", bool excludeAll = false, int acceptedCount = 2)
        {
            var existing = await Service().GetAsync(input.Id, Token);
            if (existing == null) await Service().StartAsync(input.Id, input.Revision, "start", true, Token);
            await using var db = await new ProjectService().OpenCurrentAsync(Root(input.Id), Token);
            return await StudioTtsCorpusService.Store(db).TransactionAsync(e =>
            {
                var r = e.Current!; string root = r.OutputRelativeRoot; Directory.CreateDirectory(db.ResolveOwnedPath(root));
                long totalSamples = (r.Recording.SampleCount + 2) / 3;
                var plans = new List<TtsCorpusPlannedClip>(); var accepted = new List<TtsCorpusAcceptedClip>();
                for (int i = 0; i < 2; i++)
                {
                    string id = Guid.NewGuid().ToString("N"), audio = $"{root}/{id}.wav", text = $"{root}/{id}.json";
                    long end = i == 0 ? 80000 : totalSamples;
                    byte[] wav = Wav(16000, (int)(end - i * 80000)), payload = Encoding.UTF8.GetBytes(i == 0 ? "draft one" : "draft two");
                    File.WriteAllBytes(db.ResolveOwnedPath(audio), wav); File.WriteAllBytes(db.ResolveOwnedPath(text), payload);
                    plans.Add(new(id, i, i * 80000, end, audio, Hash(wav)));
                    if (i < acceptedCount) accepted.Add(new(plans[i], text, Hash(payload), i == 0 ? "draft one" : "draft two", [], []));
                }
                byte[] planBytes = JsonSerializer.SerializeToUtf8Bytes(plans); string planPath = root + "/plan.json"; File.WriteAllBytes(db.ResolveOwnedPath(planPath), planBytes);
                var plan = new TtsCorpusPreparedPlan(planPath, Hash(planBytes), r.Recording.Sha256, r.RuntimeFingerprint, totalSamples, plans);
                var clips = accepted.Select(x => new TtsCorpusClipDto(x.Plan.Id, x.Plan.Index, x.Plan.StartSample, x.Plan.EndSample, x.DraftText, excludeAll, false, x.WarningCodes, x.Words)).ToArray();
                var snapshot = r.Snapshot with { State = state, Phase = state, Revision = r.Snapshot.Revision + 1, TotalClipCount = 2, CompletedClipCount = accepted.Count, Clips = clips, CanResume = accepted.Count < 2 && !TtsCorpusRecordStore.Active(state) };
                r = r with { Snapshot = snapshot, Plan = plan, Accepted = accepted };
                return (e with { Current = r }, TtsCorpusRecordStore.PublicSnapshot(r));
            }, Token);
        }
        public async Task<string> Manifest(string id, string versionId) => await StorePath(id, r => r.VersionReferences.Single(x => x.Id == versionId).ManifestRelativePath);
        public async Task<string> Audio(string id) => await StorePath(id, r => r.Accepted[0].Plan.AudioRelativePath);
        private async Task<string> StorePath(string id, Func<TtsCorpusRecord, string> path)
        {
            await using var db = await new ProjectService().OpenCurrentAsync(Root(id), Token);
            return await StudioTtsCorpusService.Store(db).TransactionAsync(e => ((TtsCorpusEnvelope?)null, db.ResolveOwnedPath(path(e.Current!))), Token);
        }
        public void Dispose() => Box.Dispose();
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[] Wav(int rate, int samples)
    {
        byte[] bytes = new byte[44 + samples * 2]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), rate); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16); "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples * 2);
        return bytes;
    }
}

using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Core.Tests;

public class SavedVoiceJobsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static SavedVoiceBinding Binding(ProjectDatabase db) => new(Guid.NewGuid().ToString("N"),
        "training/actions/" + Guid.NewGuid().ToString("N") + "/accepted-state/workflow.json", new string('a', 64),
        db.State.VoiceRecording!.Id, db.State.VoiceRecording.WavRelativePath, db.State.VoiceRecording.Sha256.ToLowerInvariant(),
        125, 1, 125, "target_epoch", "rvc-saved-voice-cpu-v1");

    [Fact]
    public async Task Fingerprints_bind_every_saved_model_field_without_changing_legacy_encoding()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        var legacy = ProjectJobStoreTests.Input(db); var model = Binding(db); var input = legacy with { SavedVoice = model };
        var variants = new[] { model with { ModelId = Guid.NewGuid().ToString("N") }, model with { WorkflowSha256 = new string('b', 64) },
            model with { WorkflowRelativePath = model.WorkflowRelativePath + "x" }, model with { SourceRecordingId = Guid.NewGuid().ToString("N") },
            model with { SourceWavRelativePath = model.SourceWavRelativePath + "x" }, model with { SourceSha256 = new string('d', 64) },
            model with { GlobalStep = 126 }, model with { CompletedEpoch = 0 }, model with { EpochSize = 126 },
            model with { StopReason = "user_stop" }, model with { ProfileVersion = "other" } };
        Assert.NotEqual(GenerationFingerprint.ForInput(legacy), GenerationFingerprint.ForInput(input));
        Assert.NotEqual(GenerationFingerprint.ForItem(legacy, legacy.Items[0], legacy.Preset), GenerationFingerprint.ForItem(input, input.Items[0], input.Preset));
        foreach (var changed in variants)
        {
            Assert.NotEqual(GenerationFingerprint.ForInput(input), GenerationFingerprint.ForInput(input with { SavedVoice = changed }));
            Assert.NotEqual(GenerationFingerprint.ForItem(input, input.Items[0], input.Preset), GenerationFingerprint.ForItem(input with { SavedVoice = changed }, input.Items[0], input.Preset));
        }
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("../other/workflow.json")]
    [InlineData("training/actions/../workflow.json")]
    public async Task Invalid_model_binding_rejected_before_enqueue(string path)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        var input = ProjectJobStoreTests.Input(db) with { SavedVoice = Binding(db) with { WorkflowRelativePath = path } };
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(lease, db.State.Revision, "bad", JobKind.Generation, input, Token))).Code);
        Assert.Empty(await db.Jobs.ListAsync(Token));
    }

    [Fact]
    public async Task Frozen_binding_survives_restart_and_request_cannot_replay_as_plain_tts()
    {
        using var box = new ProjectSandbox(); SavedVoiceBinding binding; string id; string[] ids;
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        {
            binding = Binding(db); var input = ProjectJobStoreTests.Input(db) with { SavedVoice = binding }; ids = input.Items.Select(x => x.SegmentId).ToArray();
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            id = (await db.Jobs.EnqueueAsync(lease, db.State.Revision, "saved", JobKind.Generation, input, Token)).Id;
            Assert.Equal("job.idempotency.conflict", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.TryReplayEnqueueAsync("saved", JobKind.Generation, ids, Token))).Code);
        }
        await using var reopened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        Assert.Equal(binding, (await reopened.Jobs.GetAsync(id, Token)).Input.SavedVoice);
        Assert.Equal(id, (await reopened.Jobs.TryReplayEnqueueAsync("saved", JobKind.Generation, ids, Token, binding.ModelId))!.Id);
        Assert.Equal("job.idempotency.conflict", (await Assert.ThrowsAsync<ProjectException>(() => reopened.Jobs.TryReplayEnqueueAsync("saved", JobKind.Generation, ids, Token, Guid.NewGuid().ToString("N")))).Code);
    }
}

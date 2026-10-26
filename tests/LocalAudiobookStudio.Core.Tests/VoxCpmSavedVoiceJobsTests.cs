using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Core.Tests;

public class VoxCpmSavedVoiceJobsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static VoxCpmSavedVoiceBinding Binding(ProjectDatabase db)
    {
        string id = Guid.NewGuid().ToString("N");
        return new(id, Guid.NewGuid().ToString("N"),
            $"voxcpm-training/actions/{Guid.NewGuid():N}/executions/{Guid.NewGuid():N}/windows/{Guid.NewGuid():N}/checkpoints/{id}/manifest.json",
            new string('a', 64), Guid.NewGuid().ToString("N"), new string('b', 64), new string('c', 64),
            db.State.VoiceRecording!.Id, db.State.VoiceRecording.Sha256.ToLowerInvariant(), new string('d', 64),
            29, 1, 28, new string('e', 64), "voxcpm2-saved-adapter-cpu-v1");
    }
    internal static GenerationInput Input(ProjectDatabase db)
    {
        var b = Binding(db); var input = ProjectJobStoreTests.Input(db);
        return input with { SavedVoxCpmVoice = b,
            Preset = new("voxcpm2-saved-adapter-v1", "voxcpm2-saved-adapter-cpu-v1", 256, 10, 2, 1, 42, false),
            Items = input.Items.Select(x => x with { Versions = x.Versions with {
                Worker = b.InferenceImplementationSha256[..40], Model = b.ManifestSha256[..40] } }).ToArray() };
    }

    [Theory]
    [InlineData(false, "2b78ca14d1b5e02b126ee3c671d3a7fd57c93d56059c3aa383611a8702f9035e")]
    [InlineData(true, "bf77aba17bc61494807a587807ca64feae59095b23848d9e97b570cef33bbeed")]
    public void Existing_plain_and_rvc_hashes_and_missing_json_field_stay_compatible(bool saved, string expected)
    {
        string voiceId = new('a', 32), segmentId = new('b', 32);
        var voice = new VoiceSnapshot(voiceId, $"voice-snapshot/{voiceId}.wav", "abc", new('A', 64), new('B', 64), new('C', 64),
            true, new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), 0, 10000);
        var item = new GenerationItemInput(segmentId, 1, "one", new('d', 64), null, null, SegmentBoundary.Sentence,
            new("app", new('1', 40), new('2', 40), "policy", "text", "seg"));
        var legacy = new GenerationInput(new('c', 32), 7, voice, new("voxcpm2-local-v1", "q8-f16-cpu", 70, 10, 2, 1, 42, true), [item]);
        if (saved) legacy = legacy with { SavedVoice = new(new('e', 32), "training/actions/" + new string('f', 32) + "/saved/workflow.json",
            new('0', 64), new('1', 32), "voice-recording/" + new string('1', 32) + ".wav", new('2', 64), 125, 1, 125, "target_epoch", "rvc-saved-voice-cpu-v1") };
        Assert.Equal(expected, GenerationFingerprint.ForInput(legacy));
        var json = JsonNode.Parse(JsonSerializer.Serialize(legacy))!.AsObject(); json.Remove("SavedVoxCpmVoice");
        var restored = JsonSerializer.Deserialize<GenerationInput>(json.ToJsonString(), new JsonSerializerOptions { RespectRequiredConstructorParameters = true })!;
        Assert.Null(restored.SavedVoxCpmVoice);
        Assert.Equal(expected, GenerationFingerprint.ForInput(restored));
    }

    [Fact]
    public async Task Every_vox_binding_field_changes_frozen_input_and_item_identity()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        var input = Input(db); var b = input.SavedVoxCpmVoice!;
        var variants = new[] { b with { ModelId = Guid.NewGuid().ToString("N") }, b with { TrainingId = Guid.NewGuid().ToString("N") },
            b with { ManifestRelativePath = b.ManifestRelativePath + "x" }, b with { ManifestSha256 = new('f', 64) },
            b with { CorpusVersionId = Guid.NewGuid().ToString("N") }, b with { CorpusManifestSha256 = new('f', 64) },
            b with { CorpusBindingSha256 = new('f', 64) }, b with { SourceRecordingId = Guid.NewGuid().ToString("N") },
            b with { SourceSha256 = new('f', 64) }, b with { RuntimeIdentitySha256 = new('f', 64) },
            b with { GlobalStep = 30 }, b with { CompletedEpochs = 0 }, b with { EpochSize = 29 },
            b with { InferenceImplementationSha256 = new('f', 64) }, b with { ProfileVersion = "other" } };
        foreach (var changed in variants)
        {
            Assert.NotEqual(GenerationFingerprint.ForInput(input), GenerationFingerprint.ForInput(input with { SavedVoxCpmVoice = changed }));
            Assert.NotEqual(GenerationFingerprint.ForItem(input, input.Items[0], input.Preset),
                GenerationFingerprint.ForItem(input with { SavedVoxCpmVoice = changed }, input.Items[0], input.Preset));
        }
    }

    [Fact]
    public async Task Saved_vox_selection_survives_restart_and_cannot_replay_as_plain_or_rvc()
    {
        using var box = new ProjectSandbox(); string id; string[] segments; VoxCpmSavedVoiceBinding selected;
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        {
            var input = Input(db); selected = input.SavedVoxCpmVoice!; segments = input.Items.Select(x => x.SegmentId).ToArray();
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            id = (await db.Jobs.EnqueueAsync(lease, db.State.Revision, "vox", JobKind.Generation, input, Token)).Id;
            foreach (string? rvc in new string?[] { null, selected.ModelId })
                Assert.Equal("job.idempotency.conflict", (await Assert.ThrowsAsync<ProjectException>(() =>
                    db.Jobs.TryReplayEnqueueAsync("vox", JobKind.Generation, segments, Token, rvc))).Code);
        }
        await using var reopened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        Assert.Equal(selected, (await reopened.Jobs.GetAsync(id, Token)).Input.SavedVoxCpmVoice);
        Assert.Equal(id, (await reopened.Jobs.TryReplayEnqueueAsync("vox", JobKind.Generation, segments, Token,
            savedVoxCpmModelId: selected.ModelId))!.Id);
        Assert.Equal("job.idempotency.conflict", (await Assert.ThrowsAsync<ProjectException>(() =>
            reopened.Jobs.TryReplayEnqueueAsync("vox", JobKind.Generation, segments, Token, savedVoxCpmModelId: Guid.NewGuid().ToString("N")))).Code);
        Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<ProjectException>(() =>
            reopened.Jobs.TryReplayEnqueueAsync("new", JobKind.Generation, segments, Token, selected.ModelId, selected.ModelId))).Code);
    }

    [Theory]
    [InlineData("namespace")] [InlineData("path-id")] [InlineData("source")] [InlineData("epoch")]
    [InlineData("runtime")] [InlineData("implementation")] [InlineData("profile")] [InlineData("preset")]
    [InlineData("fallback")] [InlineData("both")] [InlineData("pins")]
    public async Task Invalid_or_changed_saved_vox_binding_never_enqueues(string change)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        var input = Input(db); var b = input.SavedVoxCpmVoice!;
        input = change switch
        {
            "namespace" => input with { SavedVoxCpmVoice = b with { ManifestRelativePath = "../manifest.json" } },
            "path-id" => input with { SavedVoxCpmVoice = b with { ModelId = Guid.NewGuid().ToString("N") } },
            "source" => input with { SavedVoxCpmVoice = b with { SourceSha256 = new('f', 64) } },
            "epoch" => input with { SavedVoxCpmVoice = b with { CompletedEpochs = 2 } },
            "runtime" => input with { SavedVoxCpmVoice = b with { RuntimeIdentitySha256 = "bad" } },
            "implementation" => input with { SavedVoxCpmVoice = b with { InferenceImplementationSha256 = "bad" } },
            "profile" => input with { SavedVoxCpmVoice = b with { ProfileVersion = "rvc-saved-voice-cpu-v1" } },
            "preset" => input with { Preset = ProjectJobStoreTests.Input(db).Preset },
            "fallback" => input with { Preset = input.Preset with { AllowCpuFallback = true } },
            "pins" => input with { Items = input.Items.Select(x => x with { Versions = x.Versions with { Worker = new('1', 40) } }).ToArray() },
            _ => input with { SavedVoice = new(Guid.NewGuid().ToString("N"), "unused", new('a', 64), b.SourceRecordingId,
                "voice-recording/" + b.SourceRecordingId + ".wav", b.SourceSha256, 1, 0, 28, "user_stop", "rvc-saved-voice-cpu-v1") }
        };
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var failure = await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.EnqueueAsync(lease, db.State.Revision, change, JobKind.Generation, input, Token));
        Assert.Equal(change == "source" ? "job.input_changed" : "job.input.invalid", failure.Code);
        Assert.Empty(await db.Jobs.ListAsync(Token));
    }
}

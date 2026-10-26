using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Core.Tests;

public sealed class VoxCpmGenerationControlsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Profile = "voxcpm2-saved-adapter-controls-cpu-v1";
    private static GenerationInput Configured(GenerationInput original, int tempo = 90)
    {
        var json = JsonSerializer.SerializeToNode(original)!.AsObject();
        json["SavedVoxCpmVoice"]!["ProfileVersion"] = Profile;
        json["SavedVoxCpmVoice"]!["Options"] = new JsonObject { ["Seed"] = 87, ["Timesteps"] = 12,
            ["Cfg"] = 1.5, ["TempoPercent"] = tempo, ["UseReference"] = false, ["ReferenceClipId"] = null };
        json["Preset"]!["Id"] = "voxcpm2-saved-adapter-controls-v1"; json["Preset"]!["ProfileId"] = Profile;
        json["Preset"]!["Seed"] = 87; json["Preset"]!["Timesteps"] = 12; json["Preset"]!["Cfg"] = 1.5;
        return JsonSerializer.Deserialize<GenerationInput>(json.ToJsonString())!;
    }

    [Fact]
    public async Task Captured_export_tempo_changes_input_and_item_identity()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        var original = VoxCpmSavedVoiceJobsTests.Input(db); var a = Configured(original, 90); var b = Configured(original, 91);
        Assert.NotEqual(GenerationFingerprint.ForInput(a), GenerationFingerprint.ForInput(b));
        Assert.NotEqual(GenerationFingerprint.ForItem(a, a.Items[0], a.Preset), GenerationFingerprint.ForItem(b, b.Items[0], b.Preset));
    }

    [Fact]
    public async Task Configured_adapter_only_profile_survives_restart_and_changed_options_conflict()
    {
        using var box = new ProjectSandbox(); string jobId;
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        {
            var old = VoxCpmSavedVoiceJobsTests.Input(db); var input = Configured(old);
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "configured", JobKind.Generation, input, Token);
            jobId = job.Id; Assert.Equal(Profile, job.EffectiveProfileId);
            Assert.Equal("job.idempotency.conflict", (await Assert.ThrowsAsync<ProjectException>(() =>
                db.Jobs.EnqueueAsync(lease, -1, "configured", JobKind.Generation, Configured(old, 91), Token))).Code);
        }
        await using var reopened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        var loaded = await reopened.Jobs.GetAsync(jobId, Token);
        Assert.Equal(90, JsonSerializer.SerializeToElement(loaded.Input.SavedVoxCpmVoice).GetProperty("Options").GetProperty("TempoPercent").GetInt32());
        var replay = await reopened.Jobs.TryReplayEnqueueAsync("configured", JobKind.Generation,
            loaded.Input.Items.Select(x => x.SegmentId).Reverse().ToArray(), Token,
            savedVoxCpmModelId: loaded.Input.SavedVoxCpmVoice!.ModelId, voxCpmOptions: loaded.Input.SavedVoxCpmVoice.Options);
        Assert.Equal(jobId, replay!.Id);
    }

    [Theory]
    [InlineData("seed-low")] [InlineData("seed-high")] [InlineData("steps-low")] [InlineData("steps-high")]
    [InlineData("cfg-low")] [InlineData("cfg-high")] [InlineData("cfg-nan")] [InlineData("cfg-infinity")]
    [InlineData("tempo-low")] [InlineData("tempo-high")] [InlineData("clip-case")] [InlineData("off-with-clip")]
    public void Invalid_controls_are_rejected(string variant)
    {
        var options = variant switch
        {
            "seed-low" => new VoxCpmGenerationOptions(Seed: -1), "seed-high" => new(Seed: 2147483648),
            "steps-low" => new(Timesteps: 3), "steps-high" => new(Timesteps: 21),
            "cfg-low" => new(Cfg: 0.99), "cfg-high" => new(Cfg: 3.01),
            "cfg-nan" => new(Cfg: double.NaN), "cfg-infinity" => new(Cfg: double.PositiveInfinity),
            "tempo-low" => new(TempoPercent: 79), "tempo-high" => new(TempoPercent: 121),
            "clip-case" => new(ReferenceClipId: new('A', 32)),
            _ => new(UseReference: false, ReferenceClipId: new('a', 32))
        };
        Assert.False(options.IsValid());
    }

    [Fact]
    public async Task Every_option_including_requested_auto_intent_changes_fingerprints()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        var input = Configured(VoxCpmSavedVoiceJobsTests.Input(db)); var options = input.SavedVoxCpmVoice!.Options!;
        foreach (var changed in new[] { options with { Seed = 88 }, options with { Timesteps = 13 }, options with { Cfg = 2 },
            options with { TempoPercent = 91 }, options with { UseReference = true }, options with { ReferenceClipId = new('a', 32) } })
        {
            var next = input with { SavedVoxCpmVoice = input.SavedVoxCpmVoice with { Options = changed } };
            Assert.NotEqual(GenerationFingerprint.ForInput(input), GenerationFingerprint.ForInput(next));
            Assert.NotEqual(GenerationFingerprint.ForItem(input, input.Items[0], input.Preset), GenerationFingerprint.ForItem(next, next.Items[0], next.Preset));
        }
    }

    [Fact]
    public async Task Bounded_history_keeps_32_terminal_jobs_alongside_the_active_job()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var input = ProjectJobStoreTests.Input(db);
        for (int i = 0; i < 32; i++)
        {
            var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "history-" + i, JobKind.Generation, input, Token);
            await db.Jobs.RequestStateAsync(job.Id, job.Revision, "cancel-" + i, JobControlCommand.Cancel, Token);
        }
        await db.Jobs.EnqueueAsync(lease, db.State.Revision, "active", JobKind.Generation, input, Token);
        var history = await db.Jobs.ListAsync(Token);
        Assert.Equal(33, history.Count); Assert.Equal(32, history.Count(x => x.State == JobState.Canceled));
        Assert.Equal(33, (await db.Jobs.ReadEventPageAsync(null, Token)).Jobs.Count);
    }
}

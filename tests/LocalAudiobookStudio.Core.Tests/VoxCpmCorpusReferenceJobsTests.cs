using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Core.Tests;

public sealed class VoxCpmCorpusReferenceJobsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Profile = "voxcpm2-saved-adapter-reference-cpu-v1";
    private static JsonObject Reference() => new()
    {
        ["Id"] = new string('1', 32), ["TrainingOrdinal"] = 13,
        ["AudioRelativePath"] = $"tts-corpus/actions/{new string('2', 32)}/executions/{new string('3', 32)}/{new string('1', 32)}.wav",
        ["AudioSha256"] = new string('4', 64), ["SampleCount"] = 201680
    };
    private static VoxCpmSavedVoiceBinding WithReference(VoxCpmSavedVoiceBinding binding, JsonObject? reference,
        string? profile = Profile)
    {
        var json = JsonSerializer.SerializeToNode(binding)!.AsObject();
        json["Reference"] = reference?.DeepClone();
        if (profile != null) json["ProfileVersion"] = profile;
        return JsonSerializer.Deserialize<VoxCpmSavedVoiceBinding>(json.ToJsonString(),
            new JsonSerializerOptions { RespectRequiredConstructorParameters = true })!;
    }
    private static GenerationInput Referenced(GenerationInput input, JsonObject? reference)
        => input with { SavedVoxCpmVoice = WithReference(input.SavedVoxCpmVoice!, reference),
            Preset = input.Preset with { Id = "voxcpm2-saved-adapter-reference-v1", ProfileId = Profile } };

    [Fact]
    public void Legacy_saved_adapter_has_exact_pre_reference_input_and_item_hashes()
    {
        string voiceId = new('a', 32), modelId = new('e', 32);
        var voice = new VoiceSnapshot(voiceId, $"voice-snapshot/{voiceId}.wav", "abc", new('A', 64), new('B', 64), new('C', 64),
            true, new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), 0, 10000);
        var item = new GenerationItemInput(new('b', 32), 1, "one", new('d', 64), null, null, SegmentBoundary.Sentence,
            new("app", new('1', 40), new('2', 40), "policy", "text", "seg"));
        var binding = new VoxCpmSavedVoiceBinding(modelId, new('f', 32),
            $"voxcpm-training/actions/{new string('8', 32)}/executions/{new string('9', 32)}/windows/{new string('a', 32)}/checkpoints/{modelId}/manifest.json",
            new('0', 64), new('1', 32), new('2', 64), new('3', 64), new('4', 32), new('5', 64), new('6', 64),
            29, 1, 28, new('7', 64), "voxcpm2-saved-adapter-cpu-v1");
        var input = new GenerationInput(new('c', 32), 7, voice,
            new("voxcpm2-saved-adapter-v1", "voxcpm2-saved-adapter-cpu-v1", 256, 10, 2, 1, 42, false), [item], SavedVoxCpmVoice: binding);
        Assert.Equal("431dd167c2348a79db807f644ce5f7eaa11ebb4ed9eced65b9b19e364fb3885e", GenerationFingerprint.ForInput(input));
        Assert.Equal("fbd3646eee32b74cd5ead58793e6f689c4fedd1d68b169f414d9375aa2950b5f", GenerationFingerprint.ForItem(input, item, input.Preset));
        Assert.DoesNotContain("\"Reference\"", JsonSerializer.Serialize(binding));
    }

    [Fact]
    public async Task Missing_and_explicit_null_reference_preserve_legacy_serialization_and_fingerprints()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        var input = VoxCpmSavedVoiceJobsTests.Input(db); var binding = input.SavedVoxCpmVoice!;
        string originalJson = JsonSerializer.Serialize(binding);
        var restored = WithReference(binding, null, profile: null);
        Assert.Equal(originalJson, JsonSerializer.Serialize(restored));
        Assert.Equal(GenerationFingerprint.ForInput(input), GenerationFingerprint.ForInput(input with { SavedVoxCpmVoice = restored }));
        Assert.Equal(GenerationFingerprint.ForItem(input, input.Items[0], input.Preset),
            GenerationFingerprint.ForItem(input with { SavedVoxCpmVoice = restored }, input.Items[0], input.Preset));
        var missing = JsonNode.Parse(originalJson)!.AsObject(); missing.Remove("Reference");
        Assert.Equal(binding, JsonSerializer.Deserialize<VoxCpmSavedVoiceBinding>(missing.ToJsonString(),
            new JsonSerializerOptions { RespectRequiredConstructorParameters = true }));
    }

    [Fact]
    public async Task Every_reference_scalar_changes_input_and_item_fingerprints()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        var old = VoxCpmSavedVoiceJobsTests.Input(db); var input = Referenced(old, Reference());
        var without = input with { SavedVoxCpmVoice = WithReference(input.SavedVoxCpmVoice!, null) };
        Assert.NotEqual(GenerationFingerprint.ForInput(input), GenerationFingerprint.ForInput(without));
        Assert.NotEqual(GenerationFingerprint.ForItem(input, input.Items[0], input.Preset),
            GenerationFingerprint.ForItem(without, without.Items[0], without.Preset));
        foreach (string field in new[] { "Id", "TrainingOrdinal", "AudioRelativePath", "AudioSha256", "SampleCount" })
        {
            var reference = Reference();
            reference[field] = field switch
            {
                "TrainingOrdinal" => JsonValue.Create(14), "SampleCount" => JsonValue.Create(201681),
                "Id" => JsonValue.Create(new string('5', 32)), "AudioSha256" => JsonValue.Create(new string('5', 64)),
                _ => JsonValue.Create(reference[field]!.GetValue<string>().Replace(".wav", "-other.wav", StringComparison.Ordinal))
            };
            var changed = Referenced(old, reference);
            Assert.NotEqual(GenerationFingerprint.ForInput(input), GenerationFingerprint.ForInput(changed));
            Assert.NotEqual(GenerationFingerprint.ForItem(input, input.Items[0], input.Preset),
                GenerationFingerprint.ForItem(changed, changed.Items[0], changed.Preset));
        }
    }

    [Fact]
    public async Task Referenced_profile_enqueues_and_survives_durable_restart()
    {
        using var box = new ProjectSandbox(); string jobId; string expected;
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        {
            var input = Referenced(VoxCpmSavedVoiceJobsTests.Input(db), Reference()); expected = JsonSerializer.Serialize(input.SavedVoxCpmVoice);
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "reference", JobKind.Generation, input, Token);
            jobId = job.Id; Assert.Equal(Profile, job.EffectiveProfileId);
        }
        await using var reopened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        Assert.Equal(expected, JsonSerializer.Serialize((await reopened.Jobs.GetAsync(jobId, Token)).Input.SavedVoxCpmVoice));
    }

    [Theory]
    [InlineData("legacy-profile")] [InlineData("missing")] [InlineData("preset")]
    [InlineData("id")] [InlineData("ordinal-low")] [InlineData("ordinal-high")]
    [InlineData("short")] [InlineData("long")] [InlineData("hash")]
    [InlineData("namespace")] [InlineData("traversal")] [InlineData("backslash")]
    public async Task Invalid_reference_or_profile_pair_never_enqueues(string kind)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        var old = VoxCpmSavedVoiceJobsTests.Input(db); var reference = Reference();
        switch (kind)
        {
            case "id": reference["Id"] = "bad"; break;
            case "ordinal-low": reference["TrainingOrdinal"] = -1; break;
            case "ordinal-high": reference["TrainingOrdinal"] = 512; break;
            case "short": reference["SampleCount"] = 79999; break;
            case "long": reference["SampleCount"] = 256001; break;
            case "hash": reference["AudioSha256"] = "bad"; break;
            case "namespace": reference["AudioRelativePath"] = "voice-recording/other.wav"; break;
            case "traversal": reference["AudioRelativePath"] = "tts-corpus/actions/../other.wav"; break;
            case "backslash": reference["AudioRelativePath"] = "tts-corpus\\actions\\other.wav"; break;
        }
        var input = Referenced(old, kind == "missing" ? null : reference);
        if (kind == "legacy-profile") input = old with { SavedVoxCpmVoice = WithReference(old.SavedVoxCpmVoice!, reference, old.SavedVoxCpmVoice!.ProfileVersion) };
        if (kind == "preset") input = input with { Preset = old.Preset };
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        Assert.Equal("job.input.invalid", (await Assert.ThrowsAsync<ProjectException>(() =>
            db.Jobs.EnqueueAsync(lease, db.State.Revision, kind, JobKind.Generation, input, Token))).Code);
        Assert.Empty(await db.Jobs.ListAsync(Token));
    }
}

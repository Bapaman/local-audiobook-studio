using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class SavedVoiceResolverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Accepting_later_checkpoint_keeps_each_public_model_bound_to_its_original_state()
    {
        using var f = new Fixture(); await f.Prepare();
        await f.Train(1, 2);
        var snapshot = (await f.Service.GetAsync(f.Id, Token))!;
        Assert.Equal(2, snapshot.Models.Count);
        Assert.All(snapshot.Models, model => Assert.True(model.Selectable));
        Assert.DoesNotContain(f.Root, JsonSerializer.Serialize(snapshot));
        using var state = JsonDocument.Parse(await File.ReadAllBytesAsync(f.StatePath, Token));
        Assert.True(state.RootElement.GetProperty("Current").TryGetProperty("ModelReferences", out var references),
            "Every accepted model must persist its own workflow reference, not merely the mutable latest reference.");
        Assert.Equal(2, references.EnumerateObject().Count());
        for (int i = 0; i < 2; i++)
        {
            var binding = references.GetProperty(snapshot.Models[i].Id);
            Assert.Equal(i + 1, binding.GetProperty("GlobalStep").GetInt64());
            Assert.Equal(f.Points[i].Sha256, binding.GetProperty("Sha256").GetString());
            Assert.Equal(f.Points[i].Path, binding.GetProperty("Path").GetString());
        }
    }

    [Fact]
    public async Task No_update_pause_preserves_model_identity_and_original_reference()
    {
        using var f = new Fixture(); await f.Prepare();
        await f.Train(1);
        var first = (await f.Service.GetAsync(f.Id, Token))!;
        await f.Service.ControlAsync(f.Id, first.Id, first.Revision, "resume", "resume", Token);
        await f.Run(new FixtureExecutor(f, [1], "user_stop"));
        var current = (await f.Service.GetAsync(f.Id, Token))!;
        Assert.Equal(first.Models[0].Id, Assert.Single(current.Models).Id);
        using var state = JsonDocument.Parse(await File.ReadAllBytesAsync(f.StatePath, Token));
        Assert.True(state.RootElement.GetProperty("Current").TryGetProperty("ModelReferences", out var references));
        Assert.Equal(f.Points[0].Sha256, references.GetProperty(first.Models[0].Id).GetProperty("Sha256").GetString());
    }

    [Fact]
    public async Task Legacy_public_snapshot_selects_only_unique_latest_matching_accepted_cursor_without_rewriting()
    {
        using var f = new Fixture(); await f.Prepare(); await f.Train(1, 2);
        await f.RemoveReferenceMap(); byte[] before = await File.ReadAllBytesAsync(f.StatePath, Token);
        var snapshot = (await f.Service.GetAsync(f.Id, Token))!;
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(snapshot));
        var models = json.RootElement.GetProperty("Models");
        Assert.True(models[0].TryGetProperty("Selectable", out var first));
        Assert.False(first.GetBoolean());
        Assert.True(models[1].GetProperty("Selectable").GetBoolean());
        Assert.Equal(before, await File.ReadAllBytesAsync(f.StatePath, Token));
    }

    [Fact]
    public async Task Historical_selection_freezes_exact_original_state_and_source_with_owned_relative_paths()
    {
        using var f = new Fixture(); await f.Prepare(); await f.Train(1, 2);
        var snapshot = (await f.Service.GetAsync(f.Id, Token))!;
        await using var db = await f.Open();
        var binding = await Resolve(db, snapshot.Models[0].Id);
        Assert.NotNull(binding);
        Assert.Equal(snapshot.Models[0].Id, binding.ModelId);
        Assert.Equal(Path.GetRelativePath(f.Root, f.Points[0].Path).Replace('\\', '/'), binding.WorkflowRelativePath);
        Assert.False(Path.IsPathRooted(binding.WorkflowRelativePath));
        Assert.Equal(f.Points[0].Sha256, binding.WorkflowSha256);
        Assert.Equal(db.State.VoiceRecording!.Id, binding.SourceRecordingId);
        Assert.Equal(db.State.VoiceRecording.WavRelativePath, binding.SourceWavRelativePath);
        Assert.Equal(db.State.VoiceRecording.Sha256.ToLowerInvariant(), binding.SourceSha256);
        Assert.Equal(1, binding.GlobalStep); Assert.Equal(0, binding.CompletedEpoch); Assert.Equal(2, binding.EpochSize);
        Assert.Equal("initial_update", binding.StopReason); Assert.Equal("rvc-saved-voice-cpu-v1", binding.ProfileVersion);
    }

    [Fact]
    public async Task Legacy_resolves_only_latest_and_does_not_adopt_available_historical_files()
    {
        using var f = new Fixture(); await f.Prepare(); await f.Train(1, 2); await f.RemoveReferenceMap();
        var snapshot = (await f.Service.GetAsync(f.Id, Token))!;
        await using var db = await f.Open();
        var binding = await Resolve(db, snapshot.Models[1].Id);
        Assert.NotNull(binding); Assert.Equal(2, binding.GlobalStep);
        Assert.Equal("training.model.unavailable", (await Assert.ThrowsAsync<StudioException>(async () => await Resolve(db, snapshot.Models[0].Id))).Code);
    }

    [Fact]
    public async Task Continuing_legacy_record_freezes_known_latest_before_overwriting_accepted()
    {
        using var f = new Fixture(); await f.Prepare(); await f.Train(1, 2); await f.RemoveReferenceMap();
        var legacy = (await f.Service.GetAsync(f.Id, Token))!;
        await f.Service.ControlAsync(f.Id, legacy.Id, legacy.Revision, "resume", "resume", Token);
        await f.Run(new FixtureExecutor(f, [3]));
        var current = (await f.Service.GetAsync(f.Id, Token))!;
        Assert.False(current.Models[0].Selectable); Assert.True(current.Models[1].Selectable); Assert.True(current.Models[2].Selectable);
        await using var db = await f.Open();
        var binding = await Resolve(db, legacy.Models[1].Id);
        Assert.NotNull(binding); Assert.Equal(f.Points[1].Sha256, binding.WorkflowSha256); Assert.Equal(2, binding.GlobalStep);
    }

    [Fact]
    public async Task Ambiguous_legacy_latest_cursor_is_not_selectable_and_cannot_resolve()
    {
        using var f = new Fixture(); await f.Prepare(); await f.Train(1); await f.RemoveReferenceMap();
        var state = JsonNode.Parse(await File.ReadAllBytesAsync(f.StatePath, Token))!;
        var models = state["Current"]!["Snapshot"]!["Models"]!.AsArray();
        var duplicate = models[0]!.DeepClone(); duplicate["Id"] = new string('b', 32); models.Add(duplicate);
        await File.WriteAllBytesAsync(f.StatePath, JsonSerializer.SerializeToUtf8Bytes(state), Token);
        var snapshot = (await f.Service.GetAsync(f.Id, Token))!;
        Assert.All(snapshot.Models, model => Assert.False(model.Selectable));
        await using var db = await f.Open();
        foreach (var model in snapshot.Models)
            Assert.Equal("training.model.unavailable", (await Assert.ThrowsAsync<StudioException>(async () => await Resolve(db, model.Id))).Code);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("generator")]
    [InlineData("receipt")]
    [InlineData("source")]
    public async Task Changed_known_model_or_source_fails_with_path_free_diagnostic(string target)
    {
        using var f = new Fixture(); await f.Prepare(); await f.Train(1);
        var snapshot = (await f.Service.GetAsync(f.Id, Token))!;
        await using var db = await f.Open();
        string root = Path.GetDirectoryName(Path.GetDirectoryName(f.Points[0].Path))!;
        string path = target switch { "state" => f.Points[0].Path, "generator" => Path.Combine(root, "point1", "generator.pt"),
            "receipt" => Path.Combine(root, "receipt1.json"), _ => db.ResolveOwnedPath(db.State.VoiceRecording!.WavRelativePath) };
        if (target == "generator") await File.WriteAllBytesAsync(path, [9, 9, 9], Token);
        else await File.AppendAllTextAsync(path, "tamper", Token);
        var error = await Assert.ThrowsAsync<StudioException>(async () => await Resolve(db, snapshot.Models[0].Id));
        Assert.Equal("training.model.unavailable", error.Code); Assert.DoesNotContain(f.Root, error.Message);
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("cccccccccccccccccccccccccccccccc")]
    public async Task Unknown_model_id_does_not_fall_back_to_current(string id)
    {
        using var f = new Fixture(); await f.Prepare(); await f.Train(1);
        await using var db = await f.Open();
        Assert.Equal("training.model.unavailable", (await Assert.ThrowsAsync<StudioException>(async () => await Resolve(db, id))).Code);
    }

    [Fact]
    public async Task Present_reference_map_with_missing_binding_is_not_treated_as_legacy()
    {
        using var f = new Fixture(); await f.Prepare(); await f.Train(1);
        var state = JsonNode.Parse(await File.ReadAllBytesAsync(f.StatePath, Token))!;
        state["Current"]!["ModelReferences"] = new JsonObject();
        await File.WriteAllBytesAsync(f.StatePath, JsonSerializer.SerializeToUtf8Bytes(state), Token);
        var snapshot = (await f.Service.GetAsync(f.Id, Token))!;
        Assert.False(Assert.Single(snapshot.Models).Selectable);
        await using var db = await f.Open();
        Assert.Equal("training.model.unavailable", (await Assert.ThrowsAsync<StudioException>(async () => await Resolve(db, snapshot.Models[0].Id))).Code);
    }

    private static Task<SavedVoiceBinding> Resolve(ProjectDatabase db, string id)
        => new SavedVoiceResolver().ResolveAsync(db, id, Token);

    private sealed class Fixture : IDisposable
    {
        public CatalogSandbox Box { get; } = new();
        public string Id { get; private set; } = "";
        public string Root => Box.ProjectRoot(Id);
        public string StatePath => Path.Combine(Root, "training", "state.json");
        public List<WorkflowReference> Points { get; } = [];
        private readonly TrainingRuntime runtime;
        public Fixture()
        {
            string python = Path.Combine(Box.Root, "python.exe"), shell = Path.Combine(Box.Root, "pwsh.exe");
            File.WriteAllText(python, "inert-python"); File.WriteAllText(shell, "inert-shell");
            File.WriteAllText(Path.Combine(Box.Root, "Run-RvcWorkflow.py"), "inert-workflow");
            runtime = new(new(python, shell, Box.Root, Box.Root, Box.Root, Box.Root, Box.Root), new TrainingRuntimeTests.InertInspector(python));
        }
        private ProjectJobAccess Access() => new(new(Box.Root), Box.Catalog(), new ProjectService());
        public StudioTrainingService Service => new(Access(), runtime);
        public async Task<ProjectDatabase> Open() => await new ProjectService().OpenCurrentAsync(Root, Token);
        public async Task Prepare()
        {
            var project = (await Box.Service().CreateAsync(new("Synthetic saved model"), "project", Token)).Snapshot;
            Id = project.Id;
            await using var db = await new ProjectService().OpenAsync(Root, Token, retainSidecars: true);
            byte[] wav = new byte[960044]; Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 48000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 96000);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
            Encoding.ASCII.GetBytes("data").CopyTo(wav, 36); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), 960000);
            await new VoiceRecordingService(new(48000)).ImportAsync(db, new("recording.wav", true), new MemoryStream(wav), Token);
        }
        public async Task Train(params long[] steps)
        {
            await using (var db = await Open())
                await Service.StartAsync(Id, db.State.Revision, "start", true, Token);
            await Run(new FixtureExecutor(this, steps));
        }
        public async Task Run(ITrainingWorkflowExecutor executor)
            => Assert.True(await new TrainingRunner(Access(), new(), executor).RunNextAsync(Token));
        public async Task RemoveReferenceMap()
        {
            var state = JsonNode.Parse(await File.ReadAllBytesAsync(StatePath, Token))!.AsObject();
            state["Current"]!.AsObject().Remove("ModelReferences");
            foreach (var model in state["Current"]!["Snapshot"]!["Models"]!.AsArray()) model!.AsObject().Remove("Selectable");
            await File.WriteAllBytesAsync(StatePath, JsonSerializer.SerializeToUtf8Bytes(state), Token);
        }
        public async Task<WorkflowReference> Point(TrainingExecution execution, long step, string reason)
        {
            string stateRoot = Path.Combine(execution.OutputRoot, "state" + step), pointRoot = Path.Combine(execution.OutputRoot, "point" + step);
            Directory.CreateDirectory(stateRoot); Directory.CreateDirectory(pointRoot);
            string run = new string('2', 32), admission = new string('a', 64);
            var files = new Dictionary<string, object>();
            foreach (string name in new[] { "generator.pt", "discriminator.pt", "training.pt" })
            {
                byte[] payload = [1, 2, (byte)step];
                await File.WriteAllBytesAsync(Path.Combine(pointRoot, name), payload, Token);
                files[name] = new { bytes = 3, sha256 = Hash(payload) };
            }
            long epoch = step / 2, ordinal = step % 2;
            string manifestHash = await WriteJson(Path.Combine(pointRoot, "manifest.json"), new { format = 2, purpose = "complete-update-checkpoint", identity = new { run_id = run },
                position = new { global_step = step, completed_epoch = epoch, epoch_size = 2, next_ordinal = ordinal, admission_sha256 = admission }, files });
            await File.WriteAllTextAsync(Path.Combine(pointRoot, "READY"), manifestHash + "\n", Token);
            string receiptPath = Path.Combine(execution.OutputRoot, "receipt" + step + ".json");
            string receiptHash = await WriteJson(receiptPath, new { purpose = "supervisor-accepted-personal-native-update", native_worker_exit_code = 0,
                native_worker_timed_out = false, native_worker_deadline_ms = 60000, accepted_after_native_worker_exit = true, quality_established = false, auto_activate_voice = false,
                point = new { run_id = run, checkpoint_root = pointRoot, checkpoint_manifest_sha256 = manifestHash,
                    global_step = step, completed_epoch = epoch, epoch_size = 2, next_ordinal = ordinal, admission_sha256 = admission } });
            string requestPath = Path.Combine(execution.OutputRoot, "request.json"), requestHash = new string('c', 64);
            string statePath = Path.Combine(stateRoot, "workflow_state.json");
            string stateHash = await WriteJson(statePath, new { format = 1, purpose = "local-personal-training-workflow-state", request = new { path = requestPath, sha256 = requestHash },
                source = new { path = execution.SourcePath, sha256 = execution.SourceSha256, consent_confirmed = true }, run_id = run, inputs = new { admission = new { sha256 = admission } },
                accepted = new { checkpoint = new { root = pointRoot, sha256 = manifestHash }, receipt = new { path = receiptPath, sha256 = receiptHash } },
                global_step = step, completed_epoch = epoch, epoch_size = 2, stop_reason = reason });
            await File.WriteAllTextAsync(Path.Combine(stateRoot, "READY"), stateHash + "\n", Token);
            var reference = new WorkflowReference(statePath, stateHash, step, epoch, 2, reason);
            Points.Add(reference); return reference;
        }
        public void Dispose() => Box.Dispose();
    }
    private sealed class FixtureExecutor(Fixture fixture, long[] steps, string reason = "initial_update") : ITrainingWorkflowExecutor
    {
        public async Task<WorkflowReference?> RunAsync(TrainingExecution execution, Func<TrainingProgress, Task> progress, Func<WorkflowReference, Task> checkpoint, CancellationToken token)
        {
            WorkflowReference? last = null;
            foreach (long step in steps) { last = await fixture.Point(execution, step, reason); await checkpoint(last); }
            return last;
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task<string> WriteJson(string path, object value)
    { byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value); await File.WriteAllBytesAsync(path, bytes, Token); return Hash(bytes); }
}

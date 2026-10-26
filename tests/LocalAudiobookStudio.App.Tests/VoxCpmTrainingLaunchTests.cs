using System.Text.Json;
using LocalAudiobookStudio.App.Hosting;
using LocalAudiobookStudio.App.VoxCpmTraining;

namespace LocalAudiobookStudio.App.Tests;
public sealed class VoxCpmTrainingLaunchTests
{
    [Fact]
    public void Explicit_training_suffix_composes_in_both_orders_without_runtime_creation()
    {
        using var temporary = new CorpusTemporaryDirectory(); string config = Config(temporary);
        foreach (string[] args in new[]
        {
            new[] { "--voxcpm-training-runtime-config", config, "--developer-cpu-runtime", temporary.Child("runtime"), temporary.Child("model"), temporary.Child("lock") },
            new[] { "--developer-cpu-runtime", temporary.Child("runtime"), temporary.Child("model"), temporary.Child("lock"), "--voxcpm-training-runtime-config", config },
            new[] { "--synthetic-data-root", temporary.Child("data"), "--voxcpm-training-runtime-config", config }
        })
        { var options = LocalAppHostDependencies.CreateForArguments(args).VoxCpmTrainingOptions; Assert.NotNull(options); Assert.Equal(temporary.Child("worker"), options.WorkerRoot); }
        Assert.False(File.Exists(temporary.Child("python.exe"))); Assert.False(Directory.Exists(temporary.Child("data")));
        Assert.Null(LocalAppHostDependencies.CreateForArguments([]).VoxCpmTrainingOptions);
    }
    [Fact]
    public void Duplicate_or_invalid_training_suffix_is_rejected()
    {
        using var t = new CorpusTemporaryDirectory(); string config = Config(t);
        Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments(["--voxcpm-training-runtime-config", config, "--voxcpm-training-runtime-config", config]));
        File.WriteAllText(config, "{}"); Assert.Throws<ArgumentException>(() => LocalAppHostDependencies.CreateForArguments(["--voxcpm-training-runtime-config", config]));
    }
    [Fact]
    public void Build_output_contains_exact_eight_worker_files_and_pinned_public_lock()
    {
        VoxCpmTrainingRuntimeInventory.VerifyWorkerDirectory(Path.Combine(AppContext.BaseDirectory, "VoxCpmTrainingWorker"));
        Assert.Equal(VoxCpmTrainingRuntimeInventory.TrainingLockSha256, VoxCpmTrainingProtocol.Digest(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "locks", "voxcpm2-training.lock.json"))));
    }
    private static string Config(CorpusTemporaryDirectory t)
    {
        string path = t.Child("vox-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { pythonPath = t.Child("python.exe"), pythonBaseRoot = t.Child("base"), dependencySites = new[] { t.Child("site") },
            workerRoot = t.Child("worker"), sourceRoot = t.Child("source"), modelRoot = t.Child("model"), trainingLockPath = t.Child("training.lock.json"), runtimeInventoryPath = t.Child("inventory.json"), cachePath = t.Child("frozen.bin") })); return path;
    }
}

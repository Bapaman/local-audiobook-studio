using LocalAudiobookStudio.App.VoxCpmTraining;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmTrainingWorkerPinTests
{
    [Fact]
    public void Public_lock_recipe_and_layout_match_the_managed_canonical_identity()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Repository(), "eng", "locks", "voxcpm2-training.lock.json"));
        Assert.Equal(VoxCpmTrainingRuntimeInventory.TrainingLockSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        using var document = VoxCpmTrainingJson.Parse(bytes); var root = document.RootElement;
        var sourceFiles = VoxCpmTrainingRuntimeInventory.AssetFiles(root.GetProperty("sourceFiles"), 38);
        Assert.Equal(0, sourceFiles.Single(f => f.Filename == "src/voxcpm/modules/__init__.py").Bytes);
        Assert.Equal(VoxCpmTrainingRuntimeInventory.Scripts.Values.OrderBy(f => f.Filename, StringComparer.Ordinal), VoxCpmTrainingRuntimeInventory.AssetFiles(root.GetProperty("workerFiles"), 8));
        foreach (string field in new[] { "recipe", "adapterLayout" })
            Assert.Equal(root.GetProperty(field + "Sha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(VoxCpmTrainingJson.Canonical(root.GetProperty(field)))));
    }
    internal static string Repository()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "LocalAudiobookStudio.sln"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root); return root;
    }
    [Fact]
    public void Worker_allowlist_is_exact_eight_files_without_pycache()
    {
        string root = Repository();
        string worker = Path.Combine(root, "src", "VoxCpmTrainingWorker");
        VoxCpmTrainingRuntimeInventory.VerifyWorkerDirectory(worker);
        using var fixture = new VoxRuntimeFixture();
        foreach (string path in Directory.GetFiles(worker)) File.Copy(path, fixture.Child(Path.GetFileName(path)));
        Directory.CreateDirectory(fixture.Child("__pycache__"));
        File.WriteAllBytes(fixture.Child("__pycache__/hidden.pyc"), [0]);
        Assert.ThrowsAny<Exception>(() => VoxCpmTrainingRuntimeInventory.VerifyWorkerDirectory(fixture.Root));
    }
}

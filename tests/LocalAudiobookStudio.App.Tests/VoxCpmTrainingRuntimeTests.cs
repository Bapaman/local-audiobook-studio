using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmTrainingRuntimeTests
{
    [Fact]
    public async Task Fresh_identity_primes_advisory_readiness_without_caching_execution_admission()
    {
        using var fixture = new VoxRuntimeFixture();
        var inventory = new VoxFakeInventory();
        var runtime = new VoxCpmTrainingRuntime(VoxCpmTrainingRuntimeOptions.Read(fixture.Config()), inventory);
        _ = await runtime.GetIdentityAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, inventory.Calls);
        Assert.Equal(1, inventory.Releases);
        Assert.True((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        Assert.Equal(1, inventory.Calls);
        using (await runtime.AcquireAsync(TestContext.Current.CancellationToken)) { }
        Assert.Equal(2, inventory.Calls);
        runtime.MarkCacheChanged();
        Assert.False((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => runtime.AcquireAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, inventory.Calls);
    }

    [Fact]
    public async Task Admission_is_inert_closed_inventory_and_descriptor_bound()
    {
        using var fixture = new VoxRuntimeFixture();
        var options = VoxCpmTrainingRuntimeOptions.Read(fixture.Config());
        var runtime = new VoxCpmTrainingRuntime(options);
        Assert.False((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        Assert.False(Directory.Exists(options.WorkerRoot));
        File.AppendAllText(options.DescriptorPath!, " ");
        await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => runtime.AcquireAsync(TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(options.SourceRoot));
    }

    [Fact]
    public async Task Positive_readiness_is_coalesced_but_launch_always_rehashes()
    {
        using var fixture = new VoxRuntimeFixture();
        var inventory = new VoxFakeInventory(); var clock = new CorpusManualTime();
        var runtime = new VoxCpmTrainingRuntime(VoxCpmTrainingRuntimeOptions.Read(fixture.Config()), inventory, clock);
        Task<VoxCpmTrainingReadinessDto> a = runtime.GetReadinessAsync(TestContext.Current.CancellationToken);
        Task<VoxCpmTrainingReadinessDto> b = runtime.GetReadinessAsync(TestContext.Current.CancellationToken);
        Assert.True((await a).Ready); Assert.True((await b).Ready);
        Assert.Equal(1, inventory.Calls); Assert.Equal(1, inventory.Releases);
        inventory.Failure = new IOException();
        Assert.True((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => runtime.AcquireAsync(TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(300));
        Assert.False((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        Assert.False((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        Assert.Equal(4, inventory.Calls);
    }

    [Fact]
    public async Task Closed_runtime_rejects_inserted_importable_and_fresh_descriptor_hash_mismatch()
    {
        using var fixture = new VoxRuntimeFixture();
        string config = fixture.Config(); var options = VoxCpmTrainingRuntimeOptions.Read(config);
        string baseRoot = options.PythonBaseRoot;
        Directory.CreateDirectory(baseRoot);
        File.WriteAllBytes(Path.Combine(baseRoot, "python.exe"), [0]);
        options = options with { PythonPath = Path.Combine(baseRoot, "python.exe") };
        foreach (string module in new[] { "numpy", "scipy", "torch", "transformers", "safetensors", "pydantic", "einops" })
        {
            string directory = Path.Combine(options.DependencySites[0], module); Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, "__init__.py"), [0]);
        }
        string repo = VoxCpmTrainingWorkerPinTests.Repository(); Directory.CreateDirectory(options.WorkerRoot);
        foreach (string file in Directory.GetFiles(Path.Combine(repo, "src", "VoxCpmTrainingWorker"))) File.Copy(file, Path.Combine(options.WorkerRoot, Path.GetFileName(file)));
        var files = new[] { baseRoot, options.DependencySites[0], options.WorkerRoot }.SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories)).Select(path => new
        {
            path, bytes = new FileInfo(path).Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
        }).ToArray();
        JsonElement inventory = JsonSerializer.SerializeToElement(new { schemaVersion = 1, files });
        using (var pins = new VoxCpmTrainingFilePins())
            await VoxCpmTrainingRuntimeInventory.VerifyClosedRuntimeAsync(options, pins, inventory, TestContext.Current.CancellationToken);
        File.WriteAllBytes(Path.Combine(options.DependencySites[0], "undeclared.py"), [0]);
        using (var pins = new VoxCpmTrainingFilePins())
            await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingRuntimeInventory.VerifyClosedRuntimeAsync(options, pins, inventory, TestContext.Current.CancellationToken));
        File.AppendAllText(config, " ");
        using (var pins = new VoxCpmTrainingFilePins())
            await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => pins.ReadAsync(config, 32768, options.DescriptorSha256, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Header_derived_layout_matches_shared_python_golden_without_tensor_import()
    {
        using var fixture = new VoxRuntimeFixture(); string path = fixture.Child("tiny.safetensors");
        byte[] header = System.Text.Encoding.UTF8.GetBytes("{\"base_lm.layers.0.self_attn.q_proj.weight\":{\"dtype\":\"F32\",\"shape\":[2,2],\"data_offsets\":[0,16]}}");
        using (var output = File.Create(path)) { output.Write(BitConverter.GetBytes((ulong)header.Length)); output.Write(header); output.Write(new byte[16]); }
        using var input = File.OpenRead(path);
        var layout = await VoxCpmTrainingRuntimeInventory.ReadAdapterLayoutAsync(input, TestContext.Current.CancellationToken);
        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(VoxCpmTrainingWorkerPinTests.Repository(), "tests", "fixtures", "VoxCpmTrainingProtocol", "request.json")));
        Assert.Equal(VoxCpmTrainingJson.Canonical(golden.RootElement.GetProperty("parameterLayout")),
            VoxCpmTrainingJson.Canonical(JsonSerializer.SerializeToElement(layout, VoxCpmTrainingJson.Options)));
        Assert.Equal("7a3c992b2af4150f9d7dd9dcbd1279a50861996f5cb749662a761178825c1ccb",
            VoxCpmTrainingRuntimeInventory.Fingerprint(Enumerable.Repeat(new string('a', 64), 6).ToArray()));
    }
}

internal sealed class VoxRuntimeFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "las-vox-runtime-" + Guid.NewGuid().ToString("N"));
    internal VoxRuntimeFixture() => Directory.CreateDirectory(Root);
    internal string Child(string relative) => Path.Combine(Root, relative);
    internal string Config()
    {
        string path = Child("descriptor.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            pythonPath = Child("python.exe"), pythonBaseRoot = Child("base"), dependencySites = new[] { Child("sites") },
            workerRoot = Child("worker"), sourceRoot = Child("source"), modelRoot = Child("model"),
            trainingLockPath = Child("lock.json"), runtimeInventoryPath = Child("inventory.json"), cachePath = Child("cache.bin")
        }));
        return path;
    }
    public void Dispose() => Directory.Delete(Root, true);
}

internal sealed class VoxFakeInventory : IVoxCpmTrainingRuntimeInventory
{
    internal int Calls, Releases;
    internal Exception? Failure;
    public Task<VoxCpmTrainingRuntimeLease> OpenAsync(VoxCpmTrainingRuntimeOptions options, CancellationToken token)
    {
        Calls++;
        if (Failure != null) throw Failure;
        string sha = new('a', 64);
        return Task.FromResult(new VoxCpmTrainingRuntimeLease(new(sha, new('b', 40), new('c', 40), sha, sha, sha, sha, sha, sha, sha), [], [], [], sha, new Release(this)));
    }
    private sealed class Release(VoxFakeInventory owner) : IDisposable { public void Dispose() => owner.Releases++; }
}

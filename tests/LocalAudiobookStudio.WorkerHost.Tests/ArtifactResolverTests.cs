using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.WorkerHost.Engine;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Tests;

public class ArtifactResolverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fixture_cleanup_waits_for_a_temporary_lock_on_its_copied_executable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = new Artifacts();
        fixture.UseFakeExecutable();
        string executable = Path.Combine(fixture.Runtime, "bin", "voxcpm2-cli.exe");
        using var locked = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(() =>
        {
            disposing.SetResult();
            fixture.Dispose();
        }, Token);
        try
        {
            try
            {
                await disposing.Task.WaitAsync(Token);
                await Task.Delay(TimeSpan.FromMilliseconds(150), Token);
            }
            finally { locked.Dispose(); await cleanup; }
            Assert.False(Directory.Exists(fixture.Root));
        }
        finally
        {
            if (Directory.Exists(fixture.Root)) fixture.Dispose();
        }
    }

    [Fact]
    public async Task Verified_developer_artifacts_ignore_manifest_absolute_paths_and_hold_selected_bytes()
    {
        using var fixture = new Artifacts();
        using var selected = await fixture.Resolver().ResolveAsync("q8-f16-hybrid-16", Token);
        Assert.Equal(Path.Combine(fixture.Runtime, "bin", "voxcpm2-cli.exe"), selected.Executable);
        Assert.Equal(Path.Combine(fixture.Model, "VoxCPM2-BaseLM-Q8_0.gguf"), selected.BaseModel);
        Assert.Equal(16, selected.GpuLayers); Assert.True(selected.Experimental);
        Assert.Throws<IOException>(() => File.WriteAllText(selected.BaseModel, "changed"));
    }
    [Theory]
    [InlineData("ready")] [InlineData("hash")] [InlineData("source")] [InlineData("patch")]
    [InlineData("duplicates")] [InlineData("closure")] [InlineData("format")] [InlineData("staging")]
    [InlineData("upstream")] [InlineData("workerStaging")] [InlineData("canonicalIdentity")]
    [InlineData("nullHash")]
    public async Task Untrusted_or_incomplete_artifacts_fail_with_safe_code(string change)
    {
        using var fixture = new Artifacts();
        switch (change)
        {
            case "ready": File.Delete(Path.Combine(fixture.Model, "READY")); break;
            case "hash": File.WriteAllText(Path.Combine(fixture.Model, "VoxCPM2-BaseLM-Q8_0.gguf"), "changed"); break;
            case "source": fixture.Worker["source"]!["commit"] = new string('a', 40); break;
            case "patch": fixture.Worker["source"]!["patch"]!["checkedWavWrites"] = false; break;
            case "duplicates": fixture.Worker["files"]!.AsArray().Add(fixture.Worker["files"]![0]!.DeepClone()); break;
            case "closure": fixture.Worker["files"]!.AsArray().RemoveAt(0); break;
            case "format": fixture.Generated["files"]![0]!["format"] = "F32"; break;
            case "staging": File.WriteAllText(Path.Combine(fixture.Model, "unexpected.part"), "x"); break;
            case "workerStaging": File.WriteAllText(Path.Combine(fixture.Runtime, "voxcpm2-worker-manifest.json.part"), "x"); break;
            case "upstream":
                var ml = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Locks, "voxcpm2.lock.json")))!;
                ml["files"]!.AsArray().RemoveAt(0); File.WriteAllText(Path.Combine(fixture.Locks, "voxcpm2.lock.json"), ml.ToJsonString()); break;
            case "canonicalIdentity":
                var wl = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Locks, "llama-cpp-omni.lock.json")))!;
                wl["repository"] = "https://example.invalid/community.git"; File.WriteAllText(Path.Combine(fixture.Locks, "llama-cpp-omni.lock.json"), wl.ToJsonString()); break;
            case "nullHash": fixture.Worker["files"]![0]!["sha256"] = null; break;
        }
        fixture.WriteManifests();
        var error = await Assert.ThrowsAsync<WorkerException>(async () => { using var unexpected = await fixture.Resolver().ResolveAsync("q8-f16-hybrid-16", Token); });
        Assert.Equal("worker.artifacts", error.Code); Assert.DoesNotContain(fixture.Root, error.ToString());
    }
    [Fact]
    public async Task Developer_profile_requires_explicit_option_and_never_defaults()
    {
        using var fixture = new Artifacts();
        Assert.Equal("worker.developer.required", (await Assert.ThrowsAsync<WorkerException>(() => fixture.Resolver(false).ResolveAsync("q8-f16-hybrid-16", Token))).Code);
        Assert.Equal("worker.profile", (await Assert.ThrowsAsync<WorkerException>(() => fixture.Resolver().ResolveAsync("", Token))).Code);
    }
    [Theory]
    [InlineData("../outside.wav")] [InlineData("/root.wav")] [InlineData("C:\\outside.wav")]
    [InlineData("segments/a.wav:stream")] [InlineData("segments/NUL.wav")] [InlineData("segments/a. /b.wav")]
    [InlineData("segments/../x.wav")] [InlineData("segments//x.wav")]
    [InlineData("segments/COM¹.wav")] [InlineData("segments/CONOUT$.wav")]
    public void Requests_cannot_widen_project_root(string path)
    {
        using var fixture = new Artifacts();
        Assert.Equal("worker.path", Assert.Throws<WorkerException>(() => WorkerPaths.Resolve(fixture.Root, path)).Code);
    }
    [Fact]
    public void Volume_root_contains_relative_children()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(Path.Combine(root, "project", "child.wav"), WorkerPaths.Resolve(root, "project/child.wav"));
    }
    internal sealed class Artifacts : IDisposable
    {
        private string? copiedExecutable;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "las-worker-" + Guid.NewGuid().ToString("N"));
        public string Runtime => Path.Combine(Root, "runtime"); public string Model => Path.Combine(Root, "model");
        public string Locks => Path.Combine(Root, "locks");
        public JsonNode Worker { get; } public JsonNode Generated { get; }
        internal static readonly string[] Closure = ["cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll", "ggml-base.dll", "ggml-cpu.dll", "ggml-cuda.dll", "ggml.dll", "llama-common.dll", "llama-quantize-impl.dll", "llama-quantize.exe", "llama.dll", "voxcpm2-cli.exe"];
        public Artifacts()
        {
            Directory.CreateDirectory(Path.Combine(Runtime, "bin")); Directory.CreateDirectory(Model); Directory.CreateDirectory(Locks);
            Worker = JsonNode.Parse("""
              {"schemaVersion":1,"source":{"repository":"https://github.com/tc-mb/llama.cpp-omni.git","tag":"b267","commit":"873056743b74e1a4ce5dcf7290e2298428e214db","patch":{"sha256":"d83350dc53c73713102c9fe9bbc5a0ab984edbeebf59926c6b45272a6c67a26a","patchedCliSha256":"39c5ceace9275582e5e2fdff88435d402ec9e4fc746e97b0a6cf2e2459ac731c","windowsUtf8Arguments":true,"unicodePaths":true,"strictPcm16Input":true,"checkedWavWrites":true}},"files":[]}
              """)!;
            foreach (string name in Closure) Worker["files"]!.AsArray().Add(Record(Path.Combine(Runtime, "bin"), name, null, null));
            Generated = JsonNode.Parse("""
              {"schemaVersion":1,"artifact":"VoxCPM2-GGUF","source":{"repository":"openbmb/VoxCPM2","revision":"32279effe8c19989596f05d353d1447f51d9e915"},"converter":{"repository":"https://github.com/tc-mb/llama.cpp-omni.git","tag":"b267","commit":"873056743b74e1a4ce5dcf7290e2298428e214db","script":"tools/omni/voxcpm2/convert_voxcpm2_to_gguf.py"},"files":[]}
              """)!;
            foreach (var item in new[] { ("BaseLM", "F16"), ("Acoustic", "F16"), ("BaseLM", "F32"), ("Acoustic", "F32"), ("BaseLM", "Q8_0") })
                Generated["files"]!.AsArray().Add(Record(Model, $"VoxCPM2-{item.Item1}-{item.Item2}.gguf", item.Item1, item.Item2));
            var sourceLock = JsonNode.Parse("""
              {"schemaVersion":1,"artifact":"llama.cpp-omni","kind":"git","repository":"https://github.com/tc-mb/llama.cpp-omni.git","tag":"b267","commit":"873056743b74e1a4ce5dcf7290e2298428e214db","license":"MIT"}
              """)!;
            var modelLock = JsonNode.Parse("""
              {"schemaVersion":1,"artifact":"VoxCPM2","kind":"huggingface","repository":"openbmb/VoxCPM2","revision":"32279effe8c19989596f05d353d1447f51d9e915","license":"Apache-2.0","generated":{"manifest":"generated-manifest.json"}}
              """)!;
            modelLock["converter"] = Generated["converter"]!.DeepClone(); modelLock["generated"]!["files"] = Generated["files"]!.DeepClone();
            modelLock["files"] = new JsonArray();
            foreach (string name in new[] { ".gitattributes", "audiovae.pth", "config.json", "model.safetensors", "README.md", "special_tokens_map.json", "tokenization_voxcpm2.py", "tokenizer_config.json", "tokenizer.json" })
                modelLock["files"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { path = name, length = 1, sha256 = new string('a', 64) }));
            File.WriteAllText(Path.Combine(Locks, "llama-cpp-omni.lock.json"), sourceLock.ToJsonString());
            File.WriteAllText(Path.Combine(Locks, "voxcpm2.lock.json"), modelLock.ToJsonString());
            File.WriteAllBytes(Path.Combine(Model, "READY"), []); WriteManifests();
        }
        private static JsonNode Record(string root, string name, string? role, string? format)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("tiny:" + name); File.WriteAllBytes(Path.Combine(root, name), bytes);
            return JsonSerializer.SerializeToNode(new { name, path = "C:\\untrusted\\elsewhere", length = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), role, format })!;
        }
        public void WriteManifests()
        {
            File.WriteAllText(Path.Combine(Runtime, "voxcpm2-worker-manifest.json"), Worker.ToJsonString());
            File.WriteAllText(Path.Combine(Model, "generated-manifest.json"), Generated.ToJsonString());
        }
        public void UseFakeExecutable()
        {
            string source = Path.GetDirectoryName(NativeLauncherTests.FakeExecutable)!;
            foreach (string name in new[] { "llama.dll", "llama.runtimeconfig.json", "llama.deps.json" }) File.Copy(Path.Combine(source, name), Path.Combine(Runtime, "bin", name), true);
            string executable = Path.Combine(Runtime, "bin", "voxcpm2-cli.exe");
            File.Copy(NativeLauncherTests.FakeExecutable, executable, true);
            copiedExecutable = executable;
            foreach (var record in Worker["files"]!.AsArray())
            {
                string name = record!["name"]!.GetValue<string>(); byte[] bytes = File.ReadAllBytes(Path.Combine(Runtime, "bin", name));
                record["length"] = bytes.Length; record["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
            WriteManifests();
        }
        public ArtifactResolver Resolver(bool developer = true) => new(Runtime, Model, Locks, developer);
        public void Dispose()
        {
            if (OperatingSystem.IsWindows() && copiedExecutable is not null)
            {
                // Windows can briefly refuse deletion of the test copy after the
                // worker assertions have confirmed shutdown. Leave other cleanup
                // failures and persistent locks visible to the test runner.
                for (int retry = 0; ; retry++)
                {
                    try { File.Delete(copiedExecutable); break; }
                    catch (Exception error) when (retry < 20
                        && error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020))
                    { Thread.Sleep(25); }
                }
            }
            Directory.Delete(Root, true);
        }
    }
}

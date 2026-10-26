using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.App.Workers;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.App.Tests;

public class WorkerSupervisorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Host => Path.Combine(AppContext.BaseDirectory, "LocalAudiobookStudio.WorkerHost.exe");

    [Fact]
    public async Task Unconfigured_runtime_is_not_ready_and_never_opens_default_user_roots()
    {
        var runtime = new WorkerRuntimeReadiness(null, "NOT_A_PATH");
        Assert.Equal(new RuntimeReadiness(false, false, "worker.runtime.not_ready", "q8-f16-cpu"), await runtime.GetAsync(Token));
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => ((IRuntimeDescriptor)runtime).GetAsync(Token));
        Assert.Equal("worker.runtime.not_ready", error.Code);
        Assert.True(error.CleanupConfirmed);
    }

    [Theory]
    [InlineData("missing")] [InlineData("hash")] [InlineData("manifest")] [InlineData("host")]
    public async Task Readiness_fails_closed_for_missing_or_changed_owned_synthetic_artifacts(string change)
    {
        using var test = new TinyRuntime();
        switch (change)
        {
            case "missing": File.Delete(Path.Combine(test.Model, "READY")); break;
            case "hash": File.AppendAllText(Path.Combine(test.Model, "VoxCPM2-BaseLM-Q8_0.gguf"), "changed"); break;
            case "manifest": test.Worker["source"]!["commit"] = new string('0', 40); test.WriteManifests(); break;
            case "host": File.Delete(Path.Combine(test.Managed, "LocalAudiobookStudio.WorkerHost.runtimeconfig.json")); break;
        }
        var runtime = new WorkerRuntimeReadiness(test.Options, test.Managed);
        var result = await runtime.GetAsync(Token);
        Assert.False(result.Ready); Assert.True(result.Experimental);
        Assert.Equal("worker.runtime.not_ready", result.DiagnosticCode);
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => ((IRuntimeDescriptor)runtime).GetAsync(Token));
        Assert.DoesNotContain(test.Root, error.ToString());
    }

    [Fact]
    public async Task Verified_descriptor_uses_fixed_CPU_preset_and_revalidates_instead_of_caching_ready()
    {
        using var test = new TinyRuntime();
        var runtime = new WorkerRuntimeReadiness(test.Options, test.Managed);
        Assert.True((await runtime.GetAsync(Token)).Ready);
        var descriptor = await ((IRuntimeDescriptor)runtime).GetAsync(Token);
        Assert.Equal("voxcpm2-local-v1", descriptor.Preset.Id);
        Assert.Equal("q8-f16-cpu", descriptor.Preset.ProfileId);
        Assert.Equal((70, 10, 2d, 1d, 42L), (descriptor.Preset.Steps, descriptor.Preset.Timesteps, descriptor.Preset.Cfg, descriptor.Preset.Temperature, descriptor.Preset.Seed));
        Assert.False(descriptor.Preset.AllowCpuFallback);
        Assert.Equal("873056743b74e1a4ce5dcf7290e2298428e214db", descriptor.Versions.Worker);
        Assert.Equal("32279effe8c19989596f05d353d1447f51d9e915", descriptor.Versions.Model);
        Assert.Equal("", descriptor.Versions.App); Assert.Equal("", descriptor.Versions.Policy);
        File.AppendAllText(Path.Combine(test.Model, "VoxCPM2-Acoustic-F16.gguf"), "changed");
        Assert.False((await runtime.GetAsync(Token)).Ready);
    }

    [Fact]
    public async Task Hybrid_requires_explicit_configuration_and_unrecognized_profile_is_not_ready()
    {
        using var test = new TinyRuntime();
        var hybrid = new WorkerRuntimeReadiness(test.Options with { ProfileId = "q8-f16-hybrid-16", AllowCpuFallback = true }, test.Managed);
        Assert.Equal("q8-f16-hybrid-16", (await hybrid.GetAsync(Token)).ProfileId);
        Assert.True((await ((IRuntimeDescriptor)hybrid).GetAsync(Token)).Preset.AllowCpuFallback);
        Assert.False((await new WorkerRuntimeReadiness(test.Options with { ProfileId = "PRIVATE_INVALID" }, test.Managed).GetAsync(Token)).Ready);
    }

    [Fact]
    public void Launch_is_hidden_parent_bound_and_secret_is_only_in_child_environment()
    {
        using var test = new TinyRuntime();
        using var parent = Process.GetCurrentProcess();
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            string pipe = "las-supervisor-test-" + Guid.NewGuid().ToString("N");
            var start = WorkerProcessSupervisor.BuildStartInfo(Host, test.Root, test.Options, pipe, secret, parent.Id, parent.StartTime.ToUniversalTime().Ticks);
            Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
            Assert.True(start.RedirectStandardOutput); Assert.True(start.RedirectStandardError);
            Assert.Equal("", start.Arguments); Assert.Contains("--developer-mode", start.ArgumentList);
            Assert.Equal(17, start.ArgumentList.Count);
            Assert.DoesNotContain(Convert.ToBase64String(secret), start.ArgumentList);
            Assert.Equal(Convert.ToBase64String(secret), start.Environment["LAS_WORKER_SESSION_SECRET"]);
            Assert.Equal(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), start.ArgumentList[start.ArgumentList.IndexOf("--parent-pid") + 1]);
            Assert.Null(Environment.GetEnvironmentVariable("LAS_WORKER_SESSION_SECRET"));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    [Fact]
    public async Task Actual_managed_host_authenticates_and_shutdown_proves_owned_tree_exit()
    {
        using var test = new TinyRuntime();
        var owner = WorkerProcessSupervisor.Start(Host, test.Root, test.Options, TimeProvider.System);
        using var process = Process.GetProcessById(owner.ProcessId);
        await using (var client = await WorkerPipeClient.ConnectAsync(owner.PipeName, owner.Secret, owner, TimeProvider.System, new(), Token))
            Assert.False(process.HasExited);
        Assert.True(process.HasExited);
        Assert.True(await owner.StopAsync(TimeSpan.FromSeconds(1), Token));
        Assert.True(owner.DrainsCompleted);
        Assert.True(owner.NativeDisposed);
        // No synthesis request: tiny fake model bytes have never been executed.
    }

    [Fact]
    public async Task Owned_host_waiting_for_handshake_can_be_forcibly_stopped_without_touching_other_processes()
    {
        using var test = new TinyRuntime();
        var owner = WorkerProcessSupervisor.Start(Host, test.Root, test.Options, TimeProvider.System);
        using var process = Process.GetProcessById(owner.ProcessId);
        Assert.True(await owner.StopAsync(TimeSpan.FromSeconds(5), Token));
        Assert.True(process.HasExited); Assert.True(owner.DrainsCompleted); Assert.True(owner.NativeDisposed);
    }

    [Fact]
    public async Task Factory_does_not_launch_when_runtime_is_unconfigured_or_token_is_already_canceled()
    {
        var runtime = new WorkerRuntimeReadiness(null, "NOT_A_PATH");
        var factory = new WorkerProcessSupervisor.Factory(runtime, TimeProvider.System, new());
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => factory.OpenAsync("NOT_A_PATH", Token));
        Assert.Equal("worker.runtime.not_ready", error.Code); Assert.True(error.CleanupConfirmed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.OpenAsync("NOT_A_PATH", new CancellationToken(true)));
    }

    [Fact]
    public async Task Invalid_client_deadlines_are_rejected_before_factory_launch()
    {
        using var test = new TinyRuntime();
        var runtime = new WorkerRuntimeReadiness(test.Options, test.Managed);
        var factory = new WorkerProcessSupervisor.Factory(runtime, TimeProvider.System, new() { Shutdown = TimeSpan.FromSeconds(31) });
        var error = await Assert.ThrowsAsync<WorkerClientException>(() => factory.OpenAsync(test.Root, Token));
        Assert.Equal("worker.configuration", error.Code); Assert.True(error.CleanupConfirmed);
    }

    [Fact]
    public async Task Wrong_parent_start_identity_is_rejected_by_actual_managed_host_with_safe_code()
    {
        using var test = new TinyRuntime(); using var parent = Process.GetCurrentProcess();
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var start = WorkerProcessSupervisor.BuildStartInfo(Host, test.Root, test.Options,
                "las-parent-identity-" + Guid.NewGuid().ToString("N"), secret, parent.Id, parent.StartTime.ToUniversalTime().Ticks + 1);
            using var child = WindowsNativeLauncher.Start(start);
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(Token), stderr = child.StandardError.ReadToEndAsync(Token);
            Assert.Equal(2, await child.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
            await child.StopTreeAsync();
            Assert.Equal("", await stdout); Assert.Equal("worker.configuration" + Environment.NewLine, await stderr);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    [Fact]
    public async Task Force_stop_during_synthetic_inference_exits_owned_host_native_child_and_grandchild()
    {
        using var test = new TinyRuntime(); test.UseFakeExecutable(); test.WritePrompt();
        var owner = WorkerProcessSupervisor.Start(Host, test.Root, test.Options, TimeProvider.System);
        using var host = Process.GetProcessById(owner.ProcessId);
        var client = await WorkerPipeClient.ConnectAsync(owner.PipeName, owner.Secret, owner, TimeProvider.System, new(), Token);
        string eventName = "Local\\las-supervisor-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        Process[] descendants = [];
        try
        {
            var command = new SynthesizeSegmentCommand("tree-" + eventName, "prompt.wav", "SYNTHETIC_PRIVATE_TRANSCRIPT", "out.wav", "q8-f16-cpu", 70, 10, 2, 1, 42);
            var synthesis = client.SynthesizeAsync(Guid.NewGuid().ToString("N"), command, _ => { }, Token);
            Assert.True(await Task.Run(() => ready.WaitOne(5000), Token));
            // IDs were emitted by this owned synthetic fixture only, not enumerated.
            descendants = File.ReadAllText(Path.Combine(test.Root, "out.wav.part.pids")).Split(',').Select(x => Process.GetProcessById(int.Parse(x, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            Assert.Equal(3, descendants.Length);
            Assert.True(await owner.StopAsync(TimeSpan.FromSeconds(5), Token));
            var error = await Assert.ThrowsAsync<WorkerClientException>(() => synthesis.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.True(error.CleanupConfirmed); Assert.Equal("worker.interrupted", error.Code);
            Assert.True(host.HasExited);
            foreach (var process in descendants) Assert.True(process.HasExited);
            Assert.True(owner.DrainsCompleted); Assert.True(owner.NativeDisposed);
        }
        finally
        {
            await owner.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            foreach (var process in descendants) process.Dispose();
            await Assert.ThrowsAsync<WorkerClientException>(async () => await client.DisposeAsync());
        }
    }

    // Owned, tiny, unsigned consistency fixture. Neither a real model nor an inference backend.
    private sealed class TinyRuntime : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "las-supervisor-" + Guid.NewGuid().ToString("N"));
        public string Runtime => Path.Combine(Root, "runtime"); public string Model => Path.Combine(Root, "model");
        public string Locks => Path.Combine(Root, "locks"); public string Managed => Path.Combine(Root, "managed");
        public DeveloperWorkerOptions Options => new(Runtime, Model, Locks);
        public JsonNode Worker { get; } public JsonNode Generated { get; }
        public TinyRuntime()
        {
            foreach (string directory in new[] { Path.Combine(Runtime, "bin"), Model, Locks, Managed }) Directory.CreateDirectory(directory);
            foreach (string extension in new[] { "exe", "dll", "deps.json", "runtimeconfig.json" }) File.WriteAllText(Path.Combine(Managed, "LocalAudiobookStudio.WorkerHost." + extension), "owned-synthetic");
            Worker = JsonNode.Parse("""
              {"schemaVersion":1,"source":{"repository":"https://github.com/tc-mb/llama.cpp-omni.git","tag":"b267","commit":"873056743b74e1a4ce5dcf7290e2298428e214db","patch":{"sha256":"d83350dc53c73713102c9fe9bbc5a0ab984edbeebf59926c6b45272a6c67a26a","patchedCliSha256":"39c5ceace9275582e5e2fdff88435d402ec9e4fc746e97b0a6cf2e2459ac731c","windowsUtf8Arguments":true,"unicodePaths":true,"strictPcm16Input":true,"checkedWavWrites":true}},"files":[]}
              """)!;
            foreach (string name in new[] { "cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll", "ggml-base.dll", "ggml-cpu.dll", "ggml-cuda.dll", "ggml.dll", "llama-common.dll", "llama-quantize-impl.dll", "llama-quantize.exe", "llama.dll", "voxcpm2-cli.exe" }) Worker["files"]!.AsArray().Add(Record(Path.Combine(Runtime, "bin"), name));
            Generated = JsonNode.Parse("""
              {"schemaVersion":1,"artifact":"VoxCPM2-GGUF","source":{"repository":"openbmb/VoxCPM2","revision":"32279effe8c19989596f05d353d1447f51d9e915"},"converter":{"repository":"https://github.com/tc-mb/llama.cpp-omni.git","tag":"b267","commit":"873056743b74e1a4ce5dcf7290e2298428e214db","script":"tools/omni/voxcpm2/convert_voxcpm2_to_gguf.py"},"files":[]}
              """)!;
            foreach (var item in new[] { ("BaseLM", "F16"), ("Acoustic", "F16"), ("BaseLM", "F32"), ("Acoustic", "F32"), ("BaseLM", "Q8_0") })
            {
                var record = Record(Model, $"VoxCPM2-{item.Item1}-{item.Item2}.gguf"); record["role"] = item.Item1; record["format"] = item.Item2; Generated["files"]!.AsArray().Add(record);
            }
            File.WriteAllText(Path.Combine(Locks, "llama-cpp-omni.lock.json"), """
              {"schemaVersion":1,"artifact":"llama.cpp-omni","kind":"git","repository":"https://github.com/tc-mb/llama.cpp-omni.git","tag":"b267","commit":"873056743b74e1a4ce5dcf7290e2298428e214db","license":"MIT"}
              """);
            var modelLock = JsonNode.Parse("""
              {"schemaVersion":1,"artifact":"VoxCPM2","kind":"huggingface","repository":"openbmb/VoxCPM2","revision":"32279effe8c19989596f05d353d1447f51d9e915","license":"Apache-2.0","generated":{"manifest":"generated-manifest.json"}}
              """)!;
            modelLock["converter"] = Generated["converter"]!.DeepClone(); modelLock["generated"]!["files"] = Generated["files"]!.DeepClone(); modelLock["files"] = new JsonArray();
            foreach (string name in new[] { ".gitattributes", "audiovae.pth", "config.json", "model.safetensors", "README.md", "special_tokens_map.json", "tokenization_voxcpm2.py", "tokenizer_config.json", "tokenizer.json" }) modelLock["files"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { path = name, length = 1, sha256 = new string('a', 64) }));
            File.WriteAllText(Path.Combine(Locks, "voxcpm2.lock.json"), modelLock.ToJsonString());
            File.WriteAllBytes(Path.Combine(Model, "READY"), []); WriteManifests();
        }
        private static JsonNode Record(string root, string name)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("tiny:" + name); File.WriteAllBytes(Path.Combine(root, name), bytes);
            return JsonSerializer.SerializeToNode(new { name, length = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() })!;
        }
        public void WriteManifests()
        { File.WriteAllText(Path.Combine(Runtime, "voxcpm2-worker-manifest.json"), Worker.ToJsonString()); File.WriteAllText(Path.Combine(Model, "generated-manifest.json"), Generated.ToJsonString()); }
        public void UseFakeExecutable()
        {
            string source = Path.Combine(AppContext.BaseDirectory, "fake-native");
            foreach (string name in new[] { "llama.dll", "llama.runtimeconfig.json", "llama.deps.json" }) File.Copy(Path.Combine(source, name), Path.Combine(Runtime, "bin", name), true);
            File.Copy(Path.Combine(source, "llama.exe"), Path.Combine(Runtime, "bin", "voxcpm2-cli.exe"), true);
            foreach (var record in Worker["files"]!.AsArray())
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(Runtime, "bin", record!["name"]!.GetValue<string>()));
                record["length"] = bytes.Length; record["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
            WriteManifests();
        }
        public void WritePrompt()
        {
            using var stream = File.Create(Path.Combine(Root, "prompt.wav")); using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8); writer.Write(40); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write(48000); writer.Write(96000); writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(4); writer.Write((short)120); writer.Write((short)-120);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}

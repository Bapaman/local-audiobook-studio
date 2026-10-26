using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Engine;

/// <summary>Local developer manifests establish consistency, not signed provenance. No discovery or download.</summary>
public sealed class ArtifactResolver
{
    public const string WorkerVersion = "873056743b74e1a4ce5dcf7290e2298428e214db";
    public const string ModelVersion = "32279effe8c19989596f05d353d1447f51d9e915";
    private const string Repository = "https://github.com/tc-mb/llama.cpp-omni.git";
    private static readonly string[] Closure = ["cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll", "ggml-base.dll", "ggml-cpu.dll", "ggml-cuda.dll", "ggml.dll", "llama-common.dll", "llama-quantize-impl.dll", "llama-quantize.exe", "llama.dll", "voxcpm2-cli.exe"];
    private static readonly Dictionary<string, (string Role, string Format)> Models = new(StringComparer.Ordinal)
    {
        ["VoxCPM2-BaseLM-F16.gguf"] = ("BaseLM", "F16"), ["VoxCPM2-Acoustic-F16.gguf"] = ("Acoustic", "F16"),
        ["VoxCPM2-BaseLM-F32.gguf"] = ("BaseLM", "F32"), ["VoxCPM2-Acoustic-F32.gguf"] = ("Acoustic", "F32"),
        ["VoxCPM2-BaseLM-Q8_0.gguf"] = ("BaseLM", "Q8_0")
    };
    private readonly string runtime, model, locks;
    private readonly bool developer;
    public ArtifactResolver(string runtimeRoot, string modelRoot, string lockRoot, bool developerMode)
    { runtime = WorkerPaths.Root(runtimeRoot); model = WorkerPaths.Root(modelRoot); locks = WorkerPaths.Root(lockRoot); developer = developerMode; }

    public async Task<ResolvedArtifacts> ResolveAsync(string profileId, CancellationToken token = default)
    {
        if (profileId is not ("q8-f16-hybrid-16" or "q8-f16-cpu")) throw new WorkerException("worker.profile");
        if (!developer) throw new WorkerException("worker.developer.required");
        var held = new List<FileStream>();
        try
        {
            foreach (string root in new[] { runtime, model }) if (Path.GetFileName(root).EndsWith(".part", StringComparison.OrdinalIgnoreCase)) Fail();
            if (Directory.EnumerateFileSystemEntries(runtime).Any(path => path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))) Fail();
            using var workerLock = Read(WorkerPaths.Resolve(locks, "llama-cpp-omni.lock.json"));
            using var modelLock = Read(WorkerPaths.Resolve(locks, "voxcpm2.lock.json"));
            using var worker = Read(WorkerPaths.Resolve(runtime, "voxcpm2-worker-manifest.json"));
            using var generated = Read(WorkerPaths.Resolve(model, "generated-manifest.json"));
            JsonElement wl = workerLock.RootElement, ml = modelLock.RootElement, w = worker.RootElement, m = generated.RootElement;
            Schema(wl); Schema(ml); Schema(w); Schema(m);
            Equal(wl, "artifact", "llama.cpp-omni"); Equal(wl, "kind", "git"); Equal(wl, "license", "MIT"); Source(wl);
            Equal(ml, "artifact", "VoxCPM2"); Equal(ml, "kind", "huggingface"); Equal(ml, "license", "Apache-2.0"); ModelSource(ml);
            var sourceRecords = ml.GetProperty("files").EnumerateArray().ToArray();
            string[] sources = [".gitattributes", "audiovae.pth", "config.json", "model.safetensors", "README.md", "special_tokens_map.json", "tokenization_voxcpm2.py", "tokenizer_config.json", "tokenizer.json"];
            if (!sourceRecords.Select(record => record.GetProperty("path").GetString()).Order(StringComparer.Ordinal).SequenceEqual(sources.Order(StringComparer.Ordinal), StringComparer.Ordinal)) Fail();
            foreach (var record in sourceRecords)
            {
                _ = WorkerPaths.Resolve(model, record.GetProperty("path").GetString()!);
                string hash = record.GetProperty("sha256").GetString()!;
                if (record.GetProperty("length").GetInt64() <= 0 || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c))) Fail();
            }
            Source(w.GetProperty("source")); ModelSource(m.GetProperty("source")); Equal(m, "artifact", "VoxCPM2-GGUF");
            Converter(ml.GetProperty("converter")); Converter(m.GetProperty("converter"));
            var patch = w.GetProperty("source").GetProperty("patch");
            Equal(patch, "sha256", "d83350dc53c73713102c9fe9bbc5a0ab984edbeebf59926c6b45272a6c67a26a");
            Equal(patch, "patchedCliSha256", "39c5ceace9275582e5e2fdff88435d402ec9e4fc746e97b0a6cf2e2459ac731c");
            foreach (string flag in new[] { "windowsUtf8Arguments", "unicodePaths", "strictPcm16Input", "checkedWavWrites" }) if (!patch.GetProperty(flag).GetBoolean()) Fail();
            var workerFiles = Records(w.GetProperty("files"), Closure);
            Equal(ml.GetProperty("generated"), "manifest", "generated-manifest.json");
            var lockedModels = Records(ml.GetProperty("generated").GetProperty("files"), Models.Keys);
            var localModels = Records(m.GetProperty("files"), Models.Keys);
            foreach ((string name, var metadata) in Models)
            {
                Equal(lockedModels[name], "role", metadata.Role); Equal(lockedModels[name], "format", metadata.Format);
                Equal(localModels[name], "role", metadata.Role); Equal(localModels[name], "format", metadata.Format);
                if (lockedModels[name].GetProperty("length").GetInt64() != localModels[name].GetProperty("length").GetInt64() || lockedModels[name].GetProperty("sha256").GetString() != localModels[name].GetProperty("sha256").GetString()) Fail();
            }
            if (!File.Exists(WorkerPaths.Resolve(model, "READY"))) Fail();
            // Native DLL search directory has an exact executable/DLL closure; extra binaries are forbidden.
            string bin = WorkerPaths.Resolve(runtime, "bin");
            foreach (string path in Directory.EnumerateFiles(bin))
                if ((Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)) && !workerFiles.ContainsKey(Path.GetFileName(path))) Fail();
            foreach (string path in Directory.EnumerateFileSystemEntries(model))
                if (!Models.ContainsKey(Path.GetFileName(path)) && Path.GetFileName(path) is not ("READY" or "generated-manifest.json")) Fail();
            foreach ((string name, JsonElement record) in workerFiles) await Hold(bin, name, record, held, token);
            string baseName = "VoxCPM2-BaseLM-Q8_0.gguf", acousticName = "VoxCPM2-Acoustic-F16.gguf";
            await Hold(model, baseName, lockedModels[baseName], held, token); await Hold(model, acousticName, lockedModels[acousticName], held, token);
            return new(WorkerPaths.Resolve(bin, "voxcpm2-cli.exe"), WorkerPaths.Resolve(model, baseName), WorkerPaths.Resolve(model, acousticName), profileId == "q8-f16-cpu", profileId == "q8-f16-cpu" ? 0 : 16, held);
        }
        catch (OperationCanceledException) { foreach (var file in held) file.Dispose(); throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or WorkerException or FormatException or ArgumentException or NullReferenceException)
        { foreach (var file in held) file.Dispose(); throw new WorkerException("worker.artifacts"); }
    }
    private static JsonDocument Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 1_048_576) Fail();
        return JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 32 });
    }
    private static void Schema(JsonElement value) { if (value.GetProperty("schemaVersion").GetInt32() != 1) Fail(); }
    private static void Equal(JsonElement value, string key, string expected) { if (value.GetProperty(key).GetString() != expected) Fail(); }
    private static void Source(JsonElement value) { Equal(value, "repository", Repository); Equal(value, "tag", "b267"); Equal(value, "commit", WorkerVersion); }
    private static void ModelSource(JsonElement value) { Equal(value, "repository", "openbmb/VoxCPM2"); Equal(value, "revision", ModelVersion); }
    private static void Converter(JsonElement value) { Source(value); Equal(value, "script", "tools/omni/voxcpm2/convert_voxcpm2_to_gguf.py"); }
    private static Dictionary<string, JsonElement> Records(JsonElement records, IEnumerable<string> expected)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.EnumerateArray())
        {
            string name = record.GetProperty("name").GetString()!;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) Fail();
            _ = WorkerPaths.Resolve(Path.GetTempPath(), name);
            if (!result.TryAdd(name, record) || record.GetProperty("length").GetInt64() <= 0) Fail();
            string hash = record.GetProperty("sha256").GetString()!;
            if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c))) Fail();
        }
        if (!result.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)) Fail();
        return result;
    }
    private static async Task Hold(string root, string name, JsonElement record, List<FileStream> held, CancellationToken token)
    {
        var file = new FileStream(WorkerPaths.Resolve(root, name), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan); held.Add(file);
        if (file.Length != record.GetProperty("length").GetInt64()) Fail();
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant();
        if (hash != record.GetProperty("sha256").GetString()) Fail(); file.Position = 0;
    }
    private static void Fail() => throw new WorkerException("worker.artifacts");
}

public sealed class ResolvedArtifacts : IDisposable
{
    private readonly List<FileStream> held;
    public string Executable { get; } public string BaseModel { get; } public string AcousticModel { get; }
    public bool UseCpu { get; } public int GpuLayers { get; } public bool Experimental => true;
    internal ResolvedArtifacts(string executable, string baseModel, string acousticModel, bool cpu, int layers, List<FileStream> held)
    { Executable = executable; BaseModel = baseModel; AcousticModel = acousticModel; UseCpu = cpu; GpuLayers = layers; this.held = held; }
    public void Dispose() { foreach (var file in held) file.Dispose(); WorkerLifetimeTrace.Report("artifacts_disposed"); }
}

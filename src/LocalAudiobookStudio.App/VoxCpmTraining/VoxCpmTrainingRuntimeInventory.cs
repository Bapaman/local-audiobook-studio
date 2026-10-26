using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using static LocalAudiobookStudio.App.VoxCpmTraining.VoxCpmTrainingJson;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

/// <summary>Static filesystem admission only. Never starts Python or discovers dependencies.</summary>
internal sealed class VoxCpmTrainingRuntimeInventory : IVoxCpmTrainingRuntimeInventory
{
    internal const string TrainingLockSha256 = "16946f34805f17a42ebd888f6a69eee1ee3d75cbba6f3f35800b6e538cf2398c";
    internal static IReadOnlyDictionary<string, VoxCpmTrainingAssetFile> Scripts { get; } =
        new ReadOnlyDictionary<string, VoxCpmTrainingAssetFile>(new Dictionary<string, VoxCpmTrainingAssetFile>(StringComparer.Ordinal)
    {
        ["checkpoint.py"] = new("checkpoint.py", 23497, "ecc70ec38c1de3d3edff2fb6bbebc04bff94f08b84c4c10e9e1f1d21c1904e85"),
        ["frozen_layout.py"] = new("frozen_layout.py", 3248, "5a3c555ffd60632c22473b5edffe335670854899639be5d75a06fb3874c0ae98"),
        ["mapped_model.py"] = new("mapped_model.py", 11448, "8a1eec7fb502ace608f91afc67b72acc906c8ddd6dd723f1a8f4be41f2c3b5f9"),
        ["owned_files.py"] = new("owned_files.py", 4771, "3d421bf379db5d8a0fa1552394cec468b5154b4d1cf0678a4bcba193eb6e4cf5"),
        ["protocol.py"] = new("protocol.py", 13283, "ba4d3c12ed4ed4fc62b6a66c16fbc83fb77e7c7ac90fd0c72922c2379b9e8dac"),
        ["runtime.py"] = new("runtime.py", 2129, "162763784296ea0b56b233fdec4daf04a0ac618c6bb818052582a18213780a4a"),
        ["trainer.py"] = new("trainer.py", 9590, "a0e8e2edc1577c5341bb3abf721c909a9e332af03ffedb1fc0277e33dcb0d77e"),
        ["worker.py"] = new("worker.py", 5981, "e863ffbe77aa7e9e50b6f22ee1a0ced66516142a48c57ea689d7c2319b13a439")
    });

    public async Task<VoxCpmTrainingRuntimeLease> OpenAsync(VoxCpmTrainingRuntimeOptions options, CancellationToken token)
    {
        var pins = new VoxCpmTrainingFilePins();
        try
        {
            Require(options.DescriptorPath != null && IsHash(options.DescriptorSha256 ?? ""));
            _ = await pins.ReadAsync(options.DescriptorPath!, 32768, options.DescriptorSha256, token);
            var lockFile = await pins.ReadAsync(options.TrainingLockPath, 262144, TrainingLockSha256, token);
            byte[] lockBytes = await Bytes(lockFile, token);
            using var document = Parse(lockBytes); var training = document.RootElement;
            Fields(training, "format", "license", "sourceCommit", "baseRevision", "sourceFiles", "modelFiles", "recipe", "recipeSha256",
                "adapterLayout", "adapterLayoutSha256", "workerFiles", "cache");
            Integer(training.GetProperty("format"), 1, 1);
            string sourceCommit = Text(training.GetProperty("sourceCommit")), baseRevision = Text(training.GetProperty("baseRevision"));
            Require(sourceCommit == "cce58a5b59303c9bd63d12f23afe6a49a4a80c59" && baseRevision == "32279effe8c19989596f05d353d1447f51d9e915");
            var sourceFiles = AssetFiles(training.GetProperty("sourceFiles"), 38);
            var modelFiles = AssetFiles(training.GetProperty("modelFiles"), 7);
            var workerFiles = AssetFiles(training.GetProperty("workerFiles"), 8);
            Require(workerFiles.SequenceEqual(Scripts.Values.OrderBy(f => f.Filename, StringComparer.Ordinal)));
            string recipeSha = Hash(training.GetProperty("recipeSha256")), adapterSha = Hash(training.GetProperty("adapterLayoutSha256"));
            Require(Digest(Canonical(training.GetProperty("recipe"))) == recipeSha && Digest(Canonical(training.GetProperty("adapterLayout"))) == adapterSha);
            var layout = Layout(training.GetProperty("adapterLayout"));
            Require(layout.Count == 384 && layout.Sum(e => (long)e.Shape[0] * e.Shape[1]) == 1130496);
            var cache = training.GetProperty("cache"); Fields(cache, "bytes", "sha256", "layoutSha256");
            long cacheBytes = Integer(cache.GetProperty("bytes"), 9536873992, 9536873992);
            string cacheSha = Hash(cache.GetProperty("sha256")), cacheLayoutSha = Hash(cache.GetProperty("layoutSha256"));
            foreach (string root in new[] { options.PythonBaseRoot, options.WorkerRoot, options.SourceRoot }.Concat(options.DependencySites))
                Require(!Within(options.CachePath, root) && !Within(options.CachePath + ".voxcpm-training.lock", root));
            // The cache itself is not an immutable importable and must permit a
            // writable mapping. Exclusive lease/full hash belongs to each window.
            Require(pins.SharedRead(options.CachePath).Length == cacheBytes);
            foreach (var file in sourceFiles) await PinAsset(pins, options.SourceRoot, file, token);
            var license = training.GetProperty("license"); Fields(license, "filename", "bytes", "sha256");
            var licenseFile = new VoxCpmTrainingAssetFile(Text(license.GetProperty("filename")), Integer(license.GetProperty("bytes"), 1, 65536), Hash(license.GetProperty("sha256")));
            Require(licenseFile.Filename == "LICENSE"); await PinAsset(pins, options.SourceRoot, licenseFile, token);
            var sourceExpected = sourceFiles.Select(f => VoxCpmTrainingFilePins.Resolve(options.SourceRoot, f.Filename)).Append(VoxCpmTrainingFilePins.Resolve(options.SourceRoot, "LICENSE")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Require(sourceExpected.SetEquals(Enumerate(pins, options.SourceRoot, rejectBytecode: true)));
            foreach (var file in modelFiles) await PinAsset(pins, options.ModelRoot, file, token);
            var modelExpected = modelFiles.Select(f => f.Filename).ToHashSet(StringComparer.Ordinal);
            // These known download receipts/docs are never imported or used as
            // model authority. All importable/weight files must be one of seven.
            var inertModelMetadata = new HashSet<string>([".gitattributes", "README.md", "artifact-lock.json", "READY"], StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFileSystemEntries(options.ModelRoot))
            {
                string name = Path.GetFileName(path);
                Require(modelExpected.Contains(name) || inertModelMetadata.Contains(name));
                if (!modelExpected.Contains(name)) _ = await pins.ReadAsync(path, 1048576, null, token);
            }
            var mainFile = modelFiles.Single(f => f.Filename == "model.safetensors");
            var main = await pins.ReadAsync(VoxCpmTrainingFilePins.Resolve(options.ModelRoot, mainFile.Filename), mainFile.Bytes, null, token);
            var actualLayout = await ReadAdapterLayoutAsync(main, token);
            Require(Digest(Canonical(JsonSerializer.SerializeToElement(actualLayout, Options))) == adapterSha);

            var inventory = await pins.ReadAsync(options.RuntimeInventoryPath, 67108864, null, token);
            byte[] inventoryBytes = await Bytes(inventory, token);
            using var runtime = Parse(inventoryBytes);
            await VerifyClosedRuntimeAsync(options, pins, runtime.RootElement, token);
            // Re-enumerate source/model after all hashes, not only import roots.
            Require(sourceExpected.SetEquals(Enumerate(pins, options.SourceRoot, rejectBytecode: true)));
            foreach (string path in Directory.EnumerateFileSystemEntries(options.ModelRoot))
                Require(modelExpected.Contains(Path.GetFileName(path)) || inertModelMetadata.Contains(Path.GetFileName(path)));
            string inventorySha = Digest(inventoryBytes), loaderSha = LoaderSha256();
            string fingerprint = Fingerprint(options.DescriptorSha256!, inventorySha, TrainingLockSha256, recipeSha, loaderSha, adapterSha);
            var identity = new VoxCpmTrainingIdentity(fingerprint, sourceCommit, baseRevision,
                Digest(Canonical(training.GetProperty("sourceFiles"))), Digest(Canonical(training.GetProperty("modelFiles"))),
                cacheSha, cacheLayoutSha, loaderSha, recipeSha, adapterSha);
            return new(identity, modelFiles, sourceFiles, layout, inventorySha, pins, cacheBytes);
        }
        catch { pins.Dispose(); throw; }
    }

    private static bool Within(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static async Task PinAsset(VoxCpmTrainingFilePins pins, string root, VoxCpmTrainingAssetFile file, CancellationToken token)
    {
        var input = await pins.ReadAsync(VoxCpmTrainingFilePins.Resolve(root, file.Filename), file.Bytes, file.Sha256, token);
        Require(input.Length == file.Bytes);
    }

    internal static IReadOnlyList<VoxCpmTrainingAssetFile> AssetFiles(JsonElement value, int count)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<VoxCpmTrainingAssetFile>();
        foreach (var field in value.EnumerateObject())
        {
            Require(names.Add(field.Name)); Fields(field.Value, "bytes", "sha256");
            result.Add(new(field.Name, Integer(field.Value.GetProperty("bytes"), 0, 17179869184), Hash(field.Value.GetProperty("sha256"))));
        }
        Require(result.Count == count);
        return result.OrderBy(f => f.Filename, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<VoxCpmTrainingAdapterLayout> Layout(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is > 0 and <= 384);
        var result = new List<VoxCpmTrainingAdapterLayout>();
        foreach (var item in value.EnumerateArray())
        {
            Fields(item, "name", "shape", "dtype");
            string name = Text(item.GetProperty("name")); Require(name.Length <= 256 && name.All(char.IsAscii) && Text(item.GetProperty("dtype")) == "F32");
            int[] shape = item.GetProperty("shape").EnumerateArray().Select(v => checked((int)Integer(v, 1, 32768))).ToArray();
            Require(shape.Length == 2 && (name.EndsWith(".lora_A", StringComparison.Ordinal) ? shape[0] == 2 : name.EndsWith(".lora_B", StringComparison.Ordinal) && shape[1] == 2));
            result.Add(new(name, shape, "F32"));
        }
        Require(result.Select(e => e.Name).SequenceEqual(result.Select(e => e.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        return result;
    }

    internal static async Task<IReadOnlyList<VoxCpmTrainingAdapterLayout>> ReadAdapterLayoutAsync(FileStream input, CancellationToken token)
    {
        input.Position = 0; byte[] prefix = new byte[8]; await input.ReadExactlyAsync(prefix, token);
        ulong size = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(prefix);
        Require(size is >= 2 and <= 1048576 && (long)size + 8 < input.Length);
        byte[] bytes = new byte[(int)size]; await input.ReadExactlyAsync(bytes, token);
        using var document = Parse(bytes); var intervals = new List<(long Start, long End)>();
        var layout = new List<VoxCpmTrainingAdapterLayout>();
        Require(document.RootElement.ValueKind == JsonValueKind.Object);
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (field.Name == "__metadata__")
            {
                Require(field.Value.ValueKind == JsonValueKind.Object && field.Value.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.String)); continue;
            }
            Require(intervals.Count < 16384); Fields(field.Value, "dtype", "shape", "data_offsets");
            string dtype = Text(field.Value.GetProperty("dtype")); Require(dtype is "BF16" or "F32");
            long[] shape = field.Value.GetProperty("shape").EnumerateArray().Select(v => Integer(v, 1, input.Length)).ToArray(); Require(shape.Length <= 8);
            long count = 1; foreach (long dimension in shape) count = checked(count * dimension);
            long[] offsets = field.Value.GetProperty("data_offsets").EnumerateArray().Select(v => Integer(v, 0, input.Length)).ToArray();
            Require(offsets.Length == 2 && offsets[1] > offsets[0] && offsets[1] - offsets[0] == checked(count * (dtype == "BF16" ? 2 : 4)));
            intervals.Add((offsets[0], offsets[1]));
            string[] parts = field.Name.Split('.');
            if (parts.Length >= 3 && (field.Name.StartsWith("base_lm.", StringComparison.Ordinal) || field.Name.StartsWith("residual_lm.", StringComparison.Ordinal)
                || field.Name.StartsWith("feat_decoder.estimator.", StringComparison.Ordinal)) && parts[^1] == "weight" && parts[^2] is "q_proj" or "k_proj" or "v_proj" or "o_proj")
            {
                Require(shape.Length == 2 && shape.All(v => v <= 32768));
                string stem = field.Name[..^6];
                layout.Add(new(stem + "lora_A", [2, (int)shape[1]], "F32")); layout.Add(new(stem + "lora_B", [(int)shape[0], 2], "F32"));
            }
        }
        long cursor = 0;
        foreach (var interval in intervals.OrderBy(v => v.Start)) { Require(interval.Start == cursor); cursor = interval.End; }
        Require(intervals.Count > 0 && cursor == input.Length - 8 - (long)size && layout.Count > 0);
        input.Position = 0; return layout.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
    }

    internal static async Task VerifyClosedRuntimeAsync(VoxCpmTrainingRuntimeOptions options, VoxCpmTrainingFilePins pins, JsonElement inventory, CancellationToken token)
    {
        Fields(inventory, "schemaVersion", "files"); Integer(inventory.GetProperty("schemaVersion"), 1, 1);
        var entries = inventory.GetProperty("files"); Require(entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() is > 0 and <= 100000);
        var files = new Dictionary<string, (long Bytes, string Sha)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateArray())
        {
            Fields(entry, "path", "bytes", "sha256");
            string path = VoxCpmTrainingFilePins.Absolute(Text(entry.GetProperty("path")));
            Require(files.TryAdd(path, (Integer(entry.GetProperty("bytes"), 0, 17179869184), Hash(entry.GetProperty("sha256")))));
        }
        HashSet<string> Required()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in new[] { options.PythonBaseRoot, options.WorkerRoot }.Concat(options.DependencySites))
                foreach (string file in Enumerate(pins, directory)) set.Add(file);
            string python = VoxCpmTrainingFilePins.Absolute(options.PythonPath), interpreter = Path.GetDirectoryName(python)!;
            pins.PinDirectory(interpreter); set.Add(python);
            foreach (string file in Directory.EnumerateFiles(interpreter)) set.Add(VoxCpmTrainingFilePins.Absolute(file));
            if (Path.GetFileName(interpreter).Equals("Scripts", StringComparison.OrdinalIgnoreCase)) set.Add(Path.Combine(Path.GetDirectoryName(interpreter)!, "pyvenv.cfg"));
            else Require(Within(python, options.PythonBaseRoot));
            return set;
        }
        var required = Required(); Require(required.SetEquals(files.Keys));
        VerifyWorkerDirectory(options.WorkerRoot);
        foreach (var script in Scripts.Values)
        {
            string path = VoxCpmTrainingFilePins.Resolve(options.WorkerRoot, script.Filename);
            Require(files.TryGetValue(path, out var declared) && declared.Bytes == script.Bytes && declared.Sha == script.Sha256);
        }
        foreach (var file in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var stream = await pins.ReadAsync(file.Key, file.Value.Bytes, file.Value.Sha, token); Require(stream.Length == file.Value.Bytes);
        }
        string interpreterDir = Path.GetDirectoryName(options.PythonPath)!;
        if (Path.GetFileName(interpreterDir).Equals("Scripts", StringComparison.OrdinalIgnoreCase))
        {
            string cfg = Path.Combine(Path.GetDirectoryName(interpreterDir)!, "pyvenv.cfg");
            var stream = await pins.ReadAsync(cfg, 32768, files[cfg].Sha, token);
            string text = new UTF8Encoding(false, true).GetString(await Bytes(stream, token));
            var homes = text.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("home", StringComparison.OrdinalIgnoreCase)).ToArray();
            Require(homes.Length == 1 && string.Equals(VoxCpmTrainingFilePins.Absolute(homes[0][1].Trim()), options.PythonBaseRoot, StringComparison.OrdinalIgnoreCase));
        }
        foreach (string module in new[] { "numpy", "scipy", "torch", "transformers", "safetensors", "pydantic", "einops" })
            Require(options.DependencySites.Any(site => files.ContainsKey(Path.Combine(site, module, "__init__.py"))));
        Require(required.SetEquals(Required())); // Detect additions before START.
    }

    internal static void VerifyWorkerDirectory(string root)
    {
        using var pins = new VoxCpmTrainingFilePins(); pins.PinDirectory(root);
        string[] paths = Directory.EnumerateFileSystemEntries(root).ToArray();
        Require(paths.Length == 8 && paths.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal).SetEquals(Scripts.Keys));
        foreach (var script in Scripts.Values)
        {
            var input = pins.ReadAsync(VoxCpmTrainingFilePins.Resolve(root, script.Filename), script.Bytes, script.Sha256, CancellationToken.None).GetAwaiter().GetResult();
            Require(input.Length == script.Bytes);
        }
    }

    private static IEnumerable<string> Enumerate(VoxCpmTrainingFilePins pins, string directory, bool rejectBytecode = false)
    {
        var pending = new Stack<string>(); pending.Push(VoxCpmTrainingFilePins.Absolute(directory)); int count = 0;
        while (pending.TryPop(out string? current))
        {
            pins.PinDirectory(current);
            foreach (string path in Directory.EnumerateFileSystemEntries(current))
            {
                Require(++count <= 150000);
                string canonical = VoxCpmTrainingFilePins.Absolute(path);
                Require(!rejectBytecode || !Path.GetFileName(canonical).Equals("__pycache__", StringComparison.OrdinalIgnoreCase));
                if (Directory.Exists(canonical)) pending.Push(canonical); else yield return canonical;
            }
        }
    }

    internal static string LoaderSha256()
    {
        var text = new StringBuilder("voxcpm2-training-loader-v1\n");
        foreach (var file in Scripts.Values.OrderBy(f => f.Filename, StringComparer.Ordinal))
            text.Append(file.Filename).Append('|').Append(file.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(file.Sha256).Append('\n');
        return Digest(Encoding.ASCII.GetBytes(text.ToString()));
    }
    internal static string Fingerprint(params string[] hashes)
    {
        Require(hashes.Length == 6 && hashes.All(IsHash));
        return Digest(Encoding.ASCII.GetBytes("voxcpm2-training-runtime-v1\n" + string.Join('\n', hashes) + "\n"));
    }
    private static string Digest(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}

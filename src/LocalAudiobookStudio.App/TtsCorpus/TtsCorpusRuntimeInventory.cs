using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.TtsCorpus;
using static LocalAudiobookStudio.App.TtsCorpus.TtsCorpusJson;

namespace LocalAudiobookStudio.App.TtsCorpus;

/// <summary>Entirely inert admission: no import, process, discovery, install, cache lookup or download.</summary>
internal sealed class TtsCorpusRuntimeInventory : ITtsCorpusRuntimeInventory
{
    internal const string ModelLockSha256 = "cafb95ad06a33d88876e0c5c894ec42740b4063d1b56944c6467a0ea5169762e";
    internal static IReadOnlyDictionary<string, string> Scripts { get; } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["worker.py"] = "ad46c3a7a689bad83e92198701d2c287b4a4d4a9f4a26d3f25992f979798be8c",
        ["protocol.py"] = "1d1b6988a3ccc8f800c71a5d09a5b87d6d75ffcb646b4d85a31ed81d5d0110ee",
        ["prepare.py"] = "6ad20f43a6811ad44d9a5680da554db0be440b7f35951c4dcab5fc6c29d6026e",
        ["asr.py"] = "661bd7d0561dad1684359c066cba3ce07eb3b4a8c56ab8b677a6383a057877dc"
    });

    internal static IReadOnlyList<TtsCorpusModelFile> ValidateModelLock(byte[] bytes)
    {
        Require(bytes.Length is > 0 and <= 65536 && Convert.ToHexStringLower(SHA256.HashData(bytes)) == ModelLockSha256);
        using var modelJson = Parse(bytes);
        var modelFiles = modelJson.RootElement.GetProperty("files").EnumerateArray().Select(f =>
        {
            Fields(f, "filename", "bytes", "sha256");
            return new TtsCorpusModelFile(Text(f.GetProperty("filename")), Integer(f.GetProperty("bytes"), 1, 4294967296), Hash(f.GetProperty("sha256")));
        }).ToArray();
        Require(modelFiles.Length == 11); return modelFiles;
    }

    public async Task<TtsCorpusRuntimeLease> OpenAsync(TtsCorpusRuntimeOptions options, CancellationToken token)
    {
        var pins = new TtsCorpusFilePins();
        try
        {
            Require(options.DescriptorPath != null && IsHash(options.DescriptorSha256 ?? ""));
            _ = await pins.ReadAsync(options.DescriptorPath!, 32768, options.DescriptorSha256, token);
            var manifest = await pins.ReadAsync(options.ModelManifestPath, 65536, ModelLockSha256, token);
            var modelFiles = ValidateModelLock(await Bytes(manifest, token));
            foreach (var item in modelFiles)
            {
                var file = await pins.ReadAsync(TtsCorpusFilePins.Resolve(options.ModelRoot, item.Filename), item.Bytes, item.Sha256, token);
                Require(file.Length == item.Bytes);
            }

            var inventory = await pins.ReadAsync(options.RuntimeInventoryPath, 67108864, null, token);
            byte[] inventoryBytes = await Bytes(inventory, token);
            using var document = Parse(inventoryBytes); var root = document.RootElement;
            Fields(root, "schemaVersion", "files"); Require(Integer(root.GetProperty("schemaVersion"), 1, 1) == 1);
            var entries = root.GetProperty("files"); Require(entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() is > 0 and <= 100000);
            var files = new Dictionary<string, (long Bytes, string Sha)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.EnumerateArray())
            {
                Fields(entry, "path", "bytes", "sha256");
                string path = TtsCorpusFilePins.Absolute(Text(entry.GetProperty("path")));
                Require(files.TryAdd(path, (Integer(entry.GetProperty("bytes"), 0, 17179869184), Hash(entry.GetProperty("sha256")))));
            }

            // A closed inventory covers every file reachable from these configured roots,
            // including bytecode, native libraries and package metadata. No glob admission.
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in new[] { options.PythonBaseRoot }.Concat(options.DependencySites))
                foreach (string file in Enumerate(pins, directory)) Require(required.Add(file) || files.ContainsKey(file));
            string python = TtsCorpusFilePins.Absolute(options.PythonPath); required.Add(python);
            string interpreterDirectory = Path.GetDirectoryName(python)!;
            foreach (string file in Directory.EnumerateFiles(interpreterDirectory)) required.Add(TtsCorpusFilePins.Absolute(file));
            pins.PinDirectory(interpreterDirectory);
            string? venv = Path.GetDirectoryName(interpreterDirectory);
            if (Path.GetFileName(interpreterDirectory).Equals("Scripts", StringComparison.OrdinalIgnoreCase))
            {
                Require(venv != null); string cfg = Path.Combine(venv!, "pyvenv.cfg"); required.Add(cfg);
                var config = await pins.ReadAsync(cfg, 32768, files.TryGetValue(cfg, out var cfgPin) ? cfgPin.Sha : throw Invalid(), token);
                string cfgText = Encoding.UTF8.GetString(await Bytes(config, token));
                var homes = cfgText.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("home", StringComparison.OrdinalIgnoreCase)).ToArray();
                Require(homes.Length == 1 && string.Equals(TtsCorpusFilePins.Absolute(homes[0][1].Trim()), TtsCorpusFilePins.Absolute(options.PythonBaseRoot), StringComparison.OrdinalIgnoreCase));
            }
            else Require(python.StartsWith(TtsCorpusFilePins.Absolute(options.PythonBaseRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

            string[] workerFiles = Enumerate(pins, options.WorkerRoot).ToArray();
            Require(workerFiles.Length == 4);
            foreach (var script in Scripts)
            {
                string path = TtsCorpusFilePins.Resolve(options.WorkerRoot, script.Key);
                Require(workerFiles.Contains(path, StringComparer.OrdinalIgnoreCase)); required.Add(path);
                Require(files.TryGetValue(path, out var scriptPin) && scriptPin.Sha == script.Value);
            }
            Require(required.SetEquals(files.Keys));
            foreach (var file in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var input = await pins.ReadAsync(file.Key, file.Value.Bytes, file.Value.Sha, token); Require(input.Length == file.Value.Bytes);
            }
            foreach (string module in new[] { "numpy", "scipy", "torch", "transformers", "safetensors" })
                Require(options.DependencySites.Any(site => files.ContainsKey(Path.Combine(site, module, "__init__.py"))));
            Require(options.DependencySites.Any(site => files.ContainsKey(Path.Combine(site, "soundfile.py"))));
            // Re-enumeration catches newly inserted importables before the gate opens.
            var after = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in new[] { options.PythonBaseRoot, options.WorkerRoot }.Concat(options.DependencySites))
                foreach (string file in Enumerate(pins, directory)) after.Add(file);
            Require(after.All(required.Contains));
            var fingerprint = new StringBuilder("tts-corpus-v1|-I|-S|-B|-u|cap13958643712|reserve3221225472|deadline300|cpu2|threads2/1\n");
            fingerprint.Append(JsonSerializer.Serialize(options, Options)).Append('\n').Append(ModelLockSha256).Append('\n');
            foreach (var entry in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                fingerprint.Append(entry.Key.ToUpperInvariant()).Append('|').Append(entry.Value.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(entry.Value.Sha).Append('\n');
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())));
            return new(hash, modelFiles, pins);
        }
        catch { pins.Dispose(); throw; }
    }

    private static IEnumerable<string> Enumerate(TtsCorpusFilePins pins, string directory)
    {
        var pending = new Stack<string>(); pending.Push(TtsCorpusFilePins.Absolute(directory)); int count = 0;
        while (pending.TryPop(out string? current))
        {
            pins.PinDirectory(current);
            foreach (string path in Directory.EnumerateFileSystemEntries(current))
            {
                Require(++count <= 150000); string canonical = TtsCorpusFilePins.Absolute(path);
                if (Directory.Exists(canonical)) pending.Push(canonical); else yield return canonical;
            }
        }
    }
    private static async Task<byte[]> Bytes(FileStream stream, CancellationToken token)
    { byte[] bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token); stream.Position = 0; return bytes; }
}

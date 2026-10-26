using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.App.Hosting;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.App.TtsCorpus;

/// <summary>Trusted startup configuration only. Parsing neither starts Python nor discovers a runtime.</summary>
public sealed record TtsCorpusRuntimeOptions(string PythonPath, string PythonBaseRoot, IReadOnlyList<string> DependencySites,
    string WorkerRoot, string ModelRoot, string ModelManifestPath, string RuntimeInventoryPath)
{
    internal string? DescriptorPath { get; init; }
    internal string? DescriptorSha256 { get; init; }

    internal static TtsCorpusRuntimeOptions Read(string path)
    {
        try
        {
            path = LocalAppHostDependencies.CanonicalLocalRoot(path);
            _ = WorkerPaths.Root(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > 32768) throw new ArgumentException("Invalid corpus configuration.");
            byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes);
            string[] names = ["pythonPath", "pythonBaseRoot", "dependencySites", "workerRoot", "modelRoot", "modelManifestPath", "runtimeInventoryPath"];
            var fields = document.RootElement.EnumerateObject().ToArray();
            if (fields.Length != names.Length || fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != names.Length
                || fields.Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) throw new ArgumentException("Invalid corpus configuration.");
            string Value(string name)
            {
                var value = document.RootElement.GetProperty(name);
                if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Invalid corpus configuration.");
                return LocalAppHostDependencies.CanonicalLocalRoot(value.GetString()!);
            }
            var sites = document.RootElement.GetProperty("dependencySites").EnumerateArray().Select(p =>
                p.ValueKind == JsonValueKind.String ? LocalAppHostDependencies.CanonicalLocalRoot(p.GetString()!) : throw new ArgumentException("Invalid corpus configuration.")).ToArray();
            if (sites.Length is < 1 or > 2 || sites.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sites.Length)
                throw new ArgumentException("Invalid corpus configuration.");
            return new(Value("pythonPath"), Value("pythonBaseRoot"), sites, Value("workerRoot"), Value("modelRoot"), Value("modelManifestPath"), Value("runtimeInventoryPath"))
            { DescriptorPath = path, DescriptorSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or LocalAudiobookStudio.WorkerHost.Pipe.WorkerException)
        { throw new ArgumentException("Invalid corpus configuration."); }
    }
}

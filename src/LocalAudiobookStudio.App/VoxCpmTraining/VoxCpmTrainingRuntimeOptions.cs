using System.Security.Cryptography;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using static LocalAudiobookStudio.App.VoxCpmTraining.VoxCpmTrainingJson;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

public sealed record VoxCpmTrainingRuntimeOptions(string PythonPath, string PythonBaseRoot, IReadOnlyList<string> DependencySites,
    string WorkerRoot, string SourceRoot, string ModelRoot, string TrainingLockPath, string RuntimeInventoryPath, string CachePath)
{
    internal string? DescriptorPath { get; init; }
    internal string? DescriptorSha256 { get; init; }
    internal static VoxCpmTrainingRuntimeOptions Read(string path)
    {
        try
        {
            path = VoxCpmTrainingFilePins.Absolute(path);
            using var pins = new VoxCpmTrainingFilePins();
            var input = pins.ReadAsync(path, 32768, null, CancellationToken.None).GetAwaiter().GetResult();
            byte[] bytes = Bytes(input, CancellationToken.None).GetAwaiter().GetResult();
            using var doc = Parse(bytes); var root = doc.RootElement;
            Fields(root, "pythonPath", "pythonBaseRoot", "dependencySites", "workerRoot", "sourceRoot", "modelRoot", "trainingLockPath", "runtimeInventoryPath", "cachePath");
            string Value(string name) => VoxCpmTrainingFilePins.Absolute(Text(root.GetProperty(name)));
            var sites = root.GetProperty("dependencySites").EnumerateArray().Select(p => VoxCpmTrainingFilePins.Absolute(Text(p))).ToArray();
            Require(sites.Length is >= 1 and <= 2 && sites.Distinct(StringComparer.OrdinalIgnoreCase).Count() == sites.Length);
            return new(Value("pythonPath"), Value("pythonBaseRoot"), sites, Value("workerRoot"), Value("sourceRoot"), Value("modelRoot"), Value("trainingLockPath"), Value("runtimeInventoryPath"), Value("cachePath"))
            { DescriptorPath = path, DescriptorSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new ArgumentException("Invalid VoxCPM training configuration."); }
    }
}

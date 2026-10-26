using LocalAudiobookStudio.App.VoxCpmTraining;
using static LocalAudiobookStudio.App.VoxCpmTraining.VoxCpmTrainingJson;

namespace LocalAudiobookStudio.App.VoxCpmInference;

internal static class VoxCpmInferenceScripts
{
    internal const string ImplementationSha256 = "bcd7312efdfdf470d8f3676d2d7606c618da3464d0a3271f48810135cf7b5928";
    private static readonly VoxCpmTrainingAssetFile[] Files = [
        new("inference_contract.py",8197,"ccd6e6d1181384bb1e3957f9de642cc908eb948acce6fa3635cda02320624066"),
        new("mapped_backend.py",7776,"e9dc87cce1b0699e93cb257c62af926bd7153a272b0e55855aa76704ddccb5ee"),
        new("synthesis.py",7009,"cdc2b2d1ddaf7c7828946a712f8f9ff6f304f73aeb8a52c64c5bcde8201c0569"),
        new("worker.py",19523,"31d380096744270e8301157e8d838db4e460a7cc5aaeae52cda5502772498a8e") ];
    internal static async Task PinAsync(VoxCpmTrainingFilePins pins, string root, string lockPath, CancellationToken token)
    {
        pins.PinDirectory(root);
        _ = await pins.ReadAsync(lockPath,4096,ImplementationSha256,token);
        void Closed() => Require(Files.Select(f=>f.Filename).ToHashSet(StringComparer.Ordinal)
            .SetEquals(Directory.EnumerateFileSystemEntries(root).Select(p=>Path.GetFileName(p)!)));
        Closed();
        foreach (var file in Files)
        {
            var input = await pins.ReadAsync(Path.Combine(root,file.Filename),file.Bytes,file.Sha256,token);
            Require(input.Length == file.Bytes);
        }
        Closed();
    }
}

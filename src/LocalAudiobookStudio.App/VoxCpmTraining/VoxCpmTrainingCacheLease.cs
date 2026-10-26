using LocalAudiobookStudio.Application.VoxCpmTraining;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

internal sealed class VoxCpmTrainingCacheLease(VoxCpmTrainingFilePins pins, FileStream cache, long bytes, string sha) : IDisposable
{
    internal static async Task<VoxCpmTrainingCacheLease> AcquireAsync(string cachePath, long bytes, string sha256, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pins = new VoxCpmTrainingFilePins();
        try
        {
            cachePath = VoxCpmTrainingFilePins.Absolute(cachePath);
            var marker = pins.ExclusiveMarker(cachePath + ".voxcpm-training.lock");
            if (marker.Length != 0) throw new VoxCpmTrainingExecutionException("voxcpm_training.cache.lock_invalid");
            if (bytes <= 0 || !VoxCpmTrainingJson.IsHash(sha256)) throw new VoxCpmTrainingExecutionException("voxcpm_training.cache.invalid");
            var cache = pins.SharedRead(cachePath);
            var lease = new VoxCpmTrainingCacheLease(pins, cache, bytes, sha256);
            await lease.VerifyUnchangedAsync(token); return lease;
        }
        catch { pins.Dispose(); throw; }
    }
    internal async Task VerifyUnchangedAsync(CancellationToken token)
    {
        if (cache.Length != bytes) throw Changed();
        cache.Position = 0;
        string actual = await VoxCpmTrainingFileHash.Sha256Async(cache, token);
        if (cache.Length != bytes || actual != sha) throw Changed();
        cache.Position = 0;
    }
    private static VoxCpmTrainingExecutionException Changed() => new("voxcpm_training.cache.changed");
    public void Dispose() => pins.Dispose();
}

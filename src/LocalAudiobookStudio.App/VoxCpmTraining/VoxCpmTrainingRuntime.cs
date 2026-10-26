using LocalAudiobookStudio.Application.VoxCpmTraining;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

internal sealed record VoxCpmTrainingAssetFile(string Filename, long Bytes, string Sha256);
internal sealed record VoxCpmTrainingAdapterLayout(string Name, int[] Shape, string Dtype);
internal interface IVoxCpmTrainingRuntimeInventory
{
    Task<VoxCpmTrainingRuntimeLease> OpenAsync(VoxCpmTrainingRuntimeOptions options, CancellationToken token);
}
internal sealed class VoxCpmTrainingRuntimeLease(VoxCpmTrainingIdentity identity, IReadOnlyList<VoxCpmTrainingAssetFile> modelFiles,
    IReadOnlyList<VoxCpmTrainingAssetFile> sourceFiles, IReadOnlyList<VoxCpmTrainingAdapterLayout> adapterLayout, string runtimeInventorySha256, IDisposable pins,
    long cacheBytes = 9536873992) : IDisposable
{
    internal VoxCpmTrainingIdentity Identity { get; } = identity;
    internal IReadOnlyList<VoxCpmTrainingAssetFile> ModelFiles { get; } = modelFiles;
    internal IReadOnlyList<VoxCpmTrainingAssetFile> SourceFiles { get; } = sourceFiles;
    internal IReadOnlyList<VoxCpmTrainingAdapterLayout> AdapterLayout { get; } = adapterLayout;
    internal string RuntimeInventorySha256 { get; } = runtimeInventorySha256;
    internal long CacheBytes { get; } = cacheBytes;
    public void Dispose() => pins.Dispose();
}
internal sealed class VoxCpmTrainingRuntime(VoxCpmTrainingRuntimeOptions? options, IVoxCpmTrainingRuntimeInventory? inventory = null, TimeProvider? clock = null) : IVoxCpmTrainingRuntime
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim admission = new(1, 1), readiness = new(1, 1);
    private sealed record CachedReadiness(VoxCpmTrainingReadinessDto Value, long Timestamp);
    private CachedReadiness? cached;
    private int cacheChanged;
    internal VoxCpmTrainingRuntimeOptions Options => options ?? throw new VoxCpmTrainingExecutionException("voxcpm_training.runtime.not_ready");
    internal void MarkCacheChanged() { Interlocked.Exchange(ref cacheChanged, 1); Volatile.Write(ref cached, null); }
    internal async Task<VoxCpmTrainingRuntimeLease> AcquireAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); await admission.WaitAsync(token);
        try
        {
            if (Volatile.Read(ref cacheChanged) != 0) throw new VoxCpmTrainingExecutionException("voxcpm_training.cache.changed");
            return await (inventory ?? new VoxCpmTrainingRuntimeInventory()).OpenAsync(Options, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (VoxCpmTrainingExecutionException) { throw; }
        catch { throw new VoxCpmTrainingExecutionException("voxcpm_training.runtime.not_ready"); }
        finally { admission.Release(); }
    }
    public async Task<VoxCpmTrainingReadinessDto> GetReadinessAsync(CancellationToken token = default)
    {
        await readiness.WaitAsync(token);
        try
        {
            if (Volatile.Read(ref cacheChanged) != 0) return new(false, "voxcpm_training.cache.changed");
            var known = Volatile.Read(ref cached);
            if (known != null && time.GetElapsedTime(known.Timestamp) < TimeSpan.FromSeconds(300)) return known.Value;
            Volatile.Write(ref cached, null);
            try { using (await AcquireAsync(token)) { } return RememberReady(); }
            catch (VoxCpmTrainingExecutionException error) { return new(false, error.Code); }
        }
        finally { readiness.Release(); }
    }
    public async Task<VoxCpmTrainingIdentity> GetIdentityAsync(CancellationToken token)
    {
        VoxCpmTrainingIdentity identity;
        using (var lease = await AcquireAsync(token)) identity = lease.Identity;
        token.ThrowIfCancellationRequested();
        _ = RememberReady();
        return identity;
    }
    private VoxCpmTrainingReadinessDto RememberReady()
    {
        if (Volatile.Read(ref cacheChanged) != 0) return new(false, "voxcpm_training.cache.changed");
        var value = new VoxCpmTrainingReadinessDto(true, null);
        Volatile.Write(ref cached, new(value, time.GetTimestamp()));
        return value;
    }
}

using LocalAudiobookStudio.Application.TtsCorpus;

namespace LocalAudiobookStudio.App.TtsCorpus;

internal interface ITtsCorpusRuntimeInventory
{
    Task<TtsCorpusRuntimeLease> OpenAsync(TtsCorpusRuntimeOptions options, CancellationToken token);
}

internal sealed class TtsCorpusRuntimeLease(string fingerprint, IReadOnlyList<TtsCorpusModelFile> modelFiles, IDisposable pins) : IDisposable
{
    internal string Fingerprint { get; } = fingerprint;
    internal IReadOnlyList<TtsCorpusModelFile> ModelFiles { get; } = modelFiles;
    public void Dispose() => pins.Dispose();
}

internal sealed class TtsCorpusRuntime(TtsCorpusRuntimeOptions? options, ITtsCorpusRuntimeInventory? inventory = null, TimeProvider? clock = null) : ITtsCorpusRuntime
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim admission = new(1, 1), readiness = new(1, 1);
    private TtsCorpusReadinessDto? cachedReady;
    private long readyTimestamp;
    internal TtsCorpusRuntimeOptions Options => options ?? throw new TtsCorpusExecutionException("tts_corpus.runtime.not_ready");
    internal async Task<TtsCorpusRuntimeLease> AcquireAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await admission.WaitAsync(token);
        try { return await (inventory ?? new TtsCorpusRuntimeInventory()).OpenAsync(Options, token); }
        catch (OperationCanceledException) { throw; }
        catch (TtsCorpusExecutionException e) when (e.Code == "tts_corpus.runtime.changed") { throw; }
        catch { throw new TtsCorpusExecutionException("tts_corpus.runtime.not_ready"); }
        finally { admission.Release(); }
    }
    public async Task<TtsCorpusReadinessDto> GetReadinessAsync(CancellationToken token = default)
    {
        await readiness.WaitAsync(token);
        try
        {
            if (cachedReady != null && time.GetElapsedTime(readyTimestamp) < TimeSpan.FromSeconds(300)) return cachedReady;
            cachedReady = null;
            try
            {
                using var lease = await AcquireAsync(token);
                readyTimestamp = time.GetTimestamp();
                return cachedReady = new(true, null);
            }
            catch (TtsCorpusExecutionException e) { return new(false, e.Code); }
        }
        finally { readiness.Release(); }
    }
    public async Task<string> GetFingerprintAsync(CancellationToken token = default)
    { using var lease = await AcquireAsync(token); return lease.Fingerprint; }
}

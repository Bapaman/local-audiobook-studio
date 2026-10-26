namespace LocalAudiobookStudio.App.TtsCorpus;

internal static class TtsCorpusResourcePolicy
{
    internal const long MemoryCapBytes = 13958643712;
    internal const ulong ReserveBytes = 3221225472;
    internal static void CheckPreflight(ulong availableBytes)
    {
        if (availableBytes < checked((ulong)MemoryCapBytes + ReserveBytes))
            throw new LocalAudiobookStudio.Application.TtsCorpus.TtsCorpusExecutionException("tts_corpus.memory.insufficient");
    }
    internal static string? Violation(ulong availableBytes, TimeSpan elapsed)
        => elapsed >= TimeSpan.FromSeconds(300) ? "tts_corpus.window.deadline"
            : availableBytes < ReserveBytes ? "tts_corpus.memory.reserve" : null;
}

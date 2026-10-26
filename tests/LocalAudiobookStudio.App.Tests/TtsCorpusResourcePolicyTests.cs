using LocalAudiobookStudio.App.TtsCorpus;
using LocalAudiobookStudio.Application.TtsCorpus;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TtsCorpusResourcePolicyTests
{
    [Theory]
    [InlineData(17179869183ul, false)]
    [InlineData(17179869184ul, true)]
    public void Preflight_requires_cap_plus_reserve(ulong available, bool allowed)
    {
        if (allowed) TtsCorpusResourcePolicy.CheckPreflight(available);
        else Assert.Equal("tts_corpus.memory.insufficient", Assert.Throws<TtsCorpusExecutionException>(() => TtsCorpusResourcePolicy.CheckPreflight(available)).Code);
    }

    [Theory]
    [InlineData(3221225471ul, 1, "tts_corpus.memory.reserve")]
    [InlineData(3221225472ul, 299, null)]
    [InlineData(3221225472ul, 300, "tts_corpus.window.deadline")]
    public void Watchdog_bounds_reserve_and_deadline(ulong available, int elapsedSeconds, string? expected)
        => Assert.Equal(expected, TtsCorpusResourcePolicy.Violation(available, TimeSpan.FromSeconds(elapsedSeconds)));
}

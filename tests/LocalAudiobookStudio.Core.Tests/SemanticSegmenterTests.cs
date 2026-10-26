using System.Globalization;
using System.Diagnostics;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Core.Tests;

public class SemanticSegmenterTests
{
    private static NormalizedText Normalize(string text) => new RussianTextFrontend().Normalize(new TextImportService().ImportPastedText(text));

    [Theory]
    [InlineData(319, 1)]
    [InlineData(320, 1)]
    [InlineData(321, 2)]
    public void Hard_limit_is_320_utf16_units_with_word_safe_coverage(int length, int count)
    {
        string input = new string('я', length - 2) + " а";
        var segments = new SemanticSegmenter().Segment(Normalize(input));
        Assert.Equal(count, segments.Count);
        Assert.Equal(input, string.Join(" ", segments.Select(x => x.NormalizedTarget)));
        Assert.All(segments, x => { Assert.InRange(x.NormalizedTarget.Length, 1, 320); Assert.Equal(x.NormalizedTarget.Length / 16d, x.EstimatedDuration.TotalSeconds); });
    }

    [Fact]
    public void Overlong_token_is_a_redacted_error()
    {
        var error = Assert.Throws<TextException>(() => new SemanticSegmenter().Segment(Normalize(new string('я', 321))));
        Assert.Equal("text.token_too_long", error.Code); Assert.Equal(error.Code, error.Message);
    }

    [Fact]
    public void Seven_hundred_character_sentence_preserves_order_and_all_punctuation()
    {
        string input = string.Join(" ", Enumerable.Repeat("слово", 116)) + " ещё.";
        Assert.Equal(700, input.Length);
        var segments = new SemanticSegmenter().Segment(Normalize(input));
        Assert.True(segments.Count >= 3);
        Assert.Equal(input, string.Join(" ", segments.Select(x => x.NormalizedTarget)));
        Assert.Equal(Enumerable.Range(0, segments.Count), segments.Select(x => x.Sequence));
        Assert.All(segments, x => Assert.InRange(x.NormalizedTarget.Length, 1, 320));
    }

    [Fact]
    public void Packs_whole_sentences_greedily_and_never_merges_dialogue_paragraphs()
    {
        string sentence = new string('я', 149) + ".";
        var segments = new SemanticSegmenter().Segment(Normalize(sentence + " " + sentence + " " + sentence + "\n\n— Да!\n— Нет..."));
        Assert.Equal(new[] { sentence + " " + sentence, sentence, "— Да!", "— Нет..." }, segments.Select(x => x.NormalizedTarget));
        Assert.Equal(SegmentBoundary.Sentence, segments[0].BoundaryAfter);
        Assert.Equal(SegmentBoundary.Paragraph, segments[1].BoundaryAfter);
        Assert.Equal(SegmentBoundary.Paragraph, segments[2].BoundaryAfter);
        Assert.Equal(SegmentBoundary.DocumentEnd, segments[3].BoundaryAfter);
        Assert.Equal(sentence, segments[0].NextSentenceContext);
        Assert.Equal(sentence, segments[1].PreviousSentenceContext);
    }

    [Theory]
    [InlineData("Он жил в г. Москве на ул. Лесной.")]
    [InlineData("А. С. Пушкин писал, и т. д. и т. п. всё продолжалось.")]
    [InlineData("Цена 3.14, дата 12.10.2026.")]
    [InlineData("Он сказал: («Да?!»)")]
    [InlineData("Он молчал... Потом думал…")]
    public void Sentence_context_respects_abbreviations_initials_decimals_quotes_and_ellipses(string input)
    {
        var segments = new SemanticSegmenter().Segment(Normalize(input + "\nПосле."));
        Assert.Equal(input, segments[0].NormalizedTarget);
        Assert.Equal("После.", segments[0].NextSentenceContext);
        Assert.Equal(input.Contains("Потом", StringComparison.Ordinal) ? "Потом думал…" : input, segments[1].PreviousSentenceContext);
    }

    [Fact]
    public void Long_sentence_prefers_clause_punctuation_over_later_whitespace()
    {
        string clause = string.Join(" ", Enumerable.Repeat("слово", 35)) + ",";
        string rest = string.Join(" ", Enumerable.Repeat("далее", 35)) + ".";
        var segments = new SemanticSegmenter().Segment(Normalize(clause + " " + rest));
        Assert.Equal(clause, segments[0].NormalizedTarget);
        Assert.Equal(SegmentBoundary.Clause, segments[0].BoundaryAfter);
        Assert.Equal(rest, segments[1].NormalizedTarget);
    }

    [Fact]
    public void Supplementary_characters_and_combining_marks_stay_inside_tokens()
    {
        string first = new string('я', 316);
        var segments = new SemanticSegmenter().Segment(Normalize(first + " 🦔а́ конец."));
        Assert.Equal(new[] { first, "🦔а́ конец." }, segments.Select(x => x.NormalizedTarget));
        Assert.All(segments, x => Assert.False(char.IsLowSurrogate(x.NormalizedTarget[0])));
        Assert.Equal(first + " 🦔а́ конец.", string.Join(" ", segments.Select(x => x.NormalizedTarget)));
    }

    [Fact]
    public void Source_ranges_map_original_whitespace_and_nfc_exactly_without_changing_document()
    {
        string first = new string('я', 315) + ".";
        string original = "\t" + first + "  \r\n\r\n  Е\u0308ж\t  зна́ет.  ";
        var normalized = Normalize(original);
        var segments = new SemanticSegmenter().Segment(normalized);
        Assert.Equal("Е\u0308ж\t  зна́ет.", segments[1].OriginalText);
        Assert.Equal("Ёж зна́ет.", segments[1].NormalizedTarget);
        Assert.Equal(original, normalized.OriginalText);
        Assert.All(segments, x =>
        {
            Assert.NotNull(x.OriginalRange);
            Assert.Equal(x.OriginalText, original.Substring(x.OriginalRange!.Start, x.OriginalRange.Length));
            Assert.Equal(x.NormalizedTarget, normalized.Normalized.Substring(x.NormalizedRange.Start, x.NormalizedRange.Length));
        });
    }

    [Fact]
    public void Stable_hashes_change_for_each_input_context_version_and_boundary_and_ignore_culture()
    {
        var segments = new SemanticSegmenter().Segment(Normalize("Ёж.\nКонец."));
        var first = segments[0];
        string Hash(string normal = "Ёж.", string spoken = "Ёж.", string? previous = null, string? next = "Конец.", string frontend = "ru-preserve-v1", string segmenter = "semantic-ru-v1", SegmentBoundary boundary = SegmentBoundary.Paragraph)
            => SemanticSegmenter.ComputeContentHash(normal, spoken, previous, next, frontend, segmenter, boundary);
        Assert.Equal(Hash(), first.ContentHash);
        // Independently encoded v2 fields: null=-1, next context digest, and Paragraph=3.
        Assert.Equal("23d91cc82167105beebe1f21998e1b6278bc01726dc3df41a767a1b55260a396", first.ContentHash);
        Assert.Matches("^[0-9a-f]{64}$", first.ContentHash);
        Assert.NotEqual(first.ContentHash, Hash(normal: "Еж.")); Assert.NotEqual(first.ContentHash, Hash(spoken: "ёж."));
        Assert.NotEqual(first.ContentHash, Hash(previous: "До.")); Assert.NotEqual(first.ContentHash, Hash(next: "Иное."));
        Assert.NotEqual(first.ContentHash, Hash(frontend: "v2")); Assert.NotEqual(first.ContentHash, Hash(segmenter: "v2"));
        Assert.NotEqual(first.ContentHash, Hash(boundary: SegmentBoundary.Sentence));
        Assert.NotEqual(Hash(previous: null), Hash(previous: ""));
        Assert.NotEqual(Hash(normal: "а", spoken: "бв"), Hash(normal: "аб", spoken: "в"));
        var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); Assert.Equal(first.ContentHash, new SemanticSegmenter().Segment(Normalize("Ёж.\nКонец."))[0].ContentHash); }
        finally { CultureInfo.CurrentCulture = old; }
        Assert.Equal(segments, new SemanticSegmenter().Segment(Normalize("Ёж.\nКонец.")));
    }

    [Fact]
    public void Outputs_compose_losslessly_into_task_two_chapter_and_segment_fields()
    {
        var text = Normalize("  Е\u0308ж\tзна́ет.\r\n— Да! ");
        var segments = new SemanticSegmenter().Segment(text); var now = DateTimeOffset.UnixEpoch;
        var pins = new VersionPins("app", "worker", "model", "policy", text.FrontendVersion, "semantic-ru-v1");
        var parameters = new GenerationParameters("neutral", 1, 1, 0, "{}");
        var chapter = new ChapterRecord("chapter", 0, "synthetic", text.OriginalText, text.Normalized, text.Spoken, now);
        Assert.Equal("  Е\u0308ж\tзна́ет.\r\n— Да! ", chapter.OriginalText);
        Assert.Equal("Ёж зна́ет.\n— Да!", chapter.NormalizedText);
        foreach (var segment in segments)
        {
            var row = new SegmentRecord("segment-" + segment.Sequence, chapter.Id, segment.Sequence, segment.OriginalText, segment.NormalizedTarget, segment.SpokenTarget, segment.ContentHash,
                "later-task-fingerprint", null, null, 1, pins, parameters, "queued", false, now, now, null);
            Assert.Equal(segment.OriginalText, row.OriginalText); Assert.Equal(segment.NormalizedTarget, row.NormalizedText);
            Assert.Equal(segment.SpokenTarget, row.SpokenText); Assert.Equal(segment.ContentHash, row.TextHash); Assert.Equal(segment.Sequence, row.Order);
        }
    }

    [Fact]
    public void Empty_lines_produce_no_empty_segments_and_ranges_cover_every_non_whitespace_character_once()
    {
        string text = "\n\n— «Да?!»\n\n" + string.Join(" ", Enumerable.Repeat("сло́во, 🦔", 90)) + ".\n\nКонец...\n";
        var normalized = Normalize(text); var segmenter = new SemanticSegmenter(); var segments = segmenter.Segment(normalized);
        Assert.All(segments, segment => Assert.False(string.IsNullOrWhiteSpace(segment.NormalizedTarget)));
        Assert.Equal(string.Concat(normalized.Normalized.Where(c => !char.IsWhiteSpace(c))),
            string.Concat(segments.SelectMany(segment => segment.NormalizedTarget.Where(c => !char.IsWhiteSpace(c)))));
        int previousEnd = 0;
        foreach (var segment in segments)
        {
            Assert.True(segment.NormalizedRange.Start >= previousEnd);
            Assert.All(normalized.Normalized[previousEnd..segment.NormalizedRange.Start], c => Assert.True(char.IsWhiteSpace(c)));
            Assert.NotNull(segment.OriginalRange);
            Assert.Equal(segment.OriginalText, text.Substring(segment.OriginalRange!.Start, segment.OriginalRange.Length));
            previousEnd = segment.NormalizedRange.Start + segment.NormalizedRange.Length;
        }
        Assert.All(normalized.Normalized[previousEnd..], c => Assert.True(char.IsWhiteSpace(c)));
        Assert.Equal(segments, segmenter.Segment(normalized));
    }

    [Fact]
    public void Neighboring_sentence_context_is_shared_and_allocation_scales_with_input_length()
    {
        var segmenter = new SemanticSegmenter();
        _ = segmenter.Segment(Normalize("Warm.\nUp."));
        static NormalizedText Input(int words) => Normalize(string.Join(" ", Enumerable.Repeat("слово", words)) + ". " + string.Join(" ", Enumerable.Repeat("далее", words)) + ".");
        long Measure(NormalizedText text, out IReadOnlyList<TextSegment> output)
        {
            long before = GC.GetAllocatedBytesForCurrentThread(); output = segmenter.Segment(text);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long small = Measure(Input(1000), out _); long large = Measure(Input(4000), out var segments);
        Assert.True(large < small * 6 + 1_000_000, $"Expected near-linear allocation, small={small}, large={large}");
        var next = segments.Where(x => x.NextSentenceContext != null).ToArray();
        Assert.True(next.Length > 20);
        Assert.All(next, segment => Assert.Same(next[0].NextSentenceContext, segment.NextSentenceContext));
        Assert.All(next, segment => Assert.Same(next[0].NextSentenceContextHash, segment.NextSentenceContextHash));
        var previous = segments.Where(x => x.PreviousSentenceContext != null).ToArray();
        Assert.All(previous, segment => Assert.Same(previous[0].PreviousSentenceContext, segment.PreviousSentenceContext));
        Assert.All(previous, segment => Assert.Same(previous[0].PreviousSentenceContextHash, segment.PreviousSentenceContextHash));
    }

    [Fact]
    public void Context_hashes_use_a_separate_length_prefixed_domain_and_keep_null_distinct_from_empty()
    {
        var segment = new SemanticSegmenter().Segment(Normalize("Ёж.\nКонец."))[0];
        Assert.Null(segment.PreviousSentenceContextHash);
        Assert.Equal("efa9b6e9cda03ac0dfa54b561022308eac8d90e90b590e7b504dcda3c235ceb6", segment.NextSentenceContextHash);
        Assert.Equal(segment.NextSentenceContextHash, SemanticSegmenter.ComputeSentenceContextHash("Конец."));
        Assert.Matches("^[0-9a-f]{64}$", SemanticSegmenter.ComputeSentenceContextHash(""));
        Assert.NotEqual(SemanticSegmenter.ComputeSentenceContextHash("Конец."), SemanticSegmenter.ComputeSentenceContextHash("Иное."));
    }

    [Theory]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    public void Unicode_paragraph_boundaries_preserve_original_ranges(char separator)
    {
        string original = "Ёж." + separator + "— Да!";
        var segments = new SemanticSegmenter().Segment(Normalize(original));
        Assert.Equal(new[] { "Ёж.", "— Да!" }, segments.Select(x => x.NormalizedTarget));
        Assert.Equal(SegmentBoundary.Paragraph, segments[0].BoundaryAfter);
        Assert.All(segments, segment => Assert.Equal(segment.OriginalText, original.Substring(segment.OriginalRange!.Start, segment.OriginalRange.Length)));
    }

    [Theory]
    [InlineData("\u0301")]
    [InlineData("\ufe0f")]
    [InlineData("\u200d")]
    public void Whitespace_grapheme_continuations_are_rejected_at_sentence_and_word_boundaries(string continuation)
    {
        foreach (string text in new[] { new string('я', 318) + ". " + continuation + "а.", new string('я', 318) + " " + continuation + "а продолжение.", " " + continuation + "а.", "А.\n " + continuation + "а." })
        {
            var error = Assert.Throws<TextException>(() => new SemanticSegmenter().Segment(Normalize(text)));
            Assert.Equal("text.grapheme_boundary", error.Code); Assert.Equal(error.Code, error.Message); Assert.Null(error.InnerException);
        }
    }

    [Theory]
    [InlineData("а\u0301")]
    [InlineData("✈\ufe0f")]
    [InlineData("👩\u200d🚀")]
    [InlineData("🦔")]
    public void Whole_graphemes_remain_intact_at_sentence_and_long_sentence_cuts(string token)
    {
        foreach (string input in new[] { new string('я', 318) + ". " + token + ".", new string('я', 318) + " " + token + " далее." })
        {
            var normalized = Normalize(input); var segments = new SemanticSegmenter().Segment(normalized);
            var boundaries = StringInfo.ParseCombiningCharacters(normalized.Normalized).ToHashSet(); boundaries.Add(normalized.Normalized.Length);
            Assert.All(segments, segment =>
            {
                Assert.Contains(segment.NormalizedRange.Start, boundaries);
                Assert.Contains(segment.NormalizedRange.Start + segment.NormalizedRange.Length, boundaries);
                Assert.InRange(segment.NormalizedTarget.Length, 1, 320);
            });
            Assert.Equal(input, string.Join(" ", segments.Select(x => x.NormalizedTarget)));
        }
    }

    [Fact]
    public void Nonterminating_punctuation_runs_are_scanned_in_near_linear_time()
    {
        var segmenter = new SemanticSegmenter();
        _ = Assert.Throws<TextException>(() => segmenter.Segment(Normalize(new string('.', 400) + "а")));
        TimeSpan Measure(int length)
        {
            var input = Normalize(new string('.', length) + "а"); var clock = Stopwatch.StartNew();
            Assert.Equal("text.token_too_long", Assert.Throws<TextException>(() => segmenter.Segment(input)).Code);
            return clock.Elapsed;
        }
        TimeSpan small = Measure(6000), large = Measure(24000);
        Assert.True(large.TotalMilliseconds < small.TotalMilliseconds * 7 + 100,
            $"Expected near-linear punctuation scan, small={small.TotalMilliseconds:F0}ms, large={large.TotalMilliseconds:F0}ms");
    }

    [Theory]
    [InlineData("Один...а продолжение. Конец.")]
    [InlineData("Один?!»а продолжение. Конец.")]
    [InlineData("Один…а продолжение. Конец.")]
    public void Punctuation_followed_by_a_letter_is_not_a_sentence_boundary(string text)
    {
        var segments = new SemanticSegmenter().Segment(Normalize(text + "\nПосле."));
        Assert.Equal(text, segments[0].NormalizedTarget);
        Assert.Equal("Конец.", segments[1].PreviousSentenceContext);
    }

    [Fact]
    public void Tiny_leading_clause_does_not_override_a_near_limit_word_cut()
    {
        var segments = new SemanticSegmenter().Segment(Normalize("Но, " + string.Join(" ", Enumerable.Repeat("слово", 100)) + "."));
        Assert.InRange(segments[0].NormalizedTarget.Length, 280, 320);
        Assert.StartsWith("Но, слово", segments[0].NormalizedTarget);
        Assert.Equal(SegmentBoundary.Word, segments[0].BoundaryAfter);
    }
}

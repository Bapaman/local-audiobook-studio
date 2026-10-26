using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAudiobookStudio.Core.Text;

public enum SegmentBoundary { Word = 0, Clause = 1, Sentence = 2, Paragraph = 3, DocumentEnd = 4 }
internal sealed record SentenceContext(string Text, string Hash);

public sealed record TextSegment
{
    public int Sequence { get; }
    public TextRange? OriginalRange { get; }
    public string OriginalText { get; }
    public TextRange NormalizedRange { get; }
    public string NormalizedTarget { get; }
    public string SpokenTarget { get; }
    public string? PreviousSentenceContext { get; }
    public string? NextSentenceContext { get; }
    public string? PreviousSentenceContextHash { get; }
    public string? NextSentenceContextHash { get; }
    public SegmentBoundary BoundaryAfter { get; }
    public string ContentHash { get; }
    public string FrontendVersion { get; }
    public string SegmenterVersion => SemanticSegmenter.Version;
    /// <summary>A character-based estimate at 16 UTF-16 code units/sec, not measured generation progress.</summary>
    public TimeSpan EstimatedDuration => TimeSpan.FromSeconds(NormalizedTarget.Length / 16d);
    internal TextSegment(int sequence, NormalizedText text, TextRange range, SentenceContext? previous, SentenceContext? next, SegmentBoundary boundary)
    {
        Sequence = sequence; NormalizedRange = range; OriginalRange = text.OriginalRange(range);
        OriginalText = OriginalRange is { } source ? text.OriginalText.Substring(source.Start, source.Length) : "";
        NormalizedTarget = text.Normalized.Substring(range.Start, range.Length); SpokenTarget = NormalizedTarget;
        PreviousSentenceContext = previous?.Text; NextSentenceContext = next?.Text;
        PreviousSentenceContextHash = previous?.Hash; NextSentenceContextHash = next?.Hash;
        BoundaryAfter = boundary; FrontendVersion = text.FrontendVersion;
        ContentHash = SemanticSegmenter.ComputeContentHashFromContextHashes(NormalizedTarget, SpokenTarget, previous?.Hash, next?.Hash, FrontendVersion, SegmenterVersion, boundary);
    }
}

public sealed class SemanticSegmenter
{
    public const string Version = "semantic-ru-v1";
    public const int MaximumCodeUnits = 320;
    // Deliberately small, pinned policy; no culture-dependent casing or expanding abbreviations.
    private static readonly Regex Abbreviations = new(@"\b(?:г|ул)\.|\bт\. [дп]\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private sealed record Sentence(int Start, int Length, int Paragraph);
    private sealed record Piece(int Start, int Length, int FirstSentence, int LastSentence, SegmentBoundary Boundary);

    public IReadOnlyList<TextSegment> Segment(NormalizedText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string value = text.Normalized;
        var boundaries = new bool[value.Length + 1];
        foreach (int boundary in StringInfo.ParseCombiningCharacters(value)) boundaries[boundary] = true;
        boundaries[value.Length] = true;
        var sentences = ReadSentences(value); var pieces = new List<Piece>();
        Piece? packed = null;
        for (int index = 0; index < sentences.Count; index++)
        {
            var sentence = sentences[index];
            bool lastInParagraph = index == sentences.Count - 1 || sentences[index + 1].Paragraph != sentence.Paragraph;
            var boundary = index == sentences.Count - 1 ? SegmentBoundary.DocumentEnd : lastInParagraph ? SegmentBoundary.Paragraph : SegmentBoundary.Sentence;
            if (packed != null && (sentences[packed.LastSentence].Paragraph != sentence.Paragraph || sentence.Start + sentence.Length - packed.Start > MaximumCodeUnits))
            { pieces.Add(packed); packed = null; }
            if (sentence.Length > MaximumCodeUnits)
            {
                if (packed != null) { pieces.Add(packed); packed = null; }
                int start = sentence.Start, end = sentence.Start + sentence.Length;
                while (end - start > MaximumCodeUnits)
                {
                    int split = FindSplit(value, boundaries, start, Math.Min(start + MaximumCodeUnits, end));
                    if (split <= start) throw new TextException("text.token_too_long");
                    var kind = value[split - 1] is ',' or ';' or ':' or '—' ? SegmentBoundary.Clause : SegmentBoundary.Word;
                    pieces.Add(new(start, split - start, index, index, kind)); start = split + 1;
                }
                pieces.Add(new(start, end - start, index, index, boundary));
            }
            else
            {
                packed = packed == null ? new(sentence.Start, sentence.Length, index, index, boundary) : packed with { Length = sentence.Start + sentence.Length - packed.Start, LastSentence = index, Boundary = boundary };
                if (lastInParagraph) { pieces.Add(packed); packed = null; }
            }
        }
        if (packed != null) pieces.Add(packed);
        foreach (var piece in pieces)
            if (!boundaries[piece.Start] || !boundaries[piece.Start + piece.Length]) throw new TextException("text.grapheme_boundary");
        // One immutable text/digest per sentence, shared by all pieces that use that neighboring context.
        var contexts = sentences.Select(sentence =>
        {
            string target = value.Substring(sentence.Start, sentence.Length);
            return new SentenceContext(target, ComputeSentenceContextHash(target));
        }).ToArray();
        return Array.AsReadOnly(pieces.Select((piece, order) => new TextSegment(order, text, new(piece.Start, piece.Length),
            piece.FirstSentence == 0 ? null : contexts[piece.FirstSentence - 1],
            piece.LastSentence + 1 == sentences.Count ? null : contexts[piece.LastSentence + 1], piece.Boundary)).ToArray());
    }

    private static List<Sentence> ReadSentences(string text)
    {
        var result = new List<Sentence>(); int paragraph = 0, cursor = 0;
        var protectedDots = new HashSet<int>();
        foreach (Match match in Abbreviations.Matches(text))
            for (int index = match.Index; index < match.Index + match.Length; index++) if (text[index] == '.') protectedDots.Add(index);
        while (cursor < text.Length)
        {
            if (text[cursor] == '\n') { paragraph++; cursor++; continue; }
            if (text[cursor] == ' ') { cursor++; continue; }
            int start = cursor;
            while (cursor < text.Length && text[cursor] != '\n')
            {
                char character = text[cursor];
                if (character is '.' or '!' or '?' or '…' && !ProtectedPoint(text, cursor, protectedDots))
                {
                    int end = cursor + 1;
                    while (end < text.Length && text[end] is '.' or '!' or '?' or '…' or '»' or '”' or '"' or '\'' or ')' or ']' or '}') end++;
                    if (end == text.Length || char.IsWhiteSpace(text[end])) { cursor = end; break; }
                    // This entire run is internal punctuation; never rescan its remaining suffixes.
                    cursor = end; continue;
                }
                cursor++;
            }
            int finish = cursor;
            while (finish > start && text[finish - 1] == ' ') finish--;
            if (finish > start) result.Add(new(start, finish - start, paragraph));
        }
        return result;
    }

    private static bool ProtectedPoint(string text, int index, HashSet<int> protectedDots)
    {
        if (text[index] != '.') return false;
        if (protectedDots.Contains(index)) return true;
        if (index > 0 && index + 1 < text.Length && char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1])) return true;
        // Uppercase single-letter initials before another initial or a surname.
        if (index == 0 || !char.IsUpper(text[index - 1]) || index > 1 && char.IsLetter(text[index - 2])) return false;
        int next = index + 1; while (next < text.Length && text[next] == ' ') next++;
        return next < text.Length && char.IsUpper(text[next]);
    }

    private static int FindSplit(string text, bool[] boundaries, int start, int limit)
    {
        int lastSpace = -1, lastClause = -1;
        for (int index = start + 1; index <= limit && index < text.Length; index++)
        {
            if (text[index] != ' ') continue;
            if (!boundaries[index] || !boundaries[index + 1]) continue;
            lastSpace = index;
            if (text[index - 1] is ',' or ';' or ':' or '—') lastClause = index;
        }
        return lastClause - start >= MaximumCodeUnits * 3 / 5 ? lastClause : lastSpace;
    }

    /// <summary>
    /// Canonical v2: signed 4-byte big-endian UTF-8 byte length + exact UTF-8 bytes per field; null length=-1,
    /// empty length=0. Order: domain text-segment-v2, normalized, spoken, previous context digest, next context
    /// digest, frontend, segmenter, invariant numeric boundary. Context digests are lowercase SHA-256 of the
    /// same framing for domain text-sentence-context-v1 + exact context text; null remains distinct from an
    /// empty context's digest. The v2 domain versions the hash layout; semantic-ru-v1 still pins segmentation.
    /// No paths, IDs or timestamps. Segment() hashes context text only once per sentence.
    /// </summary>
    public static string ComputeContentHash(string normalized, string spoken, string? previous, string? next, string frontendVersion, string segmenterVersion, SegmentBoundary boundary)
        => ComputeContentHashFromContextHashes(normalized, spoken, previous == null ? null : ComputeSentenceContextHash(previous),
            next == null ? null : ComputeSentenceContextHash(next), frontendVersion, segmenterVersion, boundary);

    public static string ComputeSentenceContextHash(string sentence) => HashFields("text-sentence-context-v1", sentence);

    public static string ComputeContentHashFromContextHashes(string normalized, string spoken, string? previousHash, string? nextHash, string frontendVersion, string segmenterVersion, SegmentBoundary boundary)
        => HashFields("text-segment-v2", normalized, spoken, previousHash, nextHash, frontendVersion, segmenterVersion, ((int)boundary).ToString(CultureInfo.InvariantCulture));

    private static string HashFields(params string?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4]; var utf8 = new UTF8Encoding(false, true);
        foreach (string? field in fields)
        {
            byte[]? bytes = field == null ? null : utf8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? -1); hash.AppendData(length);
            if (bytes != null) hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

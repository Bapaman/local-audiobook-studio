using System.Text;
using System.Globalization;

namespace LocalAudiobookStudio.Core.Text;

public sealed record TextRange(int Start, int Length);
internal sealed record SourceToken(int NormalizedStart, int NormalizedLength, int OriginalStart, int OriginalLength);

public sealed record NormalizedText
{
    public ImportedText Imported { get; }
    public string OriginalText => Imported.OriginalText;
    public string Normalized { get; }
    public string Spoken => Normalized;
    public string FrontendVersion => RussianTextFrontend.Version;
    private readonly SourceToken[] tokens;
    internal NormalizedText(ImportedText imported, string normalized, SourceToken[] tokens)
    { Imported = imported; Normalized = normalized; this.tokens = tokens; }

    // Segment boundaries are token boundaries. NFC can change a token's length, but its original span remains exact.
    internal TextRange? OriginalRange(TextRange range)
    {
        int first = FindToken(range.Start), last = FindToken(range.Start + range.Length - 1);
        if (first < 0 || last < 0 || tokens[first].NormalizedStart != range.Start ||
            tokens[last].NormalizedStart + tokens[last].NormalizedLength != range.Start + range.Length) return null;
        return new(tokens[first].OriginalStart, tokens[last].OriginalStart + tokens[last].OriginalLength - tokens[first].OriginalStart);
    }

    private int FindToken(int position)
    {
        int low = 0, high = tokens.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2; var token = tokens[middle];
            if (position < token.NormalizedStart) high = middle - 1;
            else if (position >= token.NormalizedStart + token.NormalizedLength) low = middle + 1;
            else return middle;
        }
        return -1;
    }
}

/// <summary>Preserving v1: NFC between explicit U+0301 stress marks, horizontal whitespace and line endings; no pronunciation guesses.</summary>
public sealed class RussianTextFrontend
{
    public const string Version = "ru-preserve-v1";

    public NormalizedText Normalize(ImportedText imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        string original = imported.OriginalText; var target = new StringBuilder(); var tokens = new List<SourceToken>();
        var sourceBoundaries = new bool[original.Length + 1];
        foreach (int boundary in StringInfo.ParseCombiningCharacters(original)) sourceBoundaries[boundary] = true;
        sourceBoundaries[original.Length] = true;
        bool pendingSpace = false;
        for (int cursor = 0; cursor < original.Length;)
        {
            char value = original[cursor];
            if (value is '\r' or '\n' or '\u2028' or '\u2029')
            {
                if (value == '\r' && cursor + 1 < original.Length && original[cursor + 1] == '\n') cursor++;
                target.Append('\n'); pendingSpace = false; cursor++; continue;
            }
            if (char.IsWhiteSpace(value)) { pendingSpace = target.Length > 0 && target[^1] != '\n'; cursor++; continue; }
            int start = cursor;
            while (cursor < original.Length && !char.IsWhiteSpace(original[cursor])) cursor++;
            // Trimming/collapsing whitespace must not detach a continuation mark or ZWJ from its source grapheme.
            if (!sourceBoundaries[start] || !sourceBoundaries[cursor]) throw new TextException("text.grapheme_boundary");
            if (pendingSpace) target.Append(' ');
            string normalized = NormalizePreservingStress(original[start..cursor]);
            tokens.Add(new(target.Length, normalized.Length, start, cursor - start));
            target.Append(normalized); pendingSpace = false;
        }
        return new(imported, target.ToString(), tokens.ToArray());
    }

    private static string NormalizePreservingStress(string token)
    {
        // Explicit pronunciation marks must remain U+0301, even where Unicode would compose e.g. Ѓ -> Ѓ.
        // The mark is a deliberate barrier to NFC composition/reordering; neighboring spans still use NFC.
        return string.Join('\u0301', token.Split('\u0301').Select(part => part.Normalize(NormalizationForm.FormC)));
    }
}

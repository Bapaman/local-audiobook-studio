using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAudiobookStudio.Core.Text;

public sealed class TextException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public enum TextSourceKind { File, Pasted }

/// <summary>The original decoded document and provenance of the exact input bytes, never a source path.</summary>
public sealed record ImportedText
{
    public string OriginalText { get; }
    public string SourceEncoding { get; }
    public string SourceHash { get; }
    public long OriginalByteCount { get; }
    public TextSourceKind SourceKind { get; }
    internal ImportedText(string original, string encoding, ReadOnlySpan<byte> bytes, TextSourceKind kind)
    {
        OriginalText = original; SourceEncoding = encoding; SourceKind = kind;
        OriginalByteCount = bytes.Length; SourceHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

public sealed class TextImportService
{
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const int MaximumPastedCodeUnits = 8_000_000;
    public const int MaximumProjectTexts = 32;
    public const int MaximumProjectSegments = 10_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ChapterHeading = new(@"\Aглава[\t\p{Zs}]+(?:[0-9]{1,4}|[ivxlcdm]{1,12})(?:[\t\p{Zs}]*[.:—–-].*)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task<ImportedText> ImportFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ImportFileAsync(file, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new TextException("text.read"); }
    }

    /// <summary>Reads bounded exact input bytes without closing the caller-owned stream.</summary>
    public async Task<ImportedText> ImportFileAsync(Stream content, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFileBytesAsync(content, cancellationToken);
        var (text, name, _, _) = Decode(bytes);
        return new(text, name, bytes, TextSourceKind.File);
    }

    public async Task<IReadOnlyList<(string Title, ImportedText Source)>> ImportBookFileAsync(Stream content, CancellationToken token)
    {
        var bytes = await ReadFileBytesAsync(content, token);
        var (text, name, encoding, bomBytes) = Decode(bytes);
        if (text.Length > MaximumPastedCodeUnits) throw new TextException("text.project_limit");

        var headings = new List<(int Start, int End, string Title)>();
        for (int start = 0; start < text.Length;)
        {
            token.ThrowIfCancellationRequested();
            int lineLength = text.AsSpan(start).IndexOfAny("\r\n\u2028\u2029");
            int end = lineLength < 0 ? text.Length : start + lineLength;
            var line = text.AsSpan(start, end - start).Trim();
            if (line.StartsWith("глава", StringComparison.OrdinalIgnoreCase) && ChapterHeading.IsMatch(line))
            {
                if (headings.Count == MaximumProjectTexts) throw new TextException("text.project_limit");
                if (line.Length > 120) throw new TextException("text.display_name.invalid");
                // Labels also name archive entries. Keep path separators only in the exact source.
                var title = line.ToString().Replace('\t', ' ').Replace('\u00a0', ' ').Replace('\u202f', ' ')
                    .Replace('/', '⁄').Replace('\\', '⧵');
                headings.Add((start, end, title));
            }
            start = end + 1;
        }
        if (headings.Count < 2) throw new TextException("text.chapters.not_found");

        var divisions = new List<(int Start, string Title)>();
        if (!text.AsSpan(0, headings[0].Start).Trim().IsEmpty) divisions.Add((0, "Вступление"));
        for (int i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            int next = i + 1 < headings.Count ? headings[i + 1].Start : text.Length;
            if (text.AsSpan(heading.End, next - heading.End).Trim().IsEmpty) throw new TextException("text.chapters.empty");
            divisions.Add((divisions.Count == 0 ? 0 : heading.Start, heading.Title));
        }
        if (divisions.Count > MaximumProjectTexts) throw new TextException("text.project_limit");

        var chapters = new List<(string Title, ImportedText Source)>(divisions.Count);
        int byteEnd = bomBytes;
        for (int i = 0; i < divisions.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var (start, title) = divisions[i];
            int end = i + 1 < divisions.Count ? divisions[i + 1].Start : text.Length;
            string chapter = text[start..end];
            int byteStart = i == 0 ? 0 : byteEnd;
            byteEnd += encoding.GetByteCount(chapter);
            chapters.Add((title, new(chapter, name, bytes.AsSpan(byteStart, byteEnd - byteStart), TextSourceKind.File)));
        }
        return chapters;
    }

    private static async Task<byte[]> ReadFileBytesAsync(Stream content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (content == null || !content.CanRead) throw new TextException("text.read");
            if (content.CanSeek && content.Length - content.Position > MaximumFileBytes) throw new TextException("text.too_large");
            using var collected = new MemoryStream();
            byte[] buffer = new byte[65536];
            while (true)
            {
                // Read at most one byte beyond the cap, including if the file grows after the length check.
                int request = (int)Math.Min(buffer.Length, MaximumFileBytes - collected.Length + 1);
                int read = await content.ReadAsync(buffer.AsMemory(0, request), cancellationToken);
                if (read == 0) break;
                if (collected.Length + read > MaximumFileBytes) throw new TextException("text.too_large");
                collected.Write(buffer, 0, read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return collected.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new TextException("text.read"); }
    }

    public ImportedText ImportPastedText(string text)
    {
        if (text == null) throw new TextException("text.empty");
        if (text.Length > MaximumPastedCodeUnits) throw new TextException("text.too_large");
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException) { throw new TextException("text.encoding.invalid"); }
        Validate(text);
        return new(text, "utf-8", bytes, TextSourceKind.Pasted);
    }

    private static (string Text, string Name, Encoding Encoding, int BomBytes) Decode(byte[] bytes)
    {
        var data = bytes.AsSpan(); int offset = 0; string name = "utf-8"; Encoding encoding = StrictUtf8;
        // UTF-32LE shares the UTF-16LE prefix: reject it first.
        if (data.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || data.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            throw new TextException("text.encoding.unsupported");
        if (data.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) offset = 3;
        else if (data.StartsWith(new byte[] { 0xff, 0xfe })) { offset = 2; name = "utf-16le"; encoding = new UnicodeEncoding(false, false, true); }
        else if (data.StartsWith(new byte[] { 0xfe, 0xff })) { offset = 2; name = "utf-16be"; encoding = new UnicodeEncoding(true, false, true); }
        string text;
        try { text = encoding.GetString(data[offset..]); }
        catch (DecoderFallbackException) { throw new TextException("text.encoding.invalid"); }
        Validate(text);
        return (text, name, encoding, offset);
    }

    private static void Validate(string text)
    {
        bool visible = false;
        foreach (char value in text)
        {
            if (char.IsControl(value) && value is not ('\t' or '\r' or '\n')) throw new TextException("text.binary");
            if (!char.IsWhiteSpace(value) && value != '\ufeff') visible = true;
        }
        if (!visible) throw new TextException("text.empty");
    }
}

using System.Security.Cryptography;
using System.Text;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Core.Tests;

public class TextImportServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_decoder_handles_fragmented_utf16_without_disposing_caller(bool bigEndian)
    {
        Encoding encoding = new UnicodeEncoding(bigEndian, true, true);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("Ёж чита\u0301ет.")];
        using var stream = new FragmentedStream(bytes);
        var imported = await new TextImportService().ImportFileAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal("Ёж чита\u0301ет.", imported.OriginalText); Assert.Equal(bytes.Length, imported.OriginalByteCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), imported.SourceHash); Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(16777216, true, false)]
    [InlineData(16777217, false, false)]
    [InlineData(16777217, false, true)]
    public async Task Nonseekable_or_growing_stream_is_bounded_to_cap_plus_one(int count, bool accepted, bool advertisesShortLength)
    {
        using var stream = new RepeatedStream(count, advertisesShortLength);
        if (accepted) Assert.Equal(count, (await new TextImportService().ImportFileAsync(stream, TestContext.Current.CancellationToken)).OriginalByteCount);
        else Assert.Equal("text.too_large", (await Assert.ThrowsAsync<TextException>(() => new TextImportService().ImportFileAsync(stream, TestContext.Current.CancellationToken))).Code);
        Assert.Equal(count, stream.ReadCount); Assert.True(stream.CanRead);
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
    private sealed class RepeatedStream(int count, bool advertisesShortLength) : Stream
    {
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => advertisesShortLength;
        public override bool CanWrite => false;
        public override long Length => advertisesShortLength ? 1 : throw new NotSupportedException();
        public override long Position { get => advertisesShortLength ? 0 : throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); int read = Math.Min(buffer.Length, count - ReadCount); buffer.Span[..read].Fill((byte)'a'); ReadCount += read;
            return ValueTask.FromResult(read);
        }
        public override int Read(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int length) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16le", true)]
    [InlineData("utf-16be", true)]
    public async Task Supported_encodings_preserve_exact_text_and_hash_all_bytes_including_preamble(string name, bool bom)
    {
        using var sandbox = new TextSandbox();
        const string original = "  Ёж е\u0308щё\tзна́ет.\r\n— 12.10.2026!\n";
        Encoding encoding = name switch { "utf-16le" => new UnicodeEncoding(false, true, true), "utf-16be" => new UnicodeEncoding(true, true, true), _ => new UTF8Encoding(bom, true) };
        byte[] bytes = [.. bom ? encoding.GetPreamble() : [], .. encoding.GetBytes(original)];
        await File.WriteAllBytesAsync(sandbox.FilePath, bytes, TestContext.Current.CancellationToken);
        var result = await new TextImportService().ImportFileAsync(sandbox.FilePath, TestContext.Current.CancellationToken);
        Assert.Equal(original, result.OriginalText);
        Assert.Equal(name, result.SourceEncoding);
        Assert.Equal(bytes.Length, result.OriginalByteCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.SourceHash);
        Assert.Equal(TextSourceKind.File, result.SourceKind);
        Assert.True(sandbox.FilePath.Length > 180);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sandbox.FilePath, TestContext.Current.CancellationToken));
    }

    public static TheoryData<byte[], string> InvalidFiles => new()
    {
        { [], "text.empty" }, { [0xef, 0xbb, 0xbf], "text.empty" }, { [0xff, 0xfe], "text.empty" },
        { [0xff, 0xfe, 0, 0, 0x41, 0, 0, 0], "text.encoding.unsupported" },
        { [0, 0, 0xfe, 0xff, 0, 0, 0, 0x41], "text.encoding.unsupported" },
        { [0xc0, 0xaf], "text.encoding.invalid" }, { [0xd0], "text.encoding.invalid" },
        { [0xed, 0xa0, 0x80], "text.encoding.invalid" }, { [0xf4, 0x90, 0x80, 0x80], "text.encoding.invalid" },
        { [0xff, 0xfe, 0x41], "text.encoding.invalid" }, { [0xfe, 0xff, 0], "text.encoding.invalid" },
        { [0xff, 0xfe, 0, 0xd8], "text.encoding.invalid" }, { [0xfe, 0xff, 0xdc, 0], "text.encoding.invalid" },
        { [0x41, 0, 0x42, 0], "text.binary" }, { [0xc6, 0xe6], "text.encoding.invalid" },
        { [0x41, 0x1b], "text.binary" }, { [0x41, 0x7f], "text.binary" },
        { [0x20, 0x09, 0x0d, 0x0a], "text.empty" }
    };

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public async Task Invalid_files_return_only_stable_private_safe_codes(byte[] bytes, string code)
    {
        using var sandbox = new TextSandbox();
        await File.WriteAllBytesAsync(sandbox.FilePath, bytes, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<TextException>(() => new TextImportService().ImportFileAsync(sandbox.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
        Assert.Equal(code, error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(16777215, true)]
    [InlineData(16777216, true)]
    [InlineData(16777217, false)]
    public async Task File_size_limit_is_inclusive(int count, bool accepted)
    {
        using var sandbox = new TextSandbox();
        byte[] bytes = new byte[count]; Array.Fill(bytes, (byte)'a');
        await File.WriteAllBytesAsync(sandbox.FilePath, bytes, TestContext.Current.CancellationToken);
        var service = new TextImportService();
        if (accepted) Assert.Equal(count, (await service.ImportFileAsync(sandbox.FilePath, TestContext.Current.CancellationToken)).OriginalByteCount);
        else Assert.Equal("text.too_large", (await Assert.ThrowsAsync<TextException>(() => service.ImportFileAsync(sandbox.FilePath, TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public void Pasted_text_uses_exact_utf8_without_bom_and_preserves_the_original()
    {
        const string text = "Ёж\r\n  зна́ет 🦔";
        var result = new TextImportService().ImportPastedText(text);
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
        Assert.Equal(text, result.OriginalText);
        Assert.Equal("utf-8", result.SourceEncoding);
        Assert.Equal(bytes.Length, result.OriginalByteCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.SourceHash);
        Assert.Equal(TextSourceKind.Pasted, result.SourceKind);
    }

    [Theory]
    [InlineData(7999999, true)]
    [InlineData(8000000, true)]
    [InlineData(8000001, false)]
    public void Pasted_limit_counts_utf16_code_units(int count, bool accepted)
    {
        var service = new TextImportService(); string text = new('ё', count);
        if (accepted) Assert.Equal(count, service.ImportPastedText(text).OriginalText.Length);
        else Assert.Equal("text.too_large", Assert.Throws<TextException>(() => service.ImportPastedText(text)).Code);
    }

    [Theory]
    [InlineData("", "text.empty")]
    [InlineData("\ufeff", "text.empty")]
    [InlineData(" \t\r\n", "text.empty")]
    [InlineData("synthetic\0secret", "text.binary")]
    [InlineData("synthetic\u0085secret", "text.binary")]
    public void Invalid_pasted_text_has_private_safe_errors(string text, string code)
    {
        var error = Assert.Throws<TextException>(() => new TextImportService().ImportPastedText(text));
        Assert.Equal(code, error.Code); Assert.Equal(code, error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public void Pasted_unpaired_surrogates_are_rejected_without_replacement()
    {
        // CLR custom-attribute strings cannot faithfully transport an unpaired surrogate.
        foreach (char surrogate in new[] { (char)0xd800, (char)0xdc00 })
            Assert.Equal("text.encoding.invalid", Assert.Throws<TextException>(() => new TextImportService().ImportPastedText("synthetic" + surrogate + "secret")).Code);
    }

    [Fact]
    public async Task Missing_file_is_redacted_and_cancellation_is_not_translated()
    {
        using var sandbox = new TextSandbox();
        Assert.Equal("text.read", (await Assert.ThrowsAsync<TextException>(() => new TextImportService().ImportFileAsync(sandbox.FilePath, TestContext.Current.CancellationToken))).Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TextImportService().ImportFileAsync(sandbox.FilePath, cancellation.Token));
    }

    [Fact]
    public async Task Byte_provenance_distinguishes_bom_and_encoding_even_when_decoded_text_is_identical()
    {
        using var sandbox = new TextSandbox(); var service = new TextImportService();
        const string original = "Ёж.";
        await File.WriteAllBytesAsync(sandbox.FilePath, Encoding.UTF8.GetBytes(original), TestContext.Current.CancellationToken);
        var plain = await service.ImportFileAsync(sandbox.FilePath, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(sandbox.FilePath, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(original)], TestContext.Current.CancellationToken);
        var bom = await service.ImportFileAsync(sandbox.FilePath, TestContext.Current.CancellationToken);
        Assert.Equal(plain.OriginalText, bom.OriginalText); Assert.NotEqual(plain.SourceHash, bom.SourceHash);
        Assert.Equal(plain.OriginalByteCount + 3, bom.OriginalByteCount);
        Assert.Equal(plain.SourceHash, service.ImportPastedText(original).SourceHash);
    }
}

internal sealed class TextSandbox : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "las-text-" + Guid.NewGuid().ToString("N"));
    public string FilePath { get; }
    public TextSandbox()
    {
        string folder = Path.Combine(root, new string('я', 65) + " пробел", new string('ё', 65) + " текст");
        Directory.CreateDirectory(folder); FilePath = Path.Combine(folder, "синтетический текст.txt");
    }
    public void Dispose() => Directory.Delete(root, true);
}

using System.Buffers.Binary;

namespace LocalAudiobookStudio.Audio;

/// <summary>One streaming PCM16/mono48k WAV. Caller retains ownership of all source streams.</summary>
public sealed class PcmWavAssembly
{
    public const int MaximumFragments = 256;
    public const long MaximumPcmBytes = 1L << 30;

    private sealed record Part(Stream Stream, long Offset, long Length);
    private readonly IReadOnlyList<Part> parts;
    private readonly long pcmLength;
    public long Length => checked(pcmLength + 44);
    private PcmWavAssembly(IReadOnlyList<Part> parts, long pcmLength) { this.parts = parts; this.pcmLength = pcmLength; }

    public static async Task<PcmWavAssembly> CreateAsync(IReadOnlyList<Stream> sources, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (sources.Count is < 1 or > MaximumFragments) throw new WavInspectionException("book.input_limit");
        var parts = new List<Part>();
        long total = 0;
        foreach (var stream in sources)
        {
            var part = await ReadPartAsync(stream, token);
            if (part.Length > MaximumPcmBytes - total) throw new WavInspectionException("book.byte_limit");
            parts.Add(part);
            total += part.Length;
        }
        return new(parts.AsReadOnly(), total);
    }

    public static async Task<long> ReadPcmLengthAsync(Stream stream, CancellationToken token = default)
        => (await ReadPartAsync(stream, token)).Length;

    private static async Task<Part> ReadPartAsync(Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!stream.CanRead || !stream.CanSeek) throw new WavInspectionException("wav.format");
            stream.Position = 0;
            byte[] header = new byte[12]; await stream.ReadExactlyAsync(header, token);
            if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8).SequenceEqual("WAVE"u8)) throw new WavInspectionException("wav.format");
            long end = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) + 8L;
            if (end != stream.Length || end < 44) throw new WavInspectionException("wav.truncated");
            bool formatFound = false; long offset = -1, length = 0; int chunks = 0; byte[] chunk = new byte[8];
            while (stream.Position < end)
            {
                token.ThrowIfCancellationRequested();
                if (++chunks > 1024 || end - stream.Position < 8) throw new WavInspectionException("wav.format");
                await stream.ReadExactlyAsync(chunk, token);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
                long next = checked(stream.Position + size + (size & 1));
                if (next > end) throw new WavInspectionException("wav.truncated");
                if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8))
                {
                    if (formatFound || size < 16) throw new WavInspectionException("wav.format");
                    byte[] format = new byte[16]; await stream.ReadExactlyAsync(format, token);
                    if (BinaryPrimitives.ReadUInt16LittleEndian(format) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2)) != 1
                        || BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)) != 48000 || BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8)) != 96000
                        || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12)) != 2 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14)) != 16)
                        throw new WavInspectionException("wav.format");
                    formatFound = true;
                }
                else if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
                {
                    if (offset != -1 || size == 0 || size % 2 != 0) throw new WavInspectionException("wav.samples");
                    offset = stream.Position; length = size;
                }
                stream.Position = next;
            }
            if (!formatFound || offset == -1) throw new WavInspectionException("wav.format");
            return new(stream, offset, length);
        }
        catch (EndOfStreamException) { throw new WavInspectionException("wav.truncated"); }
        catch (OverflowException) { throw new WavInspectionException("book.byte_limit"); }
    }

    public async Task CopyToAsync(Stream destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        byte[] header = new byte[44];
        "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)pcmLength + 36));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 48000); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 96000);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), checked((uint)pcmLength));
        await destination.WriteAsync(header, token);
        byte[] buffer = new byte[65536];
        foreach (var part in parts)
        {
            part.Stream.Position = part.Offset; long left = part.Length;
            while (left > 0)
            {
                int count = (int)Math.Min(left, buffer.Length);
                await part.Stream.ReadExactlyAsync(buffer.AsMemory(0, count), token);
                await destination.WriteAsync(buffer.AsMemory(0, count), token); left -= count;
            }
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LocalAudiobookStudio.Audio;

public sealed class WavInspectionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record WavInspection(ushort Channels, uint SampleRate, ushort BitsPerSample, ushort BlockAlignment,
    long DataOffset, long DataLength, long SampleCount, TimeSpan Duration, double Peak, bool IsClipped, string Sha256);

/// <summary>Inspects the slice's mono 48 kHz PCM16 interchange format. Duration policy belongs to the caller.</summary>
public static class WavInspector
{
    public static async Task<WavInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] header = new byte[12];
            await ReadAsync(stream, header, cancellationToken);
            if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw new WavInspectionException("wav.format");
            long end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (end != stream.Length || end < 12) throw new WavInspectionException("wav.truncated");
            bool formatFound = false; long dataOffset = -1, dataLength = 0;
            ushort channels = 0, bits = 0, alignment = 0; uint rate = 0;
            byte[] chunk = new byte[8];
            while (stream.Position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (end - stream.Position < 8) throw new WavInspectionException("wav.truncated");
                await ReadAsync(stream, chunk, cancellationToken);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
                long next = stream.Position + size + (size & 1);
                if (next > end) throw new WavInspectionException("wav.truncated");
                if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8))
                {
                    if (formatFound || size < 16) throw new WavInspectionException("wav.format");
                    byte[] format = new byte[16]; await ReadAsync(stream, format, cancellationToken);
                    ushort compression = BinaryPrimitives.ReadUInt16LittleEndian(format);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2)); rate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
                    uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
                    alignment = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12)); bits = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
                    if (compression != 1 || channels != 1 || rate != 48000 || bits != 16 || alignment != 2 || byteRate != 96000) throw new WavInspectionException("wav.format");
                    formatFound = true;
                }
                else if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
                {
                    if (dataOffset != -1) throw new WavInspectionException("wav.format");
                    dataOffset = stream.Position; dataLength = size;
                }
                stream.Position = next;
            }
            if (!formatFound || dataOffset == -1) throw new WavInspectionException("wav.format");
            if (dataLength == 0) throw new WavInspectionException("wav.empty");
            if (dataLength % alignment != 0) throw new WavInspectionException("wav.samples");
            stream.Position = dataOffset; long remaining = dataLength; int peak = 0; bool clipped = false;
            byte[] buffer = new byte[65536];
            while (remaining > 0)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                await ReadAsync(stream, buffer.AsMemory(0, count), cancellationToken);
                for (int i = 0; i < count; i += 2)
                {
                    short value = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i, 2));
                    peak = Math.Max(peak, Math.Abs((int)value)); clipped |= value is short.MinValue or short.MaxValue;
                }
                remaining -= count;
            }
            stream.Position = 0;
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            long samples = dataLength / alignment;
            return new(channels, rate, bits, alignment, dataOffset, dataLength, samples, TimeSpan.FromSeconds((double)samples / rate), peak / 32768.0, clipped, hash);
        }
        catch (WavInspectionException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { throw new WavInspectionException("wav.io"); }
        catch (UnauthorizedAccessException) { throw new WavInspectionException("wav.io"); }
        catch (ArgumentException) { throw new WavInspectionException("wav.io"); }
    }

    private static async Task ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        try { await stream.ReadExactlyAsync(buffer, token); }
        catch (EndOfStreamException) { throw new WavInspectionException("wav.truncated"); }
    }
}

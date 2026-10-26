using System.Buffers.Binary;

namespace LocalAudiobookStudio.Audio;

/// <summary>Copies an exact half-open interval of the PCM16 mono 48 kHz interchange format.</summary>
public static class PcmWavClipper
{
    public static async Task ExtractAsync(string source, string destination, TimeSpan start, TimeSpan end, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool created = false, completed = false;
        try
        {
            // Retain a read-only handle throughout inspection and extraction so an active writer cannot change the source.
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            var inspection = await WavInspector.InspectAsync(source, cancellationToken);
            if (start < TimeSpan.Zero || end <= start || start.Ticks % TimeSpan.TicksPerMillisecond != 0 || end.Ticks % TimeSpan.TicksPerMillisecond != 0)
                throw new WavInspectionException("voice.anchor.range");
            long first = checked(start.Ticks / TimeSpan.TicksPerMillisecond * 48), last = checked(end.Ticks / TimeSpan.TicksPerMillisecond * 48);
            if (last > inspection.SampleCount) throw new WavInspectionException("voice.anchor.range");
            long length = checked((last - first) * 2);
            if (length > uint.MaxValue - 36) throw new WavInspectionException("voice.anchor.range");
            byte[] header = new byte[44];
            "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)length + 36); "WAVEfmt "u8.CopyTo(header.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 1); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 48000);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 96000); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16); "data"u8.CopyTo(header.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)length);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                created = true; await output.WriteAsync(header, cancellationToken);
                input.Position = inspection.DataOffset + first * 2; byte[] buffer = new byte[65536];
                while (length > 0)
                {
                    int count = (int)Math.Min(buffer.Length, length);
                    await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken); length -= count;
                }
                await output.FlushAsync(cancellationToken); output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested(); completed = true;
        }
        catch (OverflowException) { throw new WavInspectionException("voice.anchor.range"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { throw new WavInspectionException("wav.io"); }
        finally
        {
            if (created && !completed)
            {
                try { File.Delete(destination); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new WavInspectionException("wav.io"); }
            }
        }
    }
}

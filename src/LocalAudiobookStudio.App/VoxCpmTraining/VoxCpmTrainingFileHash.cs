using System.Buffers;
using System.Security.Cryptography;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

internal static class VoxCpmTrainingFileHash
{
    internal static async Task<string> Sha256Async(Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Pinned streams stay unbuffered: tens of thousands of retained handles
        // must not each retain a read buffer. Small files also need only a small
        // pooled block to read and clear; larger/nonseekable streams use 1 MiB.
        int size = stream.CanSeek ? (int)Math.Clamp(stream.Length - stream.Position, 1L, 1024 * 1024) : 1024 * 1024;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = await stream.ReadAsync(buffer.AsMemory(0, size), token).ConfigureAwait(false);
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

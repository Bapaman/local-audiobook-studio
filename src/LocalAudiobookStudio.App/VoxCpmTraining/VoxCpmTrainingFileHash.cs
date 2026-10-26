using System.Buffers;
using System.Security.Cryptography;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

internal static class VoxCpmTrainingFileHash
{
    internal static async Task<string> Sha256Async(Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Pinned streams stay unbuffered: tens of thousands of retained handles
        // must not each retain a read buffer. Pool one large block for this hash.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

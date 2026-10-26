using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Workers;
using Microsoft.Win32.SafeHandles;

namespace LocalAudiobookStudio.Application.Artifacts;

internal static class ConvertedWavePublication
{
    // Caller holds the artifact lease and parent pins. Candidate is immutable.
    internal static async Task PublishAsync(string source, string final, string expectedHash, long expectedLength, CancellationToken token)
    {
        string part = final + ".part";
        using var file = CatalogPaths.OpenFile(part, 0xc0010000, 1, 1); // CREATE_NEW, read/write/DELETE, share-read.
        await using var staged = new FileStream(file, FileAccess.ReadWrite, 65536, false);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await input.CopyToAsync(staged, token);
        await staged.FlushAsync(token); staged.Flush(true);
        if (staged.Length != expectedLength) throw new WorkerClientException("worker.output.invalid", true);
        staged.Position = 0;
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(staged, token)) != expectedHash)
            throw new WorkerClientException("worker.output.invalid", true);
        CatalogPaths.ValidateHandle(file, false);
        token.ThrowIfCancellationRequested();
        // Rename this exact pinned handle, without replacing any target.
        string target = Path.GetFullPath(final);
        if (!target.StartsWith("\\\\?\\", StringComparison.Ordinal)) target = "\\\\?\\" + target;
        byte[] name = Encoding.Unicode.GetBytes(target);
        int offset = IntPtr.Size == 8 ? 20 : 12, lengthOffset = IntPtr.Size == 8 ? 16 : 8;
        byte[] info = new byte[offset + name.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(lengthOffset), checked((uint)name.Length)); name.CopyTo(info, offset);
        if (!SetFileInformationByHandle(file, 3, info, (uint)info.Length)) throw new IOException("worker.storage");
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, byte[] info, uint size);
}

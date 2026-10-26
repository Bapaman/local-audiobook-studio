using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Engine;

/// <summary>
/// Pins one staging-file identity against writes/deletes and renames that exact file handle.
/// The parent directory is held without delete sharing for the same interval.
/// </summary>
internal sealed class PinnedOutputFile : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileTypeDisk = 1;
    private const int FileAttributeTagInfo = 9;
    private const int FileStandardInfo = 1;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int FileIdInfoClass = 18;

    internal readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh);

    private readonly string parent;
    private readonly SafeFileHandle directory;
    private readonly FileStream stream;

    private PinnedOutputFile(string parent, SafeFileHandle directory, FileStream stream)
    {
        this.parent = parent;
        this.directory = directory;
        this.stream = stream;
    }

    public static FileIdentity CaptureIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) throw new WorkerException("worker.unsupported.platform");
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out FileIdInformation info, (uint)Marshal.SizeOf<FileIdInformation>())) ThrowIo();
        return new(info.VolumeSerialNumber, info.FileIdLow, info.FileIdHigh);
    }

    public static PinnedOutputFile Open(string part, string final, FileIdentity expectedIdentity) => OpenCore(part, final, expectedIdentity, allowHardLinks: false);

    private static PinnedOutputFile OpenCore(string part, string final, FileIdentity expectedIdentity, bool allowHardLinks)
    {
        if (!OperatingSystem.IsWindows()) throw new WorkerException("worker.unsupported.platform");
        string sourceParent = Path.GetFullPath(Path.GetDirectoryName(part) ?? throw new WorkerException("worker.path"));
        string targetParent = Path.GetFullPath(Path.GetDirectoryName(final) ?? throw new WorkerException("worker.path"));
        if (!string.Equals(sourceParent, targetParent, StringComparison.OrdinalIgnoreCase)) throw new WorkerException("worker.path");

        SafeFileHandle? directory = null;
        SafeFileHandle? file = null;
        try
        {
            directory = CreateFileW(Extended(sourceParent), FileReadAttributes, FileShareRead | FileShareWrite, 0, OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
            EnsureHandle(directory);
            EnsureDirectory(directory);

            file = CreateFileW(Extended(part), GenericRead | GenericWrite | Delete, FileShareRead, 0, OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan, 0);
            EnsureHandle(file);
            EnsureRegularFile(file, allowHardLinks);
            EnsureExpectedPath(file, part);
            if (CaptureIdentity(file) != expectedIdentity) throw new WorkerException("worker.output.invalid");
            var stream = new FileStream(file, FileAccess.ReadWrite, 65_536, isAsync: false);
            file = null; // FileStream owns it.
            return new(sourceParent, directory, stream);
        }
        catch
        {
            file?.Dispose();
            directory?.Dispose();
            throw;
        }
    }

    public static void DeleteOwnedIfPresent(string part, string final, FileIdentity expectedIdentity)
    {
        if (!File.Exists(part)) return;
        try
        {
            using var pinned = OpenCore(part, final, expectedIdentity, allowHardLinks: true);
            byte[] disposition = [1];
            if (!SetFileInformationByHandle(pinned.stream.SafeFileHandle, FileDispositionInfo, disposition, (uint)disposition.Length)) ThrowIo();
        }
        catch (WorkerException error) when (error.Code is "worker.output.invalid" or "worker.path")
        {
            // A different identity or unsafe path belongs to lease recovery, not this request's cleanup.
        }
    }

    public void Flush() => stream.Flush(flushToDisk: true);

    public async Task<long> VerifyIdentityAsync(string inspectedSha256, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        string pinnedHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(pinnedHash, inspectedSha256, StringComparison.OrdinalIgnoreCase)) throw new WorkerException("worker.output.invalid");
        return stream.Length;
    }

    public async Task<WavInspection> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        byte[] header = new byte[12];
        await ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw new WavInspectionException("wav.format");
        long end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (end != stream.Length || end < 12) throw new WavInspectionException("wav.truncated");
        bool formatFound = false;
        long dataOffset = -1, dataLength = 0;
        ushort channels = 0, bits = 0, alignment = 0;
        uint rate = 0;
        byte[] chunk = new byte[8];
        while (stream.Position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (end - stream.Position < 8) throw new WavInspectionException("wav.truncated");
            await ReadExactlyAsync(chunk, cancellationToken);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            long next = stream.Position + size + (size & 1);
            if (next > end) throw new WavInspectionException("wav.truncated");
            if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (formatFound || size < 16) throw new WavInspectionException("wav.format");
                byte[] format = new byte[16];
                await ReadExactlyAsync(format, cancellationToken);
                ushort compression = BinaryPrimitives.ReadUInt16LittleEndian(format);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
                rate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
                uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
                alignment = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
                if (compression != 1 || channels != 1 || rate != 48000 || bits != 16 || alignment != 2 || byteRate != 96000)
                    throw new WavInspectionException("wav.format");
                formatFound = true;
            }
            else if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (dataOffset != -1) throw new WavInspectionException("wav.format");
                dataOffset = stream.Position;
                dataLength = size;
            }
            stream.Position = next;
        }
        if (!formatFound || dataOffset == -1) throw new WavInspectionException("wav.format");
        if (dataLength == 0) throw new WavInspectionException("wav.empty");
        if (dataLength % alignment != 0) throw new WavInspectionException("wav.samples");
        stream.Position = dataOffset;
        long remaining = dataLength;
        int peak = 0;
        bool clipped = false;
        byte[] buffer = new byte[65536];
        while (remaining > 0)
        {
            int count = (int)Math.Min(buffer.Length, remaining);
            await ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            for (int i = 0; i < count; i += 2)
            {
                short value = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i, 2));
                peak = Math.Max(peak, Math.Abs((int)value));
                clipped |= value is short.MinValue or short.MaxValue;
            }
            remaining -= count;
        }
        stream.Position = 0;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        long samples = dataLength / alignment;
        return new(channels, rate, bits, alignment, dataOffset, dataLength, samples, TimeSpan.FromSeconds((double)samples / rate),
            peak / 32768.0, clipped, hash);
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try { await stream.ReadExactlyAsync(buffer, cancellationToken); }
        catch (EndOfStreamException) { throw new WavInspectionException("wav.truncated"); }
    }

    public void Rename(string final)
    {
        EnsureRegularFile(stream.SafeFileHandle);
        string targetParent = Path.GetFullPath(Path.GetDirectoryName(final) ?? throw new WorkerException("worker.path"));
        if (!string.Equals(parent, targetParent, StringComparison.OrdinalIgnoreCase)) throw new WorkerException("worker.path");
        byte[] fileName = Encoding.Unicode.GetBytes(Extended(Path.GetFullPath(final)));
        int fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
        int fileNameLengthOffset = IntPtr.Size == 8 ? 16 : 8;
        byte[] info = new byte[checked(fileNameOffset + fileName.Length + sizeof(char))];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(fileNameLengthOffset), checked((uint)fileName.Length));
        fileName.CopyTo(info, fileNameOffset);
        if (!SetFileInformationByHandle(stream.SafeFileHandle, FileRenameInfo, info, checked((uint)info.Length))) ThrowIo();
    }

    public void Dispose()
    {
        stream.Dispose();
        directory.Dispose();
    }

    private static void EnsureHandle(SafeFileHandle handle)
    {
        if (handle.IsInvalid) ThrowIo();
    }

    private static void EnsureDirectory(SafeFileHandle handle)
    {
        EnsureDisk(handle);
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out FileAttributeTag attributes, (uint)Marshal.SizeOf<FileAttributeTag>())) ThrowIo();
        if ((attributes.FileAttributes & FileAttributeReparsePoint) != 0) throw new WorkerException("worker.path");
        if (!GetFileInformationByHandleEx(handle, FileStandardInfo, out FileStandard standard, (uint)Marshal.SizeOf<FileStandard>())) ThrowIo();
        if (!standard.Directory) throw new WorkerException("worker.path");
    }

    private static void EnsureRegularFile(SafeFileHandle handle, bool allowHardLinks = false)
    {
        EnsureDisk(handle);
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out FileAttributeTag attributes, (uint)Marshal.SizeOf<FileAttributeTag>())) ThrowIo();
        if ((attributes.FileAttributes & FileAttributeReparsePoint) != 0) throw new WorkerException("worker.output.invalid");
        if (!GetFileInformationByHandleEx(handle, FileStandardInfo, out FileStandard standard, (uint)Marshal.SizeOf<FileStandard>())) ThrowIo();
        if (standard.Directory || standard.NumberOfLinks == 0 || (!allowHardLinks && standard.NumberOfLinks != 1) || standard.DeletePending)
            throw new WorkerException("worker.output.invalid");
    }

    private static void EnsureExpectedPath(SafeFileHandle handle, string expected)
    {
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) ThrowIo();
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) ThrowIo();
        }
        if (!string.Equals(buffer.ToString(), Extended(expected), StringComparison.OrdinalIgnoreCase)) throw new WorkerException("worker.path");
    }

    private static void EnsureDisk(SafeFileHandle handle)
    {
        uint type = GetFileType(handle);
        if (type != FileTypeDisk) throw new WorkerException("worker.output.invalid");
    }

    private static string Extended(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static void ThrowIo()
    {
        int error = Marshal.GetLastWin32Error();
        throw new IOException("worker.storage", new System.ComponentModel.Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTag
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandard
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileAttributeTag information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileStandard information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileIdInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        byte[] information, uint size);
}

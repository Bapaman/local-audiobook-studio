using System.Runtime.InteropServices;
using LocalAudiobookStudio.App.Hosting;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.WorkerHost.Engine;
using Microsoft.Win32.SafeHandles;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

internal sealed class VoxCpmTrainingFilePins : IDisposable
{
    private readonly List<IDisposable> owned = [];
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    internal static string Absolute(string path) => WorkerPaths.Root(LocalAppHostDependencies.CanonicalLocalRoot(path));
    internal static string Resolve(string root, string relative)
    {
        if (relative.Length is < 1 or > 2048 || relative.Contains('\\') || relative.Any(char.IsControl)) throw Invalid();
        return WorkerPaths.Resolve(Absolute(root), relative);
    }
    internal void PinDirectory(string path)
    {
        path = Absolute(path); var chain = new Stack<string>();
        for (string? item = path; item != null; item = Path.GetDirectoryName(item)) chain.Push(item);
        while (chain.TryPop(out string? item))
        {
            if (!directories.Add(item)) continue;
            var handle = CreateFileW(Extended(item), 1, 3, 0, 3, 0x02200000, 0);
            owned.Add(handle); Check(handle, true);
        }
    }
    internal async Task<FileStream> ReadAsync(string path, long maximum, string? sha, CancellationToken token)
    {
        var stream = Open(path, 0x80000000, 1, 3, FileAccess.Read);
        if (maximum < 0 || stream.Length > maximum) throw Invalid();
        if (sha != null && await VoxCpmTrainingFileHash.Sha256Async(stream, token) != sha) throw Invalid();
        stream.Position = 0; return stream;
    }
    internal FileStream SharedRead(string path) => Open(path, 0x80000000, 3, 3, FileAccess.Read);
    internal FileStream ExclusiveMarker(string path) => Open(path, 0xc0000000, 0, 4, FileAccess.ReadWrite);
    internal void PinCreatedFile(FileStream stream) { owned.Add(stream); Check(stream.SafeFileHandle, false); }
    private FileStream Open(string path, uint access, uint share, uint disposition, FileAccess fileAccess)
    {
        path = Absolute(path); PinDirectory(Path.GetDirectoryName(path)!);
        var handle = CreateFileW(Extended(path), access, share, 0, disposition, 0x00200000, 0);
        int error = Marshal.GetLastWin32Error(); owned.Add(handle);
        if (handle.IsInvalid && share == 0 && error is 32 or 33) throw new VoxCpmTrainingExecutionException("voxcpm_training.cache.busy");
        Check(handle, false);
        var stream = new FileStream(handle, fileAccess, 1); owned.Add(stream); return stream;
    }
    internal static string Extended(string path) => "\\\\?\\" + path;
    private static void Check(SafeFileHandle handle, bool directory)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1)) throw Invalid();
    }
    private static VoxCpmTrainingExecutionException Invalid() => new("voxcpm_training.file.invalid");
    public void Dispose() { for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); owned.Clear(); directories.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}

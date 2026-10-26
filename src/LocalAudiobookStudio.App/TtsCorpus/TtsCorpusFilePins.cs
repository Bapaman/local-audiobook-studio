using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.App.Hosting;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.App.TtsCorpus;

internal sealed class TtsCorpusFilePins : IDisposable
{
    private readonly List<IDisposable> owned = [];
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);

    internal static string Absolute(string path) => WorkerPaths.Root(LocalAppHostDependencies.CanonicalLocalRoot(path));
    internal static string Resolve(string root, string relative)
    {
        if (relative.Length is < 1 or > 1024 || relative.Contains('\\') || relative.Any(char.IsControl)) throw Invalid();
        return WorkerPaths.Resolve(Absolute(root), relative);
    }

    internal void PinDirectory(string path)
    {
        path = WorkerPaths.Root(path);
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (!directories.Add(current)) continue;
            var handle = CreateFileW(Extended(current), 0x80, 3, 0, 3, 0x02200000, 0);
            owned.Add(handle); Check(handle, directory: true);
        }
    }

    internal async Task<FileStream> ReadAsync(string path, long maximumBytes, string? sha256, CancellationToken token)
    {
        try
        {
            path = Absolute(path); PinDirectory(Path.GetDirectoryName(path)!);
            var handle = CreateFileW(Extended(path), 0x80000000, 1, 0, 3, 0x00200000, 0);
            owned.Add(handle); Check(handle, directory: false);
            // Tens of thousands of handles remain pinned. Disable per-stream buffers;
            // hashing supplies its own bounded pooled read buffer.
            var input = new FileStream(handle, FileAccess.Read, 1); owned.Add(input);
            if (maximumBytes < 0 || input.Length > maximumBytes) throw Invalid();
            if (sha256 != null && Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != sha256) throw Invalid();
            input.Position = 0; return input;
        }
        catch (OperationCanceledException) { throw; }
        catch (TtsCorpusExecutionException) { throw; }
        catch { throw Invalid(); }
    }

    internal void PinCreatedFile(FileStream stream)
    {
        owned.Add(stream); Check(stream.SafeFileHandle, directory: false);
    }

    private static string Extended(string path) => "\\\\?\\" + path;
    private static void Check(SafeFileHandle handle, bool directory)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0
            || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.NumberOfLinks != 1)) throw Invalid();
    }
    private static TtsCorpusExecutionException Invalid() => new("tts_corpus.file.invalid");
    public void Dispose() { for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); owned.Clear(); directories.Clear(); }

    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string filename, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}

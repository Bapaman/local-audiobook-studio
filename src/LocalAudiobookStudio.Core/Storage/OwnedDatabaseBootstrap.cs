using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Core.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace LocalAudiobookStudio.Core.Storage;

/// <summary>Publishes a complete database without ever initializing an existing final identity.</summary>
public static class OwnedDatabaseBootstrap
{
    public const string Namespace = ".studio-bootstrap";

    // Failed attempts are deliberately not reopened or deleted. A fresh attempt is
    // bounded, but repeated interruptions can accumulate files in this namespace.
    public static async Task<IDisposable> CreateAsync(string target, string purpose, string context,
        Func<SqliteConnection, CancellationToken, Task> initialize, CancellationToken token,
        Action<string>? observer = null)
    {
        try { return await CreateOwnedAsync(target, purpose, context, initialize, token, observer); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        { throw new ProjectException("project.storage"); }
    }

    private static async Task<IDisposable> CreateOwnedAsync(string target, string purpose, string context,
        Func<SqliteConnection, CancellationToken, Task> initialize, CancellationToken token,
        Action<string>? observer = null)
    {
        token.ThrowIfCancellationRequested();
        target = Path.GetFullPath(target);
        string parent = Path.GetDirectoryName(target)!;
        ProjectPaths.CanonicalRoot(parent);
        Directory.CreateDirectory(parent);
        using var directories = new Handles();
        PinAncestors(parent, directories);
        if (File.Exists(target) || Directory.Exists(target)) throw new ProjectException("project.exists");
        string staging = Path.Combine(parent, Namespace);
        ProjectPaths.CanonicalRoot(staging);
        Directory.CreateDirectory(staging);
        PinAncestors(staging, directories);
        string attempt = Guid.NewGuid().ToString("N");
        string database = Path.Combine(staging, attempt + ".sqlite");
        string marker = Path.Combine(staging, attempt + ".json");
        Identity identity;
        using (var main = Open(database, 0xc0000000, 3, 1))
        {
            identity = Identify(main);
            Boundary("bootstrap-main-reserved");
            using var record = new FileStream(Open(marker, 0xc0000000, 1, 1), FileAccess.ReadWrite);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Purpose = purpose, Context = context,
                Target = Path.GetFileName(target), Attempt = attempt, Identity = identity });
            record.Write(bytes.AsSpan(0, bytes.Length / 2)); record.Flush(true);
            Boundary("bootstrap-marker-partial");
            record.Write(bytes.AsSpan(bytes.Length / 2)); record.Flush(true);
            Boundary("bootstrap-marker-committed");
            using var sidecars = new Handles();
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) sidecars.Add(Open(database + suffix, 0x80000000, 3, 1));
            await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Extended(database), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                await db.OpenAsync(token);
                await SqliteSidecarLifetime.ConfigureWritesAsync(db, create: true, retainSidecars: true, token);
                await initialize(db, token);
                Boundary("bootstrap-initialized");
                using var checkpoint = db.CreateCommand(); checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                using var reader = await checkpoint.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token) || reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0)
                    throw new ProjectException("project.storage");
            }
            if (new FileInfo(database + "-wal").Length != 0) throw new ProjectException("project.storage");
            RandomAccess.FlushToDisk(main);
            Boundary("bootstrap-checkpointed");
        }
        // The native SQLite connection is closed. Reacquire with DELETE access,
        // deny writers/deleters, and compare the exact identity before renaming.
        // Ancestors remain pinned across this handle transition.
        using (var publish = Open(database, 0xc0010000, 1, 3))
        {
            if (Identify(publish) != identity) throw new ProjectException("project.path");
            RandomAccess.FlushToDisk(publish);
            Boundary("bootstrap-before-publish");
            byte[] name = Encoding.Unicode.GetBytes(Extended(target));
            int nameOffset = IntPtr.Size == 8 ? 20 : 12, lengthOffset = IntPtr.Size == 8 ? 16 : 8;
            byte[] rename = new byte[nameOffset + name.Length + 2]; // ReplaceIfExists=false.
            BinaryPrimitives.WriteUInt32LittleEndian(rename.AsSpan(lengthOffset), (uint)name.Length);
            name.CopyTo(rename, nameOffset);
            if (!SetFileInformationByHandle(publish, 3, rename, (uint)rename.Length)) throw new ProjectException("project.storage");
            Boundary("bootstrap-published");
        }
        // Recheck after dropping DELETE access. The returned read pin allows
        // SQLite writes, but forbids replacement until the caller closes SQLite.
        var published = Open(target, 0x80000000, 3, 3);
        try
        {
            if (Identify(published) != identity) throw new ProjectException("project.path");
            directories.Add(published);
            return directories.Detach();
        }
        catch { published.Dispose(); throw; }

        void Boundary(string boundary)
        {
            try { observer?.Invoke(boundary); } catch (Exception) { }
            token.ThrowIfCancellationRequested();
        }
    }

    private static string Extended(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path : "\\\\?\\" + path;
    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition)
    {
        var handle = CreateFileW(Extended(path), access, share, 0, disposition, 0x00200000, 0);
        try { Check(handle, false); return handle; } catch { handle.Dispose(); throw; }
    }
    private static void PinAncestors(string path, Handles handles)
    {
        if (!OperatingSystem.IsWindows()) throw new ProjectException("project.platform.unsupported");
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            var handle = CreateFileW(Extended(current), 1, 3, 0, 3, 0x02200000, 0);
            handles.Add(handle); Check(handle, true);
        }
    }
    private static FileInformation Check(SafeFileHandle handle, bool directory)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw new ProjectException("project.storage");
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || !directory && info.NumberOfLinks != 1)
            throw new ProjectException("project.path");
        return info;
    }
    private static Identity Identify(SafeFileHandle handle)
    {
        var info = Check(handle, false);
        return new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }
    private sealed record Identity(uint Volume, uint High, uint Low);
    private sealed class Handles : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        public void Add(SafeFileHandle handle) => handles.Add(handle);
        public Handles Detach() { var result = new Handles(); result.handles.AddRange(handles); handles.Clear(); return result; }
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] info, uint length);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LocalAudiobookStudio.Application.Errors;

namespace LocalAudiobookStudio.Application.Projects;

// Application owns these guards independently of the desktop host and HTTP stack.
internal sealed class CatalogPaths
{
    public string Root { get; }
    public long MaximumSnapshotBytes { get; }
    public string Projects => Path.Combine(Root, "projects");
    public string Database => Path.Combine(Root, "catalog.sqlite");
    public CatalogPaths(ProjectCatalogOptions options)
    {
        if (options.MaximumSnapshotBytes <= 0) throw new StudioException("catalog.options.invalid");
        MaximumSnapshotBytes = options.MaximumSnapshotBytes;
        try { Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DataRoot)); RejectReparse(Root); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { throw new StudioException("catalog.path"); }
    }

    public static bool IsId(string id) => Guid.TryParseExact(id, "N", out var parsed) && parsed.ToString("N") == id;
    public static void ValidateId(string id) { if (!IsId(id)) throw new StudioException("project.id.invalid"); }
    public string Project(string id) { ValidateId(id); return Path.Combine(Projects, id); }
    public static string SqlitePath(string path) => OperatingSystem.IsWindows() && !path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? "\\\\?\\" + path : path;

    // Lock order is always studio.lock, then catalog.lock. Each is an OS file lease,
    // so separate instances and processes serialize without holding a SQLite transaction.
    public async Task<IDisposable> AcquireAsync(bool studio, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectReparse(Root);
        Directory.CreateDirectory(Root);
        var pins = new Pins();
        try
        {
            PinDirectories(Root, pins);
            string path = Path.Combine(Root, studio ? "studio.lock" : "catalog.lock");
            var waiting = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested(); RejectReparse(path);
                try
                {
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    pins.Add(stream); ValidateHandle(stream.SafeFileHandle, directory: false);
                    return pins;
                }
                catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
                {
                    if (waiting.Elapsed >= TimeSpan.FromSeconds(30)) throw new StudioException("catalog.busy");
                    await Task.Delay(25, token);
                }
            }
        }
        catch { pins.Dispose(); throw; }
    }

    public IDisposable PinProject(string id, bool create = false)
    {
        var pins = new Pins();
        try
        {
            string project = Project(id); RejectReparse(project);
            if (create) Directory.CreateDirectory(project);
            if (!Directory.Exists(project)) throw new StudioException("project.unavailable");
            PinDirectories(project, pins);
            PinDatabaseFiles(Path.Combine(project, "project.sqlite"), pins);
            PinFileIfPresent(Path.Combine(project, "artifact-ownership.lock"), pins, metadataOnly: true);
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    internal void CheckSnapshotSize(string id)
    {
        // Call only while the project's checked identities are pinned. SHM is a
        // volatile index, not copied database content; match guarded-copy policy.
        string database = Path.Combine(Project(id), "project.sqlite");
        long remaining = MaximumSnapshotBytes;
        foreach (string suffix in new[] { "", "-wal", "-journal" })
        {
            string path = database + suffix;
            if (!File.Exists(path)) continue;
            long length = new FileInfo(path).Length;
            if (length > remaining) throw new StudioException("catalog.snapshot.too_large");
            remaining -= length;
        }
    }

    public IDisposable PinCatalog()
    {
        var pins = new Pins();
        try { PinDirectories(Root, pins); pins.Add(OpenFile(Database, 0x80000000, 3, 4)); PinDatabaseFiles(Database, pins, reserveSidecars: true); return pins; }
        catch { pins.Dispose(); throw; }
    }

    public IDisposable PinCatalogPreMain()
    {
        var pins = new Pins();
        try
        {
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            {
                string path = Database + suffix; RejectReparse(path);
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                var handle = OpenFile(path, 0x80000000, 3, 3); pins.Add(handle);
                if (RandomAccess.GetLength(handle) != 0) throw new StudioException("catalog.schema.invalid");
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    public IDisposable? PinPreMainReservation(string id, bool create = false)
    {
        var pins = new Pins();
        try
        {
            string project = Project(id); RejectReparse(project);
            if (create) Directory.CreateDirectory(project);
            if (!Directory.Exists(project)) { pins.Dispose(); return null; }
            PinDirectories(project, pins);
            string[] allowed = ["project.sqlite-wal", "project.sqlite-shm", "project.sqlite-journal"];
            var handles = new List<SafeFileHandle>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(project))
            {
                if (Path.GetFileName(entry) == LocalAudiobookStudio.Core.Storage.OwnedDatabaseBootstrap.Namespace)
                { PinDirectories(entry, pins); continue; }
                if (!allowed.Contains(Path.GetFileName(entry), StringComparer.Ordinal)) { pins.Dispose(); return null; }
                var handle = OpenFile(entry, 0x80000000, 3, 3); pins.Add(handle); handles.Add(handle);
            }
            if (handles.Any(handle => RandomAccess.GetLength(handle) != 0)) { pins.Dispose(); return null; }
            if (create)
            {
                // Complete only the known generated namespace, retaining every
                // checked identity until Core closes. Never delete partial state.
                foreach (string name in allowed)
                {
                    var handle = OpenFile(Path.Combine(project, name), 0x80000000, 3, 4);
                    pins.Add(handle); handles.Add(handle);
                }
                if (Directory.EnumerateFileSystemEntries(project).Any(entry => Path.GetFileName(entry) != LocalAudiobookStudio.Core.Storage.OwnedDatabaseBootstrap.Namespace && !allowed.Contains(Path.GetFileName(entry), StringComparer.Ordinal)) ||
                    handles.Any(handle => RandomAccess.GetLength(handle) != 0)) { pins.Dispose(); return null; }
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    public IDisposable PinProjectForWrite(string id, bool create = false)
    {
        var pins = new Pins();
        try
        {
            string project = Project(id); RejectReparse(project);
            if (create) Directory.CreateDirectory(project);
            PinDirectories(project, pins);
            PinDatabaseFiles(Path.Combine(project, "project.sqlite"), pins, reserveSidecars: true);
            PinFileIfPresent(Path.Combine(project, "artifact-ownership.lock"), pins, metadataOnly: true);
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    public void EnsureProjects()
    {
        RejectReparse(Projects); Directory.CreateDirectory(Projects);
    }

    public static void RejectReparse(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new StudioException("catalog.path");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void PinDirectories(string path, Pins pins)
    {
        if (!OperatingSystem.IsWindows()) throw new StudioException("catalog.platform.unsupported");
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            var handle = CreateFileW(SqlitePath(current), 1, 3, 0, 3, 0x02200000, 0);
            pins.Add(handle); ValidateHandle(handle, directory: true);
        }
    }

    private static void PinDatabaseFiles(string path, Pins pins, bool reserveSidecars = false)
    {
        PinFileIfPresent(path, pins);
        // Keep every present identity until SQLite closes. Writable callers first
        // validate a guarded snapshot, then atomically reserve missing sidecars.
        // SQLite may retain these bounded files on close; the next connection can
        // reuse them and checkpoints still truncate the WAL normally.
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (reserveSidecars) pins.Add(OpenFile(path + suffix, 0x80000000, 3, 4));
            else PinFileIfPresent(path + suffix, pins);
        }
    }

    private static void PinFileIfPresent(string path, Pins pins, bool metadataOnly = false)
    {
        RejectReparse(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.Directory) != 0) throw new StudioException("catalog.path");
        // Core opens its artifact lease with FileShare.None. Its metadata guard
        // must not hold data access that would conflict with that exclusive lease.
        pins.Add(OpenFile(path, metadataOnly ? 0x80u : 0x80000000u, 3, 3));
    }

    internal static SafeFileHandle OpenFile(string path, uint access, uint share, uint disposition)
    {
        RejectReparse(path);
        var handle = CreateFileW(SqlitePath(path), access, share, 0, disposition, 0x00200000, 0);
        try { ValidateHandle(handle, directory: false); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static void ValidateHandle(SafeFileHandle handle, bool directory)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw new StudioException("catalog.storage");
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.NumberOfLinks != 1))
            throw new StudioException("catalog.path");
    }

    internal sealed class Pins : IDisposable
    {
        private readonly List<IDisposable> items = [];
        public void Add(IDisposable item) => items.Add(item);
        public void Dispose() { for (int i = items.Count - 1; i >= 0; i--) items[i].Dispose(); items.Clear(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string filename, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}

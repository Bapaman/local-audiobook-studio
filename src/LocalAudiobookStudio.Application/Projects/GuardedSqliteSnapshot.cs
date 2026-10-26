using LocalAudiobookStudio.Application.Errors;

namespace LocalAudiobookStudio.Application.Projects;

/// <summary>A stable database/WAL copy whose names stay pinned for all SQLite use.</summary>
internal sealed class GuardedSqliteSnapshot : IDisposable
{
    private static readonly string[] Suffixes = ["", "-wal", "-shm", "-journal"];
    // Opt-in test characterization only; no pathname/content or production logging.
    private static readonly AsyncLocal<Action<long>?> copyObserver = new();
    private readonly CatalogPaths.Pins directoryPins = new();
    private readonly List<FileStream> targets = [];
    private readonly string directory;
    public string Database { get; }

    private GuardedSqliteSnapshot(CatalogPaths paths)
    {
        directory = Path.Combine(paths.Root, "sqlite-probe-" + Guid.NewGuid().ToString("N"));
        Database = Path.Combine(directory, "snapshot.sqlite");
    }

    public static GuardedSqliteSnapshot Create(CatalogPaths paths, string sourceDatabase, CancellationToken token)
    {
        var snapshot = new GuardedSqliteSnapshot(paths);
        try
        {
            token.ThrowIfCancellationRequested();
            using var sourceDirectoryPins = new CatalogPaths.Pins();
            CatalogPaths.PinDirectories(Path.GetDirectoryName(sourceDatabase)!, sourceDirectoryPins);
            // Acquire the main read handle first. FileShare.Read denies existing or
            // new SQLite writers, so its committed main/WAL pair cannot change while
            // we copy. Contention is a transient storage failure, not corruption.
            var sources = new List<(string Suffix, FileStream Stream)>();
            try
            {
                foreach (string suffix in Suffixes)
                {
                    token.ThrowIfCancellationRequested();
                    string path = sourceDatabase + suffix;
                    try { File.GetAttributes(path); }
                    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                    { if (suffix.Length == 0) throw new StudioException("project.unavailable"); else continue; }
                    sources.Add((suffix, new FileStream(CatalogPaths.OpenFile(path, 0x80000000, 1, 3), FileAccess.Read)));
                }
                Directory.CreateDirectory(snapshot.directory);
                CatalogPaths.PinDirectories(snapshot.directory, snapshot.directoryPins);
                foreach (string suffix in Suffixes)
                {
                    token.ThrowIfCancellationRequested();
                    snapshot.targets.Add(new FileStream(CatalogPaths.OpenFile(snapshot.Database + suffix, 0xc0000000, 3, 1), FileAccess.ReadWrite));
                }
                long copiedBytes = 0;
                foreach (var source in sources)
                {
                    if (source.Suffix == "-shm") continue; // SQLite regenerates its index only in the copy.
                    var target = snapshot.targets[Array.IndexOf(Suffixes, source.Suffix)];
                    byte[] buffer = new byte[81920]; int read;
                    while ((read = source.Stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (read > paths.MaximumSnapshotBytes - copiedBytes) throw new StudioException("catalog.snapshot.too_large");
                        target.Write(buffer, 0, read); copiedBytes += read;
                        try { copyObserver.Value?.Invoke(copiedBytes); } catch { /* Observation cannot change copy behavior. */ }
                        token.ThrowIfCancellationRequested();
                    }
                    target.Flush();
                }
            }
            finally { foreach (var source in sources) source.Stream.Dispose(); }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    public void Dispose()
    {
        // Never recursively traverse a temporary directory. Dispose SQLite before
        // this owner; exact-file deletions cannot follow a substituted file link.
        for (int i = targets.Count - 1; i >= 0; i--) targets[i].Dispose();
        for (int i = 0; i < targets.Count; i++)
        {
            try { File.Delete(Database + Suffixes[i]); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        targets.Clear(); directoryPins.Dispose();
        try { Directory.Delete(directory, recursive: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

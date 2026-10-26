using System.Diagnostics;
using LocalAudiobookStudio.Core.Projects;
using Microsoft.Data.Sqlite;

namespace LocalAudiobookStudio.Core.Storage;

public sealed partial class ProjectDatabase
{
    internal static async Task<OwnedProjectSession> ConnectOwnedFromSnapshotAsync(string root, string snapshotDatabasePath, CancellationToken token, bool requireIdleJobs)
    {
        root = ProjectPaths.CanonicalRoot(root);
        string path = ProjectPaths.Resolve(root, "project.sqlite");
        var pins = new GenerationPathPins(); ProjectDatabase? project = null; ProjectArtifactLease? lease = null;
        try
        {
            pins.PinDirectories(root); var main = pins.PinFile(path, allowWrites: true);
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
                if (pins.PinFileIfPresent(path + suffix, allowWrites: true)) present.Add(suffix);
            // The caller keeps its validated application-owned copy pinned. A bad
            // copy must fail before ownership acquisition or source name creation.
            var preview = await ProbeDetailsAsync(root, token, snapshotDatabasePath);
            if (requireIdleJobs && preview.HasActiveJob) throw new ProjectException("job.active");
            string databasePath = OperatingSystem.IsWindows() && !path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? "\\\\?\\" + path : path;
            project = new(root, new(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString()));
            // No source SQLite connection (including a readonly WAL index open)
            // precedes the actual OS artifact owner. Contention is bounded.
            lease = await project.AcquirePreparationLeaseAsync(token);
            bool wal = ReadWalHeader(main);
            foreach (string suffix in wal ? new[] { "-wal", "-shm" } : new[] { "-journal" })
                if (!present.Contains(suffix)) pins.ReserveFile(path + suffix);
            var fresh = await ProbeDetailsAsync(root, token, retainSidecars: true, defaultTimeout: 1);
            if (fresh.State.Metadata.Id != preview.State.Metadata.Id || ReadWalHeader(main) != wal)
                throw new ProjectException("project.schema.invalid");
            if (requireIdleJobs && fresh.HasActiveJob) throw new ProjectException("job.active");
            await project.connection.OpenAsync(token);
            await SqliteSidecarLifetime.ConfigureWritesAsync(project.connection, false, true, token);
            await ProjectMigrations.InitializeAsync(project.connection, false, token);
            await project.LoadAsync(token); await project.Jobs.ValidatePersistentAsync(token);
            if (requireIdleJobs) await lease.RequireNoActiveJobsAsync(token);
            project.currentPins = pins;
            return new(project, lease);
        }
        catch (SqliteException e)
        {
            try { if (lease != null) await lease.DisposeAsync(); }
            finally { try { if (project != null) await project.DisposeAsync(); } finally { pins.Dispose(); } }
            throw ReadFailure(e);
        }
        catch
        {
            try { if (lease != null) await lease.DisposeAsync(); }
            finally { try { if (project != null) await project.DisposeAsync(); } finally { pins.Dispose(); } }
            throw;
        }
    }

    private async Task<ProjectArtifactLease> AcquirePreparationLeaseAsync(CancellationToken token)
    {
        var waiting = Stopwatch.StartNew(); TimeSpan timeout = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var handle = GenerationPathPins.OpenOwnership(ResolveOwnedPath("artifact-ownership.lock"));
                try { return new(this, new FileStream(handle, FileAccess.ReadWrite)); }
                catch { handle.Dispose(); throw; }
            }
            catch (ProjectException e) when (e.Code == "project.busy")
            {
                TimeSpan remaining = timeout - waiting.Elapsed;
                if (remaining <= TimeSpan.Zero) throw;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50), token);
            }
        }
    }
}

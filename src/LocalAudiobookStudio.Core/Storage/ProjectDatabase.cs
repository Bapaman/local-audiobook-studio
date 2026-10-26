using System.Text.Json;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Text;
using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.Core.Storage;

public sealed partial class ProjectDatabase : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private IDisposable? creationReservation;
    private IDisposable? currentPins;
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Root { get; }
    public ProjectState State { get; private set; } = null!;
    public ProjectJobStore Jobs { get; }
    private ProjectDatabase(string root, SqliteConnection connection) { Root = root; this.connection = connection; Jobs = new(this); }
    public string ResolveOwnedPath(string relative) => ProjectPaths.Resolve(Root, relative);
    internal void RetainCreationPin(IDisposable publication) => creationReservation = publication;

    internal static async Task InitializeMetadataAsync(SqliteConnection connection, ProjectMetadata metadata, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO project(id,payload,revision) VALUES($id,$payload,1)";
        command.Parameters.AddWithValue("$id", metadata.Id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(metadata));
        await command.ExecuteNonQueryAsync(token); token.ThrowIfCancellationRequested(); transaction.Commit();
    }

    public async Task<ProjectArtifactLease> AcquireArtifactLeaseAsync(TimeSpan? waitTimeout = null, CancellationToken cancellationToken = default)
    {
        TimeSpan timeout = waitTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ProjectException("project.lease.invalid");
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(ResolveOwnedPath("artifact-ownership.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new(this, stream);
            }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
            {
                TimeSpan remaining = timeout - waiting.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new ProjectException("project.busy");
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50), cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ProjectException("project.storage"); }
        }
    }

    public async Task<ProjectArtifactLease> AcquireInputMutationLeaseAsync(CancellationToken cancellationToken = default)
    {
        ProjectArtifactLease lease;
        try { lease = await AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), cancellationToken); }
        catch (ProjectException e) when (e.Code == "project.busy")
        {
            if ((await ReadCurrentSnapshotAsync(cancellationToken)).HasActiveJob) throw new ProjectException("job.active");
            throw;
        }
        try
        {
            await lease.RefreshAsync(cancellationToken);
            await lease.RequireNoActiveJobsAsync(cancellationToken);
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    internal async Task RefreshUnderLeaseAsync(ProjectArtifactLease lease, CancellationToken token)
    {
        lease.ValidateOwner(this);
        await gate.WaitAsync(token);
        try { await LoadAsync(token); }
        finally { gate.Release(); }
    }

    internal static async Task<ProjectDatabase> ConnectAsync(string root, CancellationToken token, bool retainSidecars = false, bool requireIdleJobs = false)
    {
        root = ProjectPaths.CanonicalRoot(root);
        string path = ProjectPaths.Resolve(root, "project.sqlite");
        if (!File.Exists(path)) throw new ProjectException("project.missing");
        var preview = await ProbeDetailsAsync(root, token, retainSidecars: retainSidecars);
        if (requireIdleJobs && preview.HasActiveJob) throw new ProjectException("job.active");
        // SQLite's Windows VFS needs the extended path form even though managed file APIs accept long paths.
        string databasePath = OperatingSystem.IsWindows() && !path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? "\\\\?\\" + path : path;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = requireIdleJobs ? 1 : 30 }.ToString());
        try
        {
            await connection.OpenAsync(token);
            var project = new ProjectDatabase(root, connection);
            // The validated wrapper exists before any journal or schema changes. Ownership does not require State.
            if (requireIdleJobs) await project.LoadAsync(token);
            await using var lease = requireIdleJobs
                ? await project.AcquireInputMutationLeaseAsync(token)
                : await project.AcquireArtifactLeaseAsync(cancellationToken: token);
            await SqliteSidecarLifetime.ConfigureWritesAsync(connection, false, retainSidecars, token);
            await ProjectMigrations.InitializeAsync(connection, false, token);
            if (requireIdleJobs) await project.LoadAsync(token);
            return project;
        }
        catch (Exception e) when (e is SqliteException or IOException) { await connection.DisposeAsync(); throw new ProjectException("project.storage"); }
        catch { await connection.DisposeAsync(); throw; }
    }

    internal static async Task<ProjectDatabase> ConnectCurrentAsync(string root, CancellationToken token, bool retainSidecars, bool requireJobSchema = false, bool requirePreparedSidecars = false)
    {
        root = ProjectPaths.CanonicalRoot(root);
        string path = ProjectPaths.Resolve(root, "project.sqlite");
        var pins = new GenerationPathPins(); SqliteConnection? connection = null;
        try
        {
            pins.PinDirectories(root); var main = pins.PinFile(path, allowWrites: true);
            var pinned = new HashSet<string>(StringComparer.Ordinal);
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
                if (pins.PinFileIfPresent(path + suffix, allowWrites: true)) pinned.Add(suffix);
            bool wal = ReadWalHeader(main);
            // Even a read-only WAL connection can create its index. Do not let it
            // create unguarded names or modify a rejected source during validation.
            if (wal && (!pinned.Contains("-wal") || !pinned.Contains("-shm"))) throw new ProjectException("project.storage");
            // Generic Core callers retain their previous validated rollback
            // reservation policy. Application reads must NEVER create a missing
            // name: the explicit owned producer prepares it before current-open.
            if (!wal && requirePreparedSidecars && !pinned.Contains("-journal")) throw new ProjectException("project.storage");
            await ProbeDetailsAsync(root, token, retainSidecars: true, defaultTimeout: 1, requireJobSchema: requireJobSchema);
            if (ReadWalHeader(main) != wal) throw new ProjectException("project.storage");
            if (!wal && !pinned.Contains("-journal")) pins.ReserveFile(path + "-journal");
            string databasePath = OperatingSystem.IsWindows() && !path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? "\\\\?\\" + path : path;
            connection = new(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
            await connection.OpenAsync(token);
            // Retention is mandatory while identities are pinned, including when
            // a caller opts out of ordinary-open retention. Never convert WAL.
            SqliteSidecarLifetime.Retain(connection);
            using (var mode = connection.CreateCommand())
            {
                mode.CommandText = "PRAGMA journal_mode";
                bool actualWal = Equals(await mode.ExecuteScalarAsync(token), "wal");
                if (actualWal != wal) throw new ProjectException("project.storage");
                if (!wal)
                {
                    mode.CommandText = "PRAGMA journal_mode=PERSIST";
                    if (!Equals(await mode.ExecuteScalarAsync(token), "persist")) throw new ProjectException("project.storage");
                }
            }
            // PERSIST only selects this rollback connection's commit behavior;
            // the persistent main header and schema are never reconfigured here.
            using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL"; await command.ExecuteNonQueryAsync(token); }
            await ProjectMigrations.ValidateReadOnlyAsync(connection, token);
            var project = new ProjectDatabase(root, connection);
            await project.LoadAsync(token); await project.Jobs.ValidatePersistentAsync(token);
            project.currentPins = pins; return project;
        }
        catch (SqliteException e) { if (connection != null) await connection.DisposeAsync(); pins.Dispose(); throw ReadFailure(e); }
        catch { if (connection != null) await connection.DisposeAsync(); pins.Dispose(); throw; }
    }

    private static bool ReadWalHeader(Microsoft.Win32.SafeHandles.SafeFileHandle main)
    {
        Span<byte> modes = stackalloc byte[2];
        if (RandomAccess.Read(main, modes, 18) != 2) throw new ProjectException("project.schema.invalid");
        if (modes[0] == 2 && modes[1] == 2) return true;
        if (modes[0] == 1 && modes[1] == 1) return false;
        throw new ProjectException("project.schema.invalid");
    }

    internal static async Task<ProjectState> ProbeAsync(string root, CancellationToken token, string? snapshotDatabasePath = null, bool retainSidecars = false)
        => (await ProbeDetailsAsync(root, token, snapshotDatabasePath, retainSidecars)).State;

    internal static async Task<ProjectReadSnapshot> ProbeDetailsAsync(string root, CancellationToken token, string? snapshotDatabasePath = null, bool retainSidecars = false, int defaultTimeout = 30, bool requireJobSchema = false)
    {
        root = ProjectPaths.CanonicalRoot(root);
        string path = snapshotDatabasePath ?? ProjectPaths.Resolve(root, "project.sqlite");
        try { File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new ProjectException("project.missing"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ProjectException("project.storage"); }
        string databasePath = OperatingSystem.IsWindows() && !path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? "\\\\?\\" + path : path;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = defaultTimeout }.ToString());
        try
        {
            await connection.OpenAsync(token);
            if (retainSidecars || snapshotDatabasePath != null) SqliteSidecarLifetime.Retain(connection);
            await ProjectMigrations.ValidateReadOnlyAsync(connection, token);
            if (requireJobSchema)
            {
                using var schema = connection.CreateCommand(); schema.CommandText = "PRAGMA user_version";
                if ((long)(await schema.ExecuteScalarAsync(token))! < 3) throw new ProjectException("project.storage");
            }
            // The wrapper has no ownership of this connection here; the outer await using closes it.
            var project = new ProjectDatabase(root, connection);
            try
            {
                await project.LoadAsync(token);
                await project.Jobs.ValidatePersistentAsync(token);
                using var query = connection.CreateCommand(); query.CommandText = "SELECT relative_path,final_relative_path FROM staging";
                using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) project.ValidatePendingArtifact(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
                return await project.ReadCurrentSnapshotAsync(token);
            }
            finally { project.gate.Dispose(); }
        }
        catch (SqliteException e) { throw ReadFailure(e); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ProjectException("project.storage"); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or ArgumentException)
        { throw new ProjectException("project.schema.invalid"); }
    }

    public async Task SaveAsync(ProjectState state, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireArtifactLeaseAsync(cancellationToken: cancellationToken);
        await SaveUnderLeaseAsync(lease, state, cancellationToken);
    }

    internal async Task SaveUnderLeaseAsync(ProjectArtifactLease lease, ProjectState state, CancellationToken cancellationToken)
    {
        lease.ValidateOwner(this);
        Validate(state);
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var transaction = connection.BeginTransaction();
            var existingVoices = await ReadAsync<VoiceSnapshot>("voice", "id", transaction, cancellationToken);
            if (existingVoices.Count > 0 && existingVoices[0] != state.Voice) throw new ProjectException("voice.exists");
            var existingRecordings = await ReadAsync<VoiceRecordingSnapshot>("voice_recording", "id", transaction, cancellationToken);
            if (existingRecordings.Count > 0 && existingRecordings[0] != state.VoiceRecording) throw new ProjectException("voice.recording.exists");
            var existingChapters = await ReadAsync<ChapterRecord>("chapters", "ordinal", transaction, cancellationToken);
            if (existingChapters.Count > 0 && (existingChapters.Count != state.Chapters.Count || existingChapters.Any(existing =>
                state.Chapters.FirstOrDefault(x => x.Id == existing.Id) is not { } candidate || candidate with { SpokenText = existing.SpokenText } != existing)))
                throw new ProjectException("text.exists");
            var existingSegments = await ReadAsync<SegmentRecord>("segments", "ordinal", transaction, cancellationToken);
            var candidateSegments = state.Segments.ToLookup(x => x.Id, StringComparer.Ordinal);
            foreach (var existing in existingSegments)
            {
                var candidate = candidateSegments[existing.Id].FirstOrDefault();
                if (candidate == null || candidate.ChapterId != existing.ChapterId || candidate.Order != existing.Order ||
                    candidate.OriginalText != existing.OriginalText || candidate.NormalizedText != existing.NormalizedText ||
                    candidate.PreviousSentenceContextHash != existing.PreviousSentenceContextHash || candidate.NextSentenceContextHash != existing.NextSentenceContextHash ||
                    candidate.BoundaryAfter != existing.BoundaryAfter || candidate.Versions.TextFrontend != existing.Versions.TextFrontend || candidate.Versions.Segmenter != existing.Versions.Segmenter)
                    throw new ProjectException("text.exists");
            }
            using var revisionQuery = connection.CreateCommand(); revisionQuery.Transaction = transaction; revisionQuery.CommandText = "SELECT revision FROM project";
            long revision = (long?)await revisionQuery.ExecuteScalarAsync(cancellationToken) ?? 0;
            if (state.Revision != revision) throw new ProjectException("project.conflict");
            await ExecuteAsync("DELETE FROM attempts; DELETE FROM segments; DELETE FROM chapters; DELETE FROM voice; DELETE FROM project;", transaction, cancellationToken);
            await InsertAsync("project", state.Metadata.Id, state.Metadata, transaction, cancellationToken);
            using var revisionUpdate = connection.CreateCommand(); revisionUpdate.Transaction = transaction; revisionUpdate.CommandText = "UPDATE project SET revision=$revision";
            revisionUpdate.Parameters.AddWithValue("$revision", revision + 1); await revisionUpdate.ExecuteNonQueryAsync(cancellationToken);
            if (state.Voice != null) await InsertAsync("voice", state.Voice.Id, state.Voice, transaction, cancellationToken);
            if (state.VoiceRecording != null && existingRecordings.Count == 0) await InsertAsync("voice_recording", state.VoiceRecording.Id, state.VoiceRecording, transaction, cancellationToken);
            foreach (var chapter in state.Chapters) await InsertAsync("chapters", chapter.Id, chapter, transaction, cancellationToken, chapter.Order);
            foreach (var segment in state.Segments) await InsertAsync("segments", segment.Id, segment, transaction, cancellationToken, segment.Order, segment.ChapterId);
            foreach (var attempt in state.Attempts)
            {
                await InsertAsync("attempts", attempt.Id, attempt, transaction, cancellationToken, attempt.Order, attempt.SegmentId);
                if (attempt.Status == "running" && attempt.StagingRelativePath != null)
                {
                    if (!attempt.StagingRelativePath.EndsWith(".part", StringComparison.Ordinal)) throw new ProjectException("project.path");
                    using var staging = connection.CreateCommand(); staging.Transaction = transaction;
                    staging.CommandText = "INSERT OR IGNORE INTO staging(relative_path) VALUES ($path)"; staging.Parameters.AddWithValue("$path", attempt.StagingRelativePath);
                    await staging.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            await RemoveAcceptedRegistrationsAsync(state, transaction, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
            State = state with { Revision = revision + 1, Chapters = state.Chapters.OrderBy(x => x.Order).ToArray(), Segments = state.Segments.OrderBy(x => x.Order).ToArray(), Attempts = state.Attempts.OrderBy(x => x.SegmentId).ThenBy(x => x.Order).ToArray() };
        }
        catch (SqliteException) { throw new ProjectException("project.storage"); }
        finally { gate.Release(); }
    }

    internal async Task LoadAsync(CancellationToken token)
    {
        try
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var metadata = await ReadAsync<ProjectMetadata>("project", "id", transaction, token);
            if (metadata.Count != 1) throw new ProjectException("project.schema.invalid");
            State = new(metadata[0], (await ReadAsync<VoiceSnapshot>("voice", "id", transaction, token)).SingleOrDefault(),
                await ReadAsync<ChapterRecord>("chapters", "ordinal", transaction, token), await ReadAsync<SegmentRecord>("segments", "ordinal", transaction, token), await ReadAsync<AttemptRecord>("attempts", "segment_id, ordinal", transaction, token));
            using var revision = connection.CreateCommand(); revision.Transaction = transaction; revision.CommandText = "SELECT revision FROM project";
            State = State with { Revision = (long)(await revision.ExecuteScalarAsync(token))! };
            using var schema = connection.CreateCommand(); schema.Transaction = transaction; schema.CommandText = "PRAGMA user_version";
            if ((long)(await schema.ExecuteScalarAsync(token))! >= 2)
                State = State with { VoiceRecording = (await ReadAsync<VoiceRecordingSnapshot>("voice_recording", "id", transaction, token)).SingleOrDefault() };
            Validate(State); transaction.Commit();
        }
        catch (SqliteException e) { throw ReadFailure(e); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or ArgumentException) { throw new ProjectException("project.schema.invalid"); }
    }

    // Contention, access and I/O failures do not prove that committed state is invalid.
    private static ProjectException ReadFailure(SqliteException error) => new(error.SqliteErrorCode switch
    {
        1 or 11 or 26 => "project.schema.invalid",
        5 or 6 => "project.busy",
        _ => "project.storage"
    });

    public async Task RegisterStagingAsync(string relativePath, CancellationToken cancellationToken = default)
        => await RegisterPendingArtifactAsync(relativePath, null, cancellationToken);

    /// <summary>Durably owns staging and its possible final publication until acceptance commits.</summary>
    public async Task RegisterPendingArtifactAsync(string relativePath, string? finalRelativePath, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireArtifactLeaseAsync(cancellationToken: cancellationToken);
        await RegisterUnderLeaseAsync(lease, relativePath, finalRelativePath, cancellationToken);
    }

    internal async Task RegisterUnderLeaseAsync(ProjectArtifactLease lease, string relativePath, string? finalRelativePath, CancellationToken cancellationToken)
    {
        lease.ValidateOwner(this);
        relativePath = NormalizeOwnedRelativePath(relativePath);
        if (finalRelativePath != null) finalRelativePath = NormalizeOwnedRelativePath(finalRelativePath);
        ValidatePendingArtifact(relativePath, finalRelativePath);
        if (!relativePath.EndsWith(".part", StringComparison.Ordinal)) throw new ProjectException("project.path");
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO staging(relative_path,final_relative_path) VALUES ($path,$final) ON CONFLICT(relative_path) DO NOTHING";
            command.Parameters.AddWithValue("$path", relativePath); command.Parameters.AddWithValue("$final", (object?)finalRelativePath ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken); transaction.Commit();
        }
        catch (SqliteException) { throw new ProjectException("project.storage"); }
        finally { gate.Release(); }
    }

    public async Task RemoveStagingRegistrationAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireArtifactLeaseAsync(cancellationToken: cancellationToken);
        await RemoveRegistrationUnderLeaseAsync(lease, relativePath, cancellationToken);
    }

    internal async Task RemoveRegistrationUnderLeaseAsync(ProjectArtifactLease lease, string relativePath, CancellationToken cancellationToken)
    {
        lease.ValidateOwner(this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM staging WHERE relative_path=$path"; command.Parameters.AddWithValue("$path", relativePath);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException) { throw new ProjectException("project.storage"); }
        finally { gate.Release(); }
    }

    internal async Task CleanStagingAsync(ProjectArtifactLease lease, CancellationToken token)
    {
        lease.ValidateOwner(this);
        // Recovery refreshed committed state while holding the same OS lease used by all saves/publications.
        var (paths, acceptedJobs) = await JobTransactionAsync(false, async (db, transaction) =>
        {
            var registered = new List<(string Part, string? Final)>();
            using (var command = ProjectJobStore.Command(db, transaction, "SELECT relative_path,final_relative_path FROM staging"))
            using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) registered.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (await ProjectJobStore.HasSchemaAsync(db, transaction, token))
                foreach (var job in await Jobs.JobsAsync(db, transaction, false, token))
                    foreach (var item in await Jobs.ItemsAsync(db, transaction, job, token))
                        if (item.Artifact != null) accepted.Add(ResolveOwnedPath(item.Artifact.RelativePath));
            return (registered, accepted);
        }, token);
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            ValidatePendingArtifact(path.Part, path.Final);
            try
            {
                if (!IsAcceptedPath(State, path.Part) && !acceptedJobs.Contains(ResolveOwnedPath(path.Part))) DeleteOwnedIfPresent(path.Part);
                if (path.Final != null && !IsAcceptedPath(State, path.Final) && !acceptedJobs.Contains(ResolveOwnedPath(path.Final))) DeleteOwnedIfPresent(path.Final);
            }
            catch (DirectoryNotFoundException) { /* A reservation may precede directory creation. */ }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ProjectException("project.storage"); }
            await RemoveRegistrationUnderLeaseAsync(lease, path.Part, token);
        }
    }

    private string NormalizeOwnedRelativePath(string path) => Path.GetRelativePath(Root, ResolveOwnedPath(path)).Replace('\\', '/');

    private void ValidatePendingArtifact(string part, string? final)
    {
        ResolveOwnedPath(part);
        if (!part.EndsWith(".part", StringComparison.Ordinal)) throw new ProjectException("project.path");
        if (final == null) return;
        ResolveOwnedPath(final);
        if (!final.Replace('\\', '/').Contains('/') || !string.Equals(part.Replace('\\', '/'), final.Replace('\\', '/') + ".part", StringComparison.OrdinalIgnoreCase)) throw new ProjectException("project.path");
    }

    private async Task RemoveAcceptedRegistrationsAsync(ProjectState state, SqliteTransaction transaction, CancellationToken token)
    {
        var accepted = new List<string>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT relative_path,final_relative_path FROM staging";
            using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                string part = reader.GetString(0);
                string? final = reader.IsDBNull(1) ? null : reader.GetString(1);
                bool partAccepted = IsAcceptedPath(state, part), finalAccepted = final != null && IsAcceptedPath(state, final);
                // Keep ownership until every unaccepted counterpart is gone. Recovery needs this row to find orphans.
                if ((partAccepted || finalAccepted) && (partAccepted || IsProvenAbsent(part)) && (final == null || finalAccepted || IsProvenAbsent(final))) accepted.Add(part);
            }
        }
        foreach (string path in accepted)
        {
            using var remove = connection.CreateCommand(); remove.Transaction = transaction; remove.CommandText = "DELETE FROM staging WHERE relative_path=$path"; remove.Parameters.AddWithValue("$path", path);
            await remove.ExecuteNonQueryAsync(token);
        }
    }

    private bool IsProvenAbsent(string relativePath)
    {
        try { File.GetAttributes(ResolveOwnedPath(relativePath)); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ProjectException("project.storage"); }
    }

    private bool IsAcceptedPath(ProjectState state, string path)
    {
        string full = ResolveOwnedPath(path);
        if (state.Voice != null && string.Equals(full, ResolveOwnedPath(state.Voice.WavRelativePath), StringComparison.OrdinalIgnoreCase)) return true;
        if (state.VoiceRecording != null && string.Equals(full, ResolveOwnedPath(state.VoiceRecording.WavRelativePath), StringComparison.OrdinalIgnoreCase)) return true;
        return state.Segments.Any(x => x.Status == "done" && x.OutputRelativePath != null && string.Equals(full, ResolveOwnedPath(x.OutputRelativePath), StringComparison.OrdinalIgnoreCase));
    }

    private void DeleteOwnedIfPresent(string path)
    {
        try { File.Delete(ResolveOwnedPath(path)); }
        catch (DirectoryNotFoundException) { /* Registration can precede directory creation. */ }
    }

    private async Task InsertAsync<T>(string table, string id, T payload, SqliteTransaction transaction, CancellationToken token, int? order = null, string? parent = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        string parentColumn = table == "segments" ? "chapter_id" : "segment_id";
        command.CommandText = $"INSERT INTO {table} (id,payload{(order.HasValue ? ",ordinal" : "")}{(parent != null ? "," + parentColumn : "")}) VALUES ($id,$payload{(order.HasValue ? ",$order" : "")}{(parent != null ? ",$parent" : "")})";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload));
        if (order.HasValue) command.Parameters.AddWithValue("$order", order.Value);
        if (parent != null) command.Parameters.AddWithValue("$parent", parent);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<List<T>> ReadAsync<T>(string table, string order, SqliteTransaction transaction, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = $"SELECT id,payload FROM {table} ORDER BY {order}";
        using var reader = await command.ExecuteReaderAsync(token); var result = new List<T>();
        while (await reader.ReadAsync(token))
        {
            T value = JsonSerializer.Deserialize<T>(reader.GetString(1)) ?? throw new ProjectException("project.schema.invalid");
            string? id = value switch { ProjectMetadata item => item.Id, VoiceSnapshot item => item.Id, VoiceRecordingSnapshot item => item.Id, ChapterRecord item => item.Id, SegmentRecord item => item.Id, AttemptRecord item => item.Id, _ => null };
            if (id == null || id != reader.GetString(0)) throw new ProjectException("project.schema.invalid");
            result.Add(value);
        }
        return result;
    }

    private async Task ExecuteAsync(string sql, SqliteTransaction transaction, CancellationToken token)
    { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; await command.ExecuteNonQueryAsync(token); }

    private void Validate(ProjectState state)
    {
        if (state.Metadata == null || string.IsNullOrEmpty(state.Metadata.Id) || state.Metadata.Title == null || state.Metadata.Versions == null || state.Metadata.Parameters == null || state.Revision < 0)
            throw new ProjectException("project.schema.invalid");
        if (state.Metadata.CreatedUtc.Offset != TimeSpan.Zero || state.Metadata.UpdatedUtc.Offset != TimeSpan.Zero) throw new ProjectException("project.timestamp");
        if (state.Voice != null) { ResolveOwnedPath(state.Voice.WavRelativePath); CheckUtc(state.Voice.ImportedUtc); }
        if (state.VoiceRecording is { } recording)
        {
            ResolveOwnedPath(recording.WavRelativePath); CheckUtc(recording.ImportedUtc);
            if (!Guid.TryParseExact(recording.Id, "N", out var id) || id.ToString("N") != recording.Id || recording.WavRelativePath != $"voice-recording/{recording.Id}.wav" ||
                recording.Sha256 == null || recording.Sha256.Length != 64 || recording.Sha256.Any(x => !(x is >= '0' and <= '9' or >= 'A' and <= 'F')) ||
                recording.SampleCount <= 0 || recording.SampleCount > 57600000 || recording.ByteLength < 44 + recording.SampleCount * 2 || recording.ByteLength > 1L << 30 || !recording.RightsConfirmed)
                throw new ProjectException("project.schema.invalid");
        }
        if (state.Voice is { } voice && (voice.StartMilliseconds.HasValue || voice.EndMilliseconds.HasValue))
        {
            if (state.VoiceRecording == null || voice.StartMilliseconds is not { } start || voice.EndMilliseconds is not { } end || start < 0 ||
                end <= start || end - start is < 10000 or > 20000 || end > state.VoiceRecording.SampleCount / 48)
                throw new ProjectException("project.schema.invalid");
        }
        foreach (var chapter in state.Chapters)
        {
            CheckUtc(chapter.CreatedUtc);
            if (chapter.Provenance is { } provenance)
            {
                if (!Enum.IsDefined(provenance.SourceKind) || provenance.DisplayName == null || provenance.DisplayName.Length is 0 or > 120 ||
                    provenance.DisplayName.Trim() != provenance.DisplayName || provenance.DisplayName.Any(char.IsControl) || provenance.DisplayName.IndexOfAny(['/', '\\']) >= 0 ||
                    provenance.SourceEncoding is not ("utf-8" or "utf-16le" or "utf-16be") || !IsTextDigest(provenance.SourceHash) || provenance.OriginalByteCount <= 0 ||
                    string.IsNullOrWhiteSpace(provenance.FrontendVersion) || string.IsNullOrWhiteSpace(provenance.SegmenterVersion) || chapter.OriginalText == null ||
                    (provenance.SourceKind == TextSourceKind.File && provenance.OriginalByteCount > TextImportService.MaximumFileBytes) ||
                    (provenance.SourceKind == TextSourceKind.Pasted && (provenance.SourceEncoding != "utf-8" || chapter.OriginalText.Length > TextImportService.MaximumPastedCodeUnits)))
                    throw new ProjectException("project.schema.invalid");
            }
        }
        foreach (var segment in state.Segments)
        {
            if (segment.Versions == null || (segment.BoundaryAfter is { } boundary && !Enum.IsDefined(boundary)) ||
                (segment.PreviousSentenceContextHash is { } previous && !IsTextDigest(previous)) || (segment.NextSentenceContextHash is { } next && !IsTextDigest(next)))
                throw new ProjectException("project.schema.invalid");
            if (segment.OutputRelativePath != null) ResolveOwnedPath(segment.OutputRelativePath); CheckUtc(segment.CreatedUtc); CheckUtc(segment.UpdatedUtc);
        }
        foreach (var attempt in state.Attempts) { if (attempt.StagingRelativePath != null) ResolveOwnedPath(attempt.StagingRelativePath); CheckUtc(attempt.StartedUtc); if (attempt.CompletedUtc.HasValue) CheckUtc(attempt.CompletedUtc.Value); }
    }
    private static bool IsTextDigest(string? value) => value is { Length: 64 } && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void CheckUtc(DateTimeOffset value) { if (value.Offset != TimeSpan.Zero) throw new ProjectException("project.timestamp"); }
    // One short gate/transaction for snapshots, jobs and future fenced units. No SQL transaction spans inference.
    internal async Task<T> JobTransactionAsync<T>(bool write, Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken token, Action<T>? committed = null)
    {
        await gate.WaitAsync(token);
        try
        {
            using var transaction = connection.BeginTransaction(deferred: !write);
            T result = await action(connection, transaction); token.ThrowIfCancellationRequested(); transaction.Commit(); committed?.Invoke(result); return result;
        }
        catch (SqliteException e) { throw write ? new ProjectException("project.storage") : ReadFailure(e); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or ArgumentException or OverflowException) { throw new ProjectException("project.schema.invalid"); }
        finally { gate.Release(); }
    }
    /// <summary>One committed state/output-ID baseline. Does not recover or overwrite cached CAS state.</summary>
    public Task<ProjectReadSnapshot> ReadCurrentSnapshotAsync(CancellationToken token = default) => JobTransactionAsync(false, async (db, transaction) =>
    {
        var state = await ReadJobProjectAsync(transaction, token);
        var ids = await Jobs.ReadCurrentOutputArtifactIdsAsync(db, transaction, state, token);
        return new ProjectReadSnapshot(state, ids, await Jobs.HasActiveJobAsync(db, transaction, token));
    }, token);

    internal async Task<ProjectState> ReadJobProjectAsync(SqliteTransaction transaction, CancellationToken token)
    {
        var metadata = await ReadAsync<ProjectMetadata>("project", "id", transaction, token);
        if (metadata.Count != 1) throw new ProjectException("project.schema.invalid");
        using var revision = connection.CreateCommand(); revision.Transaction = transaction; revision.CommandText = "SELECT revision FROM project";
        using var schema = connection.CreateCommand(); schema.Transaction = transaction; schema.CommandText = "PRAGMA user_version";
        var recording = (long)(await schema.ExecuteScalarAsync(token))! >= 2 ? (await ReadAsync<VoiceRecordingSnapshot>("voice_recording", "id", transaction, token)).SingleOrDefault() : null;
        var state = new ProjectState(metadata[0], (await ReadAsync<VoiceSnapshot>("voice", "id", transaction, token)).SingleOrDefault(), await ReadAsync<ChapterRecord>("chapters", "ordinal", transaction, token), await ReadAsync<SegmentRecord>("segments", "ordinal", transaction, token), await ReadAsync<AttemptRecord>("attempts", "segment_id, ordinal", transaction, token), (long)(await revision.ExecuteScalarAsync(token))!, recording);
        Validate(state); return state;
    }
    public async ValueTask DisposeAsync() { try { await connection.DisposeAsync(); } finally { currentPins?.Dispose(); creationReservation?.Dispose(); gate.Dispose(); } }
}

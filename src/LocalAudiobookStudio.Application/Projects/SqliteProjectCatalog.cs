using Microsoft.Data.Sqlite;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Application.Errors;

namespace LocalAudiobookStudio.Application.Projects;

/// <summary>Catalog bookkeeping only; never reads or opens a Core project database.</summary>
public sealed class SqliteProjectCatalog(ProjectCatalogOptions options) : IProjectCatalog
{
    private readonly CatalogPaths paths = new(options);
    // Per-instance diagnostic boundary observer; absent in normal callers.
    private Action<string>? lifecycleObserver = null;

    public Task<IReadOnlyList<CatalogProject>> ListAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync<IReadOnlyList<CatalogProject>>(async (db, token) =>
        {
            using var command = db.CreateCommand(); command.CommandText = "SELECT id,last_opened_ticks FROM projects WHERE quarantined=0 ORDER BY last_opened_ticks DESC,id";
            using var reader = await command.ExecuteReaderAsync(token); var result = new List<CatalogProject>();
            while (await reader.ReadAsync(token)) result.Add(ReadProject(reader));
            return result;
        }, cancellationToken);

    public Task<CatalogProject?> FindAsync(string projectId, CancellationToken cancellationToken = default)
    {
        CatalogPaths.ValidateId(projectId);
        return ExecuteAsync<CatalogProject?>(async (db, token) =>
        {
            using var command = db.CreateCommand(); command.CommandText = "SELECT id,last_opened_ticks FROM projects WHERE id=$id AND quarantined=0"; command.Parameters.AddWithValue("$id", projectId);
            using var reader = await command.ExecuteReaderAsync(token); return await reader.ReadAsync(token) ? ReadProject(reader) : null;
        }, cancellationToken);
    }

    public Task InsertAsync(CatalogProject project, CancellationToken cancellationToken = default) => FinalizeAsync(project, null, cancellationToken);

    public Task<IdempotencyReceipt?> FindReceiptAsync(string idempotencyKey, CancellationToken cancellationToken = default)
        => ExecuteAsync<IdempotencyReceipt?>(async (db, token) =>
        {
            using var command = db.CreateCommand(); command.CommandText = "SELECT key_hash,fingerprint,project_id,completed FROM receipts WHERE key_hash=$key"; command.Parameters.AddWithValue("$key", IdempotencyReceipt.Hash(idempotencyKey));
            using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            var receipt = new IdempotencyReceipt(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)); ValidateReceipt(receipt); return receipt;
        }, cancellationToken);

    public Task StoreReceiptAsync(IdempotencyReceipt receipt, CancellationToken cancellationToken = default)
    {
        ValidateReceipt(receipt);
        return ExecuteAsync(async (db, token) =>
        {
            using var transaction = db.BeginTransaction();
            await WriteReceiptAsync(db, transaction, receipt, token); transaction.Commit(); return true;
        }, cancellationToken);
    }

    public Task FinalizeAsync(CatalogProject project, IdempotencyReceipt? receipt, CancellationToken cancellationToken = default)
    {
        CatalogPaths.ValidateId(project.Id); CheckUtc(project.LastOpenedUtc);
        if (receipt != null) { ValidateReceipt(receipt); if (receipt.ProjectId != project.Id || !receipt.Completed) throw new StudioException("catalog.receipt.invalid"); }
        return ExecuteAsync(async (db, token) =>
        {
            using var transaction = db.BeginTransaction();
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO projects(id,last_opened_ticks,quarantined) VALUES($id,$opened,0) ON CONFLICT(id) DO UPDATE SET last_opened_ticks=MAX(last_opened_ticks,excluded.last_opened_ticks) WHERE quarantined=0";
            command.Parameters.AddWithValue("$id", project.Id); command.Parameters.AddWithValue("$opened", project.LastOpenedUtc.UtcTicks);
            if (await command.ExecuteNonQueryAsync(token) == 0) throw new StudioException("project.unavailable");
            if (receipt != null) await WriteReceiptAsync(db, transaction, receipt, token);
            transaction.Commit(); return true;
        }, cancellationToken);
    }

    public Task TouchOpenedAsync(string projectId, DateTimeOffset openedUtc, CancellationToken cancellationToken = default)
    {
        CatalogPaths.ValidateId(projectId); CheckUtc(openedUtc);
        return ExecuteAsync(async (db, token) =>
        {
            using var transaction = db.BeginTransaction();
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE projects SET last_opened_ticks=MAX(last_opened_ticks,$opened) WHERE id=$id AND quarantined=0";
            command.Parameters.AddWithValue("$id", projectId); command.Parameters.AddWithValue("$opened", openedUtc.UtcTicks);
            if (await command.ExecuteNonQueryAsync(token) != 1) throw new StudioException("project.unavailable");
            transaction.Commit(); return true;
        }, cancellationToken);
    }

    public Task QuarantineAsync(string projectId, CancellationToken cancellationToken = default)
    {
        CatalogPaths.ValidateId(projectId);
        return ExecuteAsync(async (db, token) =>
        {
            using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO projects(id,last_opened_ticks,quarantined) VALUES($id,0,1) ON CONFLICT(id) DO UPDATE SET quarantined=1";
            command.Parameters.AddWithValue("$id", projectId); await command.ExecuteNonQueryAsync(token); transaction.Commit(); return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CatalogCandidate>> ReconcileAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync<IReadOnlyList<CatalogCandidate>>(async (db, token) =>
        {
            var candidates = new HashSet<string>(StringComparer.Ordinal); var quarantined = new HashSet<string>(StringComparer.Ordinal);
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT id,quarantined FROM projects"; using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    string id = reader.GetString(0);
                    if (!CatalogPaths.IsId(id)) continue;
                    if (reader.GetBoolean(1)) quarantined.Add(id); else candidates.Add(id);
                }
            }
            paths.EnsureProjects();
            // Only exact ID names become candidates; no persisted root or title is ever a path.
            foreach (string directory in Directory.EnumerateDirectories(paths.Projects))
            {
                token.ThrowIfCancellationRequested(); string id = Path.GetFileName(directory);
                if (CatalogPaths.IsId(id)) candidates.Add(id);
            }
            candidates.ExceptWith(quarantined);
            var pending = new HashSet<string>(StringComparer.Ordinal);
            using (var query = db.CreateCommand())
            {
                query.CommandText = "SELECT project_id FROM receipts WHERE completed=0"; using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) pending.Add(reader.GetString(0));
            }
            return candidates.Order(StringComparer.Ordinal).Select(id => new CatalogCandidate(id, pending.Contains(id))).ToArray();
        }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken token)
    {
        try
        {
            using var lease = await paths.AcquireAsync(studio: false, token);
            using var publication = new CatalogPaths.Pins();
            ObserveLifecycle("before-main-reservation");
            if (!File.Exists(paths.Database) && !Directory.Exists(paths.Database))
            {
                using var existing = paths.PinCatalogPreMain();
                publication.Add(await OwnedDatabaseBootstrap.CreateAsync(paths.Database, "catalog-v1", "catalog-v1", async (db, ct) =>
                {
                    using var transaction = db.BeginTransaction();
                    using var schema = db.CreateCommand(); schema.Transaction = transaction;
                    schema.CommandText = """
                        CREATE TABLE projects(id TEXT PRIMARY KEY, last_opened_ticks INTEGER NOT NULL, quarantined INTEGER NOT NULL CHECK(quarantined IN (0,1)));
                        CREATE TABLE receipts(key_hash TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, project_id TEXT NOT NULL UNIQUE, completed INTEGER NOT NULL CHECK(completed IN (0,1)));
                        PRAGMA user_version=1;
                        """;
                    await schema.ExecuteNonQueryAsync(ct); transaction.Commit();
                }, token, ObserveLifecycle));
            }
            using var main = CatalogPaths.OpenFile(paths.Database, 0x80000000, 3, 3);
            await ValidateReadOnlyAsync(token);
            ObserveLifecycle("catalog-validated");
            using var pins = paths.PinCatalog();
            await using var db = Connection(SqliteOpenMode.ReadWrite);
            await db.OpenAsync(token);
            ObserveLifecycle("connection-open");
            await SqliteSidecarLifetime.ConfigureWritesAsync(db, create: false, retainSidecars: true, token);
            return await action(db, token);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or LocalAudiobookStudio.Core.Projects.ProjectException)
        { throw new StudioException("catalog.storage"); }
    }

    private async Task ValidateReadOnlyAsync(CancellationToken token)
    {
        using var snapshot = GuardedSqliteSnapshot.Create(paths, paths.Database, token);
        await using var db = Connection(SqliteOpenMode.ReadOnly, snapshot.Database); await db.OpenAsync(token); SqliteSidecarLifetime.Retain(db);
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
        long version = (long)(await command.ExecuteScalarAsync(token))!;
        if (version > 1) throw new StudioException("catalog.schema.future");
        if (version != 1) throw new StudioException("catalog.schema.invalid");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type IN ('trigger','view')";
        if ((long)(await command.ExecuteScalarAsync(token))! != 0) throw new StudioException("catalog.schema.invalid");
        command.CommandText = "PRAGMA quick_check";
        if (!Equals(await command.ExecuteScalarAsync(token), "ok")) throw new StudioException("catalog.schema.invalid");
        var projects = new HashSet<string>(StringComparer.Ordinal);
        command.CommandText = "SELECT id,last_opened_ticks,quarantined FROM projects";
        using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (reader.GetValue(0) is not string id || !CatalogPaths.IsId(id) || !projects.Add(id) ||
                    reader.GetValue(1) is not long ticks || ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks ||
                    reader.GetValue(2) is not long quarantined || quarantined is not (0 or 1))
                    throw new StudioException("catalog.schema.invalid");
            }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var receiptProjects = new HashSet<string>(StringComparer.Ordinal);
        command.CommandText = "SELECT key_hash,fingerprint,project_id,completed FROM receipts";
        using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (reader.GetValue(0) is not string key || !IsHash(key) || !keys.Add(key) ||
                    reader.GetValue(1) is not string fingerprint || !IsHash(fingerprint) ||
                    reader.GetValue(2) is not string id || !CatalogPaths.IsId(id) || !receiptProjects.Add(id) ||
                    reader.GetValue(3) is not long completed || completed is not (0 or 1) || completed == 1 && !projects.Contains(id))
                    throw new StudioException("catalog.schema.invalid");
            }
    }

    private void ObserveLifecycle(string boundary)
    {
        try { lifecycleObserver?.Invoke(boundary); }
        catch (Exception) { } // Diagnostics must not change storage behavior.
    }

    private SqliteConnection Connection(SqliteOpenMode mode, string? database = null) => new(new SqliteConnectionStringBuilder { DataSource = CatalogPaths.SqlitePath(database ?? paths.Database), Mode = mode, Pooling = false }.ToString());
    private static CatalogProject ReadProject(SqliteDataReader reader)
    {
        string id = reader.GetString(0); CatalogPaths.ValidateId(id);
        return new(id, new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero));
    }
    private static void CheckUtc(DateTimeOffset timestamp) { if (timestamp.Offset != TimeSpan.Zero) throw new StudioException("catalog.timestamp"); }
    private static void ValidateReceipt(IdempotencyReceipt receipt)
    {
        CatalogPaths.ValidateId(receipt.ProjectId);
        if (!IsHash(receipt.KeyHash) || !IsHash(receipt.RequestFingerprint)) throw new StudioException("catalog.receipt.invalid");
    }
    private static bool IsHash(string hash) => hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static async Task WriteReceiptAsync(SqliteConnection db, SqliteTransaction transaction, IdempotencyReceipt receipt, CancellationToken token)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO receipts(key_hash,fingerprint,project_id,completed) VALUES($key,$fingerprint,$id,$completed)
            ON CONFLICT(key_hash) DO UPDATE SET completed=MAX(completed,excluded.completed)
            WHERE fingerprint=excluded.fingerprint AND project_id=excluded.project_id
            """;
        command.Parameters.AddWithValue("$key", receipt.KeyHash); command.Parameters.AddWithValue("$fingerprint", receipt.RequestFingerprint);
        command.Parameters.AddWithValue("$id", receipt.ProjectId); command.Parameters.AddWithValue("$completed", receipt.Completed);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new StudioException("project.idempotency.conflict");
    }
}

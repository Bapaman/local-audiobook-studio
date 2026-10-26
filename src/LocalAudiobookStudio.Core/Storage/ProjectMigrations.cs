using Microsoft.Data.Sqlite;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Core.Storage;

internal static class ProjectMigrations
{
    public static async Task ValidateReadOnlyAsync(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version";
        long version = (long)(await command.ExecuteScalarAsync(token))!;
        if (version > 3) throw new ProjectException("project.schema.future");
        if (version is not (1 or 2 or 3)) throw new ProjectException("project.schema.invalid");
        if (version < 3)
        {
            // A version label cannot authorize overwriting a partial or poisoned jobs schema.
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('jobs','job_items','job_receipts','job_events')";
            if ((long)(await command.ExecuteScalarAsync(token))! != 0) throw new ProjectException("project.schema.invalid");
        }
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type IN ('trigger','view')";
        if ((long)(await command.ExecuteScalarAsync(token))! != 0) throw new ProjectException("project.schema.invalid");
        command.CommandText = "PRAGMA quick_check";
        if (!Equals(await command.ExecuteScalarAsync(token), "ok")) throw new ProjectException("project.schema.invalid");
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token)) throw new ProjectException("project.schema.invalid");
    }

    public static async Task InitializeAsync(SqliteConnection connection, bool create, CancellationToken token)
    {
        using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
        long current = (long)(await version.ExecuteScalarAsync(token))!;
        if (current > 3) throw new ProjectException("project.schema.future");
        if (!create && current is not (1 or 2 or 3)) throw new ProjectException("project.schema.invalid");
        if (current == 3) return;
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        // JSON payloads are schema-v1 value snapshots; row identity, ordering and relationships remain SQL constraints.
        if (current == 0) command.CommandText = """
            CREATE TABLE project (id TEXT PRIMARY KEY, payload TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE voice (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE chapters (id TEXT PRIMARY KEY, ordinal INTEGER NOT NULL UNIQUE, payload TEXT NOT NULL);
            CREATE TABLE segments (id TEXT PRIMARY KEY, chapter_id TEXT NOT NULL REFERENCES chapters(id), ordinal INTEGER NOT NULL UNIQUE, payload TEXT NOT NULL);
            CREATE TABLE attempts (id TEXT PRIMARY KEY, segment_id TEXT NOT NULL REFERENCES segments(id), ordinal INTEGER NOT NULL, payload TEXT NOT NULL, UNIQUE(segment_id, ordinal));
            CREATE TABLE staging (relative_path TEXT PRIMARY KEY, final_relative_path TEXT);
            """;
        if (current == 0) await command.ExecuteNonQueryAsync(token);
        if (current < 2) command.CommandText = """
            CREATE TABLE voice_recording (id TEXT PRIMARY KEY, payload TEXT NOT NULL, singleton INTEGER NOT NULL DEFAULT 1 UNIQUE CHECK(singleton=1));
            """;
        if (current < 2) await command.ExecuteNonQueryAsync(token);
        command.CommandText = """
            CREATE TABLE jobs (id TEXT PRIMARY KEY, input_hash TEXT NOT NULL, active INTEGER NOT NULL CHECK(active IN (0,1)), payload TEXT NOT NULL);
            CREATE UNIQUE INDEX one_active_job ON jobs(active) WHERE active=1;
            CREATE TABLE job_items (job_id TEXT NOT NULL REFERENCES jobs(id), segment_id TEXT NOT NULL, ordinal INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(job_id,segment_id), UNIQUE(job_id,ordinal));
            CREATE TABLE job_receipts (key_hash TEXT PRIMARY KEY, request_hash TEXT NOT NULL, scope TEXT NOT NULL CHECK(scope IN ('enqueue','control')), job_id TEXT NOT NULL REFERENCES jobs(id), payload TEXT);
            CREATE TABLE job_events (sequence INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL REFERENCES jobs(id), job_revision INTEGER NOT NULL, payload TEXT NOT NULL);
            PRAGMA user_version=3;
            """;
        await command.ExecuteNonQueryAsync(token); transaction.Commit();
    }
}

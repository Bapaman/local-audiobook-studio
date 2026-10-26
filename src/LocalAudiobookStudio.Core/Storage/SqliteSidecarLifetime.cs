using System.Runtime.InteropServices;
using LocalAudiobookStudio.Core.Projects;
using Microsoft.Data.Sqlite;

namespace LocalAudiobookStudio.Core.Storage;

/// <summary>Uses SQLite's per-connection control to retain identity-pinned WAL/SHM files.</summary>
public static class SqliteSidecarLifetime
{
    public static async Task ConfigureWritesAsync(SqliteConnection connection, bool create, bool retainSidecars, CancellationToken token)
    {
        if (retainSidecars) Retain(connection);
        using var command = connection.CreateCommand();
        string mode = "wal";
        if (create)
        {
            // The empty header contains no durable state. Do not create/delete a
            // bootstrap rollback journal while its name is pinned.
            command.CommandText = "PRAGMA journal_mode=MEMORY"; await command.ExecuteNonQueryAsync(token);
        }
        else if (retainSidecars)
        {
            command.CommandText = "PRAGMA journal_mode";
            if (!Equals(await command.ExecuteScalarAsync(token), "wal")) mode = "persist";
        }
        // Existing rollback-mode databases use durable PERSIST rather than a WAL
        // conversion that would delete their pinned journal. Never MEMORY/OFF.
        command.CommandText = "PRAGMA journal_mode=" + mode;
        if (!Equals(await command.ExecuteScalarAsync(token), mode)) throw new ProjectException("project.storage");
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL"; await command.ExecuteNonQueryAsync(token);
    }

    public static void Retain(SqliteConnection connection)
    {
        if (!OperatingSystem.IsWindows()) throw new ProjectException("project.platform.unsupported");
        int enabled = 1;
        if (sqlite3_file_control(connection.Handle ?? throw new ProjectException("project.storage"), "main", 10, ref enabled) != 0)
            throw new ProjectException("project.storage");
    }

    // Microsoft.Data.Sqlite's pinned bundle uses e_sqlite3; SafeHandle marshaling
    // keeps that existing native connection alive through this synchronous call.
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_file_control(SQLitePCL.sqlite3 connection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string database, int operation, ref int value);
}

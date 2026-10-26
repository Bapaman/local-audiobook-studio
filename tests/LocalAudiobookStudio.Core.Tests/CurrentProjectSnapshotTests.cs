using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Core.Tests;

public class CurrentProjectSnapshotTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Current_session_bounds_sqlite_busy_wait_instead_of_stalling_the_queue_for_thirty_seconds()
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using var writer = await service.CreateAsync(box.ProjectRoot, "Synthetic", Guid.NewGuid().ToString("N"), Token, retainSidecars: true);
        await using var reader = await service.OpenCurrentAsync(box.ProjectRoot, Token);
        var field = typeof(LocalAudiobookStudio.Core.Storage.ProjectDatabase).GetField("connection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)field.GetValue(reader)!;
        Assert.Equal(1, connection.DefaultTimeout);
        Assert.Equal(1, (await reader.ReadCurrentSnapshotAsync(Token)).State.Revision);
    }

    [Fact]
    public async Task Current_snapshot_reads_fresh_committed_revision_without_overwriting_cached_writer_state()
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using var writer = await service.CreateAsync(box.ProjectRoot, "Before", Guid.NewGuid().ToString("N"), Token, retainSidecars: true);
        await using var reader = await service.OpenCurrentAsync(box.ProjectRoot, Token);
        Assert.Equal(1, reader.State.Revision);
        await writer.SaveAsync(writer.State with { Metadata = writer.State.Metadata with { Title = "After" } }, Token);
        var snapshot = await reader.ReadCurrentSnapshotAsync(Token);
        Assert.Equal(2, snapshot.State.Revision); Assert.Equal("After", snapshot.State.Metadata.Title);
        Assert.Empty(snapshot.OutputArtifactIds);
        Assert.Equal(1, reader.State.Revision); // A read cannot silently rewrite a caller's old CAS input.
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Legacy_snapshot_does_not_migrate_or_query_missing_job_tables(int schema)
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(box.ProjectRoot, "Legacy", Token)) { }
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs;" + (schema == 1 ? "DROP TABLE voice_recording;" : "") + $"PRAGMA user_version={schema}; PRAGMA journal_mode=DELETE");
        await using var current = await service.OpenCurrentAsync(box.ProjectRoot, Token);
        var snapshot = await current.ReadCurrentSnapshotAsync(Token);
        Assert.Equal("Legacy", snapshot.State.Metadata.Title); Assert.Equal(1, snapshot.State.Revision);
        Assert.Empty(snapshot.OutputArtifactIds);
        Assert.Equal((long)schema, VoiceRecordingServiceTests.Sql(box.ProjectRoot, "PRAGMA user_version"));
    }

    [Fact]
    public async Task Snapshot_details_use_only_the_owned_copy_without_repairing_closed_source_sidecars()
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(box.ProjectRoot, "Closed", Token)) { }
        string main = Path.Combine(box.ProjectRoot, "project.sqlite"), copy = Path.Combine(box.Root, "snapshot.sqlite");
        byte[] before = await File.ReadAllBytesAsync(main, Token); File.Copy(main, copy);
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
        var snapshot = await service.ProbeSnapshotDetailsAsync(box.ProjectRoot, copy, Token);
        Assert.Equal("Closed", snapshot.State.Metadata.Title); Assert.Empty(snapshot.OutputArtifactIds);
        Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task Strict_application_current_open_requires_prepared_rollback_journal_without_creating_it(int schema)
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(box.ProjectRoot, "Strict", Token)) { }
        string downgrade = schema < 3 ? "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs;" : "";
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, downgrade + (schema == 1 ? "DROP TABLE voice_recording;" : "") +
            $"PRAGMA user_version={schema}; PRAGMA journal_mode=DELETE; BEGIN IMMEDIATE; UPDATE project SET revision=revision; COMMIT");
        string main = Path.Combine(box.ProjectRoot, "project.sqlite"); byte[] before = await File.ReadAllBytesAsync(main, Token);
        Assert.False(File.Exists(main + "-journal"));
        using var owner = File.Open(Path.Combine(box.ProjectRoot, "artifact-ownership.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal("project.storage", (await Assert.ThrowsAsync<ProjectException>(async () =>
        { await using var unexpected = await service.OpenCurrentAsync(box.ProjectRoot, Token, requirePreparedSidecars: true); })).Code);
        Assert.False(File.Exists(main + "-journal")); Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
    }
}

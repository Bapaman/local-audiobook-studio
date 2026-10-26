using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using System.Runtime.InteropServices;

namespace LocalAudiobookStudio.Core.Tests;

public class OwnedProjectPreparationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Closed_wal_preparation_retains_mode_and_revision_and_returns_actual_ownership()
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(box.ProjectRoot, "Closed", Token)) { }
        string main = Path.Combine(box.ProjectRoot, "project.sqlite"), copy = Copy(box);
        byte[] before = await File.ReadAllBytesAsync(main, Token);
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
        await using (var owned = await service.OpenOwnedFromSnapshotAsync(box.ProjectRoot, copy, Token))
        {
            Assert.Equal("Closed", owned.Project.State.Metadata.Title); Assert.Equal(1, owned.Project.State.Revision);
            Assert.Equal("wal", VoiceRecordingServiceTests.Sql(box.ProjectRoot, "PRAGMA journal_mode"));
            Assert.True(File.Exists(main + "-wal")); Assert.True(File.Exists(main + "-shm"));
            Assert.Equal("project.busy", (await Assert.ThrowsAsync<ProjectException>(async () =>
            { await using var unexpected = await owned.Project.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token); })).Code);
            await owned.Lease.RefreshAsync(Token);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
        await using var current = await service.OpenCurrentAsync(box.ProjectRoot, Token, requireJobSchema: true);
        Assert.Equal(1, current.State.Revision);
        await using var available = await current.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Owned_legacy_migration_preserves_rollback_mode_and_does_not_recover_staging(int schema)
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(box.ProjectRoot, "Legacy", Token))
        {
            await created.RegisterPendingArtifactAsync("segments/pending.wav.part", "segments/pending.wav", Token);
            Directory.CreateDirectory(Path.Combine(box.ProjectRoot, "segments"));
            await File.WriteAllBytesAsync(created.ResolveOwnedPath("segments/pending.wav.part"), [1, 2, 3], Token);
        }
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs;" +
            (schema == 1 ? "DROP TABLE voice_recording;" : "") + $"PRAGMA user_version={schema}; PRAGMA journal_mode=DELETE");
        string main = Path.Combine(box.ProjectRoot, "project.sqlite"); Assert.False(File.Exists(main + "-journal"));
        await using var owned = await service.OpenOwnedFromSnapshotAsync(box.ProjectRoot, Copy(box), Token);
        Assert.Equal(3L, VoiceRecordingServiceTests.Sql(box.ProjectRoot, "PRAGMA user_version"));
        Assert.Equal(1, owned.Project.State.Revision); Assert.Equal("Legacy", owned.Project.State.Metadata.Title);
        using (var header = File.Open(main, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        { header.Position = 18; Assert.Equal(1, header.ReadByte()); Assert.Equal(1, header.ReadByte()); }
        Assert.True(File.Exists(main + "-journal")); Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(owned.Project.ResolveOwnedPath("segments/pending.wav.part"), Token));
        Assert.Empty(await owned.Project.Jobs.ListAsync(Token));
    }

    [Fact]
    public async Task Busy_actual_owner_prevents_missing_sidecar_reservation()
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(box.ProjectRoot, "Busy", Token)) { }
        string main = Path.Combine(box.ProjectRoot, "project.sqlite"), copy = Copy(box);
        byte[] before = await File.ReadAllBytesAsync(main, Token);
        using var owner = File.Open(Path.Combine(box.ProjectRoot, "artifact-ownership.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal("project.busy", (await Assert.ThrowsAsync<ProjectException>(async () =>
        { await using var unexpected = await service.OpenOwnedFromSnapshotAsync(box.ProjectRoot, copy, Token); })).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm")); Assert.False(File.Exists(main + "-journal"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Invalid_snapshot_is_rejected_before_source_preparation(bool future)
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(box.ProjectRoot, "Invalid", Token)) { }
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, future ? "PRAGMA user_version=99" : "UPDATE project SET payload='{}'");
        string main = Path.Combine(box.ProjectRoot, "project.sqlite"), copy = Copy(box); byte[] before = await File.ReadAllBytesAsync(main, Token);
        Assert.Equal(future ? "project.schema.future" : "project.schema.invalid", (await Assert.ThrowsAsync<ProjectException>(async () =>
        { await using var unexpected = await service.OpenOwnedFromSnapshotAsync(box.ProjectRoot, copy, Token); })).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm")); Assert.False(File.Exists(main + "-journal"));
    }

    [Fact]
    public async Task Idle_preparation_rejects_closed_paused_job_without_recovery_or_missing_names()
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        await using (var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token))
        {
            var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "enqueue", JobKind.Generation, ProjectJobStoreTests.Input(db), Token);
            await db.Jobs.RequestStateAsync(job.Id, job.Revision, "pause", JobControlCommand.Pause, Token);
        }
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "PRAGMA wal_checkpoint(TRUNCATE)");
        string main = Path.Combine(box.ProjectRoot, "project.sqlite"), copy = Copy(box); byte[] before = await File.ReadAllBytesAsync(main, Token);
        Assert.Equal("job.active", (await Assert.ThrowsAsync<ProjectException>(async () =>
        { await using var unexpected = await service.OpenOwnedFromSnapshotAsync(box.ProjectRoot, copy, Token, requireIdleJobs: true); })).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(main, Token));
        Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm"));
    }

    private static string Copy(ProjectSandbox box)
    {
        string copy = Path.Combine(box.Root, "snapshot.sqlite"); File.Copy(Path.Combine(box.ProjectRoot, "project.sqlite"), copy); return copy;
    }

    [Fact]
    public async Task Fresh_source_rechecks_late_enqueue_instead_of_trusting_the_idle_copy()
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var prepared = await ProjectJobStoreTests.Prepared(box)) { }
        string copy = Copy(box);
        await using var writer = await service.OpenAsync(box.ProjectRoot, Token, retainSidecars: true);
        JobRecord job;
        await using (var owner = await writer.AcquireArtifactLeaseAsync(cancellationToken: Token))
            job = await writer.Jobs.EnqueueAsync(owner, writer.State.Revision, "late", JobKind.Generation, ProjectJobStoreTests.Input(writer), Token);
        long head = await writer.Jobs.GetEventSequenceAsync(Token);
        Assert.Equal("job.active", (await Assert.ThrowsAsync<ProjectException>(async () =>
        { await using var unexpected = await service.OpenOwnedFromSnapshotAsync(box.ProjectRoot, copy, Token, requireIdleJobs: true); })).Code);
        Assert.Equal(head, await writer.Jobs.GetEventSequenceAsync(Token));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(job), System.Text.Json.JsonSerializer.Serialize(await writer.Jobs.GetAsync(job.Id, Token)));
        await using var released = await writer.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token);
    }

    [Fact]
    public async Task Valid_copy_cannot_hide_invalid_fresh_source_or_trigger_migration()
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(box.ProjectRoot, "Before", Token)) { }
        string copy = Copy(box);
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "UPDATE project SET payload='{}'");
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(box.ProjectRoot, "project.sqlite"), Token);
        Assert.Equal("project.schema.invalid", (await Assert.ThrowsAsync<ProjectException>(async () =>
        { await using var unexpected = await service.OpenOwnedFromSnapshotAsync(box.ProjectRoot, copy, Token); })).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(box.ProjectRoot, "project.sqlite"), Token));
        Assert.Equal(3L, VoiceRecordingServiceTests.Sql(box.ProjectRoot, "PRAGMA user_version"));
    }

    [Theory]
    [InlineData("")] [InlineData("-wal")] [InlineData("-shm")] [InlineData("ownership")]
    public async Task Actual_main_sidecar_and_ownership_hardlink_aliases_are_rejected_before_preparation(string suffix)
    {
        using var box = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(box.ProjectRoot, "Alias", Token)) { }
        string main = Path.Combine(box.ProjectRoot, "project.sqlite"), copy = Copy(box);
        string target = suffix == "ownership" ? Path.Combine(box.ProjectRoot, "artifact-ownership.lock") : main + suffix;
        if (!File.Exists(target)) await File.WriteAllBytesAsync(target, [], Token);
        byte[] before = await File.ReadAllBytesAsync(target, Token);
        Assert.True(CreateHardLinkW("\\\\?\\" + Path.Combine(box.Root, "alias"), "\\\\?\\" + target, 0));
        Assert.Equal("project.path", (await Assert.ThrowsAsync<ProjectException>(async () =>
        { await using var unexpected = await service.OpenOwnedFromSnapshotAsync(box.ProjectRoot, copy, Token); })).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(target, Token));
        if (suffix is "" or "ownership") { Assert.False(File.Exists(main + "-wal")); Assert.False(File.Exists(main + "-shm")); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string target, nint security);
}

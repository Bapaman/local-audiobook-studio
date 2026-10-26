using LocalAudiobookStudio.Core.Projects;
using Microsoft.Data.Sqlite;

namespace LocalAudiobookStudio.Core.Tests;

public class ProjectServiceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Open_current_missing_WAL_sidecars_fails_without_creating_source_files(bool emptyWal)
    {
        using var sandbox = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(sandbox.ProjectRoot, "Synthetic", TestContext.Current.CancellationToken)) { }
        string path = Path.Combine(sandbox.ProjectRoot, "project.sqlite");
        if (emptyWal) using (File.Create(path + "-wal")) { }
        byte[] before = File.ReadAllBytes(path); var files = Directory.GetFiles(sandbox.ProjectRoot).Order().ToArray();
        LocalAudiobookStudio.Core.Storage.ProjectDatabase? opened = null;
        var error = await Record.ExceptionAsync(async () => opened = await service.OpenCurrentAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken));
        if (opened != null) await opened.DisposeAsync();
        Assert.Equal("project.storage", Assert.IsType<ProjectException>(error).Code);
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(files, Directory.GetFiles(sandbox.ProjectRoot).Order().ToArray());
        Assert.False(File.Exists(path + "-shm"));
    }

    [Fact]
    public async Task Open_current_valid_rollback_reserves_missing_journal_identity_without_changing_database()
    {
        using var sandbox = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(sandbox.ProjectRoot, "Synthetic", TestContext.Current.CancellationToken)) { }
        VoiceRecordingServiceTests.Sql(sandbox.ProjectRoot, "PRAGMA journal_mode=DELETE");
        string path = Path.Combine(sandbox.ProjectRoot, "project.sqlite"); byte[] before = File.ReadAllBytes(path);
        Assert.False(File.Exists(path + "-journal"));
        await using (var current = await service.OpenCurrentAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken))
        {
            Assert.True(File.Exists(path + "-journal"));
            Assert.Throws<IOException>(() => File.Move(path + "-journal", path + "-journal.moved"));
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] currentBytes = new byte[before.Length]; reader.ReadExactly(currentBytes); Assert.Equal(before, currentBytes);
        }
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(File.Exists(path + "-wal")); Assert.False(File.Exists(path + "-shm"));
    }

    [Fact]
    public async Task Open_current_rejected_future_WAL_preserves_durable_content_and_sidecar_names()
    {
        using var sandbox = new ProjectSandbox(); var service = new ProjectService();
        await using var created = await service.CreateAsync(sandbox.ProjectRoot, "Synthetic", TestContext.Current.CancellationToken);
        VoiceRecordingServiceTests.Sql(sandbox.ProjectRoot, "PRAGMA user_version=99");
        string path = Path.Combine(sandbox.ProjectRoot, "project.sqlite");
        string[] names = [path, path + "-wal", path + "-shm"];
        var before = names.Select(ReadSharedBytes).ToArray(); var files = Directory.GetFiles(sandbox.ProjectRoot).Order().ToArray();
        Assert.Equal("project.schema.future", (await Assert.ThrowsAsync<ProjectException>(() => service.OpenCurrentAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken))).Code);
        Assert.Equal(before[0], ReadSharedBytes(names[0])); Assert.Equal(before[1], ReadSharedBytes(names[1]));
        // SHM is volatile reader coordination; reading can update its read marks
        // even when schema validation rejects. It contains no database content.
        Assert.Equal(before[2].Length, ReadSharedBytes(names[2]).Length);
        Assert.Equal(files, Directory.GetFiles(sandbox.ProjectRoot).Order().ToArray());
    }

    [Theory]
    [InlineData("")] [InlineData("-wal")] [InlineData("-shm")]
    public async Task Open_current_rejects_existing_database_hardlink_alias_before_SQLite_use(string suffix)
    {
        using var sandbox = new ProjectSandbox(); var service = new ProjectService();
        await using var created = await service.CreateAsync(sandbox.ProjectRoot, "Synthetic", TestContext.Current.CancellationToken);
        string path = Path.Combine(sandbox.ProjectRoot, "project.sqlite") + suffix, alias = path + ".alias";
        Assert.True(CreateHardLinkW("\\\\?\\" + alias, "\\\\?\\" + path, 0));
        try
        {
            Assert.Equal("project.path", (await Assert.ThrowsAsync<ProjectException>(() => service.OpenCurrentAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken))).Code);
        }
        finally { await created.DisposeAsync(); File.Delete(alias); }
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[checked((int)reader.Length)]; reader.ReadExactly(bytes); return bytes;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string target, nint security);

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Open_current_reads_legacy_without_migration_or_job_queries(int schema)
    {
        using var sandbox = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(sandbox.ProjectRoot, "Synthetic", TestContext.Current.CancellationToken)) { }
        VoiceRecordingServiceTests.Sql(sandbox.ProjectRoot, "PRAGMA journal_mode=DELETE; DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs; " + (schema == 1 ? "DROP TABLE voice_recording; " : "") + "PRAGMA user_version=" + schema);
        string path = Path.Combine(sandbox.ProjectRoot, "project.sqlite"); byte[] before = File.ReadAllBytes(path);
        await using (var current = await service.OpenCurrentAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken))
        {
            Assert.Empty(await current.Jobs.ListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await current.Jobs.ReadEventsAsync(0, TestContext.Current.CancellationToken));
            Assert.Equal(0, await current.Jobs.GetEventSequenceAsync(TestContext.Current.CancellationToken));
            Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
            Assert.Equal((long)schema, VoiceRecordingServiceTests.Sql(sandbox.ProjectRoot, "PRAGMA user_version"));
            Assert.Equal("delete", VoiceRecordingServiceTests.Sql(sandbox.ProjectRoot, "PRAGMA journal_mode"));
        }
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(File.Exists(path + "-wal"));
        await using var migrated = await service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Equal(3L, VoiceRecordingServiceTests.Sql(sandbox.ProjectRoot, "PRAGMA user_version"));
    }

    [Theory]
    [InlineData("PRAGMA user_version=99", "project.schema.future")]
    [InlineData("UPDATE project SET payload='invalid-json'", "project.schema.invalid")]
    [InlineData("INSERT INTO staging(relative_path) VALUES('../escape.part')", "project.path")]
    public async Task Open_current_rejects_invalid_before_mutable_work(string poison, string code)
    {
        using var sandbox = new ProjectSandbox(); var service = new ProjectService();
        await using (var created = await service.CreateAsync(sandbox.ProjectRoot, "Synthetic", TestContext.Current.CancellationToken)) { }
        VoiceRecordingServiceTests.Sql(sandbox.ProjectRoot, "PRAGMA journal_mode=DELETE; " + poison);
        string path = Path.Combine(sandbox.ProjectRoot, "project.sqlite"); byte[] before = File.ReadAllBytes(path);
        var files = Directory.GetFiles(sandbox.ProjectRoot).Order().ToArray();
        Assert.Equal(code, (await Assert.ThrowsAsync<ProjectException>(() => service.OpenCurrentAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken))).Code);
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(files, Directory.GetFiles(sandbox.ProjectRoot).Order().ToArray());
    }

    [Fact]
    public async Task Metadata_only_observer_failure_does_not_change_create_or_current_open()
    {
        using var sandbox = new ProjectSandbox(); var service = new ProjectService(); var boundaries = new List<string>();
        typeof(ProjectService).GetField("lifecycleObserver", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, (Action<string>)(boundary => { boundaries.Add(boundary); throw new InvalidOperationException("synthetic observer failure"); }));
        await using var created = await service.CreateAsync(sandbox.ProjectRoot, "Synthetic", TestContext.Current.CancellationToken);
        await using var current = await service.OpenCurrentAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Equal(created.State.Metadata.Id, current.State.Metadata.Id); Assert.NotEmpty(boundaries);
        Assert.All(boundaries, value => Assert.StartsWith("bootstrap-", value));
    }

    [Fact]
    public async Task Probe_snapshot_validates_owned_paths_against_original_root_not_copy_root()
    {
        using var sandbox = new ProjectSandbox(); var service = new ProjectService();
        string original = Path.Combine(sandbox.Root, "origin"), copy = Path.Combine(sandbox.Root, "copy"), external = Path.Combine(sandbox.Root, "external");
        await using (var project = await service.CreateAsync(original, "Synthetic", TestContext.Current.CancellationToken))
            await project.RegisterStagingAsync("escape/pending.part", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(copy); Directory.CreateDirectory(external);
        string snapshot = Path.Combine(copy, "project.sqlite"); File.Copy(Path.Combine(original, "project.sqlite"), snapshot);
        string junction = Path.Combine(original, "escape");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(external);
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(TestContext.Current.CancellationToken); Assert.Equal(0, process.ExitCode);
        try
        {
            var error = await Assert.ThrowsAsync<ProjectException>(() => service.ProbeSnapshotAsync(original, snapshot, TestContext.Current.CancellationToken));
            Assert.Equal("project.path", error.Code); Assert.Equal(error.Code, error.Message);
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public async Task Probe_preserves_requested_identity_and_does_not_recover_running_state()
    {
        using var sandbox = new ProjectSandbox(); var service = new ProjectService();
        string id = Guid.NewGuid().ToString("N");
        await using (var project = await service.CreateAsync(sandbox.ProjectRoot, "Synthetic", id, TestContext.Current.CancellationToken))
            await project.SaveAsync(ProjectSandbox.State(project.State), TestContext.Current.CancellationToken);
        var state = await service.ProbeAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Equal(id, state.Metadata.Id); Assert.Equal("running", state.Segments.Single(x => x.Id == "s-run").Status);
        Assert.Equal(2, state.Revision);
    }

    [Theory]
    [InlineData("PRAGMA user_version=99", "project.schema.future")]
    [InlineData("UPDATE project SET payload='null'", "project.schema.invalid")]
    [InlineData("UPDATE project SET payload='invalid-json'", "project.schema.invalid")]
    [InlineData("INSERT INTO staging(relative_path) VALUES ('../escape.part')", "project.path")]
    [InlineData("UPDATE project SET id='different-row-identity'", "project.schema.invalid")]
    public async Task Invalid_probe_and_open_do_not_enable_wal_or_recover(string poison, string code)
    {
        using var sandbox = new ProjectSandbox(); var service = new ProjectService();
        await using (var project = await service.CreateAsync(sandbox.ProjectRoot, "Synthetic", TestContext.Current.CancellationToken)) { }
        string path = Path.Combine(sandbox.ProjectRoot, "project.sqlite");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = "\\\\?\\" + path, Pooling = false }.ToString()))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA journal_mode=DELETE; " + poison; command.ExecuteNonQuery(); }
        byte[] before = File.ReadAllBytes(path);
        Assert.Equal(code, (await Assert.ThrowsAsync<ProjectException>(() => service.ProbeAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken))).Code);
        Assert.Equal(code, (await Assert.ThrowsAsync<ProjectException>(() => service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken))).Code);
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(File.Exists(path + "-wal"));
    }

    [Fact]
    public async Task Reopen_preserves_order_ids_all_snapshots_hashes_transcript_whitespace_and_locks()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "Книга", TestContext.Current.CancellationToken);
        var state = ProjectSandbox.State(project.State);
        await project.SaveAsync(state, TestContext.Current.CancellationToken);
        // The fidelity fixture uses an inactive status; recovery is tested separately.
        await project.SaveAsync(project.State with { Segments = state.Segments.Select(x => x with { Status = "done" }).ToArray(), Attempts = state.Attempts.Select(x => x with { Status = "done" }).ToArray() }, TestContext.Current.CancellationToken);
        state = project.State;
        await using var reopened = await service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Equal(state.Metadata, reopened.State.Metadata);
        Assert.Equal(state.Voice, reopened.State.Voice);
        Assert.Equal(state.Chapters.OrderBy(x => x.Order), reopened.State.Chapters);
        Assert.Equal(state.Segments.OrderBy(x => x.Order), reopened.State.Segments);
        Assert.Equal(state.Attempts, reopened.State.Attempts);
        Assert.True(reopened.State.Segments.Single(x => x.Id == "s-done").Locked);
        Assert.True(sandbox.ProjectRoot.Length > 180);
    }

    [Fact]
    public async Task Recovery_closes_running_attempt_requeues_only_running_and_removes_only_registered_parts_idempotently()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "Книга", TestContext.Current.CancellationToken);
        var state = ProjectSandbox.State(project.State);
        await project.SaveAsync(state, TestContext.Current.CancellationToken);
        string owned = project.ResolveOwnedPath("temp/known.wav.part");
        Directory.CreateDirectory(Path.GetDirectoryName(owned)!);
        await File.WriteAllTextAsync(owned, "synthetic", TestContext.Current.CancellationToken);
        await project.RegisterStagingAsync("temp/known.wav.part", TestContext.Current.CancellationToken);
        string unrelated = Path.Combine(Path.GetDirectoryName(owned)!, "unrelated.part");
        await File.WriteAllTextAsync(unrelated, "keep", TestContext.Current.CancellationToken);
        await using var opened = await service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Equal("queued", opened.State.Segments.Single(x => x.Id == "s-run").Status);
        Assert.Equal(state.Segments.Single(x => x.Id == "s-done"), opened.State.Segments.Single(x => x.Id == "s-done"));
        Assert.Equal("interrupted", opened.State.Attempts[0].Status);
        Assert.Equal("project.interrupted", opened.State.Attempts[0].DiagnosticCode);
        Assert.NotNull(opened.State.Attempts[0].CompletedUtc);
        Assert.False(File.Exists(owned)); Assert.True(File.Exists(unrelated));
        var completed = opened.State.Attempts[0].CompletedUtc;
        await service.RecoverInterruptedAsync(opened, TestContext.Current.CancellationToken);
        Assert.Equal(completed, opened.State.Attempts[0].CompletedUtc);
    }

    [Fact]
    public async Task Create_collision_missing_database_and_future_schema_are_rejected_without_overwriting()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        Assert.Equal("project.missing", (await Assert.ThrowsAsync<ProjectException>(() => service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken))).Code);
        await using (var project = await service.CreateAsync(sandbox.ProjectRoot, "first", TestContext.Current.CancellationToken))
            Assert.Equal("project.exists", (await Assert.ThrowsAsync<ProjectException>(() => service.CreateAsync(sandbox.ProjectRoot, "other", TestContext.Current.CancellationToken))).Code);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = "\\\\?\\" + Path.Combine(sandbox.ProjectRoot, "project.sqlite"), Pooling = false }.ToString()))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery(); }
        Assert.Equal("project.schema.future", (await Assert.ThrowsAsync<ProjectException>(() => service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task Invalid_foreign_key_rolls_back_entire_save()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "original", TestContext.Current.CancellationToken);
        var invalid = ProjectSandbox.State(project.State);
        invalid = invalid with { Segments = [invalid.Segments[0] with { ChapterId = "missing" }] };
        Assert.Equal("project.storage", (await Assert.ThrowsAsync<ProjectException>(() => project.SaveAsync(invalid, TestContext.Current.CancellationToken))).Code);
        await using var reopened = await new ProjectService().OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Equal("original", reopened.State.Metadata.Title); Assert.Empty(reopened.State.Chapters);
    }

    [Theory]
    [InlineData("../sibling/file.wav")]
    [InlineData("C:\\external.wav")]
    [InlineData("C:relative.wav")]
    [InlineData("\\\\?\\C:\\file.wav")]
    [InlineData("temp/file.wav:stream")]
    [InlineData("temp/../file.wav")]
    [InlineData("CON.wav")]
    public async Task Owned_paths_reject_escape_device_and_ads(string path)
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        Assert.Equal("project.path", Assert.Throws<ProjectException>(() => project.ResolveOwnedPath(path)).Code);
    }

    [Fact]
    public async Task Recovery_removes_orphan_part_when_registered_final_is_accepted_and_interrupted_save_runs_first()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await project.SaveAsync(ProjectSandbox.State(project.State), TestContext.Current.CancellationToken);
        string final = project.ResolveOwnedPath("voice-snapshot/test.wav"), part = project.ResolveOwnedPath("voice-snapshot/test.wav.part");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!); File.WriteAllText(final, "accepted"); File.WriteAllText(part, "orphan");
        await project.RegisterPendingArtifactAsync("voice-snapshot/test.wav.part", "VOICE-SNAPSHOT\\test.wav", TestContext.Current.CancellationToken);
        await service.RecoverInterruptedAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("accepted", File.ReadAllText(final)); Assert.False(File.Exists(part));
        Assert.Equal("queued", project.State.Segments.Single(x => x.Id == "s-run").Status);
    }

    [Fact]
    public async Task Recovery_removes_orphan_final_when_registered_part_is_accepted_done_output()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        var state = ProjectSandbox.State(project.State);
        var done = state.Segments.Single(x => x.Id == "s-done") with { OutputRelativePath = "temp/test.wav.part" };
        await project.SaveAsync(state with { Segments = [state.Segments[0], done] }, TestContext.Current.CancellationToken);
        string final = project.ResolveOwnedPath("temp/test.wav"), part = project.ResolveOwnedPath("temp/test.wav.part");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!); File.WriteAllText(final, "orphan"); File.WriteAllText(part, "accepted");
        await project.RegisterPendingArtifactAsync("temp/test.wav.part", "TEMP\\test.wav", TestContext.Current.CancellationToken);
        await service.RecoverInterruptedAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("accepted", File.ReadAllText(part)); Assert.False(File.Exists(final));
        Assert.Equal(done, project.State.Segments.Single(x => x.Id == "s-done"));
    }

    [Fact]
    public async Task Stale_connection_recovery_uses_committed_voice_and_preserves_alias_registered_final()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var first = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await using var stale = await service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        await first.SaveAsync(ProjectSandbox.State(first.State) with { Attempts = [], Segments = [], Chapters = [] }, TestContext.Current.CancellationToken);
        string final = first.ResolveOwnedPath("voice-snapshot/test.wav"); Directory.CreateDirectory(Path.GetDirectoryName(final)!); File.WriteAllText(final, "accepted");
        await stale.RegisterPendingArtifactAsync("voice-snapshot/test.wav.part", "VOICE-SNAPSHOT\\test.wav", TestContext.Current.CancellationToken);
        await service.RecoverInterruptedAsync(stale, TestContext.Current.CancellationToken);
        Assert.Equal("accepted", File.ReadAllText(final)); Assert.Equal(first.State.Voice, stale.State.Voice);
    }

    [Theory]
    [InlineData("output-hash")]
    [InlineData(null)]
    public async Task Recovery_preserves_locked_done_output_even_when_it_collides_with_registered_part(string? outputHash)
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        var state = ProjectSandbox.State(project.State);
        var done = state.Segments.Single(x => x.Id == "s-done") with { OutputRelativePath = "temp/accepted.wav.part", OutputHash = outputHash };
        await project.SaveAsync(state with { Segments = [done], Attempts = [] }, TestContext.Current.CancellationToken);
        string part = project.ResolveOwnedPath("temp/accepted.wav.part"); Directory.CreateDirectory(Path.GetDirectoryName(part)!); File.WriteAllText(part, "accepted");
        await project.RegisterStagingAsync("temp/accepted.wav.part", TestContext.Current.CancellationToken);
        await service.RecoverInterruptedAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("accepted", File.ReadAllText(part)); Assert.Equal(done, Assert.Single(project.State.Segments));
    }

    [Fact]
    public async Task Recovery_waits_for_cross_connection_publication_and_acceptance_lease()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var first = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await using var other = await service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        string part = first.ResolveOwnedPath("voice-snapshot/test.wav.part"), final = first.ResolveOwnedPath("voice-snapshot/test.wav");
        var lease = await first.AcquireArtifactLeaseAsync(cancellationToken: TestContext.Current.CancellationToken);
        Task recovery;
        try
        {
            await lease.RegisterPendingArtifactAsync("voice-snapshot/test.wav.part", "VOICE-SNAPSHOT\\test.wav", TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(part)!); File.WriteAllText(part, "accepted"); File.Move(part, final);
            recovery = service.RecoverInterruptedAsync(other, TestContext.Current.CancellationToken);
            // Calling recovery enters its first await synchronously: it must wait at the held OS lease.
            Assert.False(recovery.IsCompleted); Assert.True(File.Exists(final));
            await lease.SaveAsync(ProjectSandbox.State(first.State) with { Attempts = [], Segments = [], Chapters = [] }, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(final));
        }
        finally { await lease.DisposeAsync(); }
        await recovery;
        Assert.Equal("accepted", File.ReadAllText(final)); Assert.Equal(first.State.Voice, other.State.Voice);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = "\\\\?\\" + Path.Combine(sandbox.ProjectRoot, "project.sqlite"), Pooling = false }.ToString());
        db.Open(); using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM staging WHERE final_relative_path IS NOT NULL";
        Assert.Equal(0L, (long)query.ExecuteScalar()!);
    }

    [Fact]
    public async Task Separate_process_holds_same_lease_and_kill_releases_it_for_orphan_recovery()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await project.RegisterPendingArtifactAsync("voice-snapshot/orphan.wav.part", "voice-snapshot/orphan.wav", TestContext.Current.CancellationToken);
        string final = project.ResolveOwnedPath("voice-snapshot/orphan.wav"); Directory.CreateDirectory(Path.GetDirectoryName(final)!); File.WriteAllText(final, "synthetic orphan");
        string lockPath = project.ResolveOwnedPath("artifact-ownership.lock").Replace("'", "''", StringComparison.Ordinal);
        string command = "$ErrorActionPreference = 'Stop'; $lease = [System.IO.File]::Open('" + lockPath + "', [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None); [Console]::WriteLine('locked'); while ($true) { [System.Threading.Thread]::Sleep(1000) }";
        var start = new System.Diagnostics.ProcessStartInfo("pwsh.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)));
        using var holder = System.Diagnostics.Process.Start(start); Assert.NotNull(holder);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); watchdog.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            Assert.Equal("locked", await holder.StandardOutput.ReadLineAsync(watchdog.Token));
            Assert.Equal("project.busy", (await Assert.ThrowsAsync<ProjectException>(() => project.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(75), watchdog.Token))).Code);
            Task recovery = service.RecoverInterruptedAsync(project, watchdog.Token);
            Assert.False(recovery.IsCompleted); Assert.True(File.Exists(final));
            holder.Kill(entireProcessTree: true); await holder.WaitForExitAsync(watchdog.Token);
            await recovery;
            Assert.False(File.Exists(final)); Assert.Null(project.State.Voice);
        }
        finally { if (!holder.HasExited) { holder.Kill(entireProcessTree: true); await holder.WaitForExitAsync(TestContext.Current.CancellationToken); } }
    }

    [Fact]
    public async Task File_lease_wait_is_cancelable_and_bounded_with_private_safe_busy_code()
    {
        using var sandbox = new ProjectSandbox();
        await using var first = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await using var other = await new ProjectService().OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        await using var lease = await first.AcquireArtifactLeaseAsync(cancellationToken: TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ProjectException>(() => other.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(75), TestContext.Current.CancellationToken));
        Assert.Equal("project.busy", error.Code); Assert.Equal(error.Code, error.Message);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = other.AcquireArtifactLeaseAsync(cancellationToken: cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Recovery_preserves_accepted_voice_when_registered_path_uses_separator_or_case_alias()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await project.SaveAsync(ProjectSandbox.State(project.State), TestContext.Current.CancellationToken);
        string final = project.ResolveOwnedPath("voice-snapshot/test.wav"); Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        File.WriteAllText(final, "accepted synthetic");
        await project.RegisterPendingArtifactAsync("voice-snapshot/test.wav.part", "VOICE-SNAPSHOT\\test.wav", TestContext.Current.CancellationToken);
        await service.RecoverInterruptedAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal("accepted synthetic", File.ReadAllText(final));
    }

    [Fact]
    public async Task Pending_registration_cannot_claim_database_or_unrelated_final_path()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        Assert.Equal("project.path", (await Assert.ThrowsAsync<ProjectException>(() => project.RegisterPendingArtifactAsync("project.sqlite.part", "project.sqlite", TestContext.Current.CancellationToken))).Code);
        Assert.Equal("project.path", (await Assert.ThrowsAsync<ProjectException>(() => project.RegisterPendingArtifactAsync("temp/a.wav.part", "voice-snapshot/b.wav", TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task Pending_final_after_atomic_move_is_removed_when_snapshot_acceptance_did_not_commit()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await project.RegisterPendingArtifactAsync("voice-snapshot/pending.wav.part", "voice-snapshot/pending.wav", TestContext.Current.CancellationToken);
        string part = project.ResolveOwnedPath("voice-snapshot/pending.wav.part");
        string final = project.ResolveOwnedPath("voice-snapshot/pending.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        File.WriteAllText(part, "synthetic"); File.Move(part, final);
        await using var reopened = await service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Null(reopened.State.Voice); Assert.False(File.Exists(part)); Assert.False(File.Exists(final));
        await service.RecoverInterruptedAsync(reopened, TestContext.Current.CancellationToken);
        Assert.Null(reopened.State.Voice);
    }

    [Fact]
    public async Task Committed_snapshot_and_pending_removal_are_atomic_and_recovery_preserves_accepted_file()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await project.RegisterPendingArtifactAsync("voice-snapshot/test.wav.part", "voice-snapshot/test.wav", TestContext.Current.CancellationToken);
        var state = ProjectSandbox.State(project.State);
        string final = project.ResolveOwnedPath("voice-snapshot/test.wav"); Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        File.WriteAllText(final, "accepted synthetic");
        await project.SaveAsync(state, TestContext.Current.CancellationToken);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = "\\\\?\\" + Path.Combine(sandbox.ProjectRoot, "project.sqlite"), Pooling = false }.ToString()))
        {
            db.Open(); using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM staging WHERE final_relative_path IS NOT NULL";
            Assert.Equal(0L, (long)query.ExecuteScalar()!);
        }
        await using var reopened = await service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Equal(state.Voice, reopened.State.Voice); Assert.Equal("accepted synthetic", File.ReadAllText(final));
    }

    [Fact]
    public async Task Stale_state_save_rejects_conflict_instead_of_losing_committed_updates()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await using var other = await service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        var stale = project.State;
        await project.SaveAsync(stale with { Metadata = stale.Metadata with { Title = "accepted" } }, TestContext.Current.CancellationToken);
        Assert.Equal("project.conflict", (await Assert.ThrowsAsync<ProjectException>(() => project.SaveAsync(stale with { Metadata = stale.Metadata with { Title = "stale" } }, TestContext.Current.CancellationToken))).Code);
        Assert.Equal("project.conflict", (await Assert.ThrowsAsync<ProjectException>(() => other.SaveAsync(other.State with { Metadata = other.State.Metadata with { Title = "other" } }, TestContext.Current.CancellationToken))).Code);
        await using var reopened = await service.OpenAsync(sandbox.ProjectRoot, TestContext.Current.CancellationToken);
        Assert.Equal("accepted", reopened.State.Metadata.Title);
    }

    [Fact]
    public async Task Project_root_file_collision_reports_only_stable_storage_code()
    {
        using var sandbox = new ProjectSandbox();
        string file = Path.Combine(sandbox.Root, "file"); File.WriteAllText(file, "synthetic");
        var error = await Assert.ThrowsAsync<ProjectException>(() => new ProjectService().CreateAsync(file, "test", TestContext.Current.CancellationToken));
        Assert.Equal("project.storage", error.Code); Assert.Equal(error.Code, error.Message);
        Assert.Equal("synthetic", File.ReadAllText(file));
    }

    [Fact]
    public async Task Running_row_without_attempt_gets_one_interruption_record_on_recovery()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await project.SaveAsync(ProjectSandbox.State(project.State) with { Attempts = [] }, TestContext.Current.CancellationToken);
        await service.RecoverInterruptedAsync(project, TestContext.Current.CancellationToken);
        var attempt = Assert.Single(project.State.Attempts);
        Assert.Equal("s-run", attempt.SegmentId); Assert.Equal(123, attempt.Seed);
        Assert.Equal("interrupted", attempt.Status); Assert.Equal("project.interrupted", attempt.DiagnosticCode);
        await service.RecoverInterruptedAsync(project, TestContext.Current.CancellationToken);
        Assert.Equal(attempt, Assert.Single(project.State.Attempts));
    }

    [Fact]
    public async Task Save_registers_attempt_staging_and_recovery_preserves_unrelated_final_files()
    {
        using var sandbox = new ProjectSandbox();
        var service = new ProjectService();
        await using var project = await service.CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await project.SaveAsync(ProjectSandbox.State(project.State), TestContext.Current.CancellationToken);
        string staging = project.ResolveOwnedPath("temp/known.wav.part");
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        File.WriteAllText(staging, "synthetic");
        string final = project.ResolveOwnedPath("temp/keep.wav"); File.WriteAllText(final, "synthetic");
        await service.RecoverInterruptedAsync(project, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(staging)); Assert.True(File.Exists(final));
    }

    [Fact]
    public async Task Update_one_segment_preserves_other_locks_attempts_and_atomic_state_on_cancellation()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await project.SaveAsync(ProjectSandbox.State(project.State), TestContext.Current.CancellationToken);
        var state = project.State;
        var changed = state with { Segments = state.Segments.Select(x => x.Id == "s-run" ? x with { Status = "queued", Seed = 999 } : x).ToArray() };
        await project.SaveAsync(changed, TestContext.Current.CancellationToken);
        Assert.Equal(state.Segments.Single(x => x.Id == "s-done"), project.State.Segments.Single(x => x.Id == "s-done"));
        Assert.Equal(state.Attempts, project.State.Attempts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => project.SaveAsync(state with { Metadata = state.Metadata with { Title = "canceled" } }, new CancellationToken(true)));
        Assert.Equal("Книга '1'", project.State.Metadata.Title);
    }

    [Fact]
    public async Task Accepted_voice_snapshot_cannot_be_replaced_by_a_save()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        await project.SaveAsync(ProjectSandbox.State(project.State), TestContext.Current.CancellationToken);
        var state = project.State;
        Assert.Equal("voice.exists", (await Assert.ThrowsAsync<ProjectException>(() => project.SaveAsync(state with { Voice = state.Voice! with { Transcript = "changed" } }, TestContext.Current.CancellationToken))).Code);
        Assert.Equal(state.Voice, project.State.Voice);
    }

    [Fact]
    public async Task Non_utc_and_owned_path_escape_are_rejected_before_persistence()
    {
        using var sandbox = new ProjectSandbox();
        await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot, "test", TestContext.Current.CancellationToken);
        var state = ProjectSandbox.State(project.State);
        Assert.Equal("project.timestamp", (await Assert.ThrowsAsync<ProjectException>(() => project.SaveAsync(state with { Metadata = state.Metadata with { UpdatedUtc = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(3)) } }, TestContext.Current.CancellationToken))).Code);
        Assert.Equal("project.path", (await Assert.ThrowsAsync<ProjectException>(() => project.SaveAsync(state with { Segments = [state.Segments[0] with { OutputRelativePath = "../escape.wav" }] }, TestContext.Current.CancellationToken))).Code);
        Assert.Empty(project.State.Segments);
    }

    [Fact]
    public async Task Existing_junction_escape_is_rejected()
    {
        using var sandbox = new ProjectSandbox();
        string shortRoot = Path.Combine(sandbox.Root, "project");
        await using var project = await new ProjectService().CreateAsync(shortRoot, "test", TestContext.Current.CancellationToken);
        string external = Path.Combine(sandbox.Root, "external"); Directory.CreateDirectory(external);
        string junction = Path.Combine(shortRoot, "escape");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { Arguments = $"/c mklink /J \"{junction}\" \"{external}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        Assert.NotNull(process); await process.WaitForExitAsync(TestContext.Current.CancellationToken); Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<ProjectException>(() => project.ResolveOwnedPath("escape/file.wav")); }
        finally { Directory.Delete(junction); }
    }
}

internal sealed class ProjectSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "las-project-" + Guid.NewGuid().ToString("N"));
    public string ProjectRoot => Path.Combine(Root, "Книги с пробелами", new string('я', 75), new string('б', 75));
    public ProjectSandbox() => Directory.CreateDirectory(Root);
    public void Dispose() => Directory.Delete(Root, true);
    public static ProjectState State(ProjectState state)
    {
        var pins = new VersionPins("app", "worker", "model", "policy", "frontend", "segmenter");
        var parameters = new GenerationParameters("warm", 1.1, .7, 120, "{\"custom\":2}");
        DateTimeOffset now = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        return state with
        {
            Metadata = state.Metadata with { Title = "Книга '1'", Author = "Автор", Versions = pins, Parameters = parameters, UpdatedUtc = now },
            Voice = new VoiceSnapshot("voice-id", "voice-snapshot/test.wav", "Точный текст  \r\n", "wav-hash", "transcript-hash", "identity-hash", true, now),
            Chapters = [new ChapterRecord("c-2", 2, "Вторая", "original", "normal", "spoken", now), new ChapterRecord("c-1", 1, "Первая", "исходный", "нормальный", "произносимый", now)],
            Segments = [new SegmentRecord("s-run", "c-1", 1, "original", "normalized", "spoken", "text-hash", "fingerprint", null, "segments/c-1/run.wav", 123, pins, parameters, "running", false, now, now, "code"), new SegmentRecord("s-done", "c-2", 2, "done-original", "done-normal", "done-spoken", "done-text", "done-fingerprint", "output-hash", "segments/c-2/done.wav", 456, pins, parameters, "done", true, now, now, null)],
            Attempts = [new AttemptRecord("attempt-1", "s-run", 1, 123, pins, parameters, "fingerprint", "running", "temp/known.wav.part", null, now, null, null)]
        };
    }
}

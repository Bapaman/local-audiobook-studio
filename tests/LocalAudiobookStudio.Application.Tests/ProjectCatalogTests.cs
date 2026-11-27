using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Projects;
using Microsoft.Data.Sqlite;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class ProjectCatalogTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Final_appearing_before_bootstrap_publication_is_never_overwritten()
    {
        using var sandbox = new CatalogSandbox(); var catalog = sandbox.Catalog();
        string path = Path.Combine(sandbox.Root, "catalog.sqlite");
        Observe(catalog, boundary => { if (boundary == "bootstrap-before-publish") File.WriteAllText(path, "Unowned sentinel"); });
        await Assert.ThrowsAsync<StudioException>(() => catalog.ListAsync(Token));
        Assert.Equal("Unowned sentinel", File.ReadAllText(path));
        await Assert.ThrowsAsync<StudioException>(() => sandbox.Catalog().ListAsync(Token));
        Assert.Equal("Unowned sentinel", File.ReadAllText(path));
    }

    [Fact]
    public async Task Unrecognized_attempts_do_not_authorize_repair_of_existing_project_main()
    {
        using var sandbox = new CatalogSandbox(); string id = Guid.NewGuid().ToString("N");
        await sandbox.Catalog().StoreReceiptAsync(IdempotencyReceipt.Pending("key", "Synthetic", id), Token);
        string root = sandbox.ProjectRoot(id), bootstrap = Path.Combine(root, ".studio-bootstrap"); Directory.CreateDirectory(bootstrap);
        File.WriteAllText(Path.Combine(bootstrap, "unrecognized.json"), "{\"Version\":99,\"Purpose\":\"wrong\"}");
        File.WriteAllBytes(Path.Combine(root, "project.sqlite"), []);
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
        await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().CreateAsync(new("Synthetic"), "key", Token));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Fact]
    public async Task Fresh_project_attempt_leaves_unrecognized_poisoned_and_hardlinked_attempts_untouched()
    {
        using var sandbox = new CatalogSandbox(); string id = Guid.NewGuid().ToString("N");
        await sandbox.Catalog().StoreReceiptAsync(IdempotencyReceipt.Pending("key", "Synthetic", id), Token);
        string bootstrap = Path.Combine(sandbox.ProjectRoot(id), ".studio-bootstrap"); Directory.CreateDirectory(bootstrap);
        string sentinel = Path.Combine(sandbox.Root, "sentinel"); File.WriteAllText(sentinel, "Unowned sentinel");
        string alias = Path.Combine(bootstrap, Guid.NewGuid().ToString("N") + ".sqlite"); Assert.True(CreateHardLinkW(alias, sentinel, 0));
        string marker = Path.Combine(bootstrap, "partial.json"); File.WriteAllText(marker, "{\"Version\":1,");
        Assert.Empty(await sandbox.Service().ListAsync(Token));
        Assert.Equal(id, (await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token)).Snapshot.Id);
        Assert.Equal("Unowned sentinel", File.ReadAllText(sentinel)); Assert.Equal("Unowned sentinel", File.ReadAllText(alias));
        Assert.Equal("{\"Version\":1,", File.ReadAllText(marker));
    }

    [Theory]
    [InlineData("bootstrap-main-reserved")]
    [InlineData("bootstrap-marker-partial")]
    [InlineData("bootstrap-marker-committed")]
    [InlineData("bootstrap-schema-committed")]
    [InlineData("bootstrap-initialized")]
    [InlineData("bootstrap-checkpointed")]
    [InlineData("bootstrap-published")]
    public async Task Interrupted_project_bootstrap_retries_same_receipt_and_preserves_previous_attempts(string boundary)
    {
        using var sandbox = new CatalogSandbox();
        var core = new ProjectService(); using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bool observed = false;
        typeof(ProjectService).GetField("lifecycleObserver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(core, (Action<string>)(current => { if (current == boundary) { observed = true; canceled.Cancel(); } }));
        var service = new StudioProjectService(new(sandbox.Root), sandbox.Catalog(), core);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(new("Synthetic"), "interrupted", canceled.Token));
        Assert.True(observed);
        var receipt = (await sandbox.Catalog().FindReceiptAsync("interrupted", Token))!;
        Assert.False(receipt.Completed);
        string root = sandbox.ProjectRoot(receipt.ProjectId);
        var previous = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(x => x, ReadShared);
        var retry = await sandbox.Service().CreateAsync(new("Synthetic"), "interrupted", Token);
        Assert.Equal(receipt.ProjectId, retry.Snapshot.Id); Assert.Equal("Synthetic", retry.Snapshot.DisplayName);
        foreach (var file in previous.Where(x => x.Key.Contains(".studio-bootstrap"))) Assert.Equal(file.Value, ReadShared(file.Key));
        Assert.False((await sandbox.Service().CreateAsync(new("Synthetic"), "interrupted", Token)).Created);
        Assert.Single(await sandbox.Service().ListAsync(Token));
        Assert.Single(Directory.GetDirectories(Path.Combine(sandbox.Root, "projects")));
        Assert.True((await sandbox.Catalog().FindReceiptAsync("interrupted", Token))!.Completed);
    }

    [Fact]
    public async Task Interrupted_catalog_bootstrap_can_restart_without_accepting_an_unowned_empty_main()
    {
        using var sandbox = new CatalogSandbox();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var catalog = sandbox.Catalog();
        Observe(catalog, boundary => { if (boundary is "catalog-validated" or "bootstrap-main-reserved") canceled.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ListAsync(canceled.Token));
        Assert.Empty(await sandbox.Catalog().ListAsync(Token));
        using var unowned = new CatalogSandbox();
        string path = Path.Combine(unowned.Root, "catalog.sqlite"); File.WriteAllBytes(path, []);
        await Assert.ThrowsAsync<StudioException>(() => unowned.Catalog().ListAsync(Token));
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("id", false)] [InlineData("ticks", false)] [InlineData("quarantined", false)]
    [InlineData("key", false)] [InlineData("fingerprint", false)] [InlineData("receipt-id", false)]
    [InlineData("completed", false)] [InlineData("orphan-completed", false)]
    [InlineData("id", true)] [InlineData("ticks", true)] [InlineData("quarantined", true)]
    [InlineData("key", true)] [InlineData("fingerprint", true)] [InlineData("receipt-id", true)]
    [InlineData("completed", true)] [InlineData("orphan-completed", true)]
    public async Task Poisoned_catalog_rows_are_rejected_before_original_writes(string poison, bool wal)
    {
        using var sandbox = new CatalogSandbox();
        await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        string path = Path.Combine(sandbox.Root, "catalog.sqlite");
        CatalogSandbox.Sql(sandbox.Root, "PRAGMA journal_mode=DELETE", "catalog.sqlite");
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        writer.Open(); using var command = writer.CreateCommand();
        command.CommandText = (wal ? "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; " : "") +
            "PRAGMA ignore_check_constraints=ON; " + (poison switch
            {
                "id" => "UPDATE projects SET id='INVALID'",
                "ticks" => "UPDATE projects SET last_opened_ticks=9223372036854775807",
                "quarantined" => "UPDATE projects SET quarantined=2",
                "key" => "UPDATE receipts SET key_hash=upper(key_hash)",
                "fingerprint" => "UPDATE receipts SET fingerprint='bad'",
                "receipt-id" => "UPDATE receipts SET project_id='INVALID'",
                "completed" => "UPDATE receipts SET completed=2",
                _ => "DELETE FROM projects"
            });
        command.ExecuteNonQuery();
        if (wal) LocalAudiobookStudio.Core.Storage.SqliteSidecarLifetime.Retain(writer);
        writer.Close();
        if (wal) Assert.True(new FileInfo(path + "-wal").Length > 0);
        var before = Directory.GetFiles(sandbox.Root, "catalog.sqlite*").ToDictionary(x => x, ReadShared);
        Assert.NotNull(await Record.ExceptionAsync(() => sandbox.Catalog().ListAsync(Token)));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(sandbox.Root, "catalog.sqlite*").Order());
        foreach (var file in before) Assert.Equal(file.Value, ReadShared(file.Key));
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
    }

    [Fact]
    public async Task Cancellation_at_catalog_validation_boundary_propagates_without_touching_open_timestamp()
    {
        using var sandbox = new CatalogSandbox();
        var created = await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var catalog = sandbox.Catalog();
        Observe(catalog, boundary => { if (boundary == "catalog-validated") canceled.Cancel(); });
        var service = new StudioProjectService(new(sandbox.Root), catalog, new ProjectService());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.OpenAsync(created.Snapshot.Id, canceled.Token));
        Assert.Equal(DateTimeOffset.MinValue, (await sandbox.Catalog().FindAsync(created.Snapshot.Id, Token))!.LastOpenedUtc);
        Assert.Equal(created.Snapshot.Id, (await sandbox.Service().OpenAsync(created.Snapshot.Id, Token)).Id);
    }

    [Fact]
    public async Task Catalog_main_identity_stays_pinned_between_snapshot_validation_and_writable_open()
    {
        using var sandbox = new CatalogSandbox(); await sandbox.Catalog().ListAsync(Token);
        var catalog = sandbox.Catalog(); string path = Path.Combine(sandbox.Root, "catalog.sqlite");
        bool observed = false, blocked = false;
        Observe(catalog, boundary =>
        {
            if (boundary != "catalog-validated") return;
            observed = true;
            try { File.Move(path, path + ".replacement"); File.Move(path + ".replacement", path); }
            catch (IOException) { blocked = true; }
        });
        Assert.Empty(await catalog.ListAsync(Token)); Assert.True(observed); Assert.True(blocked);
        File.Move(path, path + ".released"); File.Move(path + ".released", path);
    }

    [Fact]
    public async Task Concurrently_appearing_future_catalog_is_validated_without_memory_bootstrap_or_mutation()
    {
        using var sandbox = new CatalogSandbox(); var catalog = sandbox.Catalog();
        string path = Path.Combine(sandbox.Root, "catalog.sqlite"); byte[]? before = null;
        Observe(catalog, boundary =>
        {
            if (boundary != "before-main-reservation") return;
            CatalogSandbox.Sql(sandbox.Root, "PRAGMA journal_mode=DELETE; PRAGMA user_version=99", "catalog.sqlite");
            before = File.ReadAllBytes(path);
        });
        var error = await Record.ExceptionAsync(() => catalog.ListAsync(Token));
        Assert.NotNull(before); Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("catalog.schema.future", Assert.IsType<StudioException>(error).Code);
        Assert.False(File.Exists(path + "-wal")); Assert.False(File.Exists(path + "-shm"));
    }

    [Fact]
    public async Task Atomically_created_catalog_main_cannot_be_replaced_before_open_or_during_connection()
    {
        using var sandbox = new CatalogSandbox(); var catalog = sandbox.Catalog();
        string path = Path.Combine(sandbox.Root, "catalog.sqlite"); bool observed = false, beforeOpenBlocked = false, connectionBlocked = false;
        Observe(catalog, boundary =>
        {
            if (boundary is not ("catalog-validated" or "connection-open")) return;
            try { File.Move(path, path + ".replacement"); File.Move(path + ".replacement", path); }
            catch (FileNotFoundException) { }
            catch (IOException)
            {
                if (boundary == "catalog-validated") beforeOpenBlocked = true; else connectionBlocked = true;
            }
            observed = true;
        });
        Assert.Empty(await catalog.ListAsync(Token)); Assert.True(observed); Assert.True(beforeOpenBlocked); Assert.True(connectionBlocked);
        File.Move(path, path + ".released"); File.Move(path + ".released", path);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Pending_generated_premain_sidecars_resume_same_identity_and_fingerprint_after_restart(int sidecarCount)
    {
        using var sandbox = new CatalogSandbox(); string id = Guid.NewGuid().ToString("N");
        var receipt = IdempotencyReceipt.Pending("crash-key", "Synthetic", id);
        await sandbox.Catalog().StoreReceiptAsync(receipt, Token);
        object paths = CatalogPathsOf(sandbox.Catalog());
        using ((IDisposable)paths.GetType().GetMethod("PinProjectForWrite")!.Invoke(paths, [id, true])!) { }
        string main = Path.Combine(sandbox.ProjectRoot(id), "project.sqlite");
        if (sidecarCount == 1) { File.Delete(main + "-shm"); File.Delete(main + "-journal"); }
        if (sidecarCount == 2) File.Delete(main + "-journal");
        Assert.False(File.Exists(main)); Assert.Empty(await sandbox.Service().ListAsync(Token));
        Assert.Equal(id, Assert.Single(await sandbox.Catalog().ReconcileAsync(Token)).Id);
        Assert.Equal("project.unavailable", (await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().GetAsync(id, Token))).Code);
        Assert.Equal(id, Assert.Single(await sandbox.Catalog().ReconcileAsync(Token)).Id);
        var retry = await sandbox.Service().CreateAsync(new("Synthetic"), "crash-key", Token);
        Assert.Equal(id, retry.Snapshot.Id); Assert.Equal("Synthetic", retry.Snapshot.DisplayName); Assert.True(retry.Created);
        Assert.False((await sandbox.Service().CreateAsync(new("Synthetic"), "crash-key", Token)).Created);
        var completed = (await sandbox.Catalog().FindReceiptAsync("crash-key", Token))!;
        Assert.True(completed.Completed); Assert.Equal(receipt.RequestFingerprint, completed.RequestFingerprint);
        Assert.Equal(DateTimeOffset.MinValue, Assert.Single(await sandbox.Service().ListAsync(Token)).LastOpenedUtc);
    }

    [Theory]
    [InlineData("nonzero")]
    [InlineData("hardlink")]
    [InlineData("zero-hardlink")]
    [InlineData("unknown")]
    [InlineData("not-pending")]
    public async Task Unsafe_or_unowned_premain_contents_are_never_resumed_or_removed(string kind)
    {
        using var sandbox = new CatalogSandbox(); string id = Guid.NewGuid().ToString("N");
        if (kind != "not-pending") await sandbox.Catalog().StoreReceiptAsync(IdempotencyReceipt.Pending("key", "Synthetic", id), Token);
        Directory.CreateDirectory(sandbox.ProjectRoot(id)); string sidecar = Path.Combine(sandbox.ProjectRoot(id), "project.sqlite-wal");
        string sentinel = Path.Combine(sandbox.Root, "sentinel"); File.WriteAllText(sentinel, kind == "zero-hardlink" ? "" : "Synthetic sentinel");
        if (kind is "hardlink" or "zero-hardlink") Assert.True(CreateHardLinkW(sidecar, sentinel, 0));
        else File.WriteAllText(sidecar, kind == "nonzero" ? "Nonempty" : "");
        if (kind == "unknown") File.WriteAllText(Path.Combine(sandbox.ProjectRoot(id), "unknown"), "Preserve");
        byte[] before = File.ReadAllBytes(sidecar);
        Assert.Empty(await sandbox.Service().ListAsync(Token));
        if (kind != "not-pending") Assert.Equal("project.unavailable", (await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().CreateAsync(new("Synthetic"), "key", Token))).Code);
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectRoot(id), "project.sqlite"))); Assert.Equal(before, File.ReadAllBytes(sidecar));
        Assert.Equal(kind == "zero-hardlink" ? "" : "Synthetic sentinel", File.ReadAllText(sentinel));
        if (kind == "unknown") Assert.Equal("Preserve", File.ReadAllText(Path.Combine(sandbox.ProjectRoot(id), "unknown")));
    }

    [Fact]
    public async Task Pending_premain_reparse_sidecar_is_not_followed_or_removed()
    {
        using var sandbox = new CatalogSandbox(); string id = Guid.NewGuid().ToString("N");
        await sandbox.Catalog().StoreReceiptAsync(IdempotencyReceipt.Pending("key", "Synthetic", id), Token);
        Directory.CreateDirectory(sandbox.ProjectRoot(id)); string external = Path.Combine(sandbox.Root, "external"); Directory.CreateDirectory(external);
        string sentinel = Path.Combine(external, "sentinel"); File.WriteAllText(sentinel, "Synthetic sentinel");
        string junction = Path.Combine(sandbox.ProjectRoot(id), "project.sqlite-wal");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(external);
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(Token); Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Empty(await sandbox.Service().ListAsync(Token));
            var error = await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().CreateAsync(new("Synthetic"), "key", Token));
            Assert.Equal("project.unavailable", error.Code); Assert.Equal(error.Code, error.Message); Assert.Null(error.InnerException);
            Assert.True(Directory.Exists(junction)); Assert.Equal("Synthetic sentinel", File.ReadAllText(sentinel));
            Assert.False(File.Exists(Path.Combine(sandbox.ProjectRoot(id), "project.sqlite")));
        }
        finally { Directory.Delete(junction); }
    }

    private static void Observe(SqliteProjectCatalog catalog, Action<string> observer)
        => typeof(SqliteProjectCatalog).GetField("lifecycleObserver", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(catalog, observer);

    [Theory]
    [InlineData("-wal", false)]
    [InlineData("-shm", false)]
    [InlineData("-journal", false)]
    [InlineData("-wal", true)]
    [InlineData("-shm", true)]
    [InlineData("-journal", true)]
    public async Task Static_sidecar_hard_links_are_rejected_before_sqlite_and_external_bytes_are_preserved(string suffix, bool catalogSidecar)
    {
        using var sandbox = new CatalogSandbox();
        var bad = await sandbox.Service().CreateAsync(new("Bad"), "bad", Token);
        var good = await sandbox.Service().CreateAsync(new("Good"), "good", Token);
        string db = catalogSidecar ? Path.Combine(sandbox.Root, "catalog.sqlite") : Path.Combine(sandbox.ProjectRoot(bad.Snapshot.Id), "project.sqlite");
        string external = Path.Combine(sandbox.Root, "external-sentinel"); File.WriteAllText(external, "Synthetic sentinel");
        byte[] before = File.ReadAllBytes(db);
        File.Delete(db + suffix); Assert.True(CreateHardLinkW(db + suffix, external, 0));
        if (catalogSidecar)
        {
            var error = await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().ListAsync(Token));
            Assert.Equal("catalog.path", error.Code); Assert.Equal(error.Code, error.Message); Assert.Null(error.InnerException);
        }
        else
        {
            Assert.Equal(good.Snapshot.Id, Assert.Single(await sandbox.Service().ListAsync(Token)).Id);
            Assert.Null(await sandbox.Catalog().FindAsync(bad.Snapshot.Id, Token));
        }
        Assert.Equal("Synthetic sentinel", File.ReadAllText(external)); Assert.Equal(before, File.ReadAllBytes(db));
        Assert.Empty(Directory.GetDirectories(sandbox.Root, "sqlite-probe-*"));
    }

    [Fact]
    public async Task Active_catalog_connection_blocks_another_process_from_replacing_reserved_journal()
    {
        using var sandbox = new CatalogSandbox(); var catalog = sandbox.Catalog();
        await catalog.ListAsync(Token);
        Func<SqliteConnection, CancellationToken, Task<bool>> action = async (db, token) =>
        {
            string journal = Path.Combine(sandbox.Root, "catalog.sqlite-journal");
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("move"); start.ArgumentList.Add("/y"); start.ArgumentList.Add(journal); start.ArgumentList.Add(Path.Combine(sandbox.Root, "replacement-window"));
            using var attacker = System.Diagnostics.Process.Start(start)!; await attacker.WaitForExitAsync(token);
            Assert.NotEqual(0, attacker.ExitCode); Assert.True(File.Exists(journal));
            using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM projects";
            Assert.Equal(0L, await command.ExecuteScalarAsync(token)); return true;
        };
        var method = typeof(SqliteProjectCatalog).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.MakeGenericMethod(typeof(bool));
        Assert.True(await (Task<bool>)method.Invoke(catalog, [action, Token])!);
        Assert.False(File.Exists(Path.Combine(sandbox.Root, "replacement-window")));
    }

    [Fact]
    public async Task Retained_project_sidecars_allow_checkpoint_reopen_and_normal_cleanup_after_pins_release()
    {
        using var sandbox = new CatalogSandbox();
        var created = await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        string root = sandbox.ProjectRoot(created.Snapshot.Id), dbPath = Path.Combine(root, "project.sqlite");
        object paths = CatalogPathsOf(sandbox.Catalog());
        using (var pins = (IDisposable)paths.GetType().GetMethod("PinProjectForWrite")!.Invoke(paths, [created.Snapshot.Id, false])!)
        {
            await using (var project = await new ProjectService().OpenAsync(root, Token, retainSidecars: true))
                await project.SaveAsync(project.State with { Metadata = project.State.Metadata with { Title = "Checkpointed" } }, Token);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
            {
                db.Open(); LocalAudiobookStudio.Core.Storage.SqliteSidecarLifetime.Retain(db);
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                using var reader = command.ExecuteReader(); Assert.True(reader.Read()); Assert.Equal(0, reader.GetInt64(0));
            }
            Assert.Equal(0, new FileInfo(dbPath + "-wal").Length);
            Assert.Throws<IOException>(() => File.Move(dbPath, dbPath + ".replaced"));
            Assert.Throws<IOException>(() => Directory.Move(root, root + ".replaced"));
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) Assert.Throws<IOException>(() => File.Delete(dbPath + suffix));
        }
        CatalogSandbox.Sql(root, "PRAGMA wal_checkpoint(TRUNCATE)");
        Assert.False(File.Exists(dbPath + "-wal")); Assert.False(File.Exists(dbPath + "-shm"));
        Assert.Equal(0, new FileInfo(dbPath + "-journal").Length);
        Assert.Equal("Checkpointed", (await sandbox.Service().GetAsync(created.Snapshot.Id, Token)).DisplayName);
        Assert.False(File.Exists(dbPath + "-wal")); Assert.False(File.Exists(dbPath + "-shm"));
        Assert.Empty(Directory.GetDirectories(sandbox.Root, "sqlite-probe-*"));
    }

    [Fact]
    public async Task Hot_journal_is_copied_and_rejected_readonly_without_silently_discarding_rollback_state()
    {
        using var sandbox = new CatalogSandbox();
        var created = await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        string root = sandbox.ProjectRoot(created.Snapshot.Id), db = Path.Combine(root, "project.sqlite");
        CatalogSandbox.Sql(root, "PRAGMA journal_mode=DELETE");
        byte[] journal = new byte[1024]; new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }.CopyTo(journal, 0);
        File.WriteAllBytes(db + "-journal", journal); byte[] before = File.ReadAllBytes(db);
        Assert.Empty(await sandbox.Service().ListAsync(Token));
        Assert.NotNull(await sandbox.Catalog().FindAsync(created.Snapshot.Id, Token));
        Assert.Equal(before, File.ReadAllBytes(db)); Assert.Equal(journal, File.ReadAllBytes(db + "-journal"));
        Assert.False(File.Exists(db + "-wal")); Assert.False(File.Exists(db + "-shm"));
        Assert.Empty(Directory.GetDirectories(sandbox.Root, "sqlite-probe-*"));
    }


    [Fact]
    public async Task Valid_delete_mode_project_and_catalog_open_durably_with_guarded_names()
    {
        using var sandbox = new CatalogSandbox();
        var created = await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        CatalogSandbox.Sql(sandbox.ProjectRoot(created.Snapshot.Id), "PRAGMA journal_mode=DELETE");
        CatalogSandbox.Sql(sandbox.Root, "PRAGMA journal_mode=DELETE", "catalog.sqlite");
        Assert.Equal(created.Snapshot.Id, (await sandbox.Service().OpenAsync(created.Snapshot.Id, Token)).Id);
        Assert.Equal(created.Snapshot.Id, (await sandbox.Service().GetAsync(created.Snapshot.Id, Token)).Id);
        Assert.Equal(created.Snapshot.Id, Assert.Single(await sandbox.Service().ListAsync(Token)).Id);
    }

    [Fact]
    public async Task Canceling_an_active_streaming_copy_removes_its_generated_files_and_preserves_catalog()
    {
        using var sandbox = new CatalogSandbox();
        var created = await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        string db = Path.Combine(sandbox.ProjectRoot(created.Snapshot.Id), "project.sqlite");
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var type = typeof(SqliteProjectCatalog).Assembly.GetType("LocalAudiobookStudio.Application.Projects.GuardedSqliteSnapshot")!;
        var field = type.GetField("copyObserver", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var observer = (AsyncLocal<Action<long>?>)field.GetValue(null)!;
        long copiedBytes = 0;
        observer.Value = bytes => { copiedBytes = bytes; canceled.Cancel(); };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            {
                object paths = CatalogPathsOf(sandbox.Catalog());
                try { using var snapshot = (IDisposable)type.GetMethod("Create")!.Invoke(null, [paths, db, canceled.Token, false])!; }
                catch (System.Reflection.TargetInvocationException error) when (error.InnerException is OperationCanceledException canceledError)
                { throw canceledError; }
            }, Token));
        }
        finally { observer.Value = null; }
        Assert.True(copiedBytes > 0); // Actual target bytes, not a fleeting directory name or timer race.
        Assert.Empty(Directory.GetDirectories(sandbox.Root, "sqlite-probe-*"));
        Assert.Equal(DateTimeOffset.MinValue, (await sandbox.Catalog().FindAsync(created.Snapshot.Id, Token))!.LastOpenedUtc);
        Assert.Equal(created.Snapshot.Id, (await sandbox.Service().GetAsync(created.Snapshot.Id, Token)).Id);
    }

    [Fact]
    public async Task Oversize_snapshot_cleans_its_copy_without_quarantining_a_valid_project()
    {
        using var sandbox = new CatalogSandbox();
        var created = await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        string db = Path.Combine(sandbox.ProjectRoot(created.Snapshot.Id), "project.sqlite");
        using (var file = new FileStream(db, FileMode.Open, FileAccess.Write, FileShare.None)) file.SetLength(2 * 1024 * 1024);
        var options = new ProjectCatalogOptions(sandbox.Root) { MaximumSnapshotBytes = 1024 * 1024 };
        var limited = new StudioProjectService(options, new SqliteProjectCatalog(options), new ProjectService());
        Assert.Empty(await limited.ListAsync(Token));
        Assert.Equal("project.unavailable", (await Assert.ThrowsAsync<StudioException>(() => limited.GetAsync(created.Snapshot.Id, Token))).Code);
        Assert.NotNull(await sandbox.Catalog().FindAsync(created.Snapshot.Id, Token));
        Assert.Empty(Directory.GetDirectories(sandbox.Root, "sqlite-probe-*"));
        Assert.Equal(created.Snapshot.Id, (await sandbox.Service().GetAsync(created.Snapshot.Id, Token)).Id);
    }


    [Theory]
    [InlineData("-wal", false)]
    [InlineData("-shm", false)]
    [InlineData("-journal", false)]
    [InlineData("-wal", true)]
    [InlineData("-shm", true)]
    [InlineData("-journal", true)]
    public async Task Writable_catalog_pin_reserves_missing_sidecars_and_blocks_replacement_until_disposed(string suffix, bool present)
    {
        using var sandbox = new CatalogSandbox();
        await sandbox.Catalog().ListAsync(Token);
        string sidecar = Path.Combine(sandbox.Root, "catalog.sqlite") + suffix;
        if (present) File.WriteAllText(sidecar, "");
        var catalog = sandbox.Catalog();
        using (var pins = Pin(catalog, "PinCatalog"))
        {
            Assert.True(File.Exists(sidecar));
            Assert.Throws<IOException>(() => File.Delete(sidecar));
            string external = Path.Combine(sandbox.Root, "sentinel"); File.WriteAllText(external, "Synthetic sentinel");
            Assert.False(CreateHardLinkW(sidecar, external, 0));
            Assert.Equal("Synthetic sentinel", File.ReadAllText(external));
        }
        File.Delete(sidecar);
    }

    [Fact]
    public async Task Snapshot_probe_reads_committed_wal_without_creating_original_missing_shm()
    {
        using var sandbox = new CatalogSandbox(); string id = Guid.NewGuid().ToString("N");
        string external = Path.Combine(sandbox.Root, "synthetic-source");
        await using (var project = await new ProjectService().CreateAsync(external, "Old", id, Token))
        {
            await project.SaveAsync(project.State with { Metadata = project.State.Metadata with { Title = "Committed in WAL" } }, Token);
            Directory.CreateDirectory(sandbox.ProjectRoot(id));
            foreach (string suffix in new[] { "", "-wal" })
                File.Copy(Path.Combine(external, "project.sqlite") + suffix, Path.Combine(sandbox.ProjectRoot(id), "project.sqlite") + suffix);
        }
        string db = Path.Combine(sandbox.ProjectRoot(id), "project.sqlite");
        byte[] mainBefore = File.ReadAllBytes(db), walBefore = File.ReadAllBytes(db + "-wal");
        var result = Assert.Single(await sandbox.Service().ListAsync(Token));
        Assert.Equal("Committed in WAL", result.DisplayName); Assert.Equal(2, result.Revision);
        Assert.False(File.Exists(db + "-shm")); Assert.False(File.Exists(db + "-journal"));
        Assert.Equal(mainBefore, File.ReadAllBytes(db)); Assert.Equal(walBefore, File.ReadAllBytes(db + "-wal"));
        Assert.Empty(Directory.GetDirectories(sandbox.Root, "sqlite-probe-*"));
        Assert.Equal("Committed in WAL", (await sandbox.Service().OpenAsync(id, Token)).DisplayName);
        Assert.Equal("Committed in WAL", (await sandbox.Service().GetAsync(id, Token)).DisplayName);
    }

    private static IDisposable Pin(SqliteProjectCatalog catalog, string method)
    {
        object paths = CatalogPathsOf(catalog);
        return (IDisposable)paths.GetType().GetMethod(method)!.Invoke(paths, null)!;
    }

    private static object CatalogPathsOf(SqliteProjectCatalog catalog) => typeof(SqliteProjectCatalog).GetField("paths", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(catalog)!;

    [Fact]
    public async Task Sharing_interruption_does_not_quarantine_project_and_other_projects_remain_visible()
    {
        using var sandbox = new CatalogSandbox();
        var interrupted = await sandbox.Service().CreateAsync(new("Interrupted"), "interrupted", Token);
        var good = await sandbox.Service().CreateAsync(new("Good"), "good", Token);
        using (var denied = new FileStream(Path.Combine(sandbox.ProjectRoot(interrupted.Snapshot.Id), "project.sqlite"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(good.Snapshot.Id, Assert.Single(await sandbox.Service().ListAsync(Token)).Id);
            var error = await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().GetAsync(interrupted.Snapshot.Id, Token));
            Assert.Equal("project.unavailable", error.Code); Assert.Equal(error.Code, error.Message); Assert.Null(error.InnerException);
            Assert.NotNull(await sandbox.Catalog().FindAsync(interrupted.Snapshot.Id, Token));
        }
        Assert.Equal(interrupted.Snapshot.Id, (await sandbox.Service().GetAsync(interrupted.Snapshot.Id, Token)).Id);
        Assert.Equal(2, (await sandbox.Service().ListAsync(Token)).Count);
    }

    [Fact]
    public async Task Access_denial_does_not_quarantine_project_and_get_succeeds_after_access_is_restored()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var sandbox = new CatalogSandbox();
        var created = await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        string path = Path.Combine(sandbox.ProjectRoot(created.Snapshot.Id), "project.sqlite");
        var file = new FileInfo(path);
        var original = System.IO.FileSystemAclExtensions.GetAccessControl(file);
        var denied = System.IO.FileSystemAclExtensions.GetAccessControl(file);
        denied.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.ReadData,
            System.Security.AccessControl.AccessControlType.Deny));
        try
        {
            System.IO.FileSystemAclExtensions.SetAccessControl(file, denied);
            var error = await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().GetAsync(created.Snapshot.Id, Token));
            Assert.Equal("project.unavailable", error.Code); Assert.Equal(error.Code, error.Message); Assert.Null(error.InnerException);
            Assert.Empty(await sandbox.Service().ListAsync(Token));
            Assert.NotNull(await sandbox.Catalog().FindAsync(created.Snapshot.Id, Token));
        }
        finally
        {
            // Mark the original DACL as modified so SetAccessControl writes it back.
            original.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access), System.Security.AccessControl.AccessControlSections.Access);
            System.IO.FileSystemAclExtensions.SetAccessControl(file, original);
        }
        Assert.Equal(created.Snapshot.Id, (await sandbox.Service().GetAsync(created.Snapshot.Id, Token)).Id);
        Assert.Equal(created.Snapshot.Id, Assert.Single(await sandbox.Service().ListAsync(Token)).Id);
    }

    [Fact]
    public async Task Sqlite_exclusive_lock_does_not_quarantine_project_and_later_probe_succeeds()
    {
        using var sandbox = new CatalogSandbox();
        var interrupted = await sandbox.Service().CreateAsync(new("Interrupted"), "interrupted", Token);
        var good = await sandbox.Service().CreateAsync(new("Good"), "good", Token);
        using (var locked = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(sandbox.ProjectRoot(interrupted.Snapshot.Id), "project.sqlite"), Pooling = false }.ToString()))
        {
            locked.Open(); using var command = locked.CreateCommand(); command.CommandText = "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
            Assert.Equal(good.Snapshot.Id, Assert.Single(await sandbox.Service().ListAsync(Token)).Id);
            Assert.NotNull(await sandbox.Catalog().FindAsync(interrupted.Snapshot.Id, Token));
            command.CommandText = "ROLLBACK"; command.ExecuteNonQuery();
        }
        Assert.Equal(interrupted.Snapshot.Id, (await sandbox.Service().GetAsync(interrupted.Snapshot.Id, Token)).Id);
        Assert.Equal(2, (await sandbox.Service().ListAsync(Token)).Count);
    }

    [Fact]
    public async Task Service_commits_receipt_before_core_creation_and_restarts_after_finalize_failure()
    {
        using var sandbox = new CatalogSandbox();
        var fault = new FinalizationFailure(sandbox);
        var service = new StudioProjectService(new(sandbox.Root), fault, new ProjectService());
        Assert.Equal("test.crash", (await Assert.ThrowsAsync<StudioException>(() => service.CreateAsync(new("Synthetic"), "key", Token))).Code);
        var pending = await sandbox.Catalog().FindReceiptAsync("key", Token);
        Assert.NotNull(pending); Assert.False(pending.Completed);
        Assert.Equal(pending.ProjectId, (await new ProjectService().ProbeAsync(sandbox.ProjectRoot(pending.ProjectId), Token)).Metadata.Id);
        Assert.Empty(await sandbox.Catalog().ListAsync(Token));
        var retry = await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        Assert.Equal(pending.ProjectId, retry.Snapshot.Id);
        Assert.Single(Directory.GetDirectories(Path.Combine(sandbox.Root, "projects")));
        Assert.False((await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token)).Created);
    }

    [Fact]
    public async Task Pending_reservation_with_created_but_empty_directory_can_resume()
    {
        using var sandbox = new CatalogSandbox(); string id = Guid.NewGuid().ToString("N");
        await sandbox.Catalog().StoreReceiptAsync(IdempotencyReceipt.Pending("key", "Synthetic", id), Token);
        Directory.CreateDirectory(sandbox.ProjectRoot(id));
        Assert.Empty(await sandbox.Service().ListAsync(Token));
        var result = await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        Assert.Equal(id, result.Snapshot.Id); Assert.Single(await sandbox.Service().ListAsync(Token));
    }

    [Fact]
    public async Task Browser_open_reads_an_owned_project_and_failed_revision_does_not_touch_timestamp()
    {
        using var sandbox = new CatalogSandbox();
        var created = await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        await using (var project = await new ProjectService().OpenAsync(sandbox.ProjectRoot(created.Snapshot.Id), Token, retainSidecars: true))
        await using (var lease = await project.AcquireArtifactLeaseAsync(cancellationToken: Token))
        {
            Assert.Equal("project.conflict", (await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().OpenAsync(created.Snapshot.Id, created.Snapshot.Revision + 1, Token))).Code);
            Assert.Equal(DateTimeOffset.MinValue, (await sandbox.Catalog().FindAsync(created.Snapshot.Id, Token))!.LastOpenedUtc);
            var opened = await sandbox.Service().OpenAsync(created.Snapshot.Id, created.Snapshot.Revision, Token);
            Assert.Equal(created.Snapshot, opened);
            Assert.True((await sandbox.Catalog().FindAsync(created.Snapshot.Id, Token))!.LastOpenedUtc > DateTimeOffset.MinValue);
        }
        var item = Assert.Single(await sandbox.Service().ListAsync(Token));
        Assert.True(item.LastOpenedUtc > DateTimeOffset.MinValue);
        Assert.Equal(created.Snapshot.Id, (await sandbox.Service().OpenAsync(created.Snapshot.Id, Token)).Id);
    }

    [Fact]
    public async Task Database_hard_link_alias_is_quarantined_without_writing_external_database()
    {
        using var sandbox = new CatalogSandbox();
        string bad = Guid.NewGuid().ToString("N");
        string external = Path.Combine(sandbox.Root, "external");
        await using (var project = await new ProjectService().CreateAsync(external, "Synthetic", bad, Token)) { }
        CatalogSandbox.Sql(external, "PRAGMA journal_mode=DELETE");
        string externalDb = Path.Combine(external, "project.sqlite"); byte[] before = File.ReadAllBytes(externalDb);
        Directory.CreateDirectory(sandbox.ProjectRoot(bad));
        Assert.True(CreateHardLinkW(Path.Combine(sandbox.ProjectRoot(bad), "project.sqlite"), externalDb, 0));
        var good = await sandbox.Service().CreateAsync(new("Good"), "good", Token);
        Assert.Equal(good.Snapshot.Id, Assert.Single(await sandbox.Service().ListAsync(Token)).Id);
        Assert.Equal("project.unavailable", (await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().OpenAsync(bad, Token))).Code);
        Assert.Equal(before, File.ReadAllBytes(externalDb)); Assert.False(File.Exists(externalDb + "-wal"));
    }

    [Fact]
    public async Task Project_junction_is_quarantined_without_following_or_moving_it()
    {
        using var sandbox = new CatalogSandbox();
        string bad = Guid.NewGuid().ToString("N"), external = Path.Combine(sandbox.Root, "external");
        await using (var project = await new ProjectService().CreateAsync(external, "Synthetic", bad, Token)) { }
        Directory.CreateDirectory(Path.Combine(sandbox.Root, "projects"));
        string junction = sandbox.ProjectRoot(bad);
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(external);
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(Token); Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Empty(await sandbox.Service().ListAsync(Token));
            Assert.Equal("project.unavailable", (await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().GetAsync(bad, Token))).Code);
            Assert.True(Directory.Exists(junction)); Assert.True(File.Exists(Path.Combine(external, "project.sqlite")));
            Assert.Throws<StudioException>(() => new SqliteProjectCatalog(new(junction)));
        }
        finally { Directory.Delete(junction); }
    }

    [Theory]
    [InlineData("catalog.sqlite")]
    [InlineData("catalog.lock")]
    [InlineData("studio.lock")]
    public async Task Catalog_and_gate_hard_links_fail_closed(string filename)
    {
        using var sandbox = new CatalogSandbox();
        string external = Path.Combine(sandbox.Root, "external-file"); File.WriteAllText(external, "Synthetic sentinel");
        Assert.True(CreateHardLinkW(Path.Combine(sandbox.Root, filename), external, 0));
        var error = await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().ListAsync(Token));
        Assert.Equal("catalog.path", error.Code); Assert.Equal(error.Code, error.Message);
        Assert.Equal("Synthetic sentinel", File.ReadAllText(external));
    }

    [Fact]
    public async Task Receipt_conflicts_and_completed_state_are_durable_across_catalog_instances()
    {
        using var sandbox = new CatalogSandbox(); string id = Guid.NewGuid().ToString("N");
        var pending = IdempotencyReceipt.Pending("key", "title", id);
        await sandbox.Catalog().StoreReceiptAsync(pending, Token);
        Assert.Equal(pending, await sandbox.Catalog().FindReceiptAsync("key", Token));
        Assert.Equal("project.idempotency.conflict", (await Assert.ThrowsAsync<StudioException>(() => sandbox.Catalog().StoreReceiptAsync(IdempotencyReceipt.Pending("key", "other", Guid.NewGuid().ToString("N")), Token))).Code);
        await sandbox.Catalog().FinalizeAsync(new(id, DateTimeOffset.MinValue), pending with { Completed = true }, Token);
        await sandbox.Catalog().StoreReceiptAsync(pending, Token);
        Assert.True((await sandbox.Catalog().FindReceiptAsync("key", Token))!.Completed);
        Assert.Single(await sandbox.Catalog().ListAsync(Token));
        await sandbox.Catalog().QuarantineAsync(id, Token);
        Assert.Empty(await sandbox.Catalog().ListAsync(Token)); Assert.Null(await sandbox.Catalog().FindAsync(id, Token));
    }

    [Fact]
    public async Task Canceled_create_does_not_publish_project_or_receipt()
    {
        using var sandbox = new CatalogSandbox();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.Service().CreateAsync(new("Synthetic"), "key", new CancellationToken(true)));
        Assert.Empty(await sandbox.Service().ListAsync(Token)); Assert.Null(await sandbox.Catalog().FindReceiptAsync("key", Token));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string filename, string existingFilename, nint security);

    private sealed class FinalizationFailure(CatalogSandbox sandbox) : IProjectCatalog
    {
        private readonly IProjectCatalog inner = sandbox.Catalog();
        public Task<IReadOnlyList<CatalogProject>> ListAsync(CancellationToken token) => inner.ListAsync(token);
        public Task<CatalogProject?> FindAsync(string id, CancellationToken token) => inner.FindAsync(id, token);
        public Task InsertAsync(CatalogProject project, CancellationToken token) => inner.InsertAsync(project, token);
        public Task<IdempotencyReceipt?> FindReceiptAsync(string key, CancellationToken token) => inner.FindReceiptAsync(key, token);
        public Task StoreReceiptAsync(IdempotencyReceipt receipt, CancellationToken token)
        {
            // This fault boundary proves the durable reservation precedes all Core filesystem work.
            Assert.False(Directory.Exists(sandbox.ProjectRoot(receipt.ProjectId)));
            return inner.StoreReceiptAsync(receipt, token);
        }
        public Task TouchOpenedAsync(string id, DateTimeOffset opened, CancellationToken token) => inner.TouchOpenedAsync(id, opened, token);
        public Task<IReadOnlyList<CatalogCandidate>> ReconcileAsync(CancellationToken token) => inner.ReconcileAsync(token);
        public Task FinalizeAsync(CatalogProject project, IdempotencyReceipt? receipt, CancellationToken token) => throw new StudioException("test.crash");
        public Task QuarantineAsync(string id, CancellationToken token) => inner.QuarantineAsync(id, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_receipt_resumes_same_identity_before_or_after_core_commit(bool coreCommitted)
    {
        using var sandbox = new CatalogSandbox();
        var id = Guid.NewGuid().ToString("N");
        await sandbox.Catalog().StoreReceiptAsync(IdempotencyReceipt.Pending("crash-key", "Книга", id), Token);
        if (coreCommitted)
        {
            await using var project = await new ProjectService().CreateAsync(sandbox.ProjectRoot(id), "Книга", id, Token);
        }
        var result = await sandbox.Service().CreateAsync(new("Книга"), "crash-key", Token);
        Assert.Equal(id, result.Snapshot.Id); Assert.True(result.Created);
        Assert.False((await sandbox.Service().CreateAsync(new("Книга"), "crash-key", Token)).Created);
        Assert.True((await sandbox.Catalog().FindReceiptAsync("crash-key", Token))!.Completed);
        Assert.Single(await sandbox.Service().ListAsync(Token));
    }

    [Fact]
    public async Task Orphan_is_reconciled_and_one_poisoned_future_missing_or_wrong_identity_candidate_does_not_hide_good_project()
    {
        using var sandbox = new CatalogSandbox();
        var core = new ProjectService();
        string good = Guid.NewGuid().ToString("N"), future = Guid.NewGuid().ToString("N"), poison = Guid.NewGuid().ToString("N"), mismatch = Guid.NewGuid().ToString("N");
        foreach (var id in new[] { good, future, poison, mismatch })
        {
            await using var project = await core.CreateAsync(sandbox.ProjectRoot(id), "Synthetic", id == mismatch ? Guid.NewGuid().ToString("N") : id, Token);
        }
        CatalogSandbox.Sql(sandbox.ProjectRoot(future), "PRAGMA journal_mode=DELETE; PRAGMA user_version=99");
        CatalogSandbox.Sql(sandbox.ProjectRoot(poison), "PRAGMA journal_mode=DELETE; UPDATE project SET payload='null'");
        var missing = await sandbox.Service().CreateAsync(new("Missing"), "missing", Token);
        Directory.Delete(sandbox.ProjectRoot(missing.Snapshot.Id), true);
        byte[] futureBefore = File.ReadAllBytes(Path.Combine(sandbox.ProjectRoot(future), "project.sqlite"));
        byte[] poisonBefore = File.ReadAllBytes(Path.Combine(sandbox.ProjectRoot(poison), "project.sqlite"));
        Assert.Equal(good, Assert.Single(await sandbox.Service().ListAsync(Token)).Id);
        Assert.Equal(futureBefore, File.ReadAllBytes(Path.Combine(sandbox.ProjectRoot(future), "project.sqlite")));
        Assert.Equal(poisonBefore, File.ReadAllBytes(Path.Combine(sandbox.ProjectRoot(poison), "project.sqlite")));
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectRoot(future), "project.sqlite-wal")));
        Assert.False(File.Exists(Path.Combine(sandbox.ProjectRoot(poison), "project.sqlite-wal")));
        foreach (var id in new[] { future, poison, mismatch, missing.Snapshot.Id })
        {
            Assert.Equal("project.unavailable", (await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().OpenAsync(id, Token))).Code);
            Assert.Null(await sandbox.Catalog().FindAsync(id, Token));
        }
        Assert.True(Directory.Exists(sandbox.ProjectRoot(future))); Assert.True(Directory.Exists(sandbox.ProjectRoot(poison)));
    }

    [Fact]
    public async Task Future_catalog_schema_fails_closed_without_mutating_database()
    {
        using var sandbox = new CatalogSandbox();
        await sandbox.Service().CreateAsync(new("Synthetic"), "key", Token);
        CatalogSandbox.Sql(sandbox.Root, "PRAGMA journal_mode=DELETE; PRAGMA user_version=99", "catalog.sqlite");
        string path = Path.Combine(sandbox.Root, "catalog.sqlite"); var before = File.ReadAllBytes(path);
        Assert.Equal("catalog.schema.future", (await Assert.ThrowsAsync<StudioException>(() => sandbox.Service().ListAsync(Token))).Code);
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(File.Exists(path + "-wal"));
    }
}

internal sealed class CatalogSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "las-catalog-" + Guid.NewGuid().ToString("N"));
    public CatalogSandbox() => Directory.CreateDirectory(Root);
    public string ProjectRoot(string id) => Path.Combine(Root, "projects", id);
    public SqliteProjectCatalog Catalog() => new(new(Root));
    public StudioProjectService Service() => new(new(Root), Catalog(), new ProjectService());
    public void Dispose() => Directory.Delete(Root, true);
    public static void Sql(string root, string sql, string filename = "project.sqlite")
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, filename), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class ApprovedTtsCorpusResolverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Uses_corrected_manifest_text_and_retained_ordinals_after_draft_replacement()
    {
        using var f = new ApprovedTtsCorpusFixture(); await f.SeedAsync(Token); await f.ReplaceDraftAsync(Token);
        File.Delete(Path.Combine(f.Root, f.AudioPaths[1])); // Excluded audio is not a training input.
        await using var db = await f.OpenAsync(Token); await using var ownership = await db.AcquireInputMutationLeaseAsync(Token);
        using var lease = await new ApprovedTtsCorpusResolver().ResolveAsync(db, f.VersionId, f.ManifestSha256, Token);
        var b = lease.Binding;
        Assert.Equal(f.CorpusId, b.CorpusId); Assert.Equal(f.ActionId, b.ActionId); Assert.Equal(f.VersionId, b.VersionId);
        Assert.Equal([0, 1], b.Clips.Select(x => x.TrainingOrdinal)); Assert.Equal([0, 2], b.Clips.Select(x => x.SourceIndex));
        Assert.Equal("исправлено", b.Clips[0].CorrectedText); Assert.Equal("вторая фраза", b.Clips[1].CorrectedText);
        Assert.Equal(10, b.RetainedDurationSeconds); Assert.Equal(2, b.Clips.Count);
        Assert.Equal(ApprovedTtsCorpusFixture.Sha(Encoding.UTF8.GetBytes("исправлено")), b.Clips[0].TextSha256);
        Assert.NotEqual(b.Clips[0].TextSha256, b.Clips[0].OriginalTextPayloadSha256);
        byte[] manifest = await File.ReadAllBytesAsync(f.ManifestPath, Token);
        Assert.Equal(ApprovedTtsCorpusFixture.Sha([.. Encoding.UTF8.GetBytes("voxcpm2-approved-binding-v1\n"), .. manifest]), b.BindingSha256);
        Assert.Throws<NotSupportedException>(() => ((IList<ApprovedTtsCorpusClip>)b.Clips)[0] = b.Clips[1]);
    }

    [Fact]
    public async Task Old_execution_audio_in_recorded_action_is_valid()
    {
        using var f = new ApprovedTtsCorpusFixture(); await f.SeedAsync(Token);
        await using var db = await f.OpenAsync(Token); await using var ownership = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        using var lease = await new ApprovedTtsCorpusResolver().ResolveAsync(db, f.VersionId, null, Token);
        Assert.Equal(f.AudioPaths[0], lease.Binding.Clips[0].AudioRelativePath);
        Assert.NotEqual(Path.GetDirectoryName(Path.GetDirectoryName(f.ManifestPath)),
            Path.GetDirectoryName(Path.Combine(f.Root, f.AudioPaths[0])));
    }

    [Fact]
    public async Task Lease_denies_writes_and_parent_rename_until_disposed()
    {
        using var f = new ApprovedTtsCorpusFixture(); await f.SeedAsync(Token);
        await using var db = await f.OpenAsync(Token); await using var ownership = await db.AcquireInputMutationLeaseAsync(Token);
        var lease = await new ApprovedTtsCorpusResolver().ResolveAsync(db, f.VersionId, null, Token);
        string[] paths = [f.ManifestPath, Path.Combine(f.Root, f.SourceRelativePath), Path.Combine(f.Root, f.AudioPaths[0])];
        string parent = Path.GetDirectoryName(paths[2])!;
        try
        {
            foreach (string path in paths) Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite));
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
        }
        finally { lease.Dispose(); }
        lease.Dispose(); // Idempotent disposal must not affect the caller's project lease.
        foreach (string path in paths) { using var writable = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); }
        Directory.Move(parent, parent + "-moved"); Directory.Move(parent + "-moved", parent);
    }

    [Fact]
    public async Task Uncommitted_version_or_changed_SHA_is_rejected()
    {
        using var f = new ApprovedTtsCorpusFixture(); await f.SeedAsync(Token);
        await using var db = await f.OpenAsync(Token); await using var ownership = await db.AcquireInputMutationLeaseAsync(Token);
        var resolver = new ApprovedTtsCorpusResolver();
        await Invalid(() => resolver.ResolveAsync(db, Guid.NewGuid().ToString("N"), null, Token));
        await Invalid(() => resolver.ResolveAsync(db, f.VersionId, new string('f', 64), Token));
        await File.AppendAllTextAsync(f.ManifestPath, " ", Token);
        await Invalid(() => resolver.ResolveAsync(db, f.VersionId, null, Token));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("wav")]
    [InlineData("rights")]
    [InlineData("replacement")]
    public async Task Changed_source_rights_or_WAV_is_rejected(string change)
    {
        using var f = new ApprovedTtsCorpusFixture(); await f.SeedAsync(Token);
        if (change is "source" or "wav")
        {
            string path = Path.Combine(f.Root, change == "source" ? f.SourceRelativePath : f.AudioPaths[2]);
            byte[] bytes = await File.ReadAllBytesAsync(path, Token); bytes[^1] = 1; await File.WriteAllBytesAsync(path, bytes, Token);
        }
        if (change == "rights") await f.RewriteManifestAsync(json => {
            var node = JsonNode.Parse(json)!; node["Source"]!["RightsConfirmed"] = false; return node.ToJsonString(); }, Token);
        if (change == "replacement")
        {
            // The product forbids a second import. Model a historical source identity
            // mismatch while leaving a readable, hash-correct WAV at the same path.
            await f.RewriteManifestAsync(json => {
                var node = JsonNode.Parse(json)!;
                node["Source"]!["ImportedUtc"] = DateTimeOffset.UtcNow.AddDays(-1);
                return node.ToJsonString(); }, Token);
        }
        await using var db = await f.OpenAsync(Token); await using var ownership = await db.AcquireInputMutationLeaseAsync(Token);
        await Invalid(() => new ApprovedTtsCorpusResolver().ResolveAsync(db, f.VersionId, null, Token));
        // Failure on the final input cannot leak previously checked source/manifest/WAV pins.
        foreach (string path in new[] { f.ManifestPath, Path.Combine(f.Root, f.SourceRelativePath), Path.Combine(f.Root, f.AudioPaths[0]) })
        { using var writable = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("nestedDuplicate")]
    [InlineData("unknown")]
    [InlineData("numericAlias")]
    [InlineData("emptyText")]
    [InlineData("overlap")]
    [InlineData("duplicateIndex")]
    [InlineData("wrongAction")]
    [InlineData("traversal")]
    [InlineData("sampleLimit")]
    public async Task Malformed_committed_manifest_is_rejected(string kind)
    {
        using var f = new ApprovedTtsCorpusFixture(); await f.SeedAsync(Token);
        await f.RewriteManifestAsync(json =>
        {
            if (kind == "duplicate") return json.Replace("\"Format\":1", "\"Format\":1,\"Format\":1", StringComparison.Ordinal);
            if (kind == "nestedDuplicate") return json.Replace("\"RightsConfirmed\":true", "\"RightsConfirmed\":true,\"RightsConfirmed\":true", StringComparison.Ordinal);
            if (kind == "numericAlias") return json.Replace("\"Format\":1", "\"Format\":1.0", StringComparison.Ordinal);
            var node = JsonNode.Parse(json)!; var clip = node["Clips"]![1]!;
            switch (kind)
            {
                case "unknown": node["Unexpected"] = 1; break;
                case "emptyText": clip["Text"] = ""; break;
                case "overlap": clip["StartSample"] = 1; break;
                case "duplicateIndex": clip["Index"] = 0; break;
                case "wrongAction": clip["AudioRelativePath"] = f.AudioPaths[2].Replace(f.ActionId, new string('f', 32), StringComparison.Ordinal); break;
                case "traversal": clip["AudioRelativePath"] = $"tts-corpus/actions/{f.ActionId}/../outside.wav"; break;
                case "sampleLimit": clip["EndSample"] = 9999999; break;
            }
            return node.ToJsonString();
        }, Token);
        await using var db = await f.OpenAsync(Token); await using var ownership = await db.AcquireInputMutationLeaseAsync(Token);
        await Invalid(() => new ApprovedTtsCorpusResolver().ResolveAsync(db, f.VersionId, null, Token));
    }

    [Fact]
    public async Task Duration_accepts_producer_roundoff_but_not_a_changed_summary()
    {
        using var f = new ApprovedTtsCorpusFixture(); await f.SeedAsync(Token, [79411, 50001, 81002]);
        await using var db = await f.OpenAsync(Token); await using var ownership = await db.AcquireInputMutationLeaseAsync(Token);
        await StudioTtsCorpusService.Store(db).TransactionAsync(e => {
            var r = e.Current!; var versions = r.Snapshot.Versions.Select(v => v with { RetainedDurationSeconds = Math.BitIncrement(v.RetainedDurationSeconds) }).ToArray();
            return (e with { Current = r with { Snapshot = r.Snapshot with { Versions = versions } } }, true); }, Token);
        using (var lease = await new ApprovedTtsCorpusResolver().ResolveAsync(db, f.VersionId, null, Token))
            Assert.Equal(160413 / 16000.0, lease.Binding.RetainedDurationSeconds);
        await StudioTtsCorpusService.Store(db).TransactionAsync(e => {
            var r = e.Current!; var versions = r.Snapshot.Versions.Select(v => v with { RetainedDurationSeconds = v.RetainedDurationSeconds + 1.0 / 16000 }).ToArray();
            return (e with { Current = r with { Snapshot = r.Snapshot with { Versions = versions } } }, true); }, Token);
        await Invalid(() => new ApprovedTtsCorpusResolver().ResolveAsync(db, f.VersionId, null, Token));
    }

    [Fact]
    public async Task Hardlinked_input_is_rejected()
    {
        using var f = new ApprovedTtsCorpusFixture(); await f.SeedAsync(Token);
        string wav = Path.Combine(f.Root, f.AudioPaths[0]);
        Assert.True(CreateHardLink(wav + ".alias", wav, 0));
        await using var db = await f.OpenAsync(Token); await using var ownership = await db.AcquireInputMutationLeaseAsync(Token);
        await Invalid(() => new ApprovedTtsCorpusResolver().ResolveAsync(db, f.VersionId, null, Token));
    }

    [Fact]
    public async Task Reparse_audio_parent_is_rejected_without_following_it()
    {
        using var f = new ApprovedTtsCorpusFixture(); await f.SeedAsync(Token); await f.ReplaceDraftAsync(Token);
        string parent = Path.GetDirectoryName(Path.Combine(f.Root, f.AudioPaths[0]))!, retained = parent + "-real";
        Directory.Move(parent, retained);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", parent, retained }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(Token); Assert.Equal(0, process.ExitCode);
        try
        {
            await using var db = await f.OpenAsync(Token); await using var ownership = await db.AcquireInputMutationLeaseAsync(Token);
            await Invalid(() => new ApprovedTtsCorpusResolver().ResolveAsync(db, f.VersionId, null, Token));
            Assert.True(File.Exists(Path.Combine(retained, Path.GetFileName(f.AudioPaths[0]))));
        }
        finally { Directory.Delete(parent); Directory.Move(retained, parent); }
    }

    [Fact]
    public async Task Missing_read_does_not_create_corpus_namespace_and_cancellation_is_not_validation_error()
    {
        using var box = new CatalogSandbox(); var project = (await box.Service().CreateAsync(new("No corpus"), "project", Token)).Snapshot;
        await using var db = await new ProjectService().OpenCurrentAsync(box.ProjectRoot(project.Id), Token);
        await using var ownership = await db.AcquireInputMutationLeaseAsync(Token);
        var resolver = new ApprovedTtsCorpusResolver();
        await Invalid(() => resolver.ResolveAsync(db, Guid.NewGuid().ToString("N"), null, Token));
        Assert.False(Directory.Exists(Path.Combine(db.Root, "tts-corpus")));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(db, Guid.NewGuid().ToString("N"), null, cancelled.Token));
    }

    private static async Task Invalid(Func<Task> action)
        => Assert.Equal("tts_corpus.approved.invalid", (await Assert.ThrowsAsync<StudioException>(action)).Code);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, nint security);
}

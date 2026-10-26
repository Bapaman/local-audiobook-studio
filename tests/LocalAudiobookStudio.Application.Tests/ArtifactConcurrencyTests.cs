using System.Security.Cryptography;
using LocalAudiobookStudio.Application.Artifacts;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Tests;

public class ArtifactConcurrencyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Accepted_stream_does_not_block_new_path_inference_and_retains_actual_identity_until_disposal()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        var artifact = await AcceptFirst(fixture);
        var reader = new ProjectArtifactReader(fixture.Box.Service());
        await using (var stream = await reader.OpenAsync(fixture.Id, artifact.Id, Token))
        {
            // No artifact-ownership lock or SQLite session is retained by playback.
            await using (var probe = await fixture.Open())
            await using (var lease = await probe.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token)) { }
            string path = Path.Combine(fixture.Box.ProjectRoot(fixture.Id), artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string parent = Path.GetDirectoryName(path)!;
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read));
            Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + ".moved"));
            var running = fixture.InFlight = fixture.Runner.RunNextAsync(Token);
            await fixture.Workers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(running.IsCompleted); Assert.NotEqual(path, fixture.Workers.LastCommand!.FinalPath);
            Assert.Equal(artifact.Length, stream.Length); Assert.Equal("audio/wav", stream.ContentType);
            Assert.Equal(artifact.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream.Content, Token)));
            // A second response can open the accepted output while the next unit holds ownership.
            await using var concurrent = await reader.OpenAsync(fixture.Id, artifact.Id, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(stream.Length, concurrent.Length);
            fixture.Workers.Release.TrySetResult(); Assert.True(await running);
            Assert.Equal(JobState.Completed, (await fixture.Job()).State);
        }
        string released = Path.Combine(fixture.Box.ProjectRoot(fixture.Id), artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        using var writeAfterResponse = File.Open(released, FileMode.Open, FileAccess.Write, FileShare.Read);
    }

    [Fact]
    public async Task Unaccepted_final_is_not_playable_and_read_never_recovers_the_live_claim()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(preview: true);
        await using var db = await fixture.Open(); await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await db.Jobs.GetAsync((await fixture.Job()).Id, Token);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        byte[] bytes = GenerationRunnerTests.Wave(4800); string final = db.ResolveOwnedPath(claim.FinalRelativePath);
        await File.WriteAllBytesAsync(final, bytes, Token); long revision = db.State.Revision;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<StudioException>(() => new ProjectArtifactReader(fixture.Box.Service()).OpenAsync(fixture.Id, claim.AttemptId, deadline.Token));
        Assert.Equal("artifact.missing", error.Code); Assert.Equal(error.Code, error.Message); Assert.Null(error.InnerException);
        Assert.Equal(claim.Token, (await db.Jobs.GetAsync(job.Id, Token)).ClaimToken);
        Assert.Equal(revision, db.State.Revision); Assert.Equal(bytes, await File.ReadAllBytesAsync(final, Token));
    }

    [Fact]
    public async Task Changed_accepted_anchor_is_rejected_before_streaming_and_failure_releases_pins()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        byte[] bytes = await File.ReadAllBytesAsync(fixture.Prompt, Token); bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(fixture.Prompt, bytes, Token);
        var error = await Assert.ThrowsAsync<StudioException>(async () =>
        {
            await using var unexpected = await new ProjectArtifactReader(fixture.Box.Service()).OpenAsync(fixture.Id, fixture.Prepared.Voice!.Id, Token);
        });
        Assert.Equal("artifact.invalid", error.Code); Assert.Equal(error.Code, error.Message); Assert.Null(error.InnerException);
        using var writable = File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read);
    }

    [Fact]
    public async Task Accepted_anchor_stream_does_not_hold_studio_lock_or_exclusive_artifact_ownership()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        using var studio = File.Open(Path.Combine(fixture.Box.Root, "studio.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await using var stream = await new ProjectArtifactReader(fixture.Box.Service()).OpenAsync(fixture.Id, fixture.Prepared.Voice!.Id, deadline.Token);
        await using var db = await fixture.Open(); await using var lease = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token);
        Assert.Equal(960044, stream.Length);
    }

    [Fact]
    public async Task Acceptance_is_rechecked_while_the_actual_file_is_pinned_and_failure_releases_all_guards()
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create();
        var artifact = await AcceptFirst(fixture);
        string root = fixture.Box.ProjectRoot(fixture.Id), path = Path.Combine(root, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var catalog = new RecheckCatalog(fixture.Box.Catalog(), () =>
        {
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read));
            Assert.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(path)!, Path.GetDirectoryName(path)! + ".moved"));
            // External mutation of ONLY this synthetic DB between the two reads.
            CatalogSandbox.Sql(root, $"UPDATE job_items SET payload=json_set(payload,'$.Artifact.Id','{Guid.NewGuid():N}') WHERE json_extract(payload,'$.Artifact.Id')='{artifact.Id}'");
        });
        var reader = new ProjectArtifactReader(new StudioProjectService(new(fixture.Box.Root), catalog, new ProjectService()));
        var error = await Assert.ThrowsAsync<StudioException>(async () => { await using var unexpected = await reader.OpenAsync(fixture.Id, artifact.Id, Token); });
        Assert.Equal("artifact.missing", error.Code); Assert.Equal(2, catalog.FindCount);
        using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read);
    }

    private sealed class RecheckCatalog(IProjectCatalog inner, Action onRecheck) : IProjectCatalog
    {
        public int FindCount { get; private set; }
        public Task<IReadOnlyList<CatalogProject>> ListAsync(CancellationToken token) => inner.ListAsync(token);
        public async Task<CatalogProject?> FindAsync(string id, CancellationToken token)
        { var found = await inner.FindAsync(id, token); if (++FindCount == 2) onRecheck(); return found; }
        public Task InsertAsync(CatalogProject project, CancellationToken token) => inner.InsertAsync(project, token);
        public Task<IdempotencyReceipt?> FindReceiptAsync(string key, CancellationToken token) => inner.FindReceiptAsync(key, token);
        public Task StoreReceiptAsync(IdempotencyReceipt receipt, CancellationToken token) => inner.StoreReceiptAsync(receipt, token);
        public Task TouchOpenedAsync(string id, DateTimeOffset opened, CancellationToken token) => inner.TouchOpenedAsync(id, opened, token);
        public Task<IReadOnlyList<CatalogCandidate>> ReconcileAsync(CancellationToken token) => inner.ReconcileAsync(token);
        public Task FinalizeAsync(CatalogProject project, IdempotencyReceipt? receipt, CancellationToken token) => inner.FinalizeAsync(project, receipt, token);
        public Task QuarantineAsync(string id, CancellationToken token) => inner.QuarantineAsync(id, token);
    }

    private static async Task<AcceptedJobArtifact> AcceptFirst(GenerationRunnerTests.Fixture fixture)
    {
        await using var db = await fixture.Open(); await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var job = await db.Jobs.GetAsync((await fixture.Job()).Id, Token);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, job.Input.Items[0].SegmentId, job.Input.Preset, Token))!;
        byte[] bytes = GenerationRunnerTests.Wave(4800); await File.WriteAllBytesAsync(db.ResolveOwnedPath(claim.FinalRelativePath), bytes, Token);
        var artifact = new AcceptedJobArtifact(Guid.NewGuid().ToString("N"), claim.FinalRelativePath, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, 4800, claim.EffectivePreset.ProfileId, claim.Input.Versions.Worker, claim.Input.Versions.Model);
        await lease.AcceptGenerationUnitAsync(claim, artifact, Token); return artifact;
    }
}

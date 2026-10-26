using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class JobAudioHistoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task All_snapshot_paths_serve_first_32_durable_results_and_control_replay_keeps_its_completion_boundary()
    {
        using var box = new CatalogSandbox(); var state = await StudioJobServiceTests.Prepare(box);
        await using var db = await new ProjectService().OpenCurrentAsync(box.ProjectRoot(state.Metadata.Id), Token);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var segments = Enumerable.Range(0, 33).Select(i => (i < 2 ? state.Segments[i] : state.Segments[0] with {
            Id = Guid.NewGuid().ToString("N"), Order = i }) with { SpokenText = "Историческая фраза " + i }).ToArray();
        await lease.SaveAsync(db.State with { Segments = segments }, Token);
        var jobs = StudioJobServiceTests.Service(box, new()); var request = new GenerationRequest(JobKind.Generation, segments.Select(x => x.Id).ToArray());
        // Enqueue needs its own short artifact lease, whereas the result reads below need none.
        await lease.DisposeAsync();
        var queued = await jobs.EnqueueAsync(state.Metadata.Id, db.State.Revision, "history", request, Token);
        JobSnapshotDto? paused = null;
        for (int i = 0; i < segments.Length; i++)
        {
            await using var owned = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            var job = await db.Jobs.GetAsync(queued.Id, Token);
            var claim = (await owned.BeginGenerationUnitAsync(job.Id, segments[i].Id, job.Input.Preset, Token))!;
            byte[] audio = GenerationRunnerTests.Wave(4800); string path = db.ResolveOwnedPath(claim.FinalRelativePath);
            await File.WriteAllBytesAsync(path, audio, Token);
            await owned.AcceptGenerationUnitAsync(claim, new(Guid.NewGuid().ToString("N"), claim.FinalRelativePath,
                Convert.ToHexStringLower(SHA256.HashData(audio)), audio.Length, 4800, claim.EffectivePreset.ProfileId,
                claim.Input.Versions.Worker, claim.Input.Versions.Model), Token);
            if (i == 0)
            {
                var first = await jobs.GetAsync(state.Metadata.Id, queued.Id, Token);
                paused = await jobs.RequestStateAsync(state.Metadata.Id, queued.Id, first.Revision, "pause-first", JobControlCommand.Pause, Token);
                Assert.Single(paused.AudioResults!);
                await jobs.RequestStateAsync(state.Metadata.Id, queued.Id, paused.Revision, "resume-first", JobControlCommand.Resume, Token);
            }
        }
        var snapshots = new[] { await jobs.GetAsync(state.Metadata.Id, queued.Id, Token),
            Assert.Single(await jobs.ListAsync(state.Metadata.Id, Token)), Assert.Single((await jobs.ReadEventPageAsync(state.Metadata.Id, null, Token)).Jobs),
            await jobs.EnqueueAsync(state.Metadata.Id, -1, "history", request, Token) };
        foreach (var snapshot in snapshots)
        {
            Assert.Equal(33, snapshot.CompletedUnits); Assert.Equal(32, snapshot.AudioResults!.Count);
            Assert.Equal(Enumerable.Range(0, 32), snapshot.AudioResults.Select(x => x.Order));
            Assert.Equal("Историческая фраза 0", snapshot.AudioResults[0].SpokenText); Assert.Equal(0.1, snapshot.AudioResults[0].Seconds);
            Assert.DoesNotContain(box.Root, JsonSerializer.Serialize(snapshot));
        }
        var oldPause = await jobs.RequestStateAsync(state.Metadata.Id, queued.Id, -1, "pause-first", JobControlCommand.Pause, Token);
        Assert.Equal(paused!.Revision, oldPause.Revision); Assert.Single(oldPause.AudioResults!);
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Core.Tests;

public class AcceptedArtifactLookupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Published_but_unaccepted_final_is_not_lookup_or_segment_mapping()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var input = ProjectJobStoreTests.Input(db); input = input with { Items = [input.Items[0]] };
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "new", JobKind.Preview, input, Token);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, input.Items[0].SegmentId, input.Preset, Token))!;
        var artifact = Publish(db, claim);
        Assert.Null(await db.Jobs.FindAcceptedArtifactAsync(artifact.Id, Token));
        Assert.Null(await db.Jobs.FindAcceptedArtifactAsync(claim.AttemptId, Token));
        Assert.Empty(await db.Jobs.ReadCurrentOutputArtifactIdsAsync(Token));
        await using var observer = await new ProjectService().OpenCurrentAsync(box.ProjectRoot, Token);
        long before = observer.State.Revision;
        await lease.AcceptGenerationUnitAsync(claim, artifact, Token);
        Assert.Equal(artifact, await db.Jobs.FindAcceptedArtifactAsync(artifact.Id, Token));
        Assert.Equal(artifact.Id, (await db.Jobs.ReadCurrentOutputArtifactIdsAsync(Token))[claim.SegmentId]);
        var snapshot = await observer.ReadCurrentSnapshotAsync(Token);
        Assert.Equal(before + 1, snapshot.State.Revision);
        Assert.Equal("done", snapshot.State.Segments[0].Status);
        Assert.Equal(artifact.RelativePath, snapshot.State.Segments[0].OutputRelativePath);
        Assert.Equal(artifact.Id, snapshot.OutputArtifactIds[claim.SegmentId]);
        Assert.Null(await db.Jobs.FindAcceptedArtifactAsync("../file.wav", Token));
    }

    [Fact]
    public async Task Retained_old_acceptance_is_available_beyond_latest32_and_mapping_selects_current_output()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        AcceptedJobArtifact? first = null, latest = null;
        AcceptedJobArtifact? previousSecond = null, latestSecond = null;
        for (int i = 0; i < 34; i++)
        {
            var input = ProjectJobStoreTests.Input(db);
            if (i >= 2) input = input with { Items = [input.Items[0]] };
            var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "new" + i, JobKind.Generation, input, Token);
            foreach (var item in input.Items)
            {
                var claim = (await lease.BeginGenerationUnitAsync(job.Id, item.SegmentId, input.Preset, Token))!;
                var artifact = Publish(db, claim);
                if (item.SegmentId == input.Items[0].SegmentId) { latest = artifact; first ??= artifact; }
                else { latestSecond = artifact; previousSecond ??= artifact; }
                await lease.AcceptGenerationUnitAsync(claim, artifact, Token);
            }
        }
        Assert.Equal(32, (await db.Jobs.ListAsync(Token)).Count);
        Assert.Equal(first, await db.Jobs.FindAcceptedArtifactAsync(first!.Id, Token));
        Assert.Equal(latest!.Id, (await db.Jobs.ReadCurrentOutputArtifactIdsAsync(Token))[db.State.Segments[0].Id]);
        Assert.True(File.Exists(db.ResolveOwnedPath(first.RelativePath)));
        var snapshot = await db.ReadCurrentSnapshotAsync(Token);
        var second = snapshot.State.Segments[1];
        Assert.Equal(latestSecond!.Id, snapshot.OutputArtifactIds[second.Id]);
        Assert.Equal(previousSecond!.Id, snapshot.PreviousAudio![second.Id].ArtifactId);
        Assert.Equal(second.SpokenText, snapshot.PreviousAudio[second.Id].SpokenText);
    }

    [Fact]
    public async Task Duplicate_accepted_ID_is_rejected_instead_of_selecting_an_arbitrary_file()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var input = ProjectJobStoreTests.Input(db);
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "new", JobKind.Generation, input, Token);
        string? id = null;
        foreach (var item in input.Items)
        {
            var claim = (await lease.BeginGenerationUnitAsync(job.Id, item.SegmentId, input.Preset, Token))!;
            var artifact = Publish(db, claim); id ??= artifact.Id;
            await lease.AcceptGenerationUnitAsync(claim, artifact, Token);
        }
        // Corrupt only the owned synthetic store to characterize fail-closed lookup.
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, $"UPDATE job_items SET payload=json_set(payload,'$.Artifact.Id','{id}')");
        Assert.Equal("project.schema.invalid", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.FindAcceptedArtifactAsync(id!, Token))).Code);
        await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.ReadCurrentOutputArtifactIdsAsync(Token));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Accepted_metadata_cannot_alias_voice_ID_or_escape_job_attempt_namespace(bool aliasId)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var input = ProjectJobStoreTests.Input(db); input = input with { Items = [input.Items[0]] };
        var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "new", JobKind.Preview, input, Token);
        var claim = (await lease.BeginGenerationUnitAsync(job.Id, input.Items[0].SegmentId, input.Preset, Token))!;
        var artifact = Publish(db, claim); await lease.AcceptGenerationUnitAsync(claim, artifact, Token);
        string property = aliasId ? "Id" : "RelativePath", value = aliasId ? db.State.Voice!.Id : db.State.Voice!.WavRelativePath;
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, $"UPDATE job_items SET payload=json_set(payload,'$.Artifact.{property}','{value}')");
        string id = aliasId ? value : artifact.Id;
        Assert.Equal("project.schema.invalid", (await Assert.ThrowsAsync<ProjectException>(() => db.Jobs.FindAcceptedArtifactAsync(id, Token))).Code);
        // Current-open must reject ambiguous acceptance before any voice-first lookup.
        await Assert.ThrowsAsync<ProjectException>(async () => { await using var current = await new ProjectService().OpenCurrentAsync(box.ProjectRoot, Token); });
    }

    [Fact]
    public async Task Legacy_current_lookup_is_empty_without_migrating_or_creating_job_tables()
    {
        using var box = new ProjectSandbox();
        await using (var db = await ProjectJobStoreTests.Prepared(box)) { }
        VoiceRecordingServiceTests.Sql(box.ProjectRoot, "DROP TABLE job_events; DROP TABLE job_receipts; DROP TABLE job_items; DROP TABLE jobs; PRAGMA user_version=2; PRAGMA journal_mode=DELETE");
        await using var current = await new ProjectService().OpenCurrentAsync(box.ProjectRoot, Token);
        Assert.Null(await current.Jobs.FindAcceptedArtifactAsync(Guid.NewGuid().ToString("N"), Token));
        Assert.Empty(await current.Jobs.ReadCurrentOutputArtifactIdsAsync(Token));
        Assert.Equal(2L, VoiceRecordingServiceTests.Sql(box.ProjectRoot, "PRAGMA user_version"));
        Assert.Equal(0L, VoiceRecordingServiceTests.Sql(box.ProjectRoot, "SELECT COUNT(*) FROM sqlite_master WHERE name='jobs'"));
    }

    private static AcceptedJobArtifact Publish(ProjectDatabase db, GenerationUnitClaim claim)
    {
        byte[] bytes = new byte[46]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 38);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 48000); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 96000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 2);
        File.WriteAllBytes(db.ResolveOwnedPath(claim.FinalRelativePath), bytes);
        return new(Guid.NewGuid().ToString("N"), claim.FinalRelativePath, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, 1, claim.EffectivePreset.ProfileId, claim.Input.Versions.Worker, claim.Input.Versions.Model);
    }
}

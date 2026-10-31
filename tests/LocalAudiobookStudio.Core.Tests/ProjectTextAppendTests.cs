using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Core.Tests;

public sealed class ProjectTextAppendTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Append_survives_reopen_and_retains_old_sources_voice_outputs_attempts_and_job_history()
    {
        using var box = new ProjectSandbox(); string jobId;
        ProjectState before;
        await using (var db = await ProjectJobStoreTests.Prepared(box))
        {
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            var job = await db.Jobs.EnqueueAsync(lease, db.State.Revision, "old-job", JobKind.Generation, ProjectJobStoreTests.Input(db), Token);
            job = await db.Jobs.RequestStateAsync(job.Id, job.Revision, "cancel", JobControlCommand.Cancel, Token);
            jobId = job.Id;
            var old = db.State.Segments[0];
            var attempt = new AttemptRecord(Guid.NewGuid().ToString("N"), old.Id, 1, old.Seed, old.Versions, old.Parameters, "", "failed", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "worker.crash");
            await lease.SaveAsync(db.State with { Segments = db.State.Segments.Select(s => s.Id == old.Id ? s with { OutputHash = new('d',64), OutputRelativePath = "segments/old.wav", Status = "done" } : s).ToArray(), Attempts = [attempt] }, Token);
            before = db.State;
            var (chapter, segments) = NewText(before);
            await lease.AppendTextAsync(before.Revision, chapter, segments, Token);
            Assert.Equal(before.Revision + 1, db.State.Revision);
            Assert.Equal(before.Voice, db.State.Voice); Assert.Equal(before.VoiceRecording, db.State.VoiceRecording);
            Assert.Equal(before.Chapters, db.State.Chapters.Take(before.Chapters.Count));
            Assert.Equal(before.Segments, db.State.Segments.Take(before.Segments.Count));
            Assert.Equal(before.Attempts, db.State.Attempts);
            Assert.Equal(chapter, db.State.Chapters[^1]); Assert.Equal(segments, db.State.Segments.Skip(before.Segments.Count));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(job), System.Text.Json.JsonSerializer.Serialize(await db.Jobs.GetAsync(jobId, Token)));
        }
        await using var reopened = await new ProjectService().OpenAsync(box.ProjectRoot, Token);
        Assert.Equal(2, reopened.State.Chapters.Count); Assert.Equal(3, reopened.State.Segments.Count);
        Assert.Equal(before.Voice, reopened.State.Voice); Assert.Equal(before.Attempts, reopened.State.Attempts);
        Assert.Equal(JobState.Canceled, (await reopened.Jobs.GetAsync(jobId, Token)).State);
    }

    [Fact]
    public async Task Generic_save_still_refuses_append_and_old_source_replacement()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var before = db.State; var (chapter, segments) = NewText(before);
        Assert.Equal("text.exists", (await Assert.ThrowsAsync<ProjectException>(() => lease.SaveAsync(before with { Chapters = [..before.Chapters,chapter], Segments = [..before.Segments,..segments] },Token))).Code);
        Assert.Equal("text.exists", (await Assert.ThrowsAsync<ProjectException>(() => lease.SaveAsync(before with { Chapters = [before.Chapters[0] with { OriginalText = "replacement" }] },Token))).Code);
        Assert.Equal(before.Revision, db.State.Revision);
    }

    [Theory]
    [InlineData("revision", "project.conflict")]
    [InlineData("order", "text.append.invalid")]
    [InlineData("chapter", "text.append.invalid")]
    [InlineData("output", "text.append.invalid")]
    [InlineData("version", "text.reimport_required")]
    [InlineData("legacy", "text.reimport_required")]
    [InlineData("limit", "text.project_limit")]
    public async Task Invalid_append_is_atomic(string mutation, string code)
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box, legacyText: mutation == "legacy");
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        var before = db.State; var (chapter, segments) = NewText(before);
        if(mutation == "order") segments = [segments[0] with { Order = before.Segments[0].Order }];
        if(mutation == "chapter") segments = [segments[0] with { ChapterId = before.Chapters[0].Id }];
        if(mutation == "output") segments = [segments[0] with { OutputHash = new('a',64) }];
        if(mutation == "version") segments = [segments[0] with { Versions = segments[0].Versions with { TextFrontend = "different" } }];
        if(mutation == "limit") chapter = chapter with { OriginalText = new('a',8_000_000) };
        Assert.Equal(code, (await Assert.ThrowsAsync<ProjectException>(() => lease.AppendTextAsync(before.Revision - (mutation == "revision" ? 1 : 0), chapter, segments, Token))).Code);
        await lease.RefreshAsync(Token); Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(db.State));
    }

    [Fact]
    public async Task Active_job_blocks_append_even_with_artifact_lease_and_current_revision()
    {
        using var box = new ProjectSandbox(); await using var db = await ProjectJobStoreTests.Prepared(box);
        await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
        await db.Jobs.EnqueueAsync(lease,db.State.Revision,"active",JobKind.Generation,ProjectJobStoreTests.Input(db),Token);
        var before = db.State; var (chapter,segments) = NewText(before);
        Assert.Equal("job.active",(await Assert.ThrowsAsync<ProjectException>(()=>lease.AppendTextAsync(before.Revision,chapter,segments,Token))).Code);
        Assert.Equal(before.Revision,db.State.Revision); Assert.Single(db.State.Chapters);
    }

    private static (ChapterRecord Chapter, SegmentRecord[] Segments) NewText(ProjectState state)
    {
        string id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow; const string text = "another";
        var chapter = new ChapterRecord(id,state.Chapters.Max(c=>c.Order)+1,"Another",text,text,text,now,
            new(TextSourceKind.Pasted,"Another","utf-8",ProjectJobStoreTests.Digest(text),text.Length,state.Metadata.Versions.TextFrontend,state.Metadata.Versions.Segmenter));
        var segment = state.Segments[0] with { Id=Guid.NewGuid().ToString("N"),ChapterId=id,Order=state.Segments.Max(s=>s.Order)+1,
            OriginalText=text,NormalizedText=text,SpokenText=text,TextHash=ProjectJobStoreTests.Digest(text),GenerationFingerprint="",OutputHash=null,OutputRelativePath=null,
            Status="draft",CreatedUtc=now,UpdatedUtc=now,DiagnosticCode=null };
        return (chapter,[segment]);
    }
}

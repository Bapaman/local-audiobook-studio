using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Text;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Application.Voices;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Voices;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class StudioTextAppendTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<ProjectSnapshotDto> First(CatalogSandbox box)
    {
        var created = (await box.Service().CreateAsync(new("Synthetic"),"create",Token)).Snapshot;
        return await new StudioTextService(box.Service()).ImportPastedAsync(created.Id,created.Revision,new("Первая книга","Первая история. Второе предложение."),Token);
    }
    [Fact]
    public async Task Append_and_restart_preserve_original_text_segments_and_voice_project_identity()
    {
        using var box = new CatalogSandbox(); var first = await First(box); var service = new StudioTextService(box.Service());
        string id = Guid.NewGuid().ToString("N");
        var appended = await service.AppendPastedAsync(first.Id,first.Revision,new("Вторая книга","Новый текст. Ещё одна мысль.",id),Token);
        Assert.Equal(first.Id,appended.Id); Assert.Equal(first.Revision+1,appended.Revision);
        Assert.Equal(first.TextDocument,appended.TextDocument); Assert.Equal(first.Segments,appended.Segments.Take(first.Segments.Count));
        var documents = Assert.IsAssignableFrom<IReadOnlyList<ProjectTextDocumentDto>>(appended.TextDocuments); Assert.Equal(2,documents.Count);
        Assert.Equal(id,documents[1].Id); Assert.Equal(first.Segments.Select(s=>s.Id),documents[0].SegmentIds);
        Assert.Equal(appended.Segments.Skip(first.Segments.Count).Select(s=>s.Id),documents[1].SegmentIds);
        Assert.Equal(Enumerable.Range(0,appended.Segments.Count),appended.Segments.Select(s=>s.Order));
        var reopened = await box.Service().OpenAsync(first.Id,Token);
        Assert.Equal(JsonSerializer.Serialize(appended),JsonSerializer.Serialize(reopened));
        Assert.DoesNotContain(box.Root,JsonSerializer.Serialize(reopened));
        Assert.Equal("text.exists",(await Assert.ThrowsAsync<StudioException>(()=>service.ImportPastedAsync(first.Id,reopened.Revision,new("Replacement","Нет."),Token))).Code);
    }
    [Fact]
    public async Task Lost_response_replay_ignores_stale_revision_but_changed_source_conflicts_without_new_revision()
    {
        using var box = new CatalogSandbox(); var first = await First(box); var service = new StudioTextService(box.Service());
        var command = new AppendPastedText("Новый","Точный исходник.\r\nПродолжение.",Guid.NewGuid().ToString("N"));
        var saved = await service.AppendPastedAsync(first.Id,first.Revision,command,Token);
        var replay = await new StudioTextService(box.Service()).AppendPastedAsync(first.Id,first.Revision,command,Token);
        Assert.Equal(JsonSerializer.Serialize(saved),JsonSerializer.Serialize(replay));
        Assert.Equal("text.idempotency.conflict",(await Assert.ThrowsAsync<StudioException>(()=>service.AppendPastedAsync(first.Id,first.Revision,command with { Text=command.Text.Replace("\r\n","\n") },Token))).Code);
        Assert.Equal("text.idempotency.conflict",(await Assert.ThrowsAsync<StudioException>(()=>service.AppendPastedAsync(first.Id,first.Revision,command with { DisplayName="Другое имя" },Token))).Code);
        Assert.Equal("project.conflict",(await Assert.ThrowsAsync<StudioException>(()=>service.AppendPastedAsync(first.Id,first.Revision,command with { TextId=Guid.NewGuid().ToString("N") },Token))).Code);
        Assert.Equal(saved.Revision,(await box.Service().GetAsync(first.Id,Token)).Revision);
    }
    [Fact]
    public async Task File_replay_compares_exact_bytes_encoding_and_source_kind()
    {
        using var box = new CatalogSandbox(); var first = await First(box); var service = new StudioTextService(box.Service());
        string id=Guid.NewGuid().ToString("N"),text="Другой исходник.";
        byte[] bytes=[..Encoding.Unicode.GetPreamble(),..Encoding.Unicode.GetBytes(text)];
        var saved=await service.AppendFileAsync(first.Id,first.Revision,id,"книга.txt",new MemoryStream(bytes),Token);
        Assert.Equal("utf-16le",saved.TextDocuments![1].Document.Encoding);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)),saved.TextDocuments[1].Document.Sha256);
        var replay=await service.AppendFileAsync(first.Id,first.Revision,id,"книга.txt",new MemoryStream(bytes),Token);
        Assert.Equal(saved.Revision,replay.Revision);
        Assert.Equal("text.idempotency.conflict",(await Assert.ThrowsAsync<StudioException>(()=>service.AppendFileAsync(first.Id,first.Revision,id,"книга.txt",new MemoryStream(Encoding.UTF8.GetBytes(text)),Token))).Code);
        Assert.Equal("text.idempotency.conflict",(await Assert.ThrowsAsync<StudioException>(()=>service.AppendPastedAsync(first.Id,first.Revision,new("книга.txt",text,id),Token))).Code);
    }
    [Fact]
    public async Task Parallel_identical_request_is_accepted_once()
    {
        using var box=new CatalogSandbox();var first=await First(box);var command=new AppendPastedText("Next","Следующая история.",Guid.NewGuid().ToString("N"));
        var calls=Enumerable.Range(0,2).Select(_=>new StudioTextService(box.Service()).AppendPastedAsync(first.Id,first.Revision,command,Token)).ToArray();
        var results=await Task.WhenAll(calls);
        Assert.All(results,x=>Assert.Equal(first.Revision+1,x.Revision));
        Assert.Equal(2,(await box.Service().GetAsync(first.Id,Token)).TextDocuments!.Count);
    }
    [Fact]
    public async Task Paused_trained_voice_remains_usable_for_new_text_without_retraining_or_touching_protected_audio()
    {
        using var f=new VoxCpmTrainingFixture();await f.SeedAsync(Token);await f.StartAsync(Token);
        var trained=await f.AddStepsAsync(1,"paused",Token);var box=f.Corpus.Box;
        var voices=new StudioVoiceService(box.Service(),new VoiceRecordingService(new(48000)));
        var anchor=await voices.ConfirmAnchorAsync(f.Id,f.Corpus.ProjectRevision,new(0,10000,"Синтетический голос для проверки.",true),Token);
        var texts=new StudioTextService(box.Service());
        var first=await texts.ImportPastedAsync(f.Id,anchor.Revision,new("Первая книга","Первая история."),Token);
        var paths=f.Corpus.AudioPaths.Append(f.Corpus.SourceRelativePath).Append(f.Corpus.ManifestRelativePath).ToArray();
        var hashes=paths.ToDictionary(p=>p,p=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Root,p)))));
        int calls=f.RuntimeProvider.Calls;f.RuntimeProvider.RejectAll=true;
        var command=new AppendPastedText("Новая книга","Совсем новый текст.",Guid.NewGuid().ToString("N"));
        var appended=await texts.AppendPastedAsync(f.Id,first.Revision,command,Token);
        Assert.Equal(first.VoiceAnchor,appended.VoiceAnchor);Assert.Equal(first.VoiceRecording,appended.VoiceRecording);
        Assert.Equal(JsonSerializer.Serialize(trained),JsonSerializer.Serialize(await f.Service.GetAsync(f.Id,Token)));
        Assert.Equal(calls,f.RuntimeProvider.Calls);
        foreach(var path in paths) Assert.Equal(hashes[path],Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Root,path)))));

        var runtime=new InferenceRuntime(f);
        var resolver=new VoxCpmSavedVoiceResolver(new ApprovedTtsCorpusResolver(),f.Checkpoints,runtime);
        var jobs=new StudioJobService(new(new(box.Root),box.Catalog(),new()),new NeverBase(),voxSavedVoices:resolver);
        var ids=appended.TextDocuments![1].SegmentIds;
        var job=await jobs.EnqueueAsync(f.Id,appended.Revision,"new-book",new(JobKind.Generation,ids,SavedVoxCpmModelId:trained.Models[0].Id,ExperimentalConfirmed:true,
            VoxCpmOptions:new(42,10,2,100,true,f.Corpus.ClipIds[0])),Token);
        Assert.Equal("Новая книга",job.TextTitle);Assert.Equal(trained.Models[0].Id,job.VoxCpmGeneration!.ModelId);
        await using(var db=await f.Corpus.OpenAsync(Token))
        {
            var stored=await db.Jobs.GetAsync(job.Id,Token);
            Assert.Equal(ids,stored.Input.Items.Select(i=>i.SegmentId));Assert.Equal(1,stored.Input.SavedVoxCpmVoice!.GlobalStep);
            Assert.Equal(f.Corpus.VersionId,stored.Input.SavedVoxCpmVoice.CorpusVersionId);
        }
        // Read-only recovery of an already committed append is safe even while generation is queued.
        Assert.Equal(appended.Revision,(await texts.AppendPastedAsync(f.Id,first.Revision,command,Token)).Revision);
        Assert.Equal("job.active",(await Assert.ThrowsAsync<StudioException>(()=>texts.AppendPastedAsync(f.Id,appended.Revision,command with { TextId=Guid.NewGuid().ToString("N") },Token))).Code);
    }
    [Fact]
    public async Task Append_document_limit_is_finite_and_does_not_remove_existing_books()
    {
        using var box=new CatalogSandbox();var current=await First(box);var texts=new StudioTextService(box.Service());
        for(int i=1;i<32;i++) current=await texts.AppendPastedAsync(current.Id,current.Revision,new("Текст "+i,"Следующая история.",Guid.NewGuid().ToString("N")),Token);
        Assert.Equal("text.project_limit",(await Assert.ThrowsAsync<StudioException>(()=>texts.AppendPastedAsync(current.Id,current.Revision,new("Ещё","Ещё одна история.",Guid.NewGuid().ToString("N")),Token))).Code);
        var after=await box.Service().OpenAsync(current.Id,Token);Assert.Equal(current.Revision,after.Revision);Assert.Equal(32,after.TextDocuments!.Count);
    }
    [Fact]
    public async Task Concurrent_retry_recovers_committed_text_when_generation_starts_after_initial_replay_lookup()
    {
        using var box=new CatalogSandbox();var first=await First(box);
        var voices=new StudioVoiceService(box.Service(),new VoiceRecordingService(new(48000)));
        var recording=await voices.UploadRecordingAsync(first.Id,first.Revision,new("synthetic.wav",true),new MemoryStream(TtsCorpusServiceTests.Wav(48000,576000)),Token);
        first=await voices.ConfirmAnchorAsync(first.Id,recording.Revision,new(0,10000,"Синтетический образец голоса.",true),Token);
        var gate=new GatedCatalog(box.Catalog());
        var delayedTexts=new StudioTextService(new StudioProjectService(new(box.Root),gate,new ProjectService()));
        var command=new AppendPastedText("Next","Новая история.",Guid.NewGuid().ToString("N"));
        var retry=delayedTexts.AppendPastedAsync(first.Id,first.Revision,command,Token);
        ProjectSnapshotDto accepted;
        try
        {
            await gate.BeforeWrite.Task.WaitAsync(TimeSpan.FromSeconds(5),Token);
            accepted=await new StudioTextService(box.Service()).AppendPastedAsync(first.Id,first.Revision,command,Token);
            var jobs=StudioJobServiceTests.Service(box,new StudioJobServiceTests.Runtime());
            var job=await jobs.EnqueueAsync(first.Id,accepted.Revision,"start-before-retry",new(JobKind.Generation,accepted.TextDocuments![1].SegmentIds),Token);
            Assert.Equal(JobState.Queued,job.State);
        }
        finally { gate.Release.TrySetResult(); }
        var recovered=await retry.WaitAsync(TimeSpan.FromSeconds(5),Token);
        Assert.Equal(accepted.Revision,recovered.Revision);Assert.Equal(2,recovered.TextDocuments!.Count);
        Assert.Equal(command.TextId,recovered.TextDocuments[1].Id);
    }
    // Gate only catalog access: the initial replay read completes, then another normal
    // request can commit+enqueue before the delayed request performs its write precheck.
    private sealed class GatedCatalog(IProjectCatalog inner) : IProjectCatalog
    {
        private int reads;
        internal TaskCompletionSource BeforeWrite { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CatalogProject?> FindAsync(string id,CancellationToken token=default)
        {
            var found=await inner.FindAsync(id,token);
            if(Interlocked.Increment(ref reads)==2){BeforeWrite.TrySetResult();await Release.Task.WaitAsync(token);}
            return found;
        }
        public Task<IReadOnlyList<CatalogProject>> ListAsync(CancellationToken token=default)=>inner.ListAsync(token);
        public Task InsertAsync(CatalogProject project,CancellationToken token=default)=>inner.InsertAsync(project,token);
        public Task<IdempotencyReceipt?> FindReceiptAsync(string key,CancellationToken token=default)=>inner.FindReceiptAsync(key,token);
        public Task StoreReceiptAsync(IdempotencyReceipt receipt,CancellationToken token=default)=>inner.StoreReceiptAsync(receipt,token);
        public Task TouchOpenedAsync(string id,DateTimeOffset opened,CancellationToken token=default)=>inner.TouchOpenedAsync(id,opened,token);
        public Task<IReadOnlyList<CatalogCandidate>> ReconcileAsync(CancellationToken token=default)=>inner.ReconcileAsync(token);
        public Task FinalizeAsync(CatalogProject project,IdempotencyReceipt? receipt,CancellationToken token=default)=>inner.FinalizeAsync(project,receipt,token);
        public Task QuarantineAsync(string id,CancellationToken token=default)=>inner.QuarantineAsync(id,token);
    }
    private sealed class NeverBase : IRuntimeDescriptor
    {
        public Task<RuntimeDescriptor> GetAsync(CancellationToken token)=>throw new InvalidOperationException("Saved voice must not fall back to a base voice");
    }
    private sealed class InferenceRuntime(VoxCpmTrainingFixture fixture) : IVoxCpmInferenceRuntime
    {
        public Task<RuntimeReadiness> GetReadinessAsync(CancellationToken token=default)=>Task.FromResult(new RuntimeReadiness(true,true,null,VoxCpmInferenceProfile.Id));
        public Task<VoxCpmInferenceIdentity> GetIdentityAsync(CancellationToken token)=>Task.FromResult(new VoxCpmInferenceIdentity(fixture.RuntimeProvider.Identity,new('5',64)));
    }
    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    public async Task Invalid_append_id_never_mutates_existing_text(string id)
    {
        using var box=new CatalogSandbox();var first=await First(box);
        Assert.Equal("text.id.invalid",(await Assert.ThrowsAsync<StudioException>(()=>new StudioTextService(box.Service()).AppendPastedAsync(first.Id,first.Revision,new("Next","Следующая история.",id),Token))).Code);
        Assert.Equal(first.Revision,(await box.Service().GetAsync(first.Id,Token)).Revision);
    }
}

using System.Security.Cryptography;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Tests;

public class VoxCpmSavedVoiceGenerationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unavailable_vox_runtime_leaves_queued_without_ever_opening_base_tts()
    {
        await using var f = await GenerationRunnerTests.Fixture.Create(preview: true);
        var old = await f.Job(); await f.Jobs.RequestStateAsync(f.Id, old.Id, old.Revision, "cancel-original", JobControlCommand.Cancel, Token);
        string jobId;
        await using (var db = await f.Open())
        {
            string id = Guid.NewGuid().ToString("N");
            var b = new VoxCpmSavedVoiceBinding(id, Guid.NewGuid().ToString("N"),
                $"voxcpm-training/actions/{Guid.NewGuid():N}/executions/{Guid.NewGuid():N}/windows/{Guid.NewGuid():N}/checkpoints/{id}/manifest.json",
                new('a', 64), Guid.NewGuid().ToString("N"), new('b', 64), new('c', 64),
                db.State.VoiceRecording!.Id, db.State.VoiceRecording.Sha256.ToLowerInvariant(), new('d', 64), 29, 1, 28, new('e', 64), "voxcpm2-saved-adapter-cpu-v1");
            var original = (await db.Jobs.GetAsync(old.Id, Token)).Input;
            var input = original with { ProjectRevision = db.State.Revision, SavedVoxCpmVoice = b,
                Preset = new("voxcpm2-saved-adapter-v1", "voxcpm2-saved-adapter-cpu-v1", 256, 10, 2, 1, 42, false),
                Items = original.Items.Select(x => x with { Versions = x.Versions with { Worker = new('e', 40), Model = new('a', 40) } }).ToArray() };
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            jobId = (await db.Jobs.EnqueueAsync(lease, db.State.Revision, "vox", JobKind.Preview, input, Token)).Id;
        }
        f.Workers.Release.TrySetResult();
        Assert.False(await f.Runner.RunNextAsync(Token));
        await using var result = await f.Open(); var job = await result.Jobs.GetAsync(jobId, Token);
        Assert.Null(f.Workers.LastCommand);
        Assert.Equal(0, f.Workers.DisposeCount);
        Assert.Equal(JobState.Queued, job.State);
        Assert.Equal("worker.runtime.not_ready",job.DiagnosticCode);
        Assert.Null(Assert.Single(await result.Jobs.ItemsAsync(jobId, Token)).Artifact);
    }
    [Fact]
    public async Task Inference_unavailable_reason_is_durable_not_repeated_and_clears_when_runtime_returns()
    {
        await using var f=await GenerationRunnerTests.Fixture.Create(preview:true);var old=await f.Job();
        await f.Jobs.RequestStateAsync(f.Id,old.Id,old.Revision,"cancel-original",JobControlCommand.Cancel,Token);
        var resolver=new VoxProductResolver();var access=new ProjectJobAccess(new(f.Box.Root),f.Box.Catalog(),new());
        var service=new StudioJobService(access,new NeverLegacyDescriptor(),voxSavedVoices:resolver);
        var job=await service.EnqueueAsync(f.Id,f.Prepared.Revision,"vox-unavailable",new(JobKind.Preview,[f.Prepared.Segments[0].Id],ExperimentalConfirmed:true,SavedVoxCpmModelId:new('4',32)),Token);
        var runtime=new VoxProductRuntime{Ready=false};var native=new VoxProductExecutor();
        f.Runner=new(access,new(),f.Workers,TimeProvider.System,voxRuntime:runtime,voxSavedVoices:resolver,voxExecutor:native);
        Assert.False(await f.Runner.RunNextAsync(Token,false));var waiting=await service.GetAsync(f.Id,job.Id,Token);
        Assert.Equal(JobState.Queued,waiting.State);Assert.Equal("worker.runtime.not_ready",waiting.DiagnosticCode);Assert.Equal(0,native.Calls);
        Assert.False(await f.Runner.RunNextAsync(Token,false));Assert.Equal(waiting.Revision,(await service.GetAsync(f.Id,job.Id,Token)).Revision);
        runtime.Ready=true;Assert.True(await f.Runner.RunNextAsync(Token,false));var complete=await service.GetAsync(f.Id,job.Id,Token);
        Assert.Equal(JobState.Completed,complete.State);Assert.Null(complete.DiagnosticCode);Assert.Equal(1,native.Calls);Assert.Null(f.Workers.LastCommand);
    }

    [Theory]
    [InlineData(null)][InlineData("worker.output.invalid")][InlineData("worker.canceled")]
    public async Task Direct_saved_voice_uses_no_prompt_or_legacy_worker_and_never_falls_back(string? failure)
    {
        await using var f=await GenerationRunnerTests.Fixture.Create(preview:true);
        var old=await f.Job();await f.Jobs.RequestStateAsync(f.Id,old.Id,old.Revision,"cancel-original",JobControlCommand.Cancel,Token);
        var resolver=new VoxProductResolver();var runtime=new VoxProductRuntime();var native=new VoxProductExecutor{FailureCode=failure};var gate=new ComputeGate();
        var access=new ProjectJobAccess(new(f.Box.Root),f.Box.Catalog(),new());
        var service=new StudioJobService(access,new NeverLegacyDescriptor(),voxSavedVoices:resolver);
        var job=await service.EnqueueAsync(f.Id,f.Prepared.Revision,"vox-direct",new(JobKind.Preview,[f.Prepared.Segments[0].Id],ExperimentalConfirmed:true,SavedVoxCpmModelId:new('4',32)),Token);
        File.Delete(f.Prompt);
        f.Runner=new(access,new(),f.Workers,TimeProvider.System,gate,voxRuntime:runtime,voxSavedVoices:resolver,voxExecutor:native);
        Assert.True(await f.Runner.RunNextAsync(Token,legacyReady:false));
        await using var db=await f.Open();var result=await db.Jobs.GetAsync(job.Id,Token);
        Assert.Null(f.Workers.LastCommand);Assert.Equal(0,f.Workers.OpenCount);Assert.Equal(1,native.Calls);
        Assert.Equal(2,resolver.Disposed);Assert.Equal(failure!=null?JobState.Failed:JobState.Completed,result.State);
        Assert.True(await gate.WaitAsync(0,Token));gate.Release();Assert.Equal(failure,result.DiagnosticCode);
        var item=Assert.Single(await db.Jobs.ItemsAsync(job.Id,Token));
        if(failure!=null)Assert.Null(item.Artifact);else{Assert.NotNull(item.Artifact);Assert.Equal("voxcpm2-saved-adapter-cpu-v1",item.Artifact.ProfileId);Assert.Equal(4800,item.Artifact.SampleCount);}
    }
    [Fact]
    public async Task Legacy_readiness_false_never_claims_legacy_job_or_polls_vox_runtime()
    {
        await using var f=await GenerationRunnerTests.Fixture.Create(preview:true);var runtime=new VoxProductRuntime();
        f.Runner=new(new ProjectJobAccess(new(f.Box.Root),f.Box.Catalog(),new()),new(),f.Workers,TimeProvider.System,voxRuntime:runtime);
        Assert.False(await f.Runner.RunNextAsync(Token,legacyReady:false));Assert.Equal(0,runtime.Calls);Assert.Equal(JobState.Queued,(await f.Job()).State);
    }
    [Fact]
    public async Task Vox_cancellation_keeps_project_and_compute_until_executor_proves_cleanup()
    {
        await using var f=await GenerationRunnerTests.Fixture.Create(preview:true);var old=await f.Job();
        await f.Jobs.RequestStateAsync(f.Id,old.Id,old.Revision,"cancel-original",JobControlCommand.Cancel,Token);
        var resolver=new VoxProductResolver();var native=new VoxProductExecutor{Hold=true};var gate=new ComputeGate();
        var access=new ProjectJobAccess(new(f.Box.Root),f.Box.Catalog(),new());
        var service=new StudioJobService(access,new NeverLegacyDescriptor(),voxSavedVoices:resolver);
        var job=await service.EnqueueAsync(f.Id,f.Prepared.Revision,"vox-held",new(JobKind.Preview,[f.Prepared.Segments[0].Id],ExperimentalConfirmed:true,SavedVoxCpmModelId:new('4',32)),Token);
        f.Runner=new(access,new(),f.Workers,TimeProvider.System,gate,voxRuntime:new VoxProductRuntime(),voxSavedVoices:resolver,voxExecutor:native);
        var run=f.InFlight=f.Runner.RunNextAsync(Token,false);await native.Started.Task.WaitAsync(TimeSpan.FromSeconds(5),Token);
        var stop=f.Runner.StopAsync(Token);
        try
        {
            await native.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5),Token);
            Assert.False(run.IsCompleted);Assert.False(stop.IsCompleted);Assert.False(await gate.WaitAsync(0,Token));Assert.Equal(1,resolver.Disposed);
            await using var db=await f.Open();
            await Assert.ThrowsAsync<LocalAudiobookStudio.Core.Projects.ProjectException>(()=>db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(50),Token));
        }
        finally{native.Release.TrySetResult();await run;await stop;}
        Assert.True(await gate.WaitAsync(0,Token));gate.Release();Assert.Equal(2,resolver.Disposed);
        Assert.Equal(JobState.Interrupted,(await service.GetAsync(f.Id,job.Id,Token)).State);
    }
}

internal sealed class NeverLegacyDescriptor:IRuntimeDescriptor
{public Task<RuntimeDescriptor> GetAsync(CancellationToken token)=>throw new InvalidOperationException("Legacy provider must not be used");}
internal sealed class VoxProductRuntime:IVoxCpmInferenceRuntime
{
    internal bool Ready=true;internal int Calls;
    public Task<RuntimeReadiness> GetReadinessAsync(CancellationToken token=default){Calls++;return Task.FromResult(new RuntimeReadiness(Ready,true,Ready?null:"worker.runtime.not_ready",VoxCpmInferenceProfile.Id));}
    public Task<VoxCpmInferenceIdentity> GetIdentityAsync(CancellationToken token)=>throw new InvalidOperationException("Resolver owns capture");
}
internal sealed class VoxProductResolver:IVoxCpmSavedVoiceResolver
{
    internal int Calls,Disposed;internal bool Reject;
    public Task<ResolvedVoxCpmSavedVoice> ResolveAsync(ProjectDatabase db,string id,CancellationToken token)
    {
        Calls++;if(Reject)throw new InvalidOperationException("Replay must precede resolver");var source=db.State.VoiceRecording!;
        string action=new('5',32),execution=new('6',32),window=new('7',32),training=new('8',32);
        var identity=new VoxCpmTrainingIdentity(new('a',64),new('b',40),new('c',40),new('d',64),new('e',64),new('f',64),new('1',64),new('2',64),new('3',64),new('4',64));
        var clips=Enumerable.Range(0,28).Select(i=>new ApprovedTtsCorpusClip(i,i,i.ToString("x32"),i*16000,(i+1)*16000,"Пример.",new('a',64),$"clips/{i}.wav",new('b',64),new('c',64))).ToArray();
        var corpus=new ApprovedTtsCorpusBinding(db.State.Metadata.Id,new('9',32),new('a',32),action,"tts-corpus/manifest.json",new('b',64),new('c',64),source,db.State.Revision,new('d',64),new('e',64),28,clips);
        var checkpoint=new VoxCpmCheckpointReference(id,training,action,execution,window,$"voxcpm-training/actions/{action}/executions/{execution}/windows/{window}/checkpoints/{id}/manifest.json",new('a',64),new(29,1,28,1,clips[1].Id),DateTimeOffset.UnixEpoch);
        var binding=new VoxCpmSavedVoiceBinding(id,training,checkpoint.ManifestRelativePath,checkpoint.ManifestSha256,corpus.VersionId,corpus.ManifestSha256,corpus.BindingSha256,source.Id,source.Sha256.ToLowerInvariant(),VoxCpmInferenceProfile.IdentityHash(identity),29,1,28,new('e',64),VoxCpmInferenceProfile.Id);
        return Task.FromResult(new ResolvedVoxCpmSavedVoice(binding,checkpoint,corpus,identity,new Owner(this)));
    }
    public Task<ResolvedVoxCpmSavedVoice> ResolveForExecutionAsync(ProjectDatabase db,VoxCpmSavedVoiceBinding binding,CancellationToken token)=>ResolveAsync(db,binding.ModelId,token);
    private sealed class Owner(VoxProductResolver resolver):IDisposable{public void Dispose()=>resolver.Disposed++;}
}
internal sealed class VoxProductExecutor:IVoxCpmInferenceExecutor
{
    internal bool Hold;internal string? FailureCode;internal int Calls;
    internal TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Canceled=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<VoxCpmInferenceCandidate> RunAsync(VoxCpmInferenceExecution e,Func<string,Task> progress,CancellationToken token)
    {
        Calls++;Started.TrySetResult();if(Hold){try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){Canceled.TrySetResult();await Release.Task;throw new WorkerClientException("worker.canceled",true);}}
        if(FailureCode!=null)throw new WorkerClientException(FailureCode,true);
        Assert.Equal(e.Voice.Binding.ModelId,e.Voice.Checkpoint.Id);Assert.False(string.IsNullOrWhiteSpace(e.Text));
        string path=Path.Combine(e.OutputRoot,"candidate","final.wav");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes=GenerationRunnerTests.Wave(4800);bytes[44]=3;await File.WriteAllBytesAsync(path,bytes,token);
        return new(path,Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes.Length,4800);
    }
}

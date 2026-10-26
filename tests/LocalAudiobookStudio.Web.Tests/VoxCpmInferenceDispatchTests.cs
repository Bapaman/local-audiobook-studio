using System.Net;
using System.Net.Http.Json;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;
namespace LocalAudiobookStudio.Web.Tests;

public sealed partial class JobCoordinatorTests
{
    [Fact]
    public async Task Vox_queue_is_dispatched_even_when_legacy_provider_is_not_ready()
    {
        var vox=new VoxDispatchRuntime();await using var f=await Fixture.Create(configure:services=>services.AddSingleton<IVoxCpmInferenceRuntime>(vox));
        var old=await f.Job();await f.Jobs.RequestStateAsync(f.Id,old.Id,old.Revision,"cancel-original",JobControlCommand.Cancel,Token);
        await using(var db=await f.Open())
        {
            string model=new('4',32);var b=new VoxCpmSavedVoiceBinding(model,new('5',32),$"voxcpm-training/actions/{new string('6',32)}/executions/{new string('7',32)}/windows/{new string('8',32)}/checkpoints/{model}/manifest.json",
                new('a',64),new('b',32),new('c',64),new('d',64),db.State.VoiceRecording!.Id,db.State.VoiceRecording.Sha256.ToLowerInvariant(),new('e',64),29,1,28,new('f',64),VoxCpmInferenceProfile.Id);
            var input=(await db.Jobs.GetAsync(old.Id,Token)).Input;
            var descriptor=VoxCpmInferenceProfile.Descriptor(b,input.Items[0].Versions);
            input=input with{ProjectRevision=db.State.Revision,SavedVoxCpmVoice=b,Preset=descriptor.Preset,Items=input.Items.Take(1).Select(i=>i with{Versions=descriptor.Versions}).ToArray()};
            await using var lease=await db.AcquireArtifactLeaseAsync(cancellationToken:Token);
            f.JobId=(await db.Jobs.EnqueueAsync(lease,db.State.Revision,"vox",JobKind.Preview,input,Token)).Id;
        }
        Assert.False(f.Runtime.Ready);await f.App.StartAsync(Token);await f.AwaitJob(JobState.Failed);
        Assert.True(vox.Calls>0);Assert.Equal(0,f.Workers.MaximumConcurrent);
    }
    [Fact]
    public async Task Idle_or_legacy_queue_does_not_admit_vox_runtime()
    {
        var vox=new VoxDispatchRuntime();await using var f=await Fixture.Create(configure:services=>services.AddSingleton<IVoxCpmInferenceRuntime>(vox));
        await f.App.StartAsync(Token);await f.Clock.WaitForDelay(TimeSpan.FromSeconds(1),2);
        Assert.Equal(0,vox.Calls);Assert.Equal(JobState.Queued,(await f.Job()).State);
    }
    private sealed class VoxDispatchRuntime:IVoxCpmInferenceRuntime
    {
        internal int Calls;
        public Task<RuntimeReadiness> GetReadinessAsync(CancellationToken token=default){Calls++;return Task.FromResult(new RuntimeReadiness(true,true,null,VoxCpmInferenceProfile.Id));}
        public Task<VoxCpmInferenceIdentity> GetIdentityAsync(CancellationToken token)=>throw new InvalidOperationException("No identity admission during dispatch");
    }
}
public sealed class VoxCpmInferenceDispatchTests
{
    [Fact]
    public async Task Http_saved_vox_selection_reaches_explicit_consent_guard()
    {
        await using var host=await ApiTestHost.StartAsync();var project=await host.CreateAsync();
        using var response=await host.SendAsync(HttpMethod.Post,$"/api/projects/{project.Id}/jobs",JsonContent.Create(new{kind="preview",segmentIds=new[]{new string('1',32)},savedVoxCpmModelId=new string('2',32)}),project.ETag,"vox-consent");
        Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.Equal("training.model.confirmation_required",(await ApiTestHost.JsonAsync(response)).GetProperty("code").GetString());
    }
}

using System.Diagnostics;
using System.Text.Json;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Tests;
public sealed class VoxCpmInferenceAcceptanceHelperTests
{
    [Fact]
    public async Task Helper_uses_normal_authenticated_command_with_exact_vox_selection_and_no_training()
    {
        var jobs=new Commands();var training=new Training();
        await using var host=await ApiTestHost.StartAsync(configureServices:s=>{s.RemoveAll<IHostedService>();s.AddSingleton<IStudioJobService>(jobs);s.AddSingleton<IStudioVoxCpmTrainingService>(training);});
        var project=await host.CreateAsync();training.ProjectId=project.Id;
        string root=Path.Combine(host.Root,"preview-evidence");Directory.CreateDirectory(root);
        string session=Path.Combine(root,"private-session.json");using(var request=host.Request(HttpMethod.Get,"/api/bootstrap"))
            File.WriteAllText(session,JsonSerializer.Serialize(new{format=1,baseUrl=host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority),cookieHeader=string.Join("; ",request.Headers.GetValues("Cookie")),csrfToken=request.Headers.GetValues("X-CSRF-TOKEN").Single()}));
        string script=Script();Assert.True(File.Exists(script),"Product preview HTTP helper is missing.");
        Assert.Equal(0,(await Run("Status",new('a',64))).Code);Assert.Equal(0,jobs.Calls);
        var bad=await Run("Preview",new('b',64));Assert.NotEqual(0,bad.Code);Assert.Equal(0,jobs.Calls);
        var success=await Run("Preview",new('a',64));Assert.Equal(0,success.Code);Assert.Equal(1,jobs.Calls);
        Assert.NotNull(jobs.Request);Assert.Equal(new string('4',32),jobs.Request.SavedVoxCpmModelId);Assert.Null(jobs.Request.SavedModelId);Assert.True(jobs.Request.ExperimentalConfirmed);
        Assert.Equal([new string('5',32)],jobs.Request.SegmentIds);Assert.DoesNotContain(host.Root,success.Out);Assert.DoesNotContain("csrf",success.Out);
        Assert.Equal(0,training.Commands);
        async Task<(int Code,string Out)> Run(string action,string sha)
        {
            var info=new ProcessStartInfo("pwsh.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"-NoProfile","-File",script,"-SessionPath",session,"-ProjectId",project.Id,"-ModelId",new string('4',32),"-ExpectedManifestSha256",sha,"-ExpectedGlobalStep","29","-OutputDirectory",root,"-Action",action})info.ArgumentList.Add(arg);
            if(action=="Preview")foreach(string arg in new[]{"-SegmentId",new string('5',32),"-OperationKey",new string('6',32),"-ExperimentalConfirmed"})info.ArgumentList.Add(arg);
            using var p=Process.Start(info)!;var stdout=p.StandardOutput.ReadToEndAsync(ApiTestHost.Token);var stderr=p.StandardError.ReadToEndAsync(ApiTestHost.Token);
            await p.WaitForExitAsync(ApiTestHost.Token);string output=await stdout;await stderr;return(p.ExitCode,output);
        }
    }
    private static string Script(){for(string? path=AppContext.BaseDirectory;path!=null;path=Path.GetDirectoryName(path))if(File.Exists(Path.Combine(path,"LocalAudiobookStudio.sln")))return Path.Combine(path,"scripts","voxcpm-inference","acceptance.ps1");throw new InvalidOperationException();}
    private sealed class Training:IStudioVoxCpmTrainingService
    {
        internal string ProjectId="";internal int Commands;
        public Task<VoxCpmTrainingReadinessDto> GetReadinessAsync(CancellationToken token=default)=>throw new InvalidOperationException("No readiness admission");
        public Task<VoxCpmTrainingSnapshotDto?> GetAsync(string id,CancellationToken token=default)=>Task.FromResult<VoxCpmTrainingSnapshotDto?>(new(new('1',32),ProjectId,155,"paused","paused",new('2',32),new('a',64),28,392.034875,29,1,1,new('3',32),2,new('9',32),[new(new('4',32),new('1',32),new('2',32),new('a',64),29,1,DateTimeOffset.UnixEpoch)],true,false,null,DateTimeOffset.UnixEpoch,DateTimeOffset.UnixEpoch));
        public Task<VoxCpmTrainingSnapshotDto> StartAsync(string id,long revision,string key,string corpus,bool consent,CancellationToken token=default){Commands++;throw new InvalidOperationException("No training");}
        public Task<VoxCpmTrainingSnapshotDto> ControlAsync(string id,string training,long revision,string key,string command,CancellationToken token=default){Commands++;throw new InvalidOperationException("No training");}
    }
    private sealed class Commands:IStudioJobService
    {
        internal int Calls;internal GenerationRequest? Request;
        public Task<JobSnapshotDto> EnqueueAsync(string id,long revision,string key,GenerationRequest request,CancellationToken token){Calls++;Request=request;return Task.FromResult(new JobSnapshotDto(new('7',32),id,JobKind.Preview,JobState.Queued,JobRequestedState.Run,"queued",0,1,1,DateTimeOffset.UnixEpoch,DateTimeOffset.UnixEpoch,null,null,new('a',64),"voxcpm2-saved-adapter-cpu-v1"));}
        public Task<IReadOnlyList<JobSnapshotDto>> ListAsync(string id,CancellationToken token)=>Task.FromResult<IReadOnlyList<JobSnapshotDto>>([]);
        public Task<JobSnapshotDto> GetAsync(string id,string job,CancellationToken token)=>throw new NotSupportedException();
        public Task<JobSnapshotDto> RequestStateAsync(string id,string job,long revision,string key,JobControlCommand command,CancellationToken token)=>throw new NotSupportedException();
        public Task<IReadOnlyList<JobEventDto>> ReadEventsAsync(string id,long cursor,CancellationToken token)=>Task.FromResult<IReadOnlyList<JobEventDto>>([]);
        public Task<long> GetEventSequenceAsync(string id,CancellationToken token)=>Task.FromResult(0L);
        public Task<JobEventPageDto> ReadEventPageAsync(string id,long? cursor,CancellationToken token)=>Task.FromResult(new JobEventPageDto(0,0,[],[],false));
    }
}

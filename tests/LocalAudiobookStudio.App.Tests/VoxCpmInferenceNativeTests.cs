using System.Diagnostics;
using LocalAudiobookStudio.App.VoxCpmInference;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.WorkerHost.Engine;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmInferenceNativeTests
{
    [Fact]
    public async Task Reference_decoder_cache_is_isolated_in_the_owned_attempt_with_long_path_support()
    {
        using var f = new VoxInferenceFixture(); f.UseReference();
        var launcher = new VoxInferenceLauncher(f) { Fault = "success" };
        var runtime = new VoxCpmInferenceRuntime(new(f.Training.Options,new VoxNativeInventory(f.Training)),f.Scripts,
            Path.Combine(AppContext.BaseDirectory,"locks","voxcpm2-inference.lock.json"));
        await using var executor = new VoxCpmInferenceNativeExecutor(runtime,launcher,new VoxNativeMemory());
        Assert.Equal(100,(await executor.RunAsync(f.Execution,_=>Task.CompletedTask,TestContext.Current.CancellationToken)).SampleCount);
        Assert.Equal(VoxCpmTrainingFilePins.Extended(Path.Combine(f.Execution.OutputRoot,"decoder-cache")),launcher.StartInfo!.Environment["NUMBA_CACHE_DIR"]);
        Assert.True(launcher.Child.Zero);
    }

    [Fact]
    public async Task Saved_identity_allows_advisory_dispatch_without_a_second_full_scan_but_native_admission_is_fresh()
    {
        using var f = new VoxInferenceFixture();
        var inventory = new VoxNativeInventory(f.Training);
        var training = new VoxCpmTrainingRuntime(f.Training.Options, inventory);
        var runtime = new VoxCpmInferenceRuntime(training, f.Scripts, Path.Combine(AppContext.BaseDirectory, "locks", "voxcpm2-inference.lock.json"));
        _ = await runtime.GetIdentityAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, inventory.Opened);
        Assert.Equal(1, inventory.Released);
        Assert.True((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        Assert.Equal(1, inventory.Opened);
        using (await runtime.AcquireAsync(TestContext.Current.CancellationToken)) { }
        Assert.Equal(2, inventory.Opened);
        Assert.Equal(2, inventory.Released);
        training.MarkCacheChanged();
        Assert.False((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
    }

    [Fact]
    public async Task Pre_canceled_entry_reports_known_cleanup_and_does_not_release_an_unacquired_slot()
    {
        using var f=new VoxInferenceFixture();var inventory=new VoxNativeInventory(f.Training);var launcher=new VoxInferenceLauncher(f){Fault="success"};
        var runtime=new VoxCpmInferenceRuntime(new(f.Training.Options,inventory),f.Scripts,Path.Combine(AppContext.BaseDirectory,"locks","voxcpm2-inference.lock.json"));
        await using var executor=new VoxCpmInferenceNativeExecutor(runtime,launcher,new VoxNativeMemory());
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        var error=await Assert.ThrowsAsync<WorkerClientException>(()=>executor.RunAsync(f.Execution,_=>Task.CompletedTask,canceled.Token));
        Assert.Equal("worker.canceled",error.Code);Assert.True(error.CleanupConfirmed);Assert.Null(launcher.StartInfo);Assert.Equal(0,inventory.Opened);
        Assert.Equal(100,(await executor.RunAsync(f.Execution,_=>Task.CompletedTask,TestContext.Current.CancellationToken)).SampleCount);
    }
    [Fact]
    public async Task Packaged_scripts_are_exact_and_extra_imports_fail_closed()
    {
        using var f=new VoxInferenceFixture();
        using(var pins=new VoxCpmTrainingFilePins())
            await VoxCpmInferenceScripts.PinAsync(pins,Path.Combine(AppContext.BaseDirectory,"VoxCpmInferenceWorker"),Path.Combine(AppContext.BaseDirectory,"locks","voxcpm2-inference.lock.json"),TestContext.Current.CancellationToken);
        string root=Path.Combine(f.Execution.ProjectRoot,"inference-copy");Directory.CreateDirectory(root);
        foreach(string source in Directory.EnumerateFiles(f.Scripts))File.Copy(source,Path.Combine(root,Path.GetFileName(source)));
        File.WriteAllText(Path.Combine(root,"extra.py"),"raise RuntimeError()");
        var training=new VoxCpmTrainingRuntime(f.Training.Options,new VoxNativeInventory(f.Training));
        Assert.True((await training.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        var runtime=new VoxCpmInferenceRuntime(training,root,Path.Combine(AppContext.BaseDirectory,"locks","voxcpm2-inference.lock.json"));
        Assert.False((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
    }
    [Theory]
    [InlineData("success")][InlineData("memory")][InlineData("recheck")][InlineData("reserve")]
    [InlineData("deadline")][InlineData("limits")][InlineData("stdout")][InlineData("stderr")]
    [InlineData("exit")][InlineData("acceptance")][InlineData("cache")][InlineData("gate-eof")]
    [InlineData("gate-order")]
    public async Task Owned_window_accepts_only_complete_audio_after_job_zero(string fault)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var f = new VoxInferenceFixture(); var inventory = new VoxNativeInventory(f.Training);
        var runtime = new VoxCpmTrainingRuntime(f.Training.Options, inventory); var launcher = new VoxInferenceLauncher(f) { Fault = fault };
        var inference = new VoxCpmInferenceRuntime(runtime, f.Scripts, Path.Combine(VoxCpmTrainingWorkerPinTests.Repository(),"eng","locks","voxcpm2-inference.lock.json"));
        var readings = fault switch { "memory" => new ulong[] { 17179869183 }, "recheck" => [17179869184,17179869183], "reserve" => [17179869184,17179869184,3221225471], _ => [] };
        await using var executor = new VoxCpmInferenceNativeExecutor(inference, launcher, new VoxNativeMemory(readings), fault == "deadline" ? new DeadlineClock() : null);
        if (fault == "success")
        {
            var result = await executor.RunAsync(f.Execution, _ => Task.CompletedTask, deadline.Token);
            Assert.Equal(100,result.SampleCount); Assert.True(launcher.Child.Zero); Assert.True(launcher.Child.Disposed);
            Assert.Equal(13958643712,launcher.Policy!.AggregateJobMemoryLimitBytes);
            Assert.Contains("--training-worker-root",launcher.StartInfo!.ArgumentList);
        }
        else
        {
            var error = await Assert.ThrowsAsync<WorkerClientException>(() => executor.RunAsync(f.Execution,_=>Task.CompletedTask,deadline.Token));
            Assert.NotEqual("worker.canceled", error.Code);
            if (fault is "memory" or "recheck") Assert.Equal("worker.memory.insufficient", error.Code);
            if (fault == "reserve") Assert.Equal("worker.memory.reserve", error.Code);
            if (fault == "limits") Assert.Equal("worker.memory.policy", error.Code);
            Assert.True(error.CleanupConfirmed);
            if (fault == "memory") { Assert.Null(launcher.StartInfo); Assert.Equal(0,inventory.Opened); }
            if (fault is "memory" or "recheck" or "reserve" or "deadline" or "limits" or "gate-eof" or "gate-order") Assert.False(launcher.Child.SawStart);
            if (fault == "cache") Assert.False((await runtime.GetReadinessAsync(TestContext.Current.CancellationToken)).Ready);
        }
        Assert.Equal(inventory.Opened,inventory.Released);
    }
    [Theory]
    [InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task Unknown_cleanup_retains_exact_child_cache_and_adapter_until_dispose(bool unknownLaunch,bool reference)
    {
        using var f = new VoxInferenceFixture(); var inventory = new VoxNativeInventory(f.Training);
        string? referencePath = reference ? f.UseReference() : null;
        var launcher = new VoxInferenceLauncher(f) { Fault = unknownLaunch ? "launch-unknown" : "success" }; launcher.Child.StopFails=true;
        var runtime = new VoxCpmInferenceRuntime(new(f.Training.Options, inventory),f.Scripts,Path.Combine(VoxCpmTrainingWorkerPinTests.Repository(),"eng","locks","voxcpm2-inference.lock.json"));
        var executor = new VoxCpmInferenceNativeExecutor(runtime,launcher,new VoxNativeMemory());
        try
        {
            var error = await Assert.ThrowsAsync<WorkerClientException>(()=>executor.RunAsync(f.Execution,_=>Task.CompletedTask,TestContext.Current.CancellationToken));
            Assert.False(error.CleanupConfirmed); Assert.Equal(0,inventory.Released); Assert.False(launcher.Child.Disposed);
            if(referencePath != null) Assert.Throws<IOException>(()=>File.Move(referencePath,referencePath+".moved"));
            if(referencePath != null) Assert.Throws<IOException>(()=>Directory.Move(Path.Combine(f.Execution.OutputRoot,"decoder-cache"),Path.Combine(f.Execution.OutputRoot,"moved-cache")));
            string adapter = Path.Combine(f.Execution.ProjectRoot, f.Execution.Voice.Checkpoint.ManifestRelativePath.Replace('/',Path.DirectorySeparatorChar));
            Assert.Throws<IOException>(()=>File.WriteAllText(adapter,"changed"));
            await Assert.ThrowsAsync<LocalAudiobookStudio.Application.VoxCpmTraining.VoxCpmTrainingExecutionException>(()=>VoxCpmTrainingCacheLease.AcquireAsync(f.Training.Options.CachePath,4,f.Execution.Voice.Identity.CacheSha256,TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<WorkerClientException>(()=>executor.RunAsync(f.Execution,_=>Task.CompletedTask,TestContext.Current.CancellationToken));
        }
        finally { launcher.Child.StopFails=false; await executor.DisposeAsync(); }
        Assert.True(launcher.Child.Zero); Assert.Equal(1,inventory.Released);
        if(referencePath != null) { File.Move(referencePath,referencePath+".moved"); File.Move(referencePath+".moved",referencePath); }
        if(referencePath != null) { Directory.Move(Path.Combine(f.Execution.OutputRoot,"decoder-cache"),Path.Combine(f.Execution.OutputRoot,"moved-cache")); Directory.Move(Path.Combine(f.Execution.OutputRoot,"moved-cache"),Path.Combine(f.Execution.OutputRoot,"decoder-cache")); }
    }
    [Fact]
    public async Task Root_exit_does_not_accept_or_release_before_job_zero()
    {
        using var f = new VoxInferenceFixture(); var inventory = new VoxNativeInventory(f.Training); var launcher = new VoxInferenceLauncher(f) { Fault = "success" };
        launcher.Child.ZeroGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new VoxCpmInferenceRuntime(new(f.Training.Options,inventory),f.Scripts,Path.Combine(VoxCpmTrainingWorkerPinTests.Repository(),"eng","locks","voxcpm2-inference.lock.json"));
        await using var executor = new VoxCpmInferenceNativeExecutor(runtime,launcher,new VoxNativeMemory());
        var run = executor.RunAsync(f.Execution,_=>Task.CompletedTask,TestContext.Current.CancellationToken);
        await launcher.Child.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted); Assert.Equal(0,inventory.Released); launcher.Child.ZeroGate.SetResult(); Assert.Equal(100,(await run).SampleCount);
    }
}
internal sealed class VoxInferenceLauncher(VoxInferenceFixture f) : IVoxCpmTrainingProcessLauncher
{
    internal string? Fault; internal ProcessStartInfo? StartInfo; internal NativeJobMemoryPolicy? Policy;
    internal VoxInferenceChild Child { get; } = new();
    public IVoxCpmTrainingOwnedProcess Start(ProcessStartInfo start, NativeJobMemoryPolicy policy)
    {
        StartInfo=start; Policy=policy;
        if(Fault=="launch-unknown") throw new NativeOwnedLaunchException(Child);
        string Arg(string key)=>start.ArgumentList[start.ArgumentList.IndexOf(key)+1];
        byte[] bytes=File.ReadAllBytes(Arg("--request"));
        var call = new VoxCpmInferenceInvocation(f.Execution,bytes,VoxCpmTrainingProtocol.Digest(bytes),$"segments/{f.Execution.JobId}/{f.Execution.AttemptId}-vox");
        Child.Start(Arg("--gate"),()=>f.Publish(call,Fault=="acceptance"?"request":null),Fault,()=>{
            using var file=new FileStream(f.Training.Options.CachePath,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);file.WriteByte(9);file.Flush(true); });
        return Child;
    }
}
internal sealed class VoxInferenceChild : IVoxCpmTrainingOwnedProcess, INativeLaunchCleanup
{
    private readonly VoxFeedStream output=new(),error=new(); private readonly CancellationTokenSource stop=new();
    private readonly TaskCompletionSource<int> exit=new(TaskCreationOptions.RunContinuationsAsynchronously); private Task? work;
    public StreamReader StandardOutput { get; } public StreamReader StandardError { get; }
    internal bool StopFails,SawStart,Zero,Disposed; private string? fault;
    internal TaskCompletionSource StopEntered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously); internal TaskCompletionSource? ZeroGate;
    internal VoxInferenceChild(){StandardOutput=new(output);StandardError=new(error);}
    internal void Start(string gate,Action publish,string? failure,Action mutate)
    {
        fault=failure;error.End();
        if(fault=="gate-eof"){output.End();exit.SetResult(1);return;}
        output.Emit(fault=="gate-order"?"voxcpm_inference.worker.loading\n":"voxcpm_inference.worker.gated\n");
        if(fault=="stderr"){error.Reset();error.Emit(new string('x',4097));error.End();}
        work=Work();
        async Task Work(){try{
            while(true){stop.Token.ThrowIfCancellationRequested();using var file=new FileStream(gate,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                byte[] bytes=new byte[file.Length];file.ReadExactly(bytes);if(bytes.AsSpan().SequenceEqual("START\n"u8)){SawStart=true;break;} await Task.Delay(2,stop.Token);}
            if(fault=="stdout")output.Emit(new string('x',4097));else{output.Emit("voxcpm_inference.worker.loading\nvoxcpm_inference.worker.generating\n");publish();if(fault=="cache")mutate();output.Emit("voxcpm_inference.worker.ready\n");}
            output.End();exit.TrySetResult(fault=="exit"?1:0);
        }catch(OperationCanceledException){output.End();exit.TrySetResult(1);}catch(Exception e){output.End();exit.TrySetException(e);}}
    }
    public NativeJobMemoryObservation ReadJobMemory()=>new(fault=="limits"?0u:0x2210,3,13958643712,100);
    public Task<int> WaitForExitAsync(CancellationToken token=default)=>exit.Task.WaitAsync(token);
    public async Task StopTreeAsync(){StopEntered.TrySetResult();if(StopFails)throw new IOException("unknown");if(ZeroGate!=null)await ZeroGate.Task;stop.Cancel();if(work!=null)await work;Zero=true;}
    public void Dispose(){Disposed=true;StandardOutput.Dispose();StandardError.Dispose();stop.Dispose();}
}

using System.Diagnostics;
using System.Text;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.WorkerHost.Engine;
using static LocalAudiobookStudio.App.VoxCpmTraining.VoxCpmTrainingJson;

namespace LocalAudiobookStudio.App.VoxCpmInference;

/// <summary>Owns one isolated synthesis window. No result can escape before owned Job-zero and cache posthash.</summary>
internal sealed class VoxCpmInferenceNativeExecutor(VoxCpmInferenceRuntime runtime, IVoxCpmTrainingProcessLauncher? launcher=null,
    IVoxCpmTrainingMemoryReader? memory=null, TimeProvider? clock=null) : IVoxCpmInferenceExecutor,IAsyncDisposable
{
    private readonly SemaphoreSlim operation=new(1,1); private Retained? retained; private bool disposed;
    public async Task<VoxCpmInferenceCandidate> RunAsync(VoxCpmInferenceExecution execution,Func<string,Task> progress,CancellationToken token)
    {
        try { await operation.WaitAsync(token); }
        catch (OperationCanceledException) { throw new WorkerClientException("worker.canceled",true); }
        VoxCpmInferenceRuntimeLease? lease=null; VoxCpmTrainingFilePins? pins=null; VoxCpmTrainingCacheLease? cache=null;
        IVoxCpmTrainingOwnedProcess? child=null; bool unknownLaunch=false,jobZero=false,cacheChecked=false;
        using var monitor=new CancellationTokenSource();using var streams=new CancellationTokenSource(); Task? stdout=null,stderr=null,watchdog=null;
        try
        {
            if(disposed||retained!=null)throw new WorkerClientException("worker.cleanup",false);
            token.ThrowIfCancellationRequested();var available=memory??new VoxCpmTrainingMemoryReader();var time=clock??TimeProvider.System;
            VoxCpmTrainingResourcePolicy.CheckPreflight(available.AvailableBytes()); lease=await runtime.AcquireAsync(token);
            Require(lease.Training.Identity==execution.Voice.Identity);
            cache=await VoxCpmTrainingCacheLease.AcquireAsync(runtime.Training.Options.CachePath,lease.Training.CacheBytes,lease.Training.Identity.CacheSha256,token);
            pins=new();var call=await VoxCpmInferenceProtocol.CreateAsync(execution,runtime.Training.Options,lease.Training,pins,VoxCpmInferenceScripts.ImplementationSha256,token);
            NewOutput(pins,execution.ProjectRoot,call.OutputRelativeRoot);
            string request=Path.Combine(execution.OutputRoot,"request.json"),gatePath=Path.Combine(execution.OutputRoot,"START");
            using(var output=new FileStream(request,FileMode.CreateNew,FileAccess.Write,FileShare.None)){await output.WriteAsync(call.RequestBytes,token);output.Flush(true);}
            _=await pins.ReadAsync(request,262144,call.RequestSha256,token);
            var gate=new FileStream(gatePath,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);pins.PinCreatedFile(gate);gate.Flush(true);
            string? decoderCache=null;
            if(execution.Voice.Binding.Reference!=null || execution.Voice.Binding.Options!=null)
            {
                decoderCache=Path.Combine(execution.OutputRoot,"decoder-cache");
                Directory.CreateDirectory(decoderCache);pins.PinDirectory(decoderCache);
                Require(!Directory.EnumerateFileSystemEntries(decoderCache).Any());
            }
            VoxCpmTrainingResourcePolicy.CheckPreflight(available.AvailableBytes());long began=time.GetTimestamp();
            child=(launcher??new VoxCpmTrainingProcessLauncher()).Start(StartInfo(runtime.Training.Options,request,call.RequestSha256,gatePath,decoderCache),new(VoxCpmTrainingResourcePolicy.MemoryCapBytes));
            VoxCpmTrainingResourcePolicy.CheckLimits(child.ReadJobMemory());var state=new StreamState();var gated=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stdout=Drain(child.StandardOutput,false,state,gated,progress,streams.Token);stderr=Drain(child.StandardError,true,state,gated,progress,streams.Token);
            watchdog=Watch(child,available,time,began,monitor.Token);Task<int> exit=child.WaitForExitAsync();
            while(!gated.Task.IsCompletedSuccessfully)
            {
                CheckFaults(stdout,stderr,watchdog);
                if(exit.IsCompleted||stdout.IsCompleted)throw Invalid();
                Task next=await VoxCpmTrainingNativeExecutor.NextSignal(gated.Task,exit,stdout,stderr,watchdog).WaitAsync(token);await next;CheckFaults(stdout,stderr,watchdog);
                if(!gated.Task.IsCompletedSuccessfully&&(exit.IsCompleted||stdout.IsCompleted))throw Invalid();
            }
            CheckFaults(stdout,stderr,watchdog);token.ThrowIfCancellationRequested();VoxCpmTrainingResourcePolicy.CheckLimits(child.ReadJobMemory());Require(!state.Error);
            state.GateOpened=true;await gate.WriteAsync("START\n"u8.ToArray(),token);gate.Flush(true);
            while(!exit.IsCompleted){Task next=await VoxCpmTrainingNativeExecutor.NextSignal(exit,stdout,stderr,watchdog).WaitAsync(token);await next;}
            int code=await exit;await Stop(child);jobZero=true;monitor.Cancel();await FinishWatch(watchdog);
            await Task.WhenAll(stdout,stderr).WaitAsync(TimeSpan.FromSeconds(2),token);
            try{await VerifyCache(cache);}finally{cacheChecked=true;}
            Require(code==0&&!state.Error&&state.Ready);
            return await VoxCpmInferenceProtocol.AcceptAsync(call,token);
        }
        catch(NativeOwnedLaunchException e){child=VoxCpmTrainingProcessLauncher.RetainFailure(e.Ownership);unknownLaunch=true;throw new WorkerClientException("worker.cleanup",false);}
        catch(WorkerClientException){throw;}
        catch(OperationCanceledException){throw new WorkerClientException("worker.canceled",true);}
        catch(VoxCpmTrainingExecutionException e){if(e.Code=="voxcpm_training.cache.changed")runtime.Training.MarkCacheChanged();throw Map(e);}
        catch{throw new WorkerClientException("worker.crash",true);}
        finally
        {
            bool zero=child==null||jobZero;
            if(child!=null&&!jobZero&&!unknownLaunch){try{await Stop(child);zero=true;}catch{zero=false;}}
            monitor.Cancel();streams.Cancel();
            if(watchdog!=null){try{await watchdog;}catch{}}
            foreach(Task? task in new[]{stdout,stderr})if(task!=null){try{await task.WaitAsync(TimeSpan.FromSeconds(2));}catch{}}
            bool changed=false;
            if(zero)
            {
                try{if(cache!=null&&!cacheChecked)await VerifyCache(cache);}catch{changed=true;}
                finally{try{child?.Dispose();}finally{try{pins?.Dispose();}finally{try{cache?.Dispose();}finally{lease?.Dispose();}}}}
            }
            else retained=new(child!,pins,cache,lease);
            operation.Release();
            if(!zero)throw new WorkerClientException("worker.cleanup",false);
            if(changed)throw new WorkerClientException("worker.runtime.not_ready",true);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await operation.WaitAsync();try{disposed=true;if(retained is{} owned){
            try{await Stop(owned.Child);}catch{throw new WorkerClientException("worker.cleanup",false);}
            try{if(owned.Cache!=null)await VerifyCache(owned.Cache);}finally{owned.Dispose();retained=null;}
        }}finally{operation.Release();}
    }
    private async Task VerifyCache(VoxCpmTrainingCacheLease cache)
    {try{await cache.VerifyUnchangedAsync(CancellationToken.None);}catch{runtime.Training.MarkCacheChanged();throw new WorkerClientException("worker.runtime.not_ready",true);}}
    private static WorkerClientException Map(VoxCpmTrainingExecutionException e)=>new(e.Code switch {
        "voxcpm_training.window.deadline"=>"worker.timeout","voxcpm_training.cache.busy"=>"worker.busy",
        "voxcpm_training.protocol.invalid" or "voxcpm_training.file.invalid"=>"worker.output.invalid",_=>"worker.runtime.not_ready"},e.CleanupConfirmed);
    private static Task Stop(IVoxCpmTrainingOwnedProcess child)=>child.StopTreeAsync().WaitAsync(TimeSpan.FromSeconds(12));
    private static async Task FinishWatch(Task task){try{await task;}catch(OperationCanceledException){}}
    private static void CheckFaults(params Task[] tasks){foreach(var task in tasks)if(task.IsFaulted||task.IsCanceled)task.GetAwaiter().GetResult();}
    private static async Task Watch(IVoxCpmTrainingOwnedProcess child,IVoxCpmTrainingMemoryReader memory,TimeProvider time,long began,CancellationToken token)
    {while(true){token.ThrowIfCancellationRequested();VoxCpmTrainingResourcePolicy.CheckLimits(child.ReadJobMemory());string? code=VoxCpmTrainingResourcePolicy.Violation(memory.AvailableBytes(),time.GetElapsedTime(began));if(code!=null)throw new VoxCpmTrainingExecutionException(code);await Task.Delay(TimeSpan.FromMilliseconds(100),time,token);}}
    private static void NewOutput(VoxCpmTrainingFilePins pins,string root,string relative)
    {
        pins.PinDirectory(root);string current=root;
        foreach(string part in relative.Split('/')){current=Path.Combine(current,part);Directory.CreateDirectory(current);pins.PinDirectory(current);}
        Require(!Directory.EnumerateFileSystemEntries(current).Any());
    }
    private ProcessStartInfo StartInfo(VoxCpmTrainingRuntimeOptions options,string request,string sha,string gate,string? decoderCache)
    {
        var start=new ProcessStartInfo(options.PythonPath){WorkingDirectory=runtime.WorkerRoot,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true};
        foreach(string arg in new[]{"-I","-S","-B","-u",Path.Combine(runtime.WorkerRoot,"worker.py"),"--training-worker-root",options.WorkerRoot,"--request",request,"--request-sha256",sha,"--gate",gate})start.ArgumentList.Add(arg);
        foreach(string key in start.Environment.Keys.Where(k=>k.StartsWith("PYTHON",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(key);
        foreach(string key in start.Environment.Keys.Where(k=>k.StartsWith("LIBROSA_CACHE_",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(key);
        start.Environment["LIBROSA_CACHE_LEVEL"]="0";
        start.Environment["PATH"]=Path.GetDirectoryName(options.PythonPath)+";"+options.PythonBaseRoot+";"+Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach(string key in new[]{"HF_HUB_OFFLINE","TRANSFORMERS_OFFLINE","HF_HUB_DISABLE_TELEMETRY"})start.Environment[key]="1";
        start.Environment["CUDA_VISIBLE_DEVICES"]="";foreach(string key in new[]{"OMP_NUM_THREADS","MKL_NUM_THREADS","OPENBLAS_NUM_THREADS","NUMEXPR_NUM_THREADS"})start.Environment[key]="2";
        // Librosa's eager Numba signatures must not add files to the admitted runtime.
        if(decoderCache!=null)start.Environment["NUMBA_CACHE_DIR"]=VoxCpmTrainingFilePins.Extended(decoderCache);
        start.Environment["TOKENIZERS_PARALLELISM"]="false";return start;
    }
    private static async Task Drain(StreamReader reader,bool error,StreamState state,TaskCompletionSource gated,Func<string,Task> progress,CancellationToken token)
    {
        char[] buffer=new char[128];var line=new StringBuilder();int total=0;
        while(true){int count=await reader.ReadAsync(buffer.AsMemory(),token);if(count==0)break;Require((total+=count)<=4096);
            for(int i=0;i<count;i++){char c=buffer[i];if(c!='\n'){Require(c is >=(char)0x20 and <=(char)0x7e||c=='\r');line.Append(c);Require(line.Length<=128);continue;}
                string value=line.ToString().TrimEnd('\r');line.Clear();
                if(error){Require(value=="voxcpm_inference.worker.failed"&&!state.Error);state.Error=true;continue;}
                if(value=="voxcpm_inference.worker.gated"){Require(!state.Gated&&state.Phase==0);state.Gated=true;gated.TrySetResult();continue;}
                Require(state.Gated&&state.GateOpened&&!state.Ready);
                if(value=="voxcpm_inference.worker.ready"){Require(state.Phase==2);state.Ready=true;continue;}
                int phase=value switch{"voxcpm_inference.worker.loading"=>1,"voxcpm_inference.worker.generating"=>2,_=>0};Require(phase>0&&phase==state.Phase+1);state.Phase=phase;
                await progress(value["voxcpm_inference.worker.".Length..]);
            }
        }Require(line.Length==0);
    }
    private sealed class StreamState{internal volatile bool Gated,GateOpened,Ready,Error;internal int Phase;}
    private sealed record Retained(IVoxCpmTrainingOwnedProcess Child,VoxCpmTrainingFilePins? Pins,VoxCpmTrainingCacheLease? Cache,VoxCpmInferenceRuntimeLease? Runtime):IDisposable
    {public void Dispose(){try{Child.Dispose();}finally{try{Pins?.Dispose();}finally{try{Cache?.Dispose();}finally{Runtime?.Dispose();}}}}}
}

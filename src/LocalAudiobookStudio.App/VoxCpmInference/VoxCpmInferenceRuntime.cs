using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.Workers;

namespace LocalAudiobookStudio.App.VoxCpmInference;

internal sealed class VoxCpmInferenceRuntime(VoxCpmTrainingRuntime training, string? workerRoot = null, string? lockPath = null) : IVoxCpmInferenceRuntime
{
    internal VoxCpmTrainingRuntime Training { get; } = training;
    internal string WorkerRoot { get; } = workerRoot ?? Path.Combine(AppContext.BaseDirectory,"VoxCpmInferenceWorker");
    private string LockPath { get; } = lockPath ?? Path.Combine(AppContext.BaseDirectory,"locks","voxcpm2-inference.lock.json");
    internal async Task<VoxCpmInferenceRuntimeLease> AcquireAsync(CancellationToken token)
    {
        var scripts = await AcquireScriptsAsync(token); VoxCpmTrainingRuntimeLease? admitted = null;
        try
        {
            admitted = await Training.AcquireAsync(token);
            return new(admitted,scripts);
        }
        catch (OperationCanceledException) { admitted?.Dispose(); scripts.Dispose(); throw; }
        catch { admitted?.Dispose(); scripts.Dispose(); throw new WorkerClientException("worker.runtime.not_ready",true); }
    }
    private async Task<VoxCpmTrainingFilePins> AcquireScriptsAsync(CancellationToken token)
    {
        var scripts = new VoxCpmTrainingFilePins();
        try
        {
            string root = VoxCpmTrainingFilePins.Absolute(WorkerRoot);
            foreach (string other in new[] { Training.Options.WorkerRoot,Training.Options.SourceRoot,Training.Options.ModelRoot,Training.Options.PythonBaseRoot }.Concat(Training.Options.DependencySites))
            {
                string path = VoxCpmTrainingFilePins.Absolute(other);
                if (Within(root,path) || Within(path,root)) throw new WorkerClientException("worker.runtime.not_ready",true);
            }
            await VoxCpmInferenceScripts.PinAsync(scripts,root,LockPath,token);
            return scripts;
        }
        catch (OperationCanceledException) { scripts.Dispose(); throw; }
        catch { scripts.Dispose(); throw new WorkerClientException("worker.runtime.not_ready",true); }
    }
    private static bool Within(string value,string root) => value.Equals(root,StringComparison.OrdinalIgnoreCase) || value.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    public async Task<RuntimeReadiness> GetReadinessAsync(CancellationToken token=default)
    {
        try
        {
            using var scripts = await AcquireScriptsAsync(token);
            var available = await Training.GetReadinessAsync(token);
            return new(available.Ready,true,available.Ready ? null : "worker.runtime.not_ready",VoxCpmInferenceProfile.Id);
        }
        catch (WorkerClientException e) { return new(false,true,e.Code,VoxCpmInferenceProfile.Id); }
    }
    public async Task<VoxCpmInferenceIdentity> GetIdentityAsync(CancellationToken token)
    {
        try
        {
            using var scripts = await AcquireScriptsAsync(token);
            return new(await Training.GetIdentityAsync(token),VoxCpmInferenceScripts.ImplementationSha256);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new WorkerClientException("worker.runtime.not_ready",true); }
    }
}
internal sealed class VoxCpmInferenceRuntimeLease(VoxCpmTrainingRuntimeLease training, VoxCpmTrainingFilePins scripts) : IDisposable
{
    internal VoxCpmTrainingRuntimeLease Training { get; }=training;
    public void Dispose(){try{Training.Dispose();}finally{scripts.Dispose();}}
}

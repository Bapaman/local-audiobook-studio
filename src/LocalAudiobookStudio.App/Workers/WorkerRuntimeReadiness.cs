using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.WorkerHost.Engine;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.App.Workers;

/// <summary>Explicit opt-in only; no runtime discovery, download, training or implicit GPU selection.</summary>
public sealed record DeveloperWorkerOptions(string RuntimeRoot, string ModelRoot, string LockRoot, string ProfileId = "q8-f16-cpu", bool AllowCpuFallback = false);

public sealed class WorkerRuntimeReadiness(DeveloperWorkerOptions? options, string managedWorkerRoot) : IRuntimeReadiness, IRuntimeDescriptor
{
    internal DeveloperWorkerOptions? Options => options;
    internal string HostExecutable => WorkerPaths.Resolve(WorkerPaths.Root(managedWorkerRoot), "LocalAudiobookStudio.WorkerHost.exe");

    public async Task<RuntimeReadiness> GetAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (options == null) return new(false, false, "worker.runtime.not_ready", "q8-f16-cpu");
        try
        {
            await ValidateAsync(token);
            return new(true, true, null, options.ProfileId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is WorkerException or IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, true, "worker.runtime.not_ready", SafeProfile()); }
    }

    async Task<RuntimeDescriptor> IRuntimeDescriptor.GetAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await ValidateAsync(token);
            // Non-runtime identities are supplied from the accepted project by the
            // application runner. Source revisions are NOT binary SHA256/provenance.
            return new(new(GenerationPreset.VoxCpmLocalId, options!.ProfileId, GenerationPreset.VoxCpmLocalDecodeSteps, 10, 2, 1, 42, options.AllowCpuFallback),
                new VersionPins("", ArtifactResolver.WorkerVersion, ArtifactResolver.ModelVersion, "", "", ""));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is WorkerException or IOException or UnauthorizedAccessException or ArgumentException)
        { throw new WorkerClientException("worker.runtime.not_ready", true); }
    }

    private async Task ValidateAsync(CancellationToken token)
    {
        if (options == null || options.ProfileId is not ("q8-f16-cpu" or "q8-f16-hybrid-16")) throw new WorkerException("worker.artifacts");
        foreach (string root in new[] { managedWorkerRoot, options.RuntimeRoot, options.ModelRoot, options.LockRoot })
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new WorkerException("worker.path");
        string managed = WorkerPaths.Root(managedWorkerRoot);
        foreach (string suffix in new[] { "exe", "dll", "deps.json", "runtimeconfig.json" })
            if (!File.Exists(WorkerPaths.Resolve(managed, "LocalAudiobookStudio.WorkerHost." + suffix))) throw new WorkerException("worker.artifacts");
        using var artifacts = await new ArtifactResolver(options.RuntimeRoot, options.ModelRoot, options.LockRoot, true).ResolveAsync(options.ProfileId, token);
    }
    private string SafeProfile() => options?.ProfileId is "q8-f16-hybrid-16" ? "q8-f16-hybrid-16" : "q8-f16-cpu";
}

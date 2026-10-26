using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Application.Workers;

/// <summary>Application paths are absolute; worker wire paths are project-relative.</summary>
public sealed class ProjectScopedWorkerClient(string projectRoot, IWorkerClient inner) : IWorkerClient
{
    private readonly string root = ProjectPaths.CanonicalRoot(projectRoot);
    public async Task<SynthesisResult> SynthesizeAsync(string requestId, SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken token)
    {
        var wire = command with { PromptWav = Relative(command.PromptWav), FinalPath = Relative(command.FinalPath) };
        var result = await inner.SynthesizeAsync(requestId, wire, progress, token);
        if (result.FinalPath != wire.FinalPath) throw new WorkerClientException("worker.output.invalid", true);
        return result with { FinalPath = Resolve(wire.FinalPath) };
    }
    private string Relative(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new ProjectException("project.path");
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!string.Equals(ProjectPaths.Resolve(root, relative), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) throw new ProjectException("project.path");
            return relative;
        }
        catch (Exception e) when (e is ProjectException or ArgumentException or NotSupportedException) { throw new WorkerClientException("worker.path", true); }
    }
    private string Resolve(string path)
    {
        try { return ProjectPaths.Resolve(root, path); }
        catch (ProjectException) { throw new WorkerClientException("worker.output.invalid", true); }
    }
    public Task CancelAsync(string requestId, CancellationToken token) => inner.CancelAsync(requestId, token);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

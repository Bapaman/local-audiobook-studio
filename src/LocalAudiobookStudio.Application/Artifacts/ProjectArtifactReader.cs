using LocalAudiobookStudio.Application.Projects;

namespace LocalAudiobookStudio.Application.Artifacts;

public sealed class ProjectArtifactReader(StudioProjectService projects) : IProjectArtifactReader
{
    public Task<ArtifactReadHandle> OpenAsync(string projectId, string artifactId, CancellationToken cancellationToken = default)
        => projects.ReadArtifactAsync(projectId, artifactId, cancellationToken);
}

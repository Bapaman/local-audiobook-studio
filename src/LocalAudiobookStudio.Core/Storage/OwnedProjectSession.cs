namespace LocalAudiobookStudio.Core.Storage;

/// <summary>Preparation ownership and its pinned database. Dispose ownership before closing the database.</summary>
public sealed class OwnedProjectSession : IAsyncDisposable
{
    public ProjectDatabase Project { get; }
    public ProjectArtifactLease Lease { get; }
    private int disposed;
    internal OwnedProjectSession(ProjectDatabase project, ProjectArtifactLease lease) { Project = project; Lease = lease; }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await Lease.DisposeAsync(); }
        finally { await Project.DisposeAsync(); }
    }
}

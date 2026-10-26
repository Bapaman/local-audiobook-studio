using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Jobs;

/// <summary>Registered ID resolution only. No studio lock, artifact ownership or recovery on current reads.</summary>
public sealed class ProjectJobAccess(ProjectCatalogOptions options, IProjectCatalog catalog, ProjectService core)
{
    private readonly CatalogPaths paths = new(options);
    internal async Task<JobProjectSession> OpenCurrentAsync(string projectId, CancellationToken token, bool requireJobSchema = false)
    {
        CatalogPaths.ValidateId(projectId);
        if (await catalog.FindAsync(projectId, token) == null) throw new StudioException("project.unavailable");
        var pins = paths.PinProject(projectId);
        ProjectDatabase? db = null;
        try
        {
            paths.CheckSnapshotSize(projectId);
            db = await core.OpenCurrentAsync(paths.Project(projectId), token, retainSidecars: true, requireJobSchema: requireJobSchema, requirePreparedSidecars: true);
            paths.CheckSnapshotSize(projectId);
            StudioProjectService.ValidateState(db.State, projectId);
            return new(db, pins);
        }
        catch { if (db != null) await db.DisposeAsync(); pins.Dispose(); throw; }
    }
    internal async Task<IReadOnlyList<string>> RegisteredIdsAsync(CancellationToken token)
        => (await catalog.ListAsync(token)).Select(x => x.Id).ToArray();

    internal Task<bool> HasTrainingStateAsync(string projectId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); CatalogPaths.ValidateId(projectId);
        using var pins = paths.PinProject(projectId);
        return Task.FromResult(File.Exists(ProjectPaths.Resolve(paths.Project(projectId), "training/state.json")));
    }

    internal Task<bool> HasTtsCorpusStateAsync(string projectId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); CatalogPaths.ValidateId(projectId);
        using var pins = paths.PinProject(projectId);
        return Task.FromResult(File.Exists(ProjectPaths.Resolve(paths.Project(projectId), "tts-corpus/state.json")));
    }
    internal Task<bool> HasVoxCpmTrainingStateAsync(string projectId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); CatalogPaths.ValidateId(projectId);
        using var pins = paths.PinProject(projectId);
        return Task.FromResult(File.Exists(ProjectPaths.Resolve(paths.Project(projectId), "voxcpm-training/state.json")));
    }

    // Resolve/copy before the runner takes its inference slot. Only the later
    // explicit owned handoff may prepare the source or migrate its schema.
    internal async Task<JobPreparation> CapturePreparationAsync(string projectId, CancellationToken token)
    {
        CatalogPaths.ValidateId(projectId);
        if (await catalog.FindAsync(projectId, token) == null) throw new StudioException("project.unavailable");
        var pins = paths.PinProject(projectId); GuardedSqliteSnapshot? snapshot = null;
        try
        {
            paths.CheckSnapshotSize(projectId);
            snapshot = GuardedSqliteSnapshot.Create(paths, Path.Combine(paths.Project(projectId), "project.sqlite"), token);
            var preview = await core.ProbeSnapshotDetailsAsync(paths.Project(projectId), snapshot.Database, token);
            StudioProjectService.ValidateState(preview.State, projectId);
            return new(projectId, pins, snapshot, paths, core);
        }
        catch { snapshot?.Dispose(); pins.Dispose(); throw; }
    }

    internal sealed class JobPreparation(string id, IDisposable pins, GuardedSqliteSnapshot snapshot, CatalogPaths paths, ProjectService core) : IDisposable
    {
        private IDisposable? retainedPins = pins;
        public async Task<JobProjectSession> OpenOwnedAsync(CancellationToken token)
        {
            var owned = await core.OpenOwnedFromSnapshotAsync(paths.Project(id), snapshot.Database, token);
            try
            {
                StudioProjectService.ValidateState(owned.Project.State, id); paths.CheckSnapshotSize(id);
                var result = new JobProjectSession(owned.Project, retainedPins!, owned); retainedPins = null;
                return result;
            }
            catch { await owned.DisposeAsync(); throw; }
        }
        public void Dispose() { try { snapshot.Dispose(); } finally { retainedPins?.Dispose(); retainedPins = null; } }
    }

    internal sealed class JobProjectSession(ProjectDatabase project, IDisposable pins, OwnedProjectSession? owned = null) : IAsyncDisposable
    {
        public ProjectDatabase Project { get; } = project;
        public ProjectArtifactLease? PreparationLease => owned?.Lease;
        public async ValueTask DisposeAsync()
        { try { if (owned != null) await owned.DisposeAsync(); else await Project.DisposeAsync(); } finally { pins.Dispose(); } }
    }
}

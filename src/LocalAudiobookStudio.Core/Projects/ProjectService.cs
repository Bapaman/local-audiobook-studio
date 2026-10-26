using LocalAudiobookStudio.Core.Storage;
using System.Security.Cryptography;
using System.Text;

namespace LocalAudiobookStudio.Core.Projects;

public sealed class ProjectService
{
    private Action<string>? lifecycleObserver = null;
    private void ObserveLifecycle(string boundary)
    {
        try { lifecycleObserver?.Invoke(boundary); } catch (Exception) { }
    }
    public Task<ProjectDatabase> CreateAsync(string root, string title, CancellationToken cancellationToken = default)
        => CreateAsync(root, title, Guid.NewGuid().ToString("N"), cancellationToken);

    public Task<ProjectDatabase> CreateAsync(string root, string title, string projectId, CancellationToken cancellationToken = default)
        => CreateAsync(root, title, projectId, cancellationToken, retainSidecars: false);

    public async Task<ProjectDatabase> CreateAsync(string root, string title, string projectId, CancellationToken cancellationToken, bool retainSidecars)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(projectId, "N", out var id) || id.ToString("N") != projectId) throw new ProjectException("project.id.invalid");
        if (title == null) throw new ProjectException("project.schema.invalid");
        root = ProjectPaths.CanonicalRoot(root);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(title)));
        var publication = await OwnedDatabaseBootstrap.CreateAsync(ProjectPaths.Resolve(root, "project.sqlite"), "project-v2", projectId + ":" + fingerprint,
            async (db, token) =>
        {
            await ProjectMigrations.InitializeAsync(db, true, token);
            ObserveLifecycle("bootstrap-schema-committed");
            token.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await ProjectDatabase.InitializeMetadataAsync(db, new(projectId, title, "", new("", "", "", "", "", ""), new("neutral", 1, 1, 0, "{}"), now, now), token);
        }, cancellationToken, ObserveLifecycle);
        try
        {
            var project = await OpenAsync(root, cancellationToken, retainSidecars);
            project.RetainCreationPin(publication); return project;
        }
        catch { publication.Dispose(); throw; }
    }

    /// <summary>Reads and validates committed state without recovery, migration or journal-mode changes.</summary>
    public Task<ProjectState> ProbeAsync(string root, CancellationToken cancellationToken = default)
        => ProjectDatabase.ProbeAsync(root, cancellationToken);

    /// <summary>Validates an application-owned stable database copy against the original root's owned paths.</summary>
    public Task<ProjectState> ProbeSnapshotAsync(string root, string snapshotDatabasePath, CancellationToken cancellationToken = default)
        => ProjectDatabase.ProbeAsync(root, cancellationToken, snapshotDatabasePath);

    public Task<ProjectReadSnapshot> ProbeSnapshotDetailsAsync(string root, string snapshotDatabasePath, CancellationToken cancellationToken = default)
        => ProjectDatabase.ProbeDetailsAsync(root, cancellationToken, snapshotDatabasePath);

    public Task<ProjectDatabase> OpenAsync(string root, CancellationToken cancellationToken = default)
        => OpenAsync(root, cancellationToken, retainSidecars: false);

    /// <summary>Loads committed state without recovery. Application callers require pre-owned sidecar preparation.</summary>
    public Task<ProjectDatabase> OpenCurrentAsync(string root, CancellationToken token, bool retainSidecars = true, bool requireJobSchema = false, bool requirePreparedSidecars = false)
        => ProjectDatabase.ConnectCurrentAsync(root, token, retainSidecars, requireJobSchema, requirePreparedSidecars);

    /// <summary>Explicit input edit: idle checks under ownership before migration; never runs recovery.</summary>
    public Task<ProjectDatabase> OpenForInputMutationAsync(string root, CancellationToken token, bool retainSidecars = true)
        => ProjectDatabase.ConnectAsync(root, token, retainSidecars, requireIdleJobs: true);

    /// <summary>Explicit coordinator/edit preparation from an owned stable copy. Holds ownership; never recovers.</summary>
    public Task<OwnedProjectSession> OpenOwnedFromSnapshotAsync(string root, string snapshotDatabasePath, CancellationToken token, bool requireIdleJobs = false)
        => ProjectDatabase.ConnectOwnedFromSnapshotAsync(root, snapshotDatabasePath, token, requireIdleJobs);

    public async Task<ProjectDatabase> OpenAsync(string root, CancellationToken cancellationToken, bool retainSidecars)
    {
        var project = await ProjectDatabase.ConnectAsync(root, cancellationToken, retainSidecars);
        try { await project.LoadAsync(cancellationToken); await RecoverInterruptedAsync(project, cancellationToken); return project; }
        catch { await project.DisposeAsync(); throw; }
    }

    public async Task RecoverInterruptedAsync(ProjectDatabase project, CancellationToken cancellationToken = default)
    {
        await using var lease = await project.AcquireArtifactLeaseAsync(cancellationToken: cancellationToken);
        await RecoverUnderLeaseAsync(project, lease, cancellationToken);
    }

    public Task RecoverUnderLeaseAsync(ProjectDatabase project, ProjectArtifactLease lease, CancellationToken cancellationToken)
        => RecoverUnderLeaseWithResultAsync(project, lease, cancellationToken);

    /// <summary>Reports a committed job/legacy running-state transition, not a wakeup or staging-only cleanup.</summary>
    public async Task<bool> RecoverUnderLeaseWithResultAsync(ProjectDatabase project, ProjectArtifactLease lease, CancellationToken cancellationToken)
    {
        lease.ValidateOwner(project);
        await lease.RefreshAsync(cancellationToken);
        bool transitioned = await project.RecoverGenerationUnitsAsync(lease, cancellationToken);
        var state = project.State; DateTimeOffset now = DateTimeOffset.UtcNow;
        if (state.Segments.Any(x => x.Status == "running") || state.Attempts.Any(x => x.Status == "running"))
        {
            var attempts = state.Attempts.Select(x => x.Status == "running" ? x with { Status = "interrupted", CompletedUtc = now, DiagnosticCode = "project.interrupted" } : x).ToList();
            foreach (var segment in state.Segments.Where(x => x.Status == "running" && !state.Attempts.Any(a => a.SegmentId == x.Id && a.Status == "running")))
                attempts.Add(new(Guid.NewGuid().ToString("N"), segment.Id, state.Attempts.Where(a => a.SegmentId == segment.Id).Select(a => a.Order).DefaultIfEmpty(0).Max() + 1,
                    segment.Seed, segment.Versions, segment.Parameters, segment.GenerationFingerprint, "interrupted", null, null, segment.UpdatedUtc, now, "project.interrupted"));
            await lease.SaveAsync(state with
            {
                Segments = state.Segments.Select(x => x.Status == "running" ? x with { Status = "queued", UpdatedUtc = now, DiagnosticCode = "project.interrupted" } : x).ToArray(),
                Attempts = attempts
            }, cancellationToken);
            transitioned = true;
        }
        await project.CleanStagingAsync(lease, cancellationToken);
        return transitioned;
    }
}

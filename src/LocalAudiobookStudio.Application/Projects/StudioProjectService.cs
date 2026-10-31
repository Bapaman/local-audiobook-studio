using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Application.Artifacts;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Audio;

namespace LocalAudiobookStudio.Application.Projects;

public sealed class StudioProjectService(ProjectCatalogOptions options, IProjectCatalog catalog, ProjectService core) : IStudioProjectService
{
    private readonly CatalogPaths paths = new(options);
    private readonly ProjectJobAccess currentAccess = new(options, catalog, core);

    public Task<IReadOnlyList<ProjectListItem>> ListAsync(CancellationToken cancellationToken = default)
        => WithLeaseAsync<IReadOnlyList<ProjectListItem>>(async token =>
        {
            var result = new List<ProjectListItem>();
            foreach (var candidate in await catalog.ReconcileAsync(token))
            {
                if (candidate.Pending && IsPreMainReservation(candidate.Id)) continue;
                var state = await ProbeCandidateAsync(candidate.Id, token);
                if (state == null) continue;
                await catalog.InsertAsync(new(candidate.Id, DateTimeOffset.MinValue), token);
                var project = await catalog.FindAsync(candidate.Id, token);
                if (project == null) continue;
                result.Add(new(state.Metadata.Id, state.Metadata.Title, state.Metadata.CreatedUtc, project.LastOpenedUtc,
                    state.Revision, state.VoiceRecording != null || state.Voice != null, state.Voice != null, state.Chapters.Count > 0));
            }
            return result.OrderByDescending(x => x.LastOpenedUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        }, cancellationToken);

    public Task<CreateProjectResult> CreateAsync(CreateProjectCommand command, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        string title = CanonicalTitle(command.Title);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256 || idempotencyKey.Any(char.IsControl)) throw new StudioException("project.idempotency.invalid");
        return WithLeaseAsync(async token =>
        {
            var receipt = await catalog.FindReceiptAsync(idempotencyKey, token);
            if (receipt != null && receipt.RequestFingerprint != IdempotencyReceipt.Hash(title)) throw new StudioException("project.idempotency.conflict");
            if (receipt == null)
            {
                receipt = IdempotencyReceipt.Pending(idempotencyKey, title, Guid.NewGuid().ToString("N"));
                await catalog.StoreReceiptAsync(receipt, token);
            }
            bool created = !receipt.Completed;
            ProjectState state;
            ProjectReadSnapshot read;
            if (receipt.Completed)
            {
                read = await RequireCandidateSnapshotAsync(receipt.ProjectId, token);
                state = read.State;
                if (await catalog.FindAsync(receipt.ProjectId, token) == null) throw new StudioException("project.unavailable");
            }
            else
            {
                string root = paths.Project(receipt.ProjectId);
                // Only this receipt's safely pinned empty/generated pre-main state
                // can resume; identity guards survive through Core connection close.
                using var reservation = PinPendingReservation(receipt.ProjectId);
                if (reservation != null)
                {
                    await using var project = await core.CreateAsync(root, title, receipt.ProjectId, token, retainSidecars: true);
                    read = await project.ReadCurrentSnapshotAsync(token);
                }
                else read = await RequireCandidateSnapshotAsync(receipt.ProjectId, token);
                state = read.State;
                if (IdempotencyReceipt.Hash(state.Metadata.Title) != receipt.RequestFingerprint)
                {
                    await catalog.QuarantineAsync(receipt.ProjectId, token); throw new StudioException("project.unavailable");
                }
                await catalog.FinalizeAsync(new(receipt.ProjectId, DateTimeOffset.MinValue), receipt with { Completed = true }, token);
            }
            return new CreateProjectResult(Map(read), created);
        }, cancellationToken);
    }

    private bool IsPreMainReservation(string id)
    {
        try { using var pins = paths.PinPreMainReservation(id); return pins != null; }
        catch (Exception e) when (e is StudioException or IOException or UnauthorizedAccessException) { return false; }
    }

    private IDisposable? PinPendingReservation(string id)
    {
        try { return paths.PinPreMainReservation(id, create: true); }
        catch (Exception e) when (e is StudioException or IOException or UnauthorizedAccessException)
        { throw new StudioException("project.unavailable"); }
    }

    public Task<ProjectSnapshotDto> GetAsync(string projectId, CancellationToken cancellationToken = default)
    {
        CatalogPaths.ValidateId(projectId);
        return WithErrorsAsync(async token => Map(await ReadBrowserSnapshotAsync(projectId, token)), cancellationToken);
    }

    public Task<ProjectSnapshotDto> OpenAsync(string projectId, CancellationToken cancellationToken = default)
        => OpenAsync(projectId, null, cancellationToken);

    public Task<ProjectSnapshotDto> OpenAsync(string projectId, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        CatalogPaths.ValidateId(projectId);
        return WithErrorsAsync(async token =>
        {
            var current = await ReadBrowserSnapshotAsync(projectId, token);
            if (expectedRevision.HasValue && current.State.Revision != expectedRevision) throw new StudioException("project.conflict");
            var snapshot = Map(current);
            await catalog.TouchOpenedAsync(projectId, DateTimeOffset.UtcNow, token);
            return snapshot;
        }, cancellationToken);
    }

    private async Task<ProjectReadSnapshot> ReadBrowserSnapshotAsync(string id, CancellationToken token)
    {
        // Registered active projects can be read without studio/artifact ownership.
        // A closed WAL/legacy or discovery candidate uses only the stable copy.
        var current = await TryReadCurrentAsync(id, token);
        if (current != null) return current;
        return await WithLeaseAsync(async inner => await FindSnapshotAsync(id, inner), token);
    }

    private async Task<ProjectReadSnapshot?> TryReadCurrentAsync(string id, CancellationToken token)
    {
        try
        {
            await using var session = await currentAccess.OpenCurrentAsync(id, token, requireJobSchema: true);
            var snapshot = await session.Project.ReadCurrentSnapshotAsync(token);
            ValidateState(snapshot.State, id); paths.CheckSnapshotSize(id); return snapshot;
        }
        catch (Exception e) when (e is ProjectException or StudioException or IOException or UnauthorizedAccessException)
        { return null; } // The bounded stable probe distinguishes invalid from transient.
    }

    private async Task<ProjectState> FindStateAsync(string id, CancellationToken token)
        => (await FindSnapshotAsync(id, token)).State;

    private async Task<ProjectReadSnapshot> FindSnapshotAsync(string id, CancellationToken token)
    {
        var candidates = await catalog.ReconcileAsync(token);
        if (!candidates.Any(x => x.Id == id)) throw new StudioException("project.unavailable");
        if (candidates.Any(x => x.Id == id && x.Pending) && IsPreMainReservation(id)) throw new StudioException("project.unavailable");
        var snapshot = await ProbeStableCandidateSnapshotAsync(id, token) ?? throw new StudioException("project.unavailable");
        await catalog.InsertAsync(new(id, DateTimeOffset.MinValue), token);
        return snapshot;
    }

    internal async Task<ArtifactReadHandle> ReadArtifactAsync(string projectId, string artifactId, CancellationToken cancellationToken)
    {
        CatalogPaths.ValidateId(projectId);
        if (!CatalogPaths.IsId(artifactId)) throw new StudioException("artifact.missing");
        var artifactPins = new CatalogPaths.Pins(); FileStream? content = null; bool transferred = false;
        try
        {
            await using (var session = await OpenPlaybackSessionAsync(projectId, cancellationToken))
            {
                var accepted = await LookupArtifactAsync(session.Project, artifactId, cancellationToken)
                    ?? throw new StudioException("artifact.missing");
                string acceptedPath = session.Project.ResolveOwnedPath(accepted.RelativePath);
                CatalogPaths.PinDirectories(Path.GetDirectoryName(acceptedPath)!, artifactPins);
                var opened = CatalogPaths.OpenFile(acceptedPath, 0x80000000, 1, 3);
                try { content = new FileStream(opened, FileAccess.Read, 65536); }
                catch { opened.Dispose(); throw; }
                var inspection = await WavInspector.InspectAsync(acceptedPath, cancellationToken);
                if (!string.Equals(accepted.Hash, inspection.Sha256, StringComparison.OrdinalIgnoreCase)
                    || accepted.Length is { } length && content.Length != length
                    || accepted.Samples is { } samples && inspection.SampleCount != samples)
                    throw new StudioException("artifact.invalid");
                // A fresh committed lookup while the actual file identity is pinned.
                // No stale ProjectDatabase.State, artifact lease or recovery is used.
                await using var check = await OpenPlaybackSessionAsync(projectId, cancellationToken);
                if (await LookupArtifactAsync(check.Project, artifactId, cancellationToken) != accepted)
                    throw new StudioException("artifact.missing");
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Both short database sessions have closed before response ownership transfers.
            var result = new ArtifactReadHandle(content!, content!.Length, "audio/wav", "audio.wav", artifactPins);
            transferred = true; return result;
        }
        catch (WavInspectionException) { throw new StudioException("artifact.invalid"); }
        finally
        {
            if (!transferred)
            {
                try { if (content != null) await content.DisposeAsync(); }
                finally { artifactPins.Dispose(); }
            }
        }
    }

    private async Task<ProjectJobAccess.JobProjectSession> OpenPlaybackSessionAsync(string projectId, CancellationToken token)
    {
        try { return await currentAccess.OpenCurrentAsync(projectId, token); }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException
            || e is StudioException { Code: "catalog.path" or "catalog.storage" })
        { throw new StudioException("project.unavailable"); }
    }

    private sealed record PlaybackAcceptance(string RelativePath, string Hash, long? Length, long? Samples);
    private static async Task<PlaybackAcceptance?> LookupArtifactAsync(ProjectDatabase project, string artifactId, CancellationToken token)
    {
        if (project.State.VoiceRecording is { } recording && recording.Id == artifactId)
            return new(recording.WavRelativePath, recording.Sha256, recording.ByteLength, recording.SampleCount);
        if (project.State.Voice is { } anchor && anchor.Id == artifactId)
            return new(anchor.WavRelativePath, anchor.WavHash, null, anchor.StartMilliseconds is { } start && anchor.EndMilliseconds is { } end ? checked((end - start) * 48) : null);
        var output = await project.Jobs.FindAcceptedArtifactAsync(artifactId, token);
        return output == null ? null : new(output.RelativePath, output.Sha256, output.Length, output.SampleCount);
    }

    private async Task<ProjectReadSnapshot> RequireCandidateSnapshotAsync(string id, CancellationToken token)
        => await ProbeCandidateSnapshotAsync(id, token) ?? throw new StudioException("project.unavailable");

    private async Task<ProjectState?> ProbeCandidateAsync(string id, CancellationToken token)
        => (await ProbeCandidateSnapshotAsync(id, token))?.State;

    private async Task<ProjectReadSnapshot?> ProbeCandidateSnapshotAsync(string id, CancellationToken token)
    {
        var current = await TryReadCurrentAsync(id, token);
        if (current != null) return current;
        return await ProbeStableCandidateSnapshotAsync(id, token);
    }

    private async Task<ProjectReadSnapshot?> ProbeStableCandidateSnapshotAsync(string id, CancellationToken token)
    {
        try
        {
            using var pins = paths.PinProject(id);
            using var snapshot = GuardedSqliteSnapshot.Create(paths, Path.Combine(paths.Project(id), "project.sqlite"), token);
            var read = await core.ProbeSnapshotDetailsAsync(paths.Project(id), snapshot.Database, token); ValidateState(read.State, id); return read;
        }
        catch (Exception e) when (IsConfirmedInvalid(e))
        {
            await catalog.QuarantineAsync(id, token); return null;
        }
        catch (Exception e) when (e is ProjectException or StudioException or IOException or UnauthorizedAccessException)
        {
            // Omit an inaccessible candidate for this probe only. It remains eligible
            // for the next reconciliation after a lock or access interruption ends.
            return null;
        }
    }

    private static bool IsConfirmedInvalid(Exception error) => error switch
    {
        ProjectException project => project.Code is "project.missing" or "project.schema.future" or "project.schema.invalid" or "project.path" or "project.timestamp",
        StudioException studio => studio.Code is "catalog.path" or "project.unavailable" or "project.title.invalid",
        _ => false
    };

    internal static void ValidateState(ProjectState state, string id)
    {
        if (state.Metadata.Id != id || state.Metadata.Title != CanonicalTitle(state.Metadata.Title)) throw new StudioException("project.unavailable");
    }
    private static string CanonicalTitle(string title)
    {
        if (title == null || title.Any(char.IsControl)) throw new StudioException("project.title.invalid");
        title = title.Trim();
        if (title.Length is 0 or > 120) throw new StudioException("project.title.invalid");
        return title;
    }
    private static ProjectSnapshotDto Map(ProjectReadSnapshot snapshot) => Map(snapshot.State, snapshot.OutputArtifactIds);
    internal static ProjectSnapshotDto Map(ProjectState state, IReadOnlyDictionary<string, string>? outputArtifactIds = null) => new(state.Metadata.Id, state.Metadata.Title, state.Revision,
        state.VoiceRecording is { } recording ? new(recording.Id, recording.Sha256, recording.ByteLength, recording.SampleCount / 48, recording.HasClippingWarning) : null,
        state.Voice is { StartMilliseconds: { } start, EndMilliseconds: { } end } voice ? new(voice.Id, start, end, voice.Transcript) : null,
        state.Chapters.FirstOrDefault() is { } chapter ? MapDocument(chapter) : null,
        state.Segments.Select(x => new SegmentDto(x.Id, x.Order, x.OriginalText, x.NormalizedText, x.SpokenText, x.TextHash, x.Status,
            outputArtifactIds != null && outputArtifactIds.TryGetValue(x.Id, out var artifactId) ? artifactId : null)).ToArray(),
        state.Chapters.Count > 0, state.Voice != null,
        state.Chapters.Count > 1 && state.Chapters.All(c => c.Provenance != null) ? state.Chapters.OrderBy(c => c.Order).Select(c =>
            new ProjectTextDocumentDto(c.Id,c.Order,MapDocument(c)!,state.Segments.Where(s => s.ChapterId == c.Id).OrderBy(s => s.Order).Select(s => s.Id).ToArray())).ToArray() : null);

    private static TextDocumentDto? MapDocument(ChapterRecord chapter) => chapter.Provenance is { } provenance ?
        new(provenance.SourceKind.ToString(),provenance.DisplayName,provenance.SourceEncoding,provenance.SourceHash,
            provenance.OriginalByteCount,chapter.OriginalText,chapter.NormalizedText) : null;

    internal async Task<ProjectSnapshotDto> WriteAsync(string projectId, long expectedRevision, Func<ProjectDatabase, CancellationToken, Task> action, CancellationToken token)
    {
        CatalogPaths.ValidateId(projectId);
        try
        {
            if ((await ReadBrowserSnapshotAsync(projectId, token)).HasActiveJob) throw new StudioException("job.active");
            using var lease = await paths.AcquireAsync(studio: true, token);
            ProjectJobAccess.JobProjectSession? live;
            try { live = await currentAccess.OpenCurrentAsync(projectId, token, requireJobSchema: true); }
            catch (Exception e) when (e is ProjectException or StudioException or IOException or UnauthorizedAccessException)
            { live = null; }
            if (live != null)
            {
                await using (live)
                {
                    // Prepared current projects already have validated/pinned names.
                    // A stable-file copy denies other live SQLite connections, even
                    // idle coordinator readers, and must be reserved for preparation.
                    var before = await live.Project.ReadCurrentSnapshotAsync(token);
                    if (before.HasActiveJob) throw new StudioException("job.active");
                    if (before.State.Revision != expectedRevision) throw new StudioException("project.conflict");
                    // Each callback acquires the input-mutation lease and rechecks
                    // active jobs/revision before any streaming or acceptance.
                    await action(live.Project, token);
                    return Map(await live.Project.ReadCurrentSnapshotAsync(token));
                }
            }
            var snapshot = await FindSnapshotAsync(projectId, token);
            if (snapshot.HasActiveJob) throw new StudioException("job.active");
            if (snapshot.State.Revision != expectedRevision) throw new StudioException("project.conflict");
            // Recheck after catalog bookkeeping (which may have raced enqueue),
            // before a stable copy that intentionally denies live SQLite writers.
            var current = await TryReadCurrentAsync(projectId, token);
            if (current?.HasActiveJob == true) throw new StudioException("job.active");
            using var pins = paths.PinProject(projectId);
            using var copy = GuardedSqliteSnapshot.Create(paths, Path.Combine(paths.Project(projectId), "project.sqlite"), token);
            var preparedPreview = await core.ProbeSnapshotDetailsAsync(paths.Project(projectId), copy.Database, token);
            ValidateState(preparedPreview.State, projectId);
            if (preparedPreview.HasActiveJob) throw new StudioException("job.active");
            await using var owned = await core.OpenOwnedFromSnapshotAsync(paths.Project(projectId), copy.Database, token, requireIdleJobs: true);
            var project = owned.Project;
            ValidateState(project.State, projectId);
            if (project.State.Revision != expectedRevision) throw new StudioException("project.conflict");
            // Text/voice callbacks acquire and recheck their own actual mutation
            // lease. Never hold an outer lease and deadlock that guarded handoff.
            await owned.Lease.DisposeAsync();
            await action(project, token);
            return Map(await project.ReadCurrentSnapshotAsync(token));
        }
        catch (ProjectException e) { throw new StudioException(e.Code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new StudioException("project.unavailable"); }
    }

    private async Task<T> WithLeaseAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
        => await WithErrorsAsync(async inner => { using var lease = await paths.AcquireAsync(studio: true, inner); return await action(inner); }, token);

    private static async Task<T> WithErrorsAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        try { return await action(token); }
        catch (Exception e) when (e is ProjectException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new StudioException("project.unavailable"); }
    }
}

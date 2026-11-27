using System.Runtime.ExceptionServices;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Audio;

namespace LocalAudiobookStudio.Application.Artifacts;

public sealed class BookDownloadService(IStudioProjectService projects, IProjectArtifactReader artifacts)
{
    public Task<BookReadHandle> OpenAsync(string projectId, long revision, CancellationToken token = default)
        => OpenAsync(projectId,revision,null,token);
    public async Task<BookReadHandle> OpenAsync(string projectId, long revision, string? textDocumentId, CancellationToken token = default)
    {
        var before = await projects.GetAsync(projectId, token);
        if (revision < 1 || before.Revision != revision) throw new StudioException("project.conflict");
        var segments = Ordered(before,textDocumentId);
        var handles = new List<ArtifactReadHandle>();
        try
        {
            foreach (var segment in segments)
                handles.Add(await artifacts.OpenAsync(projectId, segment.OutputArtifactId!, token));
            var assembly = await PcmWavAssembly.CreateAsync(handles.Select(h => h.Content).ToArray(), token);
            var after = await projects.GetAsync(projectId, token);
            if (after.Revision != revision || !segments.SequenceEqual(Ordered(after,textDocumentId))) throw new StudioException("project.conflict");
            token.ThrowIfCancellationRequested();
            return new(assembly, handles.AsReadOnly());
        }
        catch (WavInspectionException error)
        {
            await BookReadHandle.ReleaseAsync(handles);
            throw new StudioException(error.Code.StartsWith("book.", StringComparison.Ordinal) ? error.Code : "artifact.invalid");
        }
        catch
        {
            await BookReadHandle.ReleaseAsync(handles);
            throw;
        }
    }
    private static SegmentDto[] Ordered(ProjectSnapshotDto snapshot, string? textDocumentId)
    {
        var segments = textDocumentId == null ? snapshot.Segments : SelectSegments(snapshot, ResolveDocument(snapshot, textDocumentId));
        if (segments.Count is < 1 or > PcmWavAssembly.MaximumFragments) throw new StudioException("book.input_limit");
        return OrderAcceptedSegments(segments);
    }

    internal static ProjectTextDocumentDto ResolveDocument(ProjectSnapshotDto snapshot, string id)
    {
        if (!Guid.TryParseExact(id, "N", out var parsed) || parsed.ToString("N") != id) throw new StudioException("text.id.invalid");
        return snapshot.TextDocuments?.FirstOrDefault(document => document.Id == id) ?? throw new StudioException("text.document.missing");
    }

    internal static IReadOnlyList<SegmentDto> SelectSegments(ProjectSnapshotDto snapshot, ProjectTextDocumentDto document)
    {
        if (document.SegmentIds.Count != document.SegmentIds.Distinct(StringComparer.Ordinal).Count()) throw new StudioException("book.incomplete");
        var byId = snapshot.Segments.ToLookup(segment => segment.Id, StringComparer.Ordinal);
        return document.SegmentIds.Select(id => byId[id].Count() == 1
            ? byId[id].Single()
            : throw new StudioException("book.incomplete")).ToArray();
    }

    internal static SegmentDto[] OrderAcceptedSegments(IReadOnlyList<SegmentDto> segments)
    {
        if (segments.Count == 0 || segments.Any(s => string.IsNullOrWhiteSpace(s.OutputArtifactId))
            || segments.Select(s => s.Order).Distinct().Count() != segments.Count
            || segments.Select(s => s.Id).Distinct().Count() != segments.Count
            || segments.Select(s => s.OutputArtifactId).Distinct().Count() != segments.Count) throw new StudioException("book.incomplete");
        return segments.OrderBy(s => s.Order).ToArray();
    }
}

public sealed class BookReadHandle(PcmWavAssembly assembly, IReadOnlyList<ArtifactReadHandle> handles) : IAsyncDisposable
{
    private int disposed;
    public long Length => assembly.Length;
    public Task CopyToAsync(Stream output, CancellationToken token) => assembly.CopyToAsync(output, token);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) await ReleaseAsync(handles);
    }
    internal static async Task ReleaseAsync(IEnumerable<ArtifactReadHandle> handles)
    {
        Exception? failure = null;
        foreach (var handle in handles)
        {
            try { await handle.DisposeAsync(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

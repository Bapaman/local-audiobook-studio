using System.Runtime.ExceptionServices;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Audio;

namespace LocalAudiobookStudio.Application.Artifacts;

public sealed class BookDownloadService(IStudioProjectService projects, IProjectArtifactReader artifacts)
{
    public async Task<BookReadHandle> OpenAsync(string projectId, long revision, CancellationToken token = default)
    {
        var before = await projects.GetAsync(projectId, token);
        if (revision < 1 || before.Revision != revision) throw new StudioException("project.conflict");
        var segments = Ordered(before);
        var handles = new List<ArtifactReadHandle>();
        try
        {
            foreach (var segment in segments)
                handles.Add(await artifacts.OpenAsync(projectId, segment.OutputArtifactId!, token));
            var assembly = await PcmWavAssembly.CreateAsync(handles.Select(h => h.Content).ToArray(), token);
            var after = await projects.GetAsync(projectId, token);
            if (after.Revision != revision || !segments.SequenceEqual(Ordered(after))) throw new StudioException("project.conflict");
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
    private static SegmentDto[] Ordered(ProjectSnapshotDto snapshot)
    {
        if (snapshot.Segments.Count is < 1 or > 256) throw new StudioException("book.input_limit");
        if (snapshot.Segments.Any(s => string.IsNullOrWhiteSpace(s.OutputArtifactId))) throw new StudioException("book.incomplete");
        if (snapshot.Segments.Select(s => s.Order).Distinct().Count() != snapshot.Segments.Count
            || snapshot.Segments.Select(s => s.Id).Distinct().Count() != snapshot.Segments.Count
            || snapshot.Segments.Select(s => s.OutputArtifactId).Distinct().Count() != snapshot.Segments.Count) throw new StudioException("book.incomplete");
        return snapshot.Segments.OrderBy(s => s.Order).ToArray();
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

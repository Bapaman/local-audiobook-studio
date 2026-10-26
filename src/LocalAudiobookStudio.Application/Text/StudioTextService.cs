using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Application.Text;

public interface IStudioTextService
{
    Task<ProjectSnapshotDto> ImportFileAsync(string projectId, long expectedRevision, string fileName, Stream content, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> ImportPastedAsync(string projectId, long expectedRevision, ImportPastedText command, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> UpdateSpokenTextAsync(string projectId, string segmentId, long expectedRevision, UpdateSpokenText command, CancellationToken cancellationToken = default);
}

public sealed class StudioTextService(StudioProjectService projects) : IStudioTextService
{
    private readonly TextImportService importer = new();
    private readonly RussianTextFrontend frontend = new();
    private readonly SemanticSegmenter segmenter = new();

    public Task<ProjectSnapshotDto> ImportFileAsync(string projectId, long expectedRevision, string fileName, Stream content, CancellationToken cancellationToken = default)
        => ImportAsync(projectId, expectedRevision, fileName, token => importer.ImportFileAsync(content, token), cancellationToken);

    public Task<ProjectSnapshotDto> ImportPastedAsync(string projectId, long expectedRevision, ImportPastedText command, CancellationToken cancellationToken = default)
        => ImportAsync(projectId, expectedRevision, command.DisplayName, token =>
        {
            token.ThrowIfCancellationRequested(); return Task.FromResult(importer.ImportPastedText(command.Text));
        }, cancellationToken);

    private Task<ProjectSnapshotDto> ImportAsync(string projectId, long expectedRevision, string displayName, Func<CancellationToken, Task<ImportedText>> read, CancellationToken cancellationToken)
        => projects.WriteAsync(projectId, expectedRevision, async (project, token) =>
        {
            try
            {
                await using var lease = await project.AcquireInputMutationLeaseAsync(token);
                string label = NormalizeLabel(displayName);
                await lease.RefreshAsync(token); CheckAcceptance(project, expectedRevision);
                var imported = await read(token);
                var normalized = frontend.Normalize(imported);
                var pieces = segmenter.Segment(normalized);
                await lease.RefreshAsync(token); CheckAcceptance(project, expectedRevision);
                token.ThrowIfCancellationRequested();
                var state = project.State; var now = DateTimeOffset.UtcNow; string chapterId = Guid.NewGuid().ToString("N");
                var provenance = new TextProvenance(imported.SourceKind, label, imported.SourceEncoding, imported.SourceHash, imported.OriginalByteCount, normalized.FrontendVersion, SemanticSegmenter.Version);
                var chapter = new ChapterRecord(chapterId, 0, label, imported.OriginalText, normalized.Normalized, normalized.Spoken, now, provenance);
                var versions = state.Metadata.Versions with { TextFrontend = normalized.FrontendVersion, Segmenter = SemanticSegmenter.Version };
                var segments = pieces.Select(piece => new SegmentRecord(Guid.NewGuid().ToString("N"), chapterId, piece.Sequence,
                    piece.OriginalText, piece.NormalizedTarget, piece.SpokenTarget, piece.ContentHash, "", null, null, 0, versions,
                    state.Metadata.Parameters, "draft", false, now, now, null, piece.PreviousSentenceContextHash, piece.NextSentenceContextHash, piece.BoundaryAfter)).ToArray();
                await lease.SaveAsync(state with { Chapters = [chapter], Segments = segments, Metadata = state.Metadata with { Versions = versions, UpdatedUtc = now } }, token);
            }
            catch (TextException error) { throw new StudioException(error.Code); }
        }, cancellationToken);

    public Task<ProjectSnapshotDto> UpdateSpokenTextAsync(string projectId, string segmentId, long expectedRevision, UpdateSpokenText command, CancellationToken cancellationToken = default)
        => projects.WriteAsync(projectId, expectedRevision, async (project, token) =>
        {
            try
            {
                await using var lease = await project.AcquireInputMutationLeaseAsync(token);
                await lease.RefreshAsync(token);
                if (project.State.Revision != expectedRevision) throw new ProjectException("project.conflict");
                var state = project.State;
                var segment = state.Segments.FirstOrDefault(x => x.Id == segmentId) ?? throw new StudioException("text.segment.missing");
                var provenance = state.Chapters.FirstOrDefault(x => x.Id == segment.ChapterId)?.Provenance;
                if (provenance == null || segment.BoundaryAfter is not { } boundary ||
                    string.IsNullOrEmpty(segment.Versions.TextFrontend) || string.IsNullOrEmpty(segment.Versions.Segmenter))
                    throw new StudioException("text.reimport_required");
                string spoken = command.SpokenText;
                if (spoken != null && spoken.Length > SemanticSegmenter.MaximumCodeUnits) throw new StudioException("text.too_large");
                // Validate exact Unicode/graphemes, but retain the caller's spoken string, not a normalized replacement.
                frontend.Normalize(importer.ImportPastedText(spoken!));
                string hash = SemanticSegmenter.ComputeContentHashFromContextHashes(segment.NormalizedText, spoken!, segment.PreviousSentenceContextHash,
                    segment.NextSentenceContextHash, segment.Versions.TextFrontend, segment.Versions.Segmenter, boundary);
                var updated = segment with { SpokenText = spoken!, TextHash = hash, UpdatedUtc = DateTimeOffset.UtcNow };
                if (hash != segment.TextHash) updated = updated with { GenerationFingerprint = "", OutputHash = null, OutputRelativePath = null, Status = "draft", DiagnosticCode = null };
                await lease.SaveAsync(state with { Segments = state.Segments.Select(x => x.Id == segmentId ? updated : x).ToArray(), Metadata = state.Metadata with { UpdatedUtc = updated.UpdatedUtc } }, token);
            }
            catch (TextException error) { throw new StudioException(error.Code); }
        }, cancellationToken);

    private static void CheckAcceptance(ProjectDatabase project, long expectedRevision)
    {
        if (project.State.Revision != expectedRevision) throw new ProjectException("project.conflict");
        if (project.State.Chapters.Count != 0 || project.State.Segments.Count != 0) throw new StudioException("text.exists");
    }

    private static string NormalizeLabel(string value)
    {
        if (value == null || value.Any(char.IsControl)) throw new StudioException("text.display_name.invalid");
        string label = value.Replace('\\', '/').Split('/')[^1].Trim();
        if (label.Length is 0 or > 120) throw new StudioException("text.display_name.invalid");
        return label;
    }
}

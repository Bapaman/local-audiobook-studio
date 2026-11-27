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
    Task<ProjectSnapshotDto> ImportBookFileAsync(string projectId, long expectedRevision, string fileName, Stream content, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> ImportPastedAsync(string projectId, long expectedRevision, ImportPastedText command, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> AppendPastedAsync(string projectId, long expectedRevision, AppendPastedText command, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> AppendFileAsync(string projectId, long expectedRevision, string textId, string displayName, Stream content, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> UpdateSpokenTextAsync(string projectId, string segmentId, long expectedRevision, UpdateSpokenText command, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> UpdateAudioReviewAsync(string projectId, string segmentId, long expectedRevision, UpdateAudioReview command,
        CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> UpdateAudioCorrectionAsync(string projectId, string segmentId, long expectedRevision,
        UpdateAudioCorrection command, CancellationToken cancellationToken = default);
}

public sealed class StudioTextService(StudioProjectService projects) : IStudioTextService
{
    private readonly TextImportService importer = new();
    private readonly RussianTextFrontend frontend = new();
    private readonly SemanticSegmenter segmenter = new();

    public Task<ProjectSnapshotDto> AppendPastedAsync(string projectId, long expectedRevision, AppendPastedText command, CancellationToken cancellationToken = default)
        => AppendAsync(projectId, expectedRevision, command.TextId, command.DisplayName, token =>
        {
            token.ThrowIfCancellationRequested(); return Task.FromResult(importer.ImportPastedText(command.Text));
        }, cancellationToken);
    public Task<ProjectSnapshotDto> AppendFileAsync(string projectId, long expectedRevision, string textId, string displayName, Stream content, CancellationToken cancellationToken = default)
        => AppendAsync(projectId, expectedRevision, textId, displayName, token => importer.ImportFileAsync(content, token), cancellationToken);

    private async Task<ProjectSnapshotDto> AppendAsync(string projectId, long expectedRevision, string textId, string displayName,
        Func<CancellationToken, Task<ImportedText>> read, CancellationToken token)
    {
        if (!Guid.TryParseExact(textId,"N",out var parsed) || parsed.ToString("N") != textId) throw new StudioException("text.id.invalid");
        string label = NormalizeLabel(displayName);
        ImportedText? imported = null;
        async Task<ImportedText> Read(CancellationToken ct)
        {
            // A file stream may be consumed only once, including a commit-race replay.
            try { return imported ??= await read(ct); }
            catch (TextException error) { throw new StudioException(error.Code); }
        }
        async Task<ProjectSnapshotDto?> Replay()
        {
            var snapshot = await projects.GetAsync(projectId,token);
            var accepted = snapshot.TextDocuments?.FirstOrDefault(d => d.Id == textId)?.Document;
            if (accepted == null) return null;
            var source = await Read(token);
            if (accepted.DisplayName != label || accepted.SourceKind != source.SourceKind.ToString() ||
                accepted.Encoding != source.SourceEncoding || accepted.Sha256 != source.SourceHash ||
                accepted.ByteCount != source.OriginalByteCount || accepted.OriginalText != source.OriginalText)
                throw new StudioException("text.idempotency.conflict");
            return snapshot;
        }
        if (await Replay() is { } replay) return replay;
        try { return await ImportAsync(projectId,expectedRevision,label,Read,token,textId); }
        catch (StudioException error) when (error.Code is "project.conflict" or "job.active")
        {
            // Another copy may have committed and started generation while we acquired ownership.
            if (await Replay() is { } committed) return committed;
            throw;
        }
    }

    public Task<ProjectSnapshotDto> ImportFileAsync(string projectId, long expectedRevision, string fileName, Stream content, CancellationToken cancellationToken = default)
        => ImportAsync(projectId, expectedRevision, fileName, token => importer.ImportFileAsync(content, token), cancellationToken);
    public Task<ProjectSnapshotDto> ImportBookFileAsync(string projectId, long expectedRevision, string fileName, Stream content, CancellationToken cancellationToken = default)
        => CommitImportAsync(projectId, expectedRevision, fileName, (_, token) => importer.ImportBookFileAsync(content, token), cancellationToken);

    public Task<ProjectSnapshotDto> ImportPastedAsync(string projectId, long expectedRevision, ImportPastedText command, CancellationToken cancellationToken = default)
        => ImportAsync(projectId, expectedRevision, command.DisplayName, token =>
        {
            token.ThrowIfCancellationRequested(); return Task.FromResult(importer.ImportPastedText(command.Text));
        }, cancellationToken);

    private Task<ProjectSnapshotDto> ImportAsync(string projectId, long expectedRevision, string displayName, Func<CancellationToken, Task<ImportedText>> read, CancellationToken cancellationToken, string? appendId = null)
        => CommitImportAsync(projectId, expectedRevision, displayName, async (label, token) => [(label, await read(token))], cancellationToken, appendId);

    private Task<ProjectSnapshotDto> CommitImportAsync(string projectId, long expectedRevision, string displayName,
        Func<string, CancellationToken, Task<IReadOnlyList<(string Title, ImportedText Source)>>> read,
        CancellationToken cancellationToken, string? appendId = null)
        => projects.WriteAsync(projectId, expectedRevision, async (project, token) =>
        {
            try
            {
                await using var lease = await project.AcquireInputMutationLeaseAsync(token);
                string label = NormalizeLabel(displayName);
                await lease.RefreshAsync(token); CheckAcceptance(project, expectedRevision, appendId != null);
                var sources = await read(label, token);
                var state = project.State;
                var now = DateTimeOffset.UtcNow;
                var versions = state.Metadata.Versions with { TextFrontend = RussianTextFrontend.Version, Segmenter = SemanticSegmenter.Version };
                int chapterOrder = appendId == null ? 0 : checked(state.Chapters.Max(c => c.Order) + 1);
                int segmentOrder = appendId == null ? 0 : checked(state.Segments.Max(s => s.Order) + 1);
                var chapters = new List<ChapterRecord>(sources.Count);
                var segments = new List<SegmentRecord>();
                foreach (var (title, imported) in sources)
                {
                    token.ThrowIfCancellationRequested();
                    var normalized = frontend.Normalize(imported);
                    var pieces = segmenter.Segment(normalized);
                    if (sources.Count > 1 && pieces.Count > TextImportService.MaximumProjectSegments - segments.Count)
                        throw new StudioException("text.project_limit");
                    string chapterId = appendId ?? Guid.NewGuid().ToString("N");
                    var provenance = new TextProvenance(imported.SourceKind, title, imported.SourceEncoding, imported.SourceHash,
                        imported.OriginalByteCount, normalized.FrontendVersion, SemanticSegmenter.Version);
                    chapters.Add(new(chapterId, chapterOrder++, title, imported.OriginalText, normalized.Normalized, normalized.Spoken, now, provenance));
                    foreach (var piece in pieces)
                        segments.Add(new(Guid.NewGuid().ToString("N"), chapterId, segmentOrder++, piece.OriginalText, piece.NormalizedTarget,
                            piece.SpokenTarget, piece.ContentHash, "", null, null, 0, versions, state.Metadata.Parameters, "draft", false, now, now,
                            null, piece.PreviousSentenceContextHash, piece.NextSentenceContextHash, piece.BoundaryAfter));
                }
                await lease.RefreshAsync(token);
                CheckAcceptance(project, expectedRevision, appendId != null);
                if (appendId != null) await lease.AppendTextAsync(expectedRevision, chapters.Single(), segments, token);
                else await lease.SaveAsync(state with { Chapters = chapters, Segments = segments, Metadata = state.Metadata with { Versions = versions, UpdatedUtc = now } }, token);
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
                if (hash != segment.TextHash) updated = updated with { GenerationFingerprint = "", OutputHash = null, OutputRelativePath = null,
                    ReviewedArtifactId = null, Status = "draft", DiagnosticCode = null };
                await lease.SaveAsync(state with { Segments = state.Segments.Select(x => x.Id == segmentId ? updated : x).ToArray(), Metadata = state.Metadata with { UpdatedUtc = updated.UpdatedUtc } }, token);
            }
            catch (TextException error) { throw new StudioException(error.Code); }
        }, cancellationToken);

    public Task<ProjectSnapshotDto> UpdateAudioReviewAsync(string projectId, string segmentId, long expectedRevision, UpdateAudioReview command,
        CancellationToken cancellationToken = default)
        => projects.WriteAsync(projectId, expectedRevision, async (project, token) =>
        {
            await using var lease = await project.AcquireInputMutationLeaseAsync(token);
            await lease.SetAudioReviewAsync(expectedRevision, segmentId, command.ArtifactId, command.Reviewed, token);
        }, cancellationToken);

    public Task<ProjectSnapshotDto> UpdateAudioCorrectionAsync(string projectId, string segmentId, long expectedRevision,
        UpdateAudioCorrection command, CancellationToken cancellationToken = default)
        => projects.WriteAsync(projectId, expectedRevision, async (project, token) =>
        {
            if (command.Expected == null) throw new StudioException("request.invalid");
            await using var lease = await project.AcquireInputMutationLeaseAsync(token);
            await lease.SetAudioCorrectionAsync(expectedRevision, segmentId, command.Note, command.Expected, command.RejectCurrent, token);
        }, cancellationToken);

    private static void CheckAcceptance(ProjectDatabase project, long expectedRevision, bool append = false)
    {
        if (project.State.Revision != expectedRevision) throw new ProjectException("project.conflict");
        if (append)
        {
            if (project.State.Chapters.Count == 0 || project.State.Segments.Count == 0 || project.State.Chapters.Any(c => c.Provenance == null))
                throw new StudioException("text.reimport_required");
        }
        else if (project.State.Chapters.Count != 0 || project.State.Segments.Count != 0) throw new StudioException("text.exists");
    }

    private static string NormalizeLabel(string value)
    {
        if (value == null || value.Any(char.IsControl)) throw new StudioException("text.display_name.invalid");
        string label = value.Replace('\\', '/').Split('/')[^1].Trim();
        if (label.Length is 0 or > 120) throw new StudioException("text.display_name.invalid");
        return label;
    }
}

using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Core.Storage;

public sealed partial class ProjectDatabase
{
    // Bounds apply to new appends only; previously accepted larger projects remain readable.
    internal async Task AppendTextUnderLeaseAsync(ProjectArtifactLease lease, long expectedRevision, ChapterRecord chapter,
        IReadOnlyList<SegmentRecord> segments, CancellationToken token)
    {
        lease.ValidateOwner(this);
        await lease.RequireNoActiveJobsAsync(token);
        await lease.RefreshAsync(token);
        var state = State;
        if (state.Revision != expectedRevision) throw new ProjectException("project.conflict");
        if (state.Chapters.Count == 0 || state.Segments.Count == 0 || state.Chapters.Any(c => c.Provenance == null))
            throw new ProjectException("text.reimport_required");
        if (chapter?.Provenance is not { } provenance || provenance.FrontendVersion != state.Metadata.Versions.TextFrontend ||
            provenance.SegmenterVersion != state.Metadata.Versions.Segmenter || segments == null ||
            segments.Any(s => s?.Versions != state.Metadata.Versions))
            throw new ProjectException("text.reimport_required");
        if (state.Chapters.Count >= TextImportService.MaximumProjectTexts || segments.Count > TextImportService.MaximumProjectSegments - state.Segments.Count ||
            state.Chapters.Sum(c => (long)c.OriginalText.Length) + chapter.OriginalText.Length > TextImportService.MaximumPastedCodeUnits)
            throw new ProjectException("text.project_limit");

        var oldIds = state.Segments.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        bool Id(string? id) => Guid.TryParseExact(id,"N",out var value) && value.ToString("N") == id;
        int firstOrder = checked(state.Segments.Max(s => s.Order) + 1);
        if (!Id(chapter.Id) || state.Chapters.Any(c => c.Id == chapter.Id) || chapter.Order != checked(state.Chapters.Max(c => c.Order) + 1) ||
            provenance.DisplayName != chapter.Title || string.IsNullOrWhiteSpace(chapter.OriginalText) ||
            string.IsNullOrWhiteSpace(chapter.NormalizedText) || string.IsNullOrWhiteSpace(chapter.SpokenText) || segments.Count == 0 ||
            segments.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != segments.Count ||
            segments.Where((s,i) => !Id(s.Id) || oldIds.Contains(s.Id) || s.ChapterId != chapter.Id || s.Order != firstOrder + i ||
                s.Status != "draft" || s.OutputHash != null || s.OutputRelativePath != null || s.GenerationFingerprint != "" ||
                s.Locked || s.DiagnosticCode != null || string.IsNullOrWhiteSpace(s.OriginalText) || string.IsNullOrWhiteSpace(s.NormalizedText) ||
                string.IsNullOrWhiteSpace(s.SpokenText) || !IsTextDigest(s.TextHash)).Any())
            throw new ProjectException("text.append.invalid");

        await SaveUnderLeaseAsync(lease, state with { Chapters = [..state.Chapters,chapter], Segments = [..state.Segments,..segments],
            Metadata = state.Metadata with { UpdatedUtc = DateTimeOffset.UtcNow } }, token, allowTextAppend: true);
    }
}

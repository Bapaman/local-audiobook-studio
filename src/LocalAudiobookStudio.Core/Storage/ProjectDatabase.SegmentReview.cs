using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Core.Storage;

public sealed partial class ProjectDatabase
{
    internal Task<ProjectState> SetAudioReviewAsync(ProjectArtifactLease lease, long expectedRevision, string segmentId,
        string artifactId, bool reviewed, CancellationToken token)
        => UpdateSegmentReviewAsync(lease, expectedRevision, segmentId, (segment, current, _) =>
        {
            if (current == null || current != artifactId) throw new ProjectException("project.conflict");
            return segment with { ReviewedArtifactId = reviewed ? current : null, Correction = reviewed ? null : segment.Correction };
        }, token);

    internal Task<ProjectState> SetAudioCorrectionAsync(ProjectArtifactLease lease, long expectedRevision, string segmentId,
        string? note, AudioCorrectionBaseline expected, bool rejectCurrent, CancellationToken token)
        => UpdateSegmentReviewAsync(lease, expectedRevision, segmentId, (segment, current, previous) =>
        {
            var source = current ?? previous;
            var baseline = new AudioCorrectionBaseline(source, segment.Correction?.Note, segment.Correction?.NeedsNewAudio(current) == true);
            if (expected != baseline) throw new ProjectException("project.conflict");
            if (note == null)
            {
                if (rejectCurrent) throw new ProjectException("request.invalid");
                return segment with { Correction = null };
            }
            var trimmed = note.Trim();
            if (note.Length > 240 || trimmed.Length == 0 || note.Any(char.IsControl))
                throw new ProjectException("request.invalid");
            var artifactId = (rejectCurrent ? current : segment.Correction?.ArtifactId ?? source)
                ?? throw new ProjectException("project.conflict");
            return segment with { ReviewedArtifactId = null, Correction = new(artifactId, trimmed) };
        }, token);

    private Task<ProjectState> UpdateSegmentReviewAsync(ProjectArtifactLease lease, long expectedRevision, string segmentId,
        Func<SegmentRecord, string?, string?, SegmentRecord> update, CancellationToken token)
        => JobTransactionAsync(true, async (db, transaction) =>
        {
            lease.ValidateOwner(this);
            var state = await ReadJobProjectAsync(transaction, token);
            if (state.Revision != expectedRevision) throw new ProjectException("project.conflict");
            if (await Jobs.HasActiveJobAsync(db, transaction, token)) throw new ProjectException("job.active");

            var segment = state.Segments.FirstOrDefault(value => value.Id == segmentId)
                ?? throw new ProjectException("text.segment.missing");
            var outputs = await Jobs.ReadOutputArtifactsAsync(db, transaction, state, token);
            var updated = update(segment, outputs.Current.GetValueOrDefault(segmentId),
                outputs.Previous.GetValueOrDefault(segmentId)?.ArtifactId);
            if (updated == segment) return state;
            updated = updated with { UpdatedUtc = DateTimeOffset.UtcNow };
            await UpdateSegmentAsync(transaction, updated, token);
            return await AdvanceProjectAsync(transaction, state with { Segments = ReplaceSegment(state, updated) }, token);
        }, token, committed => State = committed);
}

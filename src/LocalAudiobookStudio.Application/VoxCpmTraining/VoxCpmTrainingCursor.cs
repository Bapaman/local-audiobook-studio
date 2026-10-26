using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Projects;

namespace LocalAudiobookStudio.Application.VoxCpmTraining;

/// <summary>The next retained clip, never its sparse source index. Advance only with accepted weights.</summary>
public sealed record VoxCpmTrainingCursor(long GlobalStep, long CompletedEpochs, int EpochSize, int NextRetainedOrdinal, string NextClipId)
{
    public static VoxCpmTrainingCursor Initial(IReadOnlyList<string> clipIds)
    {
        RequireIds(clipIds); return new(0, 0, clipIds.Count, 0, clipIds[0]);
    }
    internal void Validate(IReadOnlyList<string> clipIds)
    {
        RequireIds(clipIds);
        try
        {
            if (GlobalStep < 0 || CompletedEpochs < 0 || EpochSize != clipIds.Count || NextRetainedOrdinal < 0
                || NextRetainedOrdinal >= EpochSize || NextClipId != clipIds[NextRetainedOrdinal]
                || GlobalStep != checked(CompletedEpochs * EpochSize + NextRetainedOrdinal)) throw Invalid();
        }
        catch (OverflowException) { throw Invalid(); }
    }
    public VoxCpmTrainingCursor Advance(IReadOnlyList<string> clipIds)
    {
        Validate(clipIds);
        try
        {
            int ordinal = NextRetainedOrdinal + 1; long epoch = CompletedEpochs;
            if (ordinal == EpochSize) { ordinal = 0; epoch = checked(epoch + 1); }
            return new(checked(GlobalStep + 1), epoch, EpochSize, ordinal, clipIds[ordinal]);
        }
        catch (OverflowException) { throw Invalid(); }
    }
    private static void RequireIds(IReadOnlyList<string>? ids)
    {
        if (ids == null || ids.Count is < 1 or > 512 || ids.Any(id => !CatalogPaths.IsId(id))
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw Invalid();
    }
    private static StudioException Invalid() => new("voxcpm_training.cursor.invalid");
}

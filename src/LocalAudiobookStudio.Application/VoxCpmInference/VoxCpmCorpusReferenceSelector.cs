using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.Application.VoxCpmInference;

internal static class VoxCpmCorpusReferenceSelector
{
    internal static bool Eligible(ApprovedTtsCorpusClip clip)
        => clip.EndSample - clip.StartSample is >= 80000 and <= 256000;

    internal static ApprovedTtsCorpusClip Select(IReadOnlyList<ApprovedTtsCorpusClip> clips)
        => clips.Where(Eligible).OrderBy(c => Math.Abs(c.EndSample - c.StartSample - 192000))
            .ThenBy(c => c.TrainingOrdinal).ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new StudioException("job.input_changed");

    internal static VoxCpmCorpusReference Capture(ApprovedTtsCorpusClip clip)
        => new(clip.Id, clip.TrainingOrdinal, clip.AudioRelativePath, clip.AudioSha256, clip.EndSample - clip.StartSample);
}

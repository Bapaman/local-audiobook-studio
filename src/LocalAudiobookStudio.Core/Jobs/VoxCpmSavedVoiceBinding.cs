using System.Text.Json.Serialization;

namespace LocalAudiobookStudio.Core.Jobs;

/// <summary>Whole approved corpus clip captured as acoustic inference conditioning.</summary>
public sealed record VoxCpmCorpusReference(string Id, int TrainingOrdinal, string AudioRelativePath,
    string AudioSha256, long SampleCount);

/// <summary>Immutable accepted adapter selection, separate from the RVC workflow.</summary>
public sealed record VoxCpmSavedVoiceBinding(string ModelId, string TrainingId, string ManifestRelativePath,
    string ManifestSha256, string CorpusVersionId, string CorpusManifestSha256, string CorpusBindingSha256,
    string SourceRecordingId, string SourceSha256, string RuntimeIdentitySha256, long GlobalStep,
    long CompletedEpochs, int EpochSize, string InferenceImplementationSha256, string ProfileVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VoxCpmCorpusReference? Reference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VoxCpmGenerationOptions? Options = null);

internal static class VoxCpmSavedVoiceValidation
{
    internal static bool Valid(VoxCpmSavedVoiceBinding b)
    {
        var p = b.ManifestRelativePath?.Split('/');
        return JobValidation.Id(b.ModelId) && JobValidation.Id(b.TrainingId)
            && JobValidation.Id(b.CorpusVersionId) && JobValidation.Id(b.SourceRecordingId)
            && JobValidation.Hash(b.ManifestSha256) && JobValidation.Hash(b.CorpusManifestSha256)
            && JobValidation.Hash(b.CorpusBindingSha256) && JobValidation.Hash(b.SourceSha256)
            && JobValidation.Hash(b.RuntimeIdentitySha256) && JobValidation.Hash(b.InferenceImplementationSha256)
            && p is { Length: 10 } && p[0] == "voxcpm-training" && p[1] == "actions"
            && JobValidation.Id(p[2]) && p[3] == "executions" && JobValidation.Id(p[4])
            && p[5] == "windows" && JobValidation.Id(p[6]) && p[7] == "checkpoints"
            && p[8] == b.ModelId && p[9] == "manifest.json"
            && b.GlobalStep is >= 1 and <= 512 && b.CompletedEpochs is >= 0 and <= 512
            && b.EpochSize is >= 1 and <= 512 && b.GlobalStep / b.EpochSize == b.CompletedEpochs
            && (b.Options == null && (b.ProfileVersion == "voxcpm2-saved-adapter-cpu-v1" && b.Reference == null
                || b.ProfileVersion == "voxcpm2-saved-adapter-reference-cpu-v1" && ReferenceValid(b))
                || b.ProfileVersion == "voxcpm2-saved-adapter-controls-cpu-v1" && b.Options is { } options && options.IsValid()
                    && (options.UseReference ? ReferenceValid(b) && (options.ReferenceClipId == null || options.ReferenceClipId == b.Reference!.Id) : b.Reference == null));
    }

    private static bool ReferenceValid(VoxCpmSavedVoiceBinding b)
        => b.Reference is { } reference && ValidReference(reference) && reference.TrainingOrdinal < b.EpochSize;

    private static bool ValidReference(VoxCpmCorpusReference r)
    {
        var parts = r.AudioRelativePath?.Split('/');
        return JobValidation.Id(r.Id) && JobValidation.Hash(r.AudioSha256)
            && r.TrainingOrdinal is >= 0 and < 512 && r.SampleCount is >= 80000 and <= 256000
            && r.AudioRelativePath is { Length: > 0 and <= 2048 }
            && !r.AudioRelativePath.Contains('\\') && !r.AudioRelativePath.Any(char.IsControl)
            && parts is { Length: >= 5 } && parts[0] == "tts-corpus" && parts[1] == "actions" && JobValidation.Id(parts[2])
            && parts.All(p => !string.IsNullOrWhiteSpace(p) && p is not "." and not "..");
    }
}

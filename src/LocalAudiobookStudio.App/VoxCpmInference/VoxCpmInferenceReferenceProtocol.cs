using System.Buffers.Binary;
using System.Text.Json;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmInference;
using static LocalAudiobookStudio.App.VoxCpmTraining.VoxCpmTrainingJson;

namespace LocalAudiobookStudio.App.VoxCpmInference;

/// <summary>Pins the whole captured approved reference; never chooses a replacement during execution.</summary>
internal static class VoxCpmInferenceReferenceProtocol
{
    internal static async Task<JsonElement?> PinAsync(VoxCpmInferenceExecution e, VoxCpmTrainingFilePins pins, CancellationToken token)
    {
        var voice = e.Voice; var binding = voice.Binding;
        bool controlled = binding.ProfileVersion == VoxCpmInferenceProfile.ControlsId;
        if (controlled)
        {
            Require(binding.Options is { } options && options.IsValid()
                && options.UseReference == (binding.Reference != null)
                && (options.ReferenceClipId == null || options.ReferenceClipId == binding.Reference?.Id));
            if (!binding.Options!.UseReference) { Require(voice.ReferenceClip == null); return null; }
        }
        else Require(binding.Options == null);
        if (binding.ProfileVersion == VoxCpmInferenceProfile.Id)
        {
            Require(binding.Reference == null && voice.ReferenceClip == null); return null;
        }
        Require((controlled || binding.ProfileVersion == VoxCpmInferenceProfile.ReferenceId) && binding.Reference != null && voice.ReferenceClip != null);
        var reference = binding.Reference!; var clip = voice.ReferenceClip!; var corpus = voice.Corpus;
        Require(corpus.Clips.Count(c=>c.Id == reference.Id) == 1
            && corpus.Clips.Single(c=>c.Id == reference.Id) == clip
            && reference.Id == clip.Id && reference.TrainingOrdinal == clip.TrainingOrdinal
            && reference.AudioRelativePath == clip.AudioRelativePath && reference.AudioSha256 == clip.AudioSha256
            && reference.SampleCount == checked(clip.EndSample-clip.StartSample)
            && reference.SampleCount is >= 80000 and <= 256000 && IsHash(reference.AudioSha256));
        long length = checked(44+2*reference.SampleCount);
        var file = await pins.ReadAsync(VoxCpmTrainingFilePins.Resolve(e.ProjectRoot,reference.AudioRelativePath),length,reference.AudioSha256,token);
        Require(file.Length == length); byte[] h = new byte[44]; await file.ReadExactlyAsync(h,token); file.Position = 0;
        Require(h.AsSpan(0,4).SequenceEqual("RIFF"u8) && h.AsSpan(8,8).SequenceEqual("WAVEfmt "u8)
            && BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)) == length-8
            && BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16)) == 16
            && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(20)) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(22)) == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(24)) == 16000 && BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(28)) == 32000
            && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(32)) == 2 && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(34)) == 16
            && h.AsSpan(36,4).SequenceEqual("data"u8) && BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(40)) == 2*reference.SampleCount);
        return JsonSerializer.SerializeToElement(new {
            corpusVersionId = corpus.VersionId, corpusManifestSha256 = corpus.ManifestSha256, corpusBindingSha256 = corpus.BindingSha256,
            clipId = clip.Id, clip.TrainingOrdinal, relativePath = clip.AudioRelativePath, bytes = length,
            sha256 = clip.AudioSha256, sampleRate = 16000, samples = reference.SampleCount },Options);
    }
}

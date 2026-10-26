using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Core.Jobs;

/// <summary>Versioned, fixed-order UTF-8 encoding. JSON numerics are invariant and strings are unambiguous.</summary>
public static class GenerationFingerprint
{
    public static string ForInput(GenerationInput input) => Hash(w =>
    {
        w.WriteStringValue("generation-input-v1"); w.WriteStringValue(input.ProjectId); w.WriteNumberValue(input.ProjectRevision);
        Voice(w, input.Voice); w.WriteStringValue(input.Voice.WavRelativePath); w.WriteStringValue(input.Voice.ImportedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Preset(w, input.Preset);
        foreach (var item in input.Items) Item(w, item);
        SavedVoice(w, input.SavedVoice);
        VoxVoice(w, input.SavedVoxCpmVoice);
    });
    public static string ForItem(GenerationInput input, GenerationItemInput item, GenerationPreset effectivePreset) => Hash(w =>
    { w.WriteStringValue("generation-item-v1"); Voice(w, input.Voice); Preset(w, effectivePreset); Item(w, item); SavedVoice(w, input.SavedVoice); VoxVoice(w, input.SavedVoxCpmVoice); });
    internal static string Request(string projectId, JobKind kind, IReadOnlyList<string> ids, string? savedModelId = null, string? savedVoxCpmModelId = null, VoxCpmGenerationOptions? voxCpmOptions = null) => Hash(w =>
    { w.WriteStringValue("enqueue-v1"); w.WriteStringValue(projectId); w.WriteNumberValue((int)kind); foreach (string id in ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) w.WriteStringValue(id);
        if (savedModelId != null) { w.WriteStartArray(); w.WriteStringValue("saved-voice-v1"); w.WriteStringValue(savedModelId); w.WriteEndArray(); }
        if (savedVoxCpmModelId != null) { w.WriteStartArray(); w.WriteStringValue("voxcpm-saved-voice-v1"); w.WriteStringValue(savedVoxCpmModelId); w.WriteEndArray(); }
        Options(w, voxCpmOptions); });
    internal static string Control(string jobId, JobControlCommand command) => Hash(w =>
    { w.WriteStringValue("control-v1"); w.WriteStringValue(jobId); w.WriteNumberValue((int)command); });
    internal static string Key(string key) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    private static string Hash(Action<Utf8JsonWriter> write)
    {
        using var bytes = new MemoryStream(); using (var w = new Utf8JsonWriter(bytes)) { w.WriteStartArray(); write(w); w.WriteEndArray(); }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
    private static void Voice(Utf8JsonWriter w, VoiceSnapshot voice)
    {
        w.WriteStartArray(); w.WriteStringValue(voice.Id); w.WriteStringValue(voice.Transcript); w.WriteStringValue(voice.WavHash); w.WriteStringValue(voice.TranscriptHash); w.WriteStringValue(voice.IdentityHash); w.WriteBooleanValue(voice.RightsConfirmed);
        if (voice.StartMilliseconds is { } start) w.WriteNumberValue(start); else w.WriteNullValue();
        if (voice.EndMilliseconds is { } end) w.WriteNumberValue(end); else w.WriteNullValue(); w.WriteEndArray();
    }
    private static void Preset(Utf8JsonWriter w, GenerationPreset preset)
    {
        w.WriteStartArray(); w.WriteStringValue(preset.Id); w.WriteStringValue(preset.ProfileId); w.WriteNumberValue(preset.Steps); w.WriteNumberValue(preset.Timesteps); w.WriteNumberValue(preset.Cfg); w.WriteNumberValue(preset.Temperature); w.WriteNumberValue(preset.Seed); w.WriteBooleanValue(preset.AllowCpuFallback); w.WriteEndArray();
    }
    private static void Item(Utf8JsonWriter w, GenerationItemInput item)
    {
        w.WriteStartArray(); w.WriteStringValue(item.SegmentId); w.WriteNumberValue(item.Order); w.WriteStringValue(item.SpokenText); w.WriteStringValue(item.TextHash); w.WriteStringValue(item.PreviousSentenceContextHash); w.WriteStringValue(item.NextSentenceContextHash);
        if (item.BoundaryAfter is { } boundary) w.WriteNumberValue((int)boundary); else w.WriteNullValue();
        VersionPins pins = item.Versions; w.WriteStringValue(pins.App); w.WriteStringValue(pins.Worker); w.WriteStringValue(pins.Model); w.WriteStringValue(pins.Policy); w.WriteStringValue(pins.TextFrontend); w.WriteStringValue(pins.Segmenter); w.WriteEndArray();
    }
    private static void SavedVoice(Utf8JsonWriter w, SavedVoiceBinding? voice)
    {
        // No extra null marker: old durable jobs retain their exact v1 hashes.
        if (voice == null) return;
        w.WriteStartArray(); w.WriteStringValue("saved-voice-v1"); w.WriteStringValue(voice.ModelId);
        w.WriteStringValue(voice.WorkflowRelativePath); w.WriteStringValue(voice.WorkflowSha256);
        w.WriteStringValue(voice.SourceRecordingId); w.WriteStringValue(voice.SourceWavRelativePath); w.WriteStringValue(voice.SourceSha256);
        w.WriteNumberValue(voice.GlobalStep); w.WriteNumberValue(voice.CompletedEpoch); w.WriteNumberValue(voice.EpochSize);
        w.WriteStringValue(voice.StopReason); w.WriteStringValue(voice.ProfileVersion); w.WriteEndArray();
    }
    private static void VoxVoice(Utf8JsonWriter w, VoxCpmSavedVoiceBinding? voice)
    {
        if (voice == null) return; // Old durable hashes contain no additional null marker.
        w.WriteStartArray(); w.WriteStringValue("voxcpm-saved-voice-v1");
        w.WriteStringValue(voice.ModelId); w.WriteStringValue(voice.TrainingId);
        w.WriteStringValue(voice.ManifestRelativePath); w.WriteStringValue(voice.ManifestSha256);
        w.WriteStringValue(voice.CorpusVersionId); w.WriteStringValue(voice.CorpusManifestSha256); w.WriteStringValue(voice.CorpusBindingSha256);
        w.WriteStringValue(voice.SourceRecordingId); w.WriteStringValue(voice.SourceSha256); w.WriteStringValue(voice.RuntimeIdentitySha256);
        w.WriteNumberValue(voice.GlobalStep); w.WriteNumberValue(voice.CompletedEpochs); w.WriteNumberValue(voice.EpochSize);
        w.WriteStringValue(voice.InferenceImplementationSha256); w.WriteStringValue(voice.ProfileVersion);
        if (voice.Reference is { } reference)
        {
            w.WriteStartArray(); w.WriteStringValue("voxcpm-corpus-reference-v1");
            w.WriteStringValue(reference.Id); w.WriteNumberValue(reference.TrainingOrdinal);
            w.WriteStringValue(reference.AudioRelativePath); w.WriteStringValue(reference.AudioSha256);
            w.WriteNumberValue(reference.SampleCount); w.WriteEndArray();
        }
        Options(w, voice.Options);
        w.WriteEndArray();
    }

    private static void Options(Utf8JsonWriter w, VoxCpmGenerationOptions? options)
    {
        if (options == null) return;
        w.WriteStartArray(); w.WriteStringValue("voxcpm-generation-controls-v1"); w.WriteNumberValue(options.Seed);
        w.WriteNumberValue(options.Timesteps); w.WriteNumberValue(options.Cfg); w.WriteNumberValue(options.TempoPercent);
        w.WriteBooleanValue(options.UseReference); w.WriteStringValue(options.ReferenceClipId); w.WriteEndArray();
    }
}

using System.Text;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Core.Jobs;

internal static class JobValidation
{
    internal static bool Terminal(JobState state) => state is JobState.Completed or JobState.Failed or JobState.Canceled;
    internal static bool Id(string? value) => value != null && Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    internal static bool Hash(string? value) => Hex(value, 64);
    private static bool Hex(string? value, int length) => value?.Length == length && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool VoiceHash(string? value) => value?.Length == 64 && value.All(x => x is >= '0' and <= '9' or >= 'A' and <= 'F');
    internal static bool Profile(string? value) => value is "q8-f16-cpu" or "q8-f16-hybrid-16"
        or "voxcpm2-saved-adapter-cpu-v1" or "voxcpm2-saved-adapter-reference-cpu-v1" or "voxcpm2-saved-adapter-controls-cpu-v1";
    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, string code = "project.schema.invalid") { if (!valid) throw new ProjectException(code); }
    internal static void Key(string key) { Require(!string.IsNullOrWhiteSpace(key) && key.Length <= 256 && !key.Any(char.IsControl) && ValidUtf8(key), "job.input.invalid"); }
    internal static void Request(JobKind kind, IReadOnlyList<string> ids)
    { Require(Enum.IsDefined(kind) && ids != null && ids.Count is >= 1 and <= 10000 && ids.All(Id) && (kind != JobKind.Preview || ids.Distinct(StringComparer.Ordinal).Count() == 1), "job.input.invalid"); }
    internal static GenerationInput Freeze(GenerationInput input) => input with { Items = Array.AsReadOnly(input.Items.ToArray()) };
    internal static void Input(GenerationInput input, JobKind kind, ProjectDatabase db, string code = "project.schema.invalid")
    {
        Require(input != null && Id(input.ProjectId) && input.ProjectRevision >= 1 && input.Voice != null && input.Preset != null && input.Items != null, code);
        var voice = input.Voice; var preset = input.Preset;
        Require(Id(voice.Id) && voice.WavRelativePath == $"voice-snapshot/{voice.Id}.wav" && !string.IsNullOrWhiteSpace(voice.Transcript) && ValidUtf8(voice.Transcript) && VoiceHash(voice.WavHash) && VoiceHash(voice.TranscriptHash) && VoiceHash(voice.IdentityHash) && voice.RightsConfirmed && voice.ImportedUtc.Offset == TimeSpan.Zero && voice.StartMilliseconds is >= 0 && voice.EndMilliseconds is { } end && end - voice.StartMilliseconds is >= 10000 and <= 20000, code);
        db.ResolveOwnedPath(voice.WavRelativePath);
        Require(input.SavedVoice == null || input.SavedVoxCpmVoice == null, code);
        if (input.SavedVoxCpmVoice is { } vox)
        {
            Require(VoxCpmSavedVoiceValidation.Valid(vox), code);
            db.ResolveOwnedPath(vox.ManifestRelativePath);
            db.ResolveOwnedPath($"voice-recording/{vox.SourceRecordingId}.wav");
            if (vox.Reference is { } reference) db.ResolveOwnedPath(reference.AudioRelativePath);
        }
        if (input.SavedVoice is { } saved)
        {
            var pieces = saved.WorkflowRelativePath?.Split('/');
            Require(Id(saved.ModelId) && Hash(saved.WorkflowSha256) && Id(saved.SourceRecordingId) && Hash(saved.SourceSha256)
                && saved.SourceWavRelativePath == $"voice-recording/{saved.SourceRecordingId}.wav"
                && pieces is { Length: >= 5 } && pieces[0] == "training" && pieces[1] == "actions" && Id(pieces[2])
                && pieces.All(x => !string.IsNullOrWhiteSpace(x) && x is not "." and not ".." && !x.Contains('\\') && !x.Any(char.IsControl))
                && saved.GlobalStep >= 1 && saved.CompletedEpoch is >= 0 and <= 200 && saved.EpochSize is >= 1 and <= 2048
                && saved.GlobalStep / saved.EpochSize == saved.CompletedEpoch
                && saved.StopReason is "initial_checkpoint" or "initial_update" or "target_epoch" or "user_stop" or "time_budget" or "checkpoint_budget"
                && saved.ProfileVersion == "rvc-saved-voice-cpu-v1", code);
            db.ResolveOwnedPath(saved.WorkflowRelativePath!); db.ResolveOwnedPath(saved.SourceWavRelativePath);
        }
        bool configured = input.SavedVoxCpmVoice?.Options != null;
        bool providerPreset = input.SavedVoxCpmVoice == null
            ? preset.Id == "voxcpm2-local-v1" && preset.ProfileId is "q8-f16-cpu" or "q8-f16-hybrid-16" && preset.Steps == 70
            : preset.Id == (configured ? "voxcpm2-saved-adapter-controls-v1" : input.SavedVoxCpmVoice.Reference == null ? "voxcpm2-saved-adapter-v1" : "voxcpm2-saved-adapter-reference-v1")
                && preset.ProfileId == input.SavedVoxCpmVoice.ProfileVersion && preset.Steps == 256 && !preset.AllowCpuFallback;
        var options = input.SavedVoxCpmVoice?.Options;
        Require(providerPreset && preset.Timesteps == (options?.Timesteps ?? 10) && double.IsFinite(preset.Cfg)
            && preset.Cfg == (options?.Cfg ?? 2) && double.IsFinite(preset.Temperature) && preset.Temperature == 1 && preset.Seed == (options?.Seed ?? 42), code);
        Require(Enum.IsDefined(kind) && input.Items.Count is >= 1 and <= 10000 && (kind != JobKind.Preview || input.Items.Count == 1) && input.Items.All(x => x != null) && input.Items.Select(x => x.SegmentId).Distinct(StringComparer.Ordinal).Count() == input.Items.Count, code);
        int? lastOrder = null;
        foreach (var item in input.Items)
        {
            Require(Id(item.SegmentId) && item.Order >= 0 && (lastOrder == null || item.Order > lastOrder) && !string.IsNullOrWhiteSpace(item.SpokenText) && item.SpokenText.Length <= SemanticSegmenter.MaximumCodeUnits && ValidUtf8(item.SpokenText) && Hash(item.TextHash) && (item.PreviousSentenceContextHash == null || Hash(item.PreviousSentenceContextHash)) && (item.NextSentenceContextHash == null || Hash(item.NextSentenceContextHash)) && item.BoundaryAfter is { } boundary && Enum.IsDefined(boundary) && item.Versions != null, code);
            var pins = item.Versions;
            Require((pins.App == "" || Pin(pins.App)) && (pins.Policy == "" || Pin(pins.Policy)) && Pin(pins.TextFrontend) && Pin(pins.Segmenter) && Hex(pins.Worker, 40) && Hex(pins.Model, 40), code); lastOrder = item.Order;
            if (input.SavedVoxCpmVoice is { } selected)
                Require(pins.Worker == selected.InferenceImplementationSha256[..40] && pins.Model == selected.ManifestSha256[..40], code);
        }
    }
    private static bool Pin(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) && ValidUtf8(value);
    private static bool ValidUtf8(string value) { try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; } catch (EncoderFallbackException) { return false; } }
    internal static void Authoritative(GenerationInput input, ProjectState state)
    {
        Require(input.ProjectId == state.Metadata.Id && input.ProjectRevision == state.Revision && input.Voice == state.Voice && state.VoiceRecording != null, "job.input_changed");
        if (input.SavedVoice is { } saved)
            Require(saved.SourceRecordingId == state.VoiceRecording!.Id && saved.SourceWavRelativePath == state.VoiceRecording.WavRelativePath
                && saved.SourceSha256 == state.VoiceRecording.Sha256.ToLowerInvariant(), "job.input_changed");
        if (input.SavedVoxCpmVoice is { } vox)
            Require(vox.SourceRecordingId == state.VoiceRecording!.Id && vox.SourceSha256 == state.VoiceRecording.Sha256.ToLowerInvariant(), "job.input_changed");
        foreach (var item in input.Items)
        {
            var segment = state.Segments.SingleOrDefault(x => x.Id == item.SegmentId);
            Require(segment != null && state.Chapters.Any(x => x.Id == segment.ChapterId && x.Provenance != null) && item.Order == segment.Order && item.SpokenText == segment.SpokenText && item.TextHash == segment.TextHash && item.PreviousSentenceContextHash == segment.PreviousSentenceContextHash && item.NextSentenceContextHash == segment.NextSentenceContextHash && item.BoundaryAfter == segment.BoundaryAfter, "job.input_changed");
            var current = state.Metadata.Versions; var pins = item.Versions;
            Require(pins.App == current.App && pins.Policy == current.Policy && pins.TextFrontend == current.TextFrontend && pins.Segmenter == current.Segmenter && pins.App == segment.Versions.App && pins.Policy == segment.Versions.Policy && pins.TextFrontend == segment.Versions.TextFrontend && pins.Segmenter == segment.Versions.Segmenter, "job.input_changed");
        }
    }
    internal static void Record(JobRecord job, ProjectDatabase db)
    {
        Require(Id(job.Id) && job.ProjectId == db.State.Metadata.Id && job.ProjectId == job.Input?.ProjectId && Hash(job.InputHash)); Input(job.Input!, job.Kind, db);
        Require(GenerationFingerprint.ForInput(job.Input) == job.InputHash && job.TotalUnits == job.Input.Items.Count && job.Revision >= 1 && job.CreatedUtc.Offset == TimeSpan.Zero && job.UpdatedUtc.Offset == TimeSpan.Zero && job.UpdatedUtc >= job.CreatedUtc && (job.CompletedUtc == null || job.CompletedUtc.Value.Offset == TimeSpan.Zero && job.CompletedUtc >= job.CreatedUtc) && (Terminal(job.State) == job.CompletedUtc.HasValue) && (job.ClaimToken == null || Id(job.ClaimToken)));
        Summary(job.State, job.RequestedState, job.Phase, job.CompletedUnits, job.TotalUnits, job.DiagnosticCode, job.EffectiveProfileId);
        Require((job.State is JobState.Preparing or JobState.Running or JobState.PauseRequested or JobState.CancelRequested) == (job.ClaimToken != null));
    }
    internal static void Summary(JobState state, JobRequestedState requested, string phase, int completed, int total, string? diagnostic, string profile)
    {
        Require(Enum.IsDefined(state) && Enum.IsDefined(requested) && completed >= 0 && total is >= 1 and <= 10000 && completed <= total && Profile(profile) && Phase(phase) && Code(diagnostic));
        Require(state switch { JobState.Queued or JobState.Preparing or JobState.Running => requested == JobRequestedState.Run, JobState.Paused or JobState.PauseRequested => requested == JobRequestedState.Pause, JobState.Canceled or JobState.CancelRequested => requested == JobRequestedState.Cancel, _ => true });
    }
    private static bool Phase(string? phase) => phase is "queued" or "preparing" or "validating_inputs" or "generating" or "validating_output" or "publishing" or "retrying_cpu" or "paused" or "interrupted" or "canceled" or "failed" or "completed";
    private static bool Code(string? code) => code is null or "worker.canceled" or "worker.timeout" or "worker.crash" or "worker.cuda.oom" or "worker.output.invalid" or "worker.output.missing" or "worker.output.exists" or "worker.arguments" or "worker.path" or "worker.artifacts" or "worker.developer.required" or "worker.profile" or "worker.storage" or "worker.cleanup" or "worker.launch" or "worker.unsupported.platform" or "worker.configuration" or "worker.internal" or "worker.authentication" or "worker.busy" or "protocol.command" or "protocol.payload" or "protocol.frame" or "protocol.version" or "worker.runtime.not_ready" or "worker.interrupted" or "job.interrupted" or "project.interrupted" or "job.input_changed" or "job.input.invalid";
}

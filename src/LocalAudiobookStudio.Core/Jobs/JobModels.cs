using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Text;

namespace LocalAudiobookStudio.Core.Jobs;

public enum JobKind { Preview, Generation }
public enum JobState { Queued, Preparing, Running, PauseRequested, Paused, CancelRequested, Canceled, Interrupted, Failed, Completed }
public enum JobRequestedState { Run, Pause, Cancel }
public enum JobControlCommand { Pause, Resume, Cancel }
public enum GenerationFailureDisposition { Terminal, RetryCpu }
public sealed record GenerationPreset(string Id, string ProfileId, int Steps, int Timesteps, double Cfg, double Temperature, long Seed, bool AllowCpuFallback)
{
    public const string VoxCpmLocalId = "voxcpm2-local-v2";
    public const int VoxCpmLocalDecodeSteps = 140;
}
public sealed record GenerationItemInput(string SegmentId, int Order, string SpokenText, string TextHash, string? PreviousSentenceContextHash, string? NextSentenceContextHash, SegmentBoundary? BoundaryAfter, VersionPins Versions);
public sealed record SavedVoiceBinding(string ModelId, string WorkflowRelativePath, string WorkflowSha256,
    string SourceRecordingId, string SourceWavRelativePath, string SourceSha256, long GlobalStep,
    long CompletedEpoch, int EpochSize, string StopReason, string ProfileVersion);
public sealed record GenerationInput(string ProjectId, long ProjectRevision, VoiceSnapshot Voice, GenerationPreset Preset, IReadOnlyList<GenerationItemInput> Items, SavedVoiceBinding? SavedVoice = null, VoxCpmSavedVoiceBinding? SavedVoxCpmVoice = null);
public sealed record JobRecord(string Id, string ProjectId, JobKind Kind, JobState State, JobRequestedState RequestedState, string Phase, int CompletedUnits, int TotalUnits, long Revision, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, DateTimeOffset? CompletedUtc, string? DiagnosticCode, string InputHash, GenerationInput Input, string EffectiveProfileId, string? ClaimToken);
public sealed record AcceptedJobArtifact(string Id, string RelativePath, string Sha256, long Length, long SampleCount, string ProfileId, string WorkerVersion, string ModelVersion);
public sealed record JobItemRecord(string JobId, string SegmentId, int Order, JobState State, string EffectiveFingerprint, bool CpuFallbackUsed, string? AttemptId, AcceptedJobArtifact? Artifact);
public sealed record GenerationUnitClaim(string JobId, string SegmentId, string AttemptId, string Token, string FinalRelativePath, GenerationItemInput Input, GenerationPreset EffectivePreset);
public sealed record JobEventRecord(long Sequence, string JobId, long JobRevision, JobState State, JobRequestedState RequestedState, string Phase, int CompletedUnits, int TotalUnits, string? DiagnosticCode, string EffectiveProfileId);
public sealed record JobEventPage(long HeadSequence, long FirstRetainedSequence, IReadOnlyList<JobRecord> Jobs, IReadOnlyList<JobEventRecord> Events, bool Reset);

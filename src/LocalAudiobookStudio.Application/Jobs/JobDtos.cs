using LocalAudiobookStudio.Core.Jobs;
using System.Text.Json.Serialization;

namespace LocalAudiobookStudio.Application.Jobs;

public sealed record GenerationRequest(JobKind Kind, IReadOnlyList<string> SegmentIds, string? SavedModelId = null, bool ExperimentalConfirmed = false, string? SavedVoxCpmModelId = null, VoxCpmGenerationOptions? VoxCpmOptions = null);
public sealed record VoxCpmGenerationInfoDto(string ModelId, long GlobalStep, VoxCpmGenerationOptions Options,
    string? ReferenceClipId, int? ReferenceTrainingOrdinal, double? ReferenceSeconds);
public sealed record JobAudioResultDto(string SegmentId, int Order, string ArtifactId, double Seconds, string SpokenText);
public sealed record JobSnapshotDto(string Id, string ProjectId, JobKind Kind, JobState State, JobRequestedState RequestedState,
    string Phase, int CompletedUnits, int TotalUnits, long Revision, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc, string? DiagnosticCode, string InputHash, string EffectiveProfileId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VoxCpmGenerationInfoDto? VoxCpmGeneration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<JobAudioResultDto>? AudioResults = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TextTitle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TextCount = null);
public sealed record JobEventDto(long Sequence, string JobId, long JobRevision, JobState State, JobRequestedState RequestedState,
    string Phase, int CompletedUnits, int TotalUnits, string? DiagnosticCode, string EffectiveProfileId);
public sealed record JobEventPageDto(long HeadSequence, long FirstRetainedSequence, IReadOnlyList<JobSnapshotDto> Jobs, IReadOnlyList<JobEventDto> Events, bool Reset);

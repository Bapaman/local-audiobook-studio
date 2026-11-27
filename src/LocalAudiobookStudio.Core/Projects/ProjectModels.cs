using LocalAudiobookStudio.Core.Text;
using System.Text.Json.Serialization;

namespace LocalAudiobookStudio.Core.Projects;

public sealed class ProjectException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record VersionPins(string App, string Worker, string Model, string Policy, string TextFrontend, string Segmenter);
public sealed record GenerationParameters(string Style, double Speed, double Expressiveness, int PauseMilliseconds, string AdditionalJson);
public sealed record ProjectMetadata(string Id, string Title, string Author, VersionPins Versions, GenerationParameters Parameters, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
public sealed record VoiceSnapshot(string Id, string WavRelativePath, string Transcript, string WavHash, string TranscriptHash, string IdentityHash, bool RightsConfirmed, DateTimeOffset ImportedUtc,
    long? StartMilliseconds = null, long? EndMilliseconds = null);
public sealed record VoiceRecordingSnapshot(string Id, string WavRelativePath, string Sha256, long ByteLength, long SampleCount, bool HasClippingWarning, bool RightsConfirmed, DateTimeOffset ImportedUtc);
public sealed record TextProvenance(TextSourceKind SourceKind, string DisplayName, string SourceEncoding, string SourceHash, long OriginalByteCount, string FrontendVersion, string SegmenterVersion);
public sealed record ChapterRecord(string Id, int Order, string Title, string OriginalText, string NormalizedText, string SpokenText, DateTimeOffset CreatedUtc, TextProvenance? Provenance = null);
public sealed record AudioCorrection(string ArtifactId, string Note)
{
    public bool NeedsNewAudio(string? currentArtifactId) => currentArtifactId == null || currentArtifactId == ArtifactId;
}
public sealed record AudioCorrectionBaseline([property: JsonRequired] string? ArtifactId,
    [property: JsonRequired] string? Note, [property: JsonRequired] bool NeedsNewAudio);
public sealed record SegmentRecord(string Id, string ChapterId, int Order, string OriginalText, string NormalizedText, string SpokenText,
    string TextHash, string GenerationFingerprint, string? OutputHash, string? OutputRelativePath, long Seed, VersionPins Versions,
    GenerationParameters Parameters, string Status, bool Locked, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string? DiagnosticCode,
    string? PreviousSentenceContextHash = null, string? NextSentenceContextHash = null, SegmentBoundary? BoundaryAfter = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewedArtifactId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AudioCorrection? Correction { get; init; }
}
public sealed record AttemptRecord(string Id, string SegmentId, int Order, long Seed, VersionPins Versions, GenerationParameters Parameters,
    string GenerationFingerprint, string Status, string? StagingRelativePath, string? OutputHash, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc, string? DiagnosticCode);
public sealed record ProjectState(ProjectMetadata Metadata, VoiceSnapshot? Voice, IReadOnlyList<ChapterRecord> Chapters, IReadOnlyList<SegmentRecord> Segments, IReadOnlyList<AttemptRecord> Attempts, long Revision = 0,
    VoiceRecordingSnapshot? VoiceRecording = null);
public sealed record PreviousSegmentAudio(string ArtifactId, string SpokenText);
public sealed record ProjectReadSnapshot(ProjectState State, IReadOnlyDictionary<string, string> OutputArtifactIds, bool HasActiveJob = false,
    IReadOnlyDictionary<string, PreviousSegmentAudio>? PreviousAudio = null);

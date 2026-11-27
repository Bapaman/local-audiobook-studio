using System.Text.Json.Serialization;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Application.Models;

public sealed record ProjectListItem(string Id, string DisplayName, DateTimeOffset CreatedUtc, DateTimeOffset LastOpenedUtc, long Revision, bool HasRecording, bool HasAnchor, bool HasText);
public sealed record ProjectSnapshotDto(string Id, string DisplayName, long Revision, VoiceRecordingDto? VoiceRecording, VoiceAnchorDto? VoiceAnchor, TextDocumentDto? TextDocument, IReadOnlyList<SegmentDto> Segments, bool HasAcceptedText = false, bool HasAcceptedVoice = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ProjectTextDocumentDto>? TextDocuments = null);
public sealed record VoiceRecordingDto(string ArtifactId, string Sha256, long ByteLength, long DurationMilliseconds, bool HasClippingWarning);
public sealed record VoiceAnchorDto(string ArtifactId, long StartMilliseconds, long EndMilliseconds, string Transcript);
public sealed record TextDocumentDto(string SourceKind, string DisplayName, string Encoding, string Sha256, long ByteCount, string OriginalText, string NormalizedText);
public sealed record ProjectTextDocumentDto(string Id, int Order, TextDocumentDto Document, IReadOnlyList<string> SegmentIds);
public sealed record PreviousSegmentAudioDto(string ArtifactId, string SpokenText);
public sealed record AudioCorrectionDto(string Note, bool NeedsNewAudio);
public sealed record SegmentDto(string Id, int Order, string OriginalText, string NormalizedText, string SpokenText, string ContentHash, string Status, string? OutputArtifactId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreviousSegmentAudioDto? PreviousAudio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AudioReviewed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AudioCorrectionDto? Correction = null);
public sealed record ImportPastedText(string DisplayName, string Text);
public sealed record AppendPastedText(string DisplayName, string Text, string TextId);
public sealed record UpdateSpokenText(string SpokenText);
public sealed record UpdateAudioReview([property: JsonRequired] string ArtifactId, [property: JsonRequired] bool Reviewed);
public sealed record UpdateAudioCorrection([property: JsonRequired] string? Note,
    [property: JsonRequired] AudioCorrectionBaseline Expected, bool RejectCurrent = false);

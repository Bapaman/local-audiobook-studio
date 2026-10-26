namespace LocalAudiobookStudio.Application.Models;

public sealed record ProjectListItem(string Id, string DisplayName, DateTimeOffset CreatedUtc, DateTimeOffset LastOpenedUtc, long Revision, bool HasRecording, bool HasAnchor, bool HasText);
public sealed record ProjectSnapshotDto(string Id, string DisplayName, long Revision, VoiceRecordingDto? VoiceRecording, VoiceAnchorDto? VoiceAnchor, TextDocumentDto? TextDocument, IReadOnlyList<SegmentDto> Segments, bool HasAcceptedText = false, bool HasAcceptedVoice = false);
public sealed record VoiceRecordingDto(string ArtifactId, string Sha256, long ByteLength, long DurationMilliseconds, bool HasClippingWarning);
public sealed record VoiceAnchorDto(string ArtifactId, long StartMilliseconds, long EndMilliseconds, string Transcript);
public sealed record TextDocumentDto(string SourceKind, string DisplayName, string Encoding, string Sha256, long ByteCount, string OriginalText, string NormalizedText);
public sealed record SegmentDto(string Id, int Order, string OriginalText, string NormalizedText, string SpokenText, string ContentHash, string Status, string? OutputArtifactId = null);
public sealed record ImportPastedText(string DisplayName, string Text);
public sealed record UpdateSpokenText(string SpokenText);

using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.TtsCorpus;

public interface IApprovedTtsCorpusResolver
{
    /// <summary>Caller owns this project's input/artifact lease. Does not acquire it again.</summary>
    Task<ApprovedTtsCorpusLease> ResolveAsync(ProjectDatabase project, string versionId,
        string? expectedManifestSha256, CancellationToken token);
}

/// <summary>Immutable approved text/audio identities; all sample offsets are mono 16 kHz.</summary>
public sealed record ApprovedTtsCorpusClip(int TrainingOrdinal, int SourceIndex, string Id,
    long StartSample, long EndSample, string CorrectedText, string TextSha256,
    string AudioRelativePath, string AudioSha256, string OriginalTextPayloadSha256);

public sealed record ApprovedTtsCorpusBinding(string ProjectId, string CorpusId, string VersionId, string ActionId,
    string ManifestRelativePath, string ManifestSha256, string BindingSha256, VoiceRecordingSnapshot Source,
    long SourceProjectRevision, string CorpusRuntimeFingerprint, string PlanSha256,
    double RetainedDurationSeconds, IReadOnlyList<ApprovedTtsCorpusClip> Clips);

/// <summary>Keep alive through native work and candidate acceptance, alongside caller project ownership.</summary>
public sealed class ApprovedTtsCorpusLease : IDisposable
{
    private IDisposable? ownership;
    internal ApprovedTtsCorpusLease(ApprovedTtsCorpusBinding binding, IDisposable ownership)
    { Binding = binding; this.ownership = ownership; }
    public ApprovedTtsCorpusBinding Binding { get; }
    public void Dispose() => Interlocked.Exchange(ref ownership, null)?.Dispose();
}

using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LocalAudiobookStudio.Application.Tests")]

namespace LocalAudiobookStudio.Application.TtsCorpus;

public sealed record TtsCorpusReadinessDto(bool Ready, string? DiagnosticCode, string Provider = "whisper-large-v3-turbo", int CpuThreads = 2);
/// <summary>Approximate ASR times in seconds relative to the clip; an unknown final end remains null.</summary>
public sealed record TtsCorpusWordDto(string Text, double Start, double? End);
/// <summary>StartSample/EndSample are half-open offsets in the clean mono 16 kHz source.</summary>
public sealed record TtsCorpusClipDto(string Id, int Index, long StartSample, long EndSample, string Text, bool Excluded, bool Reviewed,
    IReadOnlyList<string> WarningCodes, IReadOnlyList<TtsCorpusWordDto> Words);
public sealed record TtsCorpusVersionDto(string Id, string Sha256, int ClipCount, double RetainedDurationSeconds, DateTimeOffset CreatedUtc);
public sealed record TtsCorpusSnapshotDto(string Id, string ProjectId, long Revision, string State, string Phase, string SourceRecordingId,
    double SourceDurationSeconds, int? TotalClipCount, int CompletedClipCount, IReadOnlyList<TtsCorpusClipDto> Clips,
    IReadOnlyList<TtsCorpusVersionDto> Versions, bool CanResume, string? DiagnosticCode, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc,
    double RetainedDurationSeconds = 0, string? ExecutionId = null);
public sealed record TtsCorpusUpdateClipRequest(string Text, bool Excluded, bool Reviewed);
public interface IStudioTtsCorpusService
{
    Task<TtsCorpusReadinessDto> GetReadinessAsync(CancellationToken token = default);
    Task<TtsCorpusSnapshotDto?> GetAsync(string projectId, CancellationToken token = default);
    Task<TtsCorpusSnapshotDto> StartAsync(string projectId, long expectedProjectRevision, string key, bool experimentalConfirmed, CancellationToken token = default);
    Task<TtsCorpusSnapshotDto> ControlAsync(string projectId, string corpusId, long expectedCorpusRevision, string key, string command, CancellationToken token = default);
    Task<TtsCorpusSnapshotDto> UpdateClipAsync(string projectId, string corpusId, string clipId, long expectedCorpusRevision, string key, TtsCorpusUpdateClipRequest request, CancellationToken token = default);
    Task<TtsCorpusSnapshotDto> ApproveAsync(string projectId, string corpusId, long expectedCorpusRevision, string key, bool transcriptAndBoundariesConfirmed, CancellationToken token = default);
    Task<TtsCorpusAudioHandle> OpenAudioAsync(string projectId, string corpusId, string clipId, CancellationToken token = default);
}
public interface ITtsCorpusRuntime
{
    Task<TtsCorpusReadinessDto> GetReadinessAsync(CancellationToken token = default);
    /// <summary>Validates the runtime files before returning the current fingerprint.</summary>
    Task<string> GetFingerprintAsync(CancellationToken token = default);
}
public interface ITtsCorpusExecutor
{
    Task<TtsCorpusWindowResult> RunAsync(TtsCorpusExecution execution, Func<TtsCorpusProgress, Task> progress, CancellationToken token);
}
/// <summary>Trusted server execution only. Paths never cross the browser API. SourceSampleCount is at 48 kHz;
/// all planned clip offsets and TotalSamples are at 16 kHz. PreparedPlan is immutable and hash-bound.
/// One call prepares a plan or returns the next accepted prefix of at most eight clips.</summary>
public sealed record TtsCorpusExecution(string ProjectId, string CorpusId, string ActionId, string ExecutionId, string ProjectRoot, string OutputRoot,
    string SourcePath, string SourceRecordingId, string SourceSha256, long SourceSampleCount, string RuntimeFingerprint,
    TtsCorpusPreparedPlan? PreparedPlan, int NextClipIndex);
public sealed record TtsCorpusProgress(string Phase);
/// <summary>Relative paths are internal project-relative paths under this corpus action; sample offsets are mono16k.</summary>
public sealed record TtsCorpusPlannedClip(string Id, int Index, long StartSample, long EndSample, string AudioRelativePath, string AudioSha256);
public sealed record TtsCorpusPreparedPlan(string PlanRelativePath, string Sha256, string SourceSha256, string RuntimeFingerprint,
    long TotalSamples, IReadOnlyList<TtsCorpusPlannedClip> Clips);
public sealed record TtsCorpusAcceptedClip(TtsCorpusPlannedClip Plan, string TextRelativePath, string TextSha256, string DraftText,
    IReadOnlyList<string> WarningCodes, IReadOnlyList<TtsCorpusWordDto> Words);
/// <summary>Successful owned/validated window only. CleanupConfirmed=false means the runner must retain ownership.
/// Stopped can accompany an empty prefix; failed executions throw and their partial outputs must not be accepted.</summary>
public sealed record TtsCorpusWindowResult(TtsCorpusPreparedPlan? PreparedPlan, IReadOnlyList<TtsCorpusAcceptedClip> Clips,
    bool Stopped = false, bool CleanupConfirmed = true);
public sealed class TtsCorpusExecutionException(string code, bool cleanupConfirmed = true) : Exception(code)
{
    public string Code { get; } = code;
    public bool CleanupConfirmed { get; } = cleanupConfirmed;
}
public sealed class UnavailableTtsCorpusRuntime : ITtsCorpusRuntime
{
    public Task<TtsCorpusReadinessDto> GetReadinessAsync(CancellationToken token = default)
        => Task.FromResult(new TtsCorpusReadinessDto(false, "tts_corpus.runtime.not_ready"));
    public Task<string> GetFingerprintAsync(CancellationToken token = default)
        => Task.FromException<string>(new TtsCorpusExecutionException("tts_corpus.runtime.not_ready"));
}
public sealed class UnavailableTtsCorpusExecutor : ITtsCorpusExecutor
{
    public Task<TtsCorpusWindowResult> RunAsync(TtsCorpusExecution execution, Func<TtsCorpusProgress, Task> progress, CancellationToken token)
        => Task.FromException<TtsCorpusWindowResult>(new TtsCorpusExecutionException("tts_corpus.runtime.not_ready"));
}
public sealed class TtsCorpusAudioHandle : IAsyncDisposable
{
    private readonly Stream content;
    private IDisposable? ownership;
    internal TtsCorpusAudioHandle(Stream content, IDisposable ownership) { this.content = content; this.ownership = ownership; }
    public Stream Content => content;
    public long Length => content.Length;
    public string ContentType => "audio/wav";
    public async ValueTask DisposeAsync()
    {
        var owned = Interlocked.Exchange(ref ownership, null); if (owned == null) return;
        try { await content.DisposeAsync(); } finally { owned.Dispose(); }
    }
}
internal sealed record TtsCorpusReceipt(string KeyHash, string RequestHash, TtsCorpusSnapshotDto Snapshot);
internal sealed record TtsCorpusVersionReference(string Id, string ActionId, string ManifestRelativePath, string Sha256);
internal sealed record TtsCorpusRecord(TtsCorpusSnapshotDto Snapshot, VoiceRecordingSnapshot Recording, long SourceProjectRevision,
    string ActionId, string OutputRelativeRoot, string RuntimeFingerprint, TtsCorpusPreparedPlan? Plan,
    IReadOnlyList<TtsCorpusAcceptedClip> Accepted, IReadOnlyList<TtsCorpusVersionReference> VersionReferences, bool StopRequested = false);
internal sealed record TtsCorpusEnvelope(int Format, string ProjectId, TtsCorpusRecord? Current, IReadOnlyList<TtsCorpusReceipt> Receipts);

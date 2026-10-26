using LocalAudiobookStudio.Application.TtsCorpus;

namespace LocalAudiobookStudio.Application.VoxCpmTraining;

public sealed record VoxCpmTrainingReadinessDto(bool Ready, string? DiagnosticCode,
    string Provider = "voxcpm2-lora-cpu-v1", int CpuThreads = 2);
public sealed record VoxCpmTrainingIdentity(string Fingerprint, string SourceCommit, string BaseRevision,
    string SourceLockSha256, string ModelLockSha256, string CacheSha256, string CacheLayoutSha256,
    string LoaderSha256, string RecipeSha256, string AdapterLayoutSha256);
public sealed record VoxCpmTrainingModelDto(string Id, string TrainingId, string CorpusVersionId,
    string ManifestSha256, long GlobalStep, long CompletedEpochs, DateTimeOffset CreatedUtc);
public sealed record VoxCpmTrainingSnapshotDto(string Id, string ProjectId, long Revision, string State, string Phase,
    string CorpusVersionId, string CorpusManifestSha256, int CorpusClipCount, double RetainedDurationSeconds,
    long GlobalStep, long CompletedEpochs, int NextRetainedOrdinal, string NextClipId, long TargetCompletedEpochs,
    string ExecutionId, IReadOnlyList<VoxCpmTrainingModelDto> Models, bool CanResume, bool QualityEstablished,
    string? DiagnosticCode, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

public interface IStudioVoxCpmTrainingService
{
    Task<VoxCpmTrainingReadinessDto> GetReadinessAsync(CancellationToken token = default);
    Task<VoxCpmTrainingSnapshotDto?> GetAsync(string projectId, CancellationToken token = default);
    Task<VoxCpmTrainingSnapshotDto> StartAsync(string projectId, long expectedProjectRevision, string key,
        string corpusVersionId, bool experimentalConfirmed, CancellationToken token = default);
    Task<VoxCpmTrainingSnapshotDto> ControlAsync(string projectId, string trainingId, long expectedTrainingRevision,
        string key, string command, CancellationToken token = default);
}
public interface IVoxCpmTrainingRuntime
{
    Task<VoxCpmTrainingReadinessDto> GetReadinessAsync(CancellationToken token = default);
    Task<VoxCpmTrainingIdentity> GetIdentityAsync(CancellationToken token);
}
public interface IVoxCpmTrainingCheckpointValidator
{
    Task VerifyAsync(string projectRoot, VoxCpmCheckpointReference checkpoint, ApprovedTtsCorpusBinding corpus,
        VoxCpmTrainingIdentity identity, CancellationToken token);
}
public interface IVoxCpmTrainingExecutor
{
    Task<VoxCpmTrainingWindowResult> RunAsync(VoxCpmTrainingExecution execution,
        Func<VoxCpmTrainingProgress, Task> progress, CancellationToken token);
}

/// <summary>A parent-validated immutable bundle, never discovered by scanning output directories.</summary>
public sealed record VoxCpmCheckpointReference(string Id, string TrainingId, string ActionId, string ExecutionId,
    string WindowId, string ManifestRelativePath, string ManifestSha256, VoxCpmTrainingCursor Cursor, DateTimeOffset CreatedUtc);
/// <summary>Server-only paths. Caller retains project/corpus ownership through acceptance.</summary>
public sealed record VoxCpmTrainingExecution(string ProjectId, string TrainingId, string ActionId, string ExecutionId,
    string WindowId, string CheckpointId, string ProjectRoot, string OutputRoot, ApprovedTtsCorpusBinding Corpus,
    VoxCpmTrainingIdentity Identity, VoxCpmTrainingCursor Cursor, VoxCpmCheckpointReference? ParentCheckpoint, long TargetCompletedEpochs);
public sealed record VoxCpmTrainingProgress(string Phase);
public sealed record VoxCpmTrainingWindowResult(VoxCpmCheckpointReference? Checkpoint, bool Stopped = false, bool CleanupConfirmed = true);
public sealed class VoxCpmTrainingExecutionException(string code, bool cleanupConfirmed = true) : Exception(code)
{
    public string Code { get; } = code;
    public bool CleanupConfirmed { get; } = cleanupConfirmed;
}
public sealed class UnavailableVoxCpmTrainingRuntime : IVoxCpmTrainingRuntime
{
    public Task<VoxCpmTrainingReadinessDto> GetReadinessAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(new VoxCpmTrainingReadinessDto(false, "voxcpm_training.runtime.not_ready")); }
    public Task<VoxCpmTrainingIdentity> GetIdentityAsync(CancellationToken token)
        => Task.FromException<VoxCpmTrainingIdentity>(new VoxCpmTrainingExecutionException("voxcpm_training.runtime.not_ready"));
}
public sealed class UnavailableVoxCpmTrainingExecutor : IVoxCpmTrainingExecutor
{
    public Task<VoxCpmTrainingWindowResult> RunAsync(VoxCpmTrainingExecution execution, Func<VoxCpmTrainingProgress, Task> progress, CancellationToken token)
        => Task.FromException<VoxCpmTrainingWindowResult>(new VoxCpmTrainingExecutionException("voxcpm_training.runtime.not_ready"));
}
public sealed class UnavailableVoxCpmTrainingCheckpointValidator : IVoxCpmTrainingCheckpointValidator
{
    public Task VerifyAsync(string projectRoot, VoxCpmCheckpointReference checkpoint, ApprovedTtsCorpusBinding corpus, VoxCpmTrainingIdentity identity, CancellationToken token)
        => Task.FromException(new VoxCpmTrainingExecutionException("voxcpm_training.runtime.not_ready"));
}

internal sealed record VoxCpmTrainingReceipt(string KeyHash, string RequestHash, VoxCpmTrainingSnapshotDto Snapshot);
internal sealed record VoxCpmTrainingLineage(string TrainingId, string ActionId, ApprovedTtsCorpusBinding Corpus, VoxCpmTrainingIdentity Identity);
internal sealed record VoxCpmTrainingRecord(VoxCpmTrainingSnapshotDto Snapshot, string ActionId, string OutputRelativeRoot,
    ApprovedTtsCorpusBinding Corpus, VoxCpmTrainingIdentity Identity, VoxCpmTrainingCursor Cursor,
    long TargetCompletedEpochs, VoxCpmCheckpointReference? Head, bool StopRequested = false);
internal sealed record VoxCpmTrainingEnvelope(int Format, string ProjectId, VoxCpmTrainingRecord? Current,
    IReadOnlyList<VoxCpmTrainingLineage> Lineages, IReadOnlyList<VoxCpmCheckpointReference> ModelReferences,
    IReadOnlyList<VoxCpmTrainingReceipt> Receipts);

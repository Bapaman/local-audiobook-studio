using LocalAudiobookStudio.Core.Voices;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.Application.Training;

public sealed record RvcTrainingOptions(string PythonPath, string PowerShellPath, string ScriptsRoot,
    string DecoderRoot, string SourceRoot, string BaseAssetRoot, string FeatureAssetRoot);
public sealed record TrainingReadinessDto(bool Ready, string? DiagnosticCode, bool Experimental = true, int CpuThreads = 2);
public sealed record TrainingModelDto(string Id, long GlobalStep, long CompletedEpoch, DateTimeOffset CreatedUtc, bool QualityEstablished = false, bool Selectable = false);
public sealed record TrainingSnapshotDto(string Id, string ProjectId, long Revision, string State, string Phase,
    string SourceRecordingId, long GlobalStep, long CompletedEpoch, int EpochSize, DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc, string? DiagnosticCode, bool CanResume, IReadOnlyList<TrainingModelDto> Models, string? ExecutionId = null);
public interface IStudioTrainingService
{
    Task<TrainingReadinessDto> GetReadinessAsync(CancellationToken token = default);
    Task<TrainingSnapshotDto?> GetAsync(string projectId, CancellationToken token = default);
    Task<TrainingSnapshotDto> StartAsync(string projectId, long expectedProjectRevision, string key, bool experimentalConfirmed, CancellationToken token = default);
    Task<TrainingSnapshotDto> ControlAsync(string projectId, string trainingId, long expectedTrainingRevision, string key, string command, CancellationToken token = default);
}
public interface ITrainingWorkflowExecutor
{
    Task<WorkflowReference?> RunAsync(TrainingExecution execution, Func<TrainingProgress, Task> progress,
        Func<WorkflowReference, Task> checkpoint, CancellationToken token);
}
public interface ITrainingSavedVoiceResolver
{
    Task<SavedVoiceBinding> ResolveAsync(ProjectDatabase project, string modelId, CancellationToken token = default);
}
public sealed record WorkflowReference(string Path, string Sha256, long GlobalStep, long CompletedEpoch, int EpochSize, string StopReason);
public sealed record TrainingProgress(string Phase, long GlobalStep, long CompletedEpoch, int EpochSize);
public sealed record TrainingExecution(string ProjectRoot, string OutputRoot, string SourcePath, string SourceSha256, WorkflowReference? Previous);
public sealed class TrainingExecutionException(string code, bool cleanupConfirmed = true) : Exception(code)
{
    public string Code { get; } = code;
    public bool CleanupConfirmed { get; } = cleanupConfirmed;
}
internal sealed record TrainingReceipt(string KeyHash, string RequestHash, TrainingSnapshotDto Snapshot);
internal sealed record TrainingRecord(TrainingSnapshotDto Snapshot, VoiceRecordingSnapshot Recording, long SourceProjectRevision,
    string ActionId, string OutputRelativeRoot, WorkflowReference? Accepted, bool StopRequested = false,
    IReadOnlyDictionary<string, WorkflowReference>? ModelReferences = null);
internal sealed record TrainingEnvelope(int Format, string ProjectId, TrainingRecord? Current, IReadOnlyList<TrainingReceipt> Receipts);

public static class RvcTrainingRegistration
{
    public static IServiceCollection AddRvcTraining(this IServiceCollection services, RvcTrainingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<ITrainingWorkflowExecutor, RvcWorkflowExecutor>();
        return services;
    }
}

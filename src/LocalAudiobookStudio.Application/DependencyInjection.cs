using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Voices;
using LocalAudiobookStudio.Application.Voices;
using LocalAudiobookStudio.Application.Text;
using LocalAudiobookStudio.Application.Artifacts;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmInference;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LocalAudiobookStudio.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddStudioProjects(this IServiceCollection services, ProjectCatalogOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton<ProjectService>();
        services.TryAddSingleton<IProjectCatalog, SqliteProjectCatalog>();
        services.TryAddSingleton<StudioProjectService>();
        services.TryAddSingleton<IStudioProjectService>(provider => provider.GetRequiredService<StudioProjectService>());
        services.TryAddSingleton<VoiceRecordingService>();
        services.TryAddSingleton<IStudioVoiceService, StudioVoiceService>();
        services.TryAddSingleton<IStudioTextService, StudioTextService>();
        services.TryAddSingleton<IProjectArtifactReader, ProjectArtifactReader>();
        services.TryAddSingleton<BookDownloadService>();
        services.TryAddSingleton<ChapterArchiveDownloadService>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<UnavailableRuntime>(_ => new());
        services.TryAddSingleton<IRuntimeReadiness>(provider => provider.GetRequiredService<UnavailableRuntime>());
        services.TryAddSingleton<IRuntimeDescriptor>(provider => provider.GetRequiredService<UnavailableRuntime>());
        services.TryAddSingleton<IWorkerClientFactory>(provider => provider.GetRequiredService<UnavailableRuntime>());
        services.TryAddSingleton<ProjectJobAccess>();
        services.TryAddSingleton<ITtsCorpusRuntime, UnavailableTtsCorpusRuntime>();
        services.TryAddSingleton<ITtsCorpusExecutor, UnavailableTtsCorpusExecutor>();
        services.TryAddSingleton<StudioTtsCorpusService>();
        services.TryAddSingleton<TtsCorpusRunner>();
        services.TryAddSingleton<IStudioTtsCorpusService>(provider => provider.GetRequiredService<StudioTtsCorpusService>());
        services.TryAddSingleton<IApprovedTtsCorpusResolver, ApprovedTtsCorpusResolver>();
        services.TryAddSingleton<IVoxCpmTrainingRuntime, UnavailableVoxCpmTrainingRuntime>();
        services.TryAddSingleton<IVoxCpmTrainingExecutor, UnavailableVoxCpmTrainingExecutor>();
        services.TryAddSingleton<IVoxCpmTrainingCheckpointValidator, UnavailableVoxCpmTrainingCheckpointValidator>();
        services.TryAddSingleton<StudioVoxCpmTrainingService>();
        services.TryAddSingleton<IStudioVoxCpmTrainingService>(provider => provider.GetRequiredService<StudioVoxCpmTrainingService>());
        services.TryAddSingleton<VoxCpmTrainingRunner>();
        services.TryAddSingleton<IVoxCpmInferenceRuntime, UnavailableVoxCpmInferenceRuntime>();
        services.TryAddSingleton<IVoxCpmInferenceExecutor, UnavailableVoxCpmInferenceExecutor>();
        services.TryAddSingleton<IVoxCpmSavedVoiceResolver, VoxCpmSavedVoiceResolver>();
        services.TryAddSingleton<IStudioVoxCpmGenerationService, StudioVoxCpmGenerationService>();
        services.TryAddSingleton<ComputeGate>();
        services.TryAddSingleton<TrainingRuntime>(provider => new(provider.GetService<RvcTrainingOptions>()));
        services.TryAddSingleton<ITrainingWorkflowExecutor, UnavailableTrainingExecutor>();
        services.TryAddSingleton<StudioTrainingService>();
        services.TryAddSingleton<IStudioTrainingService>(provider => provider.GetRequiredService<StudioTrainingService>());
        services.TryAddSingleton<TrainingRunner>();
        services.TryAddSingleton<ITrainingSavedVoiceResolver, SavedVoiceResolver>();
        services.TryAddSingleton<ISavedVoiceConverter, RvcSavedVoiceConverter>();
        services.TryAddSingleton<StudioJobService>();
        services.TryAddSingleton<IStudioJobService>(provider => provider.GetRequiredService<StudioJobService>());
        services.TryAddSingleton<GenerationRunner>();
        return services;
    }

    private sealed class UnavailableTrainingExecutor : ITrainingWorkflowExecutor
    {
        public Task<WorkflowReference?> RunAsync(TrainingExecution execution, Func<TrainingProgress, Task> progress,
            Func<WorkflowReference, Task> checkpoint, CancellationToken token)
            => Task.FromException<WorkflowReference?>(new TrainingExecutionException("training.runtime.not_ready"));
    }

    // Generic localhost hosts have no desktop runtime provider. Registration is
    // not readiness and never discovers artifacts or starts background inference.
    private sealed class UnavailableRuntime : IRuntimeReadiness, IRuntimeDescriptor, IWorkerClientFactory
    {
        public Task<RuntimeReadiness> GetAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeReadiness(false, false, "worker.runtime.not_ready", "q8-f16-cpu"));
        }
        Task<RuntimeDescriptor> IRuntimeDescriptor.GetAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            throw new WorkerClientException("worker.runtime.not_ready", true);
        }
        public Task<IWorkerClient> OpenAsync(string projectRoot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            throw new WorkerClientException("worker.runtime.not_ready", true);
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Training;

/// <summary>Resolves a known immutable model version, never discovers or adopts checkpoint files.</summary>
public sealed class SavedVoiceResolver : ITrainingSavedVoiceResolver
{
    public async Task<SavedVoiceBinding> ResolveAsync(ProjectDatabase project, string modelId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        token.ThrowIfCancellationRequested();
        try
        {
            if (!TrainingRecordStore.Id(modelId)) throw Unavailable();
            var record = await StudioTrainingService.Store(project).TransactionAsync(e => ((TrainingEnvelope?)null, e.Current), token);
            var point = record == null ? null : TrainingRecordStore.ModelReference(record, modelId);
            if (record == null || point == null) throw Unavailable();
            using var sourcePin = await StudioTrainingService.VerifySourceAsync(project, record.Recording, token);

            // The existing acceptance validator needs the request identity from
            // this exact SHA-bound state. Read bounded known JSON, not a path
            // discovered from directories or a caller-supplied workflow file.
            if (!TrainingRecordStore.WithinTraining(project.Root, point.Path) || !TrainingRecordStore.HashValid(point.Sha256)) throw Unavailable();
            await using var statePin = new FileStream(point.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (statePin.Length is <= 0 or > 1_048_576) throw Unavailable();
            byte[] bytes = new byte[checked((int)statePin.Length)];
            await statePin.ReadExactlyAsync(bytes, token);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != point.Sha256) throw Unavailable();
            using var state = JsonDocument.Parse(bytes);
            var request = state.RootElement.GetProperty("request");
            var execution = new TrainingExecution(project.Root, project.ResolveOwnedPath(record.OutputRelativeRoot),
                project.ResolveOwnedPath(record.Recording.WavRelativePath), record.Recording.Sha256.ToLowerInvariant(), null);
            await RvcWorkflowExecutor.ValidateReferenceAsync(execution, point, request.GetProperty("path").GetString()!, request.GetProperty("sha256").GetString()!, token);
            token.ThrowIfCancellationRequested();
            return new(modelId, Path.GetRelativePath(project.Root, point.Path).Replace('\\', '/'), point.Sha256,
                record.Recording.Id, record.Recording.WavRelativePath, record.Recording.Sha256.ToLowerInvariant(),
                point.GlobalStep, point.CompletedEpoch, point.EpochSize, point.StopReason, "rvc-saved-voice-cpu-v1");
        }
        catch (Exception error) when (error is StudioException or TrainingExecutionException or ProjectException or WavInspectionException
            or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException
            or ArgumentException or OverflowException or NotSupportedException)
        {
            throw Unavailable();
        }
    }
    private static StudioException Unavailable() => new("training.model.unavailable");
}

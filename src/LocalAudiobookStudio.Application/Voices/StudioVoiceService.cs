using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Application.Voices;

public interface IStudioVoiceService
{
    Task<ProjectSnapshotDto> UploadRecordingAsync(string projectId, long expectedRevision, UploadVoiceRecording command, Stream content, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> ConfirmAnchorAsync(string projectId, long expectedRevision, ConfirmVoiceAnchor command, CancellationToken cancellationToken = default);
}

public enum VoiceMessageFormat { Ogg, M4a, Mp3 }

public interface IVoiceRecordingDecoder
{
    Task<Stream> DecodeAsync(Stream content, VoiceMessageFormat format, CancellationToken cancellationToken);
}

public sealed class StudioVoiceService(StudioProjectService projects, VoiceRecordingService recordings, IVoiceRecordingDecoder? decoder = null) : IStudioVoiceService
{
    public async Task<ProjectSnapshotDto> UploadRecordingAsync(string projectId, long expectedRevision, UploadVoiceRecording command, Stream content, CancellationToken cancellationToken = default)
    {
        VoiceMessageFormat? format = Path.GetExtension(command.FileName)?.ToLowerInvariant() switch
        {
            ".ogg" or ".oga" or ".opus" => VoiceMessageFormat.Ogg,
            ".m4a" => VoiceMessageFormat.M4a,
            ".mp3" => VoiceMessageFormat.Mp3,
            _ => null,
        };
        if (format is null) return await SaveAsync(content);
        if (!command.RightsConfirmed) throw new StudioException("voice.rights");
        await projects.CheckVoiceUploadAsync(projectId, expectedRevision, cancellationToken);
        if (decoder is null) throw new StudioException("voice.decoder.unavailable");
        await using var decoded = await decoder.DecodeAsync(content, format.Value, cancellationToken);
        return await SaveAsync(decoded);

        Task<ProjectSnapshotDto> SaveAsync(Stream wav) => projects.WriteAsync(projectId, expectedRevision, async (project, token) =>
        {
            await recordings.ImportAsync(project, command, wav, expectedRevision, token, requireNoActiveJob: true);
        }, cancellationToken);
    }
    public Task<ProjectSnapshotDto> ConfirmAnchorAsync(string projectId, long expectedRevision, ConfirmVoiceAnchor command, CancellationToken cancellationToken = default)
        => projects.WriteAsync(projectId, expectedRevision, async (project, token) => { await recordings.ConfirmAnchorAsync(project, command, expectedRevision, token, requireNoActiveJob: true); }, cancellationToken);
}

using LocalAudiobookStudio.Application.Models;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Core.Voices;

namespace LocalAudiobookStudio.Application.Voices;

public interface IStudioVoiceService
{
    Task<ProjectSnapshotDto> UploadRecordingAsync(string projectId, long expectedRevision, UploadVoiceRecording command, Stream content, CancellationToken cancellationToken = default);
    Task<ProjectSnapshotDto> ConfirmAnchorAsync(string projectId, long expectedRevision, ConfirmVoiceAnchor command, CancellationToken cancellationToken = default);
}

public sealed class StudioVoiceService(StudioProjectService projects, VoiceRecordingService recordings) : IStudioVoiceService
{
    public Task<ProjectSnapshotDto> UploadRecordingAsync(string projectId, long expectedRevision, UploadVoiceRecording command, Stream content, CancellationToken cancellationToken = default)
        => projects.WriteAsync(projectId, expectedRevision, async (project, token) => { await recordings.ImportAsync(project, command, content, expectedRevision, token, requireNoActiveJob: true); }, cancellationToken);
    public Task<ProjectSnapshotDto> ConfirmAnchorAsync(string projectId, long expectedRevision, ConfirmVoiceAnchor command, CancellationToken cancellationToken = default)
        => projects.WriteAsync(projectId, expectedRevision, async (project, token) => { await recordings.ConfirmAnchorAsync(project, command, expectedRevision, token, requireNoActiveJob: true); }, cancellationToken);
}

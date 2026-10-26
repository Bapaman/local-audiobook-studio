using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.Application.Jobs;

public interface IStudioJobService
{
    Task<JobSnapshotDto> EnqueueAsync(string projectId, long expectedProjectRevision, string key, GenerationRequest request, CancellationToken token);
    Task<IReadOnlyList<JobSnapshotDto>> ListAsync(string projectId, CancellationToken token);
    Task<JobSnapshotDto> GetAsync(string projectId, string jobId, CancellationToken token);
    Task<JobSnapshotDto> RequestStateAsync(string projectId, string jobId, long expectedJobRevision, string key, JobControlCommand command, CancellationToken token);
    Task<IReadOnlyList<JobEventDto>> ReadEventsAsync(string projectId, long cursor, CancellationToken token);
    Task<long> GetEventSequenceAsync(string projectId, CancellationToken token);
    Task<JobEventPageDto> ReadEventPageAsync(string projectId, long? cursor, CancellationToken token);
}

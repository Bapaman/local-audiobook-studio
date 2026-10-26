using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Application.VoxCpmInference;

namespace LocalAudiobookStudio.Application.Jobs;

/// <summary>Durable commands return after commit; wakeups/progress are not their authority.</summary>
public sealed class StudioJobService(ProjectJobAccess access, IRuntimeDescriptor runtime, ITrainingSavedVoiceResolver? savedVoices = null, TrainingRuntime? trainingRuntime = null, IVoxCpmSavedVoiceResolver? voxSavedVoices = null) : IStudioJobService
{
    public Task<JobSnapshotDto> EnqueueAsync(string projectId, long expectedProjectRevision, string key, GenerationRequest request, CancellationToken token)
    {
        if (request == null || request.SegmentIds == null || request.VoxCpmOptions != null &&
            (request.SavedVoxCpmModelId == null || !request.VoxCpmOptions.IsValid())) return Task.FromException<JobSnapshotDto>(new StudioException("job.input.invalid"));
        request = request with { SegmentIds = Array.AsReadOnly(request.SegmentIds.ToArray()) };
        return WithProject(projectId, async (db, ct) =>
        {
            if (request == null || request.SegmentIds == null) throw new StudioException("job.input.invalid");
            var replay = await db.Jobs.TryReplayEnqueueAsync(key, request.Kind, request.SegmentIds, ct, request.SavedModelId, request.SavedVoxCpmModelId, request.VoxCpmOptions);
            if (replay != null) return await MapAsync(db, replay, ct);
            if ((request.SavedModelId != null || request.SavedVoxCpmModelId != null) && !request.ExperimentalConfirmed) throw new StudioException("training.model.confirmation_required");
            if (request.SavedVoxCpmModelId != null && voxSavedVoices == null) throw new WorkerClientException("worker.runtime.not_ready", true);
            if (request.SavedModelId != null && (savedVoices == null || trainingRuntime == null || !(await trainingRuntime.GetAsync(ct)).Ready))
                throw new StudioException("training.runtime.not_ready");
            var descriptor = request.SavedVoxCpmModelId == null ? await runtime.GetAsync(ct) : null;
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: ct);
            await lease.RefreshAsync(ct);
            var state = db.State;
            if (state.Voice == null || state.VoiceRecording == null) throw new StudioException("job.input.invalid");
            using var vox = request.SavedVoxCpmModelId == null ? null : request.VoxCpmOptions is { } options
                ? await voxSavedVoices!.ResolveConfiguredAsync(db, request.SavedVoxCpmModelId, options, ct)
                : await voxSavedVoices!.ResolveAsync(db, request.SavedVoxCpmModelId, ct);
            descriptor ??= VoxCpmInferenceProfile.Descriptor(vox!.Binding, state.Metadata.Versions);
            var selectedIds = request.SegmentIds.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var selected = state.Segments.Where(x => selectedIds.Contains(x.Id)).OrderBy(x => x.Order).ToArray();
            if (selected.Length != selectedIds.Count) throw new StudioException("job.input.invalid");
            var pins = state.Metadata.Versions with { Worker = descriptor.Versions.Worker, Model = descriptor.Versions.Model };
            var items = selected.Select(x => new GenerationItemInput(x.Id, x.Order, x.SpokenText, x.TextHash,
                x.PreviousSentenceContextHash, x.NextSentenceContextHash, x.BoundaryAfter, pins)).ToArray();
            var saved = request.SavedModelId == null ? null : await savedVoices!.ResolveAsync(db, request.SavedModelId, ct);
            if (saved != null && (saved.SourceRecordingId != state.VoiceRecording.Id || saved.SourceSha256 != state.VoiceRecording.Sha256.ToLowerInvariant()))
                throw new StudioException("training.model.source_changed");
            var input = new GenerationInput(projectId, state.Revision, state.Voice, descriptor.Preset, items, saved, vox?.Binding);
            return await MapAsync(db, await db.Jobs.EnqueueAsync(lease, expectedProjectRevision, key, request.Kind, input, ct), ct);
        }, token);
    }

    public Task<IReadOnlyList<JobSnapshotDto>> ListAsync(string projectId, CancellationToken token)
        => WithProject<IReadOnlyList<JobSnapshotDto>>(projectId, async (db, ct) => await MapAllAsync(db, await db.Jobs.ListAsync(ct), ct), token);
    public Task<JobSnapshotDto> GetAsync(string projectId, string jobId, CancellationToken token)
        => WithProject(projectId, async (db, ct) => await MapAsync(db, await db.Jobs.GetAsync(jobId, ct), ct), token);
    public Task<JobSnapshotDto> RequestStateAsync(string projectId, string jobId, long expectedJobRevision, string key, JobControlCommand command, CancellationToken token)
        => WithProject(projectId, async (db, ct) => await MapAsync(db, await db.Jobs.RequestStateAsync(jobId, expectedJobRevision, key, command, ct), ct), token);
    public Task<IReadOnlyList<JobEventDto>> ReadEventsAsync(string projectId, long cursor, CancellationToken token)
        => WithProject<IReadOnlyList<JobEventDto>>(projectId, async (db, ct) => (await db.Jobs.ReadEventsAsync(cursor, ct)).Select(Map).ToArray(), token);
    public Task<long> GetEventSequenceAsync(string projectId, CancellationToken token)
        => WithProject(projectId, (db, ct) => db.Jobs.GetEventSequenceAsync(ct), token);
    public Task<JobEventPageDto> ReadEventPageAsync(string projectId, long? cursor, CancellationToken token)
        => WithProject(projectId, async (db, ct) =>
        {
            var page = await db.Jobs.ReadEventPageAsync(cursor, ct);
            return new JobEventPageDto(page.HeadSequence, page.FirstRetainedSequence, await MapAllAsync(db, page.Jobs, ct), page.Events.Select(Map).ToArray(), page.Reset);
        }, token);

    private async Task<T> WithProject<T>(string projectId, Func<ProjectDatabase, CancellationToken, Task<T>> action, CancellationToken token)
    {
        try { await using var session = await access.OpenCurrentAsync(projectId, token); return await action(session.Project, token); }
        catch (ProjectException e) { throw new StudioException(e.Code); }
        catch (WorkerClientException e) { throw new StudioException(e.Code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new StudioException("project.unavailable"); }
    }
    private static async Task<IReadOnlyList<JobSnapshotDto>> MapAllAsync(ProjectDatabase db, IReadOnlyList<JobRecord> jobs, CancellationToken token)
    {
        var result = new List<JobSnapshotDto>(jobs.Count);
        foreach (var job in jobs) result.Add(await MapAsync(db, job, token));
        return result;
    }
    private static async Task<JobSnapshotDto> MapAsync(ProjectDatabase db, JobRecord job, CancellationToken token)
    {
        var snapshot = Map(job);
        if (job.CompletedUnits == 0) return snapshot;
        var inputs = job.Input.Items.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);
        var results = (await db.Jobs.ItemsAsync(job.Id, token)).Where(x => x.Artifact != null).OrderBy(x => x.Order).Take(Math.Min(32, job.CompletedUnits))
            .Select(x => new JobAudioResultDto(x.SegmentId, x.Order, x.Artifact!.Id,
                x.Artifact.SampleCount / 48000.0, inputs[x.SegmentId].SpokenText)).ToArray();
        return snapshot with { AudioResults = results.Length == 0 ? null : new AudioResultList(results) };
    }
    internal static JobSnapshotDto Map(JobRecord job)
    {
        var binding = job.Input.SavedVoxCpmVoice; var reference = binding?.Reference;
        var info = binding == null ? null : new VoxCpmGenerationInfoDto(binding.ModelId, binding.GlobalStep,
            binding.Options ?? new(job.Input.Preset.Seed, job.Input.Preset.Timesteps, job.Input.Preset.Cfg, 100, reference != null),
            reference?.Id, reference?.TrainingOrdinal, reference?.SampleCount / 16000.0);
        return new(job.Id, job.ProjectId, job.Kind, job.State, job.RequestedState, job.Phase,
            job.CompletedUnits, job.TotalUnits, job.Revision, job.CreatedUtc, job.UpdatedUtc, job.CompletedUtc,
            job.DiagnosticCode, job.InputHash, job.EffectiveProfileId, info);
    }
    private sealed class AudioResultList(JobAudioResultDto[] values) : System.Collections.ObjectModel.ReadOnlyCollection<JobAudioResultDto>(values)
    {
        public override bool Equals(object? other) => other is IReadOnlyList<JobAudioResultDto> list && this.SequenceEqual(list);
        public override int GetHashCode() { var hash = new HashCode(); foreach (var value in this) hash.Add(value); return hash.ToHashCode(); }
    }
    internal static JobEventDto Map(JobEventRecord item) => new(item.Sequence, item.JobId, item.JobRevision, item.State, item.RequestedState,
        item.Phase, item.CompletedUnits, item.TotalUnits, item.DiagnosticCode, item.EffectiveProfileId);
}

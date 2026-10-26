using System.Text.Json;
using Microsoft.Data.Sqlite;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Core.Jobs;

/// <summary>Project-owned durable job commands. Artifact ownership is required only when capturing new input.</summary>
public sealed class ProjectJobStore
{
    private readonly ProjectDatabase project;
    private static readonly JsonSerializerOptions StoredJson = new() { RespectRequiredConstructorParameters = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    internal ProjectJobStore(ProjectDatabase project) => this.project = project;

    public Task<JobRecord> EnqueueAsync(ProjectArtifactLease lease, long expectedProjectRevision, string key, JobKind kind, GenerationInput input, CancellationToken token)
    {
        lease.ValidateOwner(project); JobValidation.Key(key);
        JobValidation.Require(input?.Items != null, "job.input.invalid");
        // Copy before any await; the caller's collection can never change the captured execution input.
        input = JobValidation.Freeze(input!);
        JobValidation.Require(input.Items.All(x => x != null), "job.input.invalid");
        var ids = input.Items.Select(x => x.SegmentId).ToArray(); JobValidation.Request(kind, ids);
        string keyHash = GenerationFingerprint.Key(key), requestHash = GenerationFingerprint.Request(project.State.Metadata.Id, kind, ids, input.SavedVoice?.ModelId, input.SavedVoxCpmVoice?.ModelId, input.SavedVoxCpmVoice?.Options);
        return project.JobTransactionAsync(true, async (connection, transaction) =>
        {
            await RequireSchemaAsync(connection, transaction, token);
            var receipt = await ReceiptAsync(connection, transaction, keyHash, token);
            if (receipt != null) return await ReplayEnqueueAsync(connection, transaction, receipt, requestHash, token);
            input = input with { Items = Array.AsReadOnly(input.Items.OrderBy(x => x.Order).ToArray()) };
            var state = await project.ReadJobProjectAsync(transaction, token);
            if (state.Revision != expectedProjectRevision) throw new ProjectException("project.conflict");
            JobValidation.Input(input, kind, project, "job.input.invalid"); JobValidation.Authoritative(input, state);
            if ((await JobsAsync(connection, transaction, false, token)).Any(x => !JobValidation.Terminal(x.State))) throw new ProjectException("job.active");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var job = new JobRecord(Guid.NewGuid().ToString("N"), state.Metadata.Id, kind, JobState.Queued, JobRequestedState.Run, "queued", 0, input.Items.Count, 1, now, now, null, null, GenerationFingerprint.ForInput(input), input, input.Preset.ProfileId, null);
            await ExecuteAsync(connection, transaction, "INSERT INTO jobs(id,input_hash,active,payload) VALUES($id,$hash,1,$payload)", token, ("$id", job.Id), ("$hash", job.InputHash), ("$payload", JsonSerializer.Serialize(job)));
            foreach (var item in input.Items)
            {
                var stored = new JobItemRecord(job.Id, item.SegmentId, item.Order, JobState.Queued, GenerationFingerprint.ForItem(input, item, input.Preset), false, null, null);
                await ExecuteAsync(connection, transaction, "INSERT INTO job_items(job_id,segment_id,ordinal,payload) VALUES($job,$segment,$order,$payload)", token, ("$job", job.Id), ("$segment", item.SegmentId), ("$order", item.Order), ("$payload", JsonSerializer.Serialize(stored)));
            }
            await AddReceiptAsync(connection, transaction, keyHash, requestHash, "enqueue", job, token);
            await AddEventAsync(connection, transaction, job, token); return job;
        }, token);
    }
    public Task<JobRecord?> TryReplayEnqueueAsync(string key, JobKind kind, IReadOnlyList<string> segmentIds, CancellationToken token, string? savedModelId = null, string? savedVoxCpmModelId = null, VoxCpmGenerationOptions? voxCpmOptions = null)
    {
        JobValidation.Key(key); JobValidation.Request(kind, segmentIds);
        JobValidation.Require(savedModelId == null || JobValidation.Id(savedModelId), "job.input.invalid");
        JobValidation.Require((savedVoxCpmModelId == null || JobValidation.Id(savedVoxCpmModelId)) && (savedModelId == null || savedVoxCpmModelId == null), "job.input.invalid");
        JobValidation.Require(voxCpmOptions == null || savedVoxCpmModelId != null && voxCpmOptions.IsValid(), "job.input.invalid");
        string requestHash = GenerationFingerprint.Request(project.State.Metadata.Id, kind, segmentIds, savedModelId, savedVoxCpmModelId, voxCpmOptions);
        return project.JobTransactionAsync<JobRecord?>(false, async (connection, transaction) =>
        {
            if (!await HasSchemaAsync(connection, transaction, token)) return null;
            var receipt = await ReceiptAsync(connection, transaction, GenerationFingerprint.Key(key), token);
            return receipt == null ? null : await ReplayEnqueueAsync(connection, transaction, receipt, requestHash, token);
        }, token);
    }
    public Task<JobRecord> GetAsync(string jobId, CancellationToken token) => project.JobTransactionAsync(false, async (connection, transaction) =>
    { if (!await HasSchemaAsync(connection, transaction, token)) throw new ProjectException("job.missing"); return await GetAsync(connection, transaction, jobId, token); }, token);
    public Task<IReadOnlyList<JobRecord>> ListAsync(CancellationToken token) => project.JobTransactionAsync<IReadOnlyList<JobRecord>>(false, async (connection, transaction) =>
    { return await HasSchemaAsync(connection, transaction, token) ? await JobsAsync(connection, transaction, true, token) : []; }, token);
    internal async Task<bool> HasActiveJobAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
        => await HasSchemaAsync(connection, transaction, token) && (await JobsAsync(connection, transaction, false, token)).Any(x => !JobValidation.Terminal(x.State));

    internal Task RequireNoActiveJobsAsync(ProjectArtifactLease lease, CancellationToken token)
    {
        lease.ValidateOwner(project);
        return project.JobTransactionAsync(false, async (connection, transaction) =>
        {
            lease.ValidateOwner(project);
            if (await HasActiveJobAsync(connection, transaction, token)) throw new ProjectException("job.active");
            return true;
        }, token);
    }
    public Task<IReadOnlyList<JobItemRecord>> ItemsAsync(string jobId, CancellationToken token) => project.JobTransactionAsync<IReadOnlyList<JobItemRecord>>(false, async (connection, transaction) =>
    { if (!await HasSchemaAsync(connection, transaction, token)) throw new ProjectException("job.missing"); var job = await GetAsync(connection, transaction, jobId, token); return await ItemsAsync(connection, transaction, job, token); }, token);
    /// <summary>Committed acceptance only, including retained outputs older than the summary window.</summary>
    public Task<AcceptedJobArtifact?> FindAcceptedArtifactAsync(string artifactId, CancellationToken token) => project.JobTransactionAsync<AcceptedJobArtifact?>(false, async (connection, transaction) =>
    {
        if (!JobValidation.Id(artifactId) || !await HasSchemaAsync(connection, transaction, token)) return null;
        return (await AcceptedItemsAsync(connection, transaction, token)).SingleOrDefault(x => x.Artifact!.Id == artifactId)?.Artifact;
    }, token);

    /// <summary>Opaque IDs for current committed segment outputs, never a guessed path or an unaccepted attempt.</summary>
    public Task<IReadOnlyDictionary<string, string>> ReadCurrentOutputArtifactIdsAsync(CancellationToken token) => project.JobTransactionAsync<IReadOnlyDictionary<string, string>>(false, async (connection, transaction) =>
    {
        var state = await project.ReadJobProjectAsync(transaction, token);
        return await ReadCurrentOutputArtifactIdsAsync(connection, transaction, state, token);
    }, token);

    internal async Task<IReadOnlyDictionary<string, string>> ReadCurrentOutputArtifactIdsAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectState state, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!await HasSchemaAsync(connection, transaction, token)) return result;
        var accepted = await AcceptedItemsAsync(connection, transaction, token);
        foreach (var segment in state.Segments)
        {
            var item = accepted.SingleOrDefault(x => x.SegmentId == segment.Id && x.Artifact!.RelativePath == segment.OutputRelativePath && x.Artifact.Sha256 == segment.OutputHash);
            if (item != null) result.Add(segment.Id, item.Artifact!.Id);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(result);
    }

    private async Task<IReadOnlyList<JobItemRecord>> AcceptedItemsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var result = new List<JobItemRecord>(); var ids = new HashSet<string>(StringComparer.Ordinal); var paths = new HashSet<string>(StringComparer.Ordinal);
        var state = await project.ReadJobProjectAsync(transaction, token);
        if (state.Voice is { } voice) JobValidation.Require(ids.Add(voice.Id));
        if (state.VoiceRecording is { } recording) JobValidation.Require(ids.Add(recording.Id));
        foreach (var job in await JobsAsync(connection, transaction, false, token))
        foreach (var item in await ItemsAsync(connection, transaction, job, token))
        {
            if (item.Artifact is not { } artifact) continue;
            JobValidation.Require(ids.Add(artifact.Id) && paths.Add(artifact.RelativePath) && artifact.RelativePath == $"segments/{job.Id}/{item.AttemptId}.wav");
            result.Add(item);
        }
        return result.AsReadOnly();
    }
    public Task<JobRecord> RequestStateAsync(string jobId, long expectedJobRevision, string key, JobControlCommand command, CancellationToken token)
    {
        JobValidation.Key(key); JobValidation.Require(JobValidation.Id(jobId) && Enum.IsDefined(command), "job.input.invalid");
        string keyHash = GenerationFingerprint.Key(key), requestHash = GenerationFingerprint.Control(jobId, command);
        return project.JobTransactionAsync(true, async (connection, transaction) =>
        {
            await RequireSchemaAsync(connection, transaction, token);
            var receipt = await ReceiptAsync(connection, transaction, keyHash, token);
            if (receipt != null)
            {
                if (receipt.Scope != "control" || receipt.RequestHash != requestHash) throw new ProjectException("job.idempotency.conflict");
                return DecodeJob(receipt.Payload ?? throw new ProjectException("project.schema.invalid"));
            }
            var job = await GetAsync(connection, transaction, jobId, token);
            if (job.Revision != expectedJobRevision) throw new ProjectException("job.conflict");
            var next = Transition(job, command);
            if (next != job)
            {
                next = next with { Revision = checked(job.Revision + 1), UpdatedUtc = DateTimeOffset.UtcNow };
                if (JobValidation.Terminal(next.State)) next = next with { CompletedUtc = next.UpdatedUtc };
                await ExecuteAsync(connection, transaction, "UPDATE jobs SET active=$active,payload=$payload WHERE id=$id", token, ("$active", JobValidation.Terminal(next.State) ? 0 : 1), ("$payload", JsonSerializer.Serialize(next)), ("$id", next.Id));
                await AddEventAsync(connection, transaction, next, token);
            }
            await AddReceiptAsync(connection, transaction, keyHash, requestHash, "control", next, token); return next;
        }, token);
    }
    private static JobRecord Transition(JobRecord job, JobControlCommand command)
    {
        if (command == JobControlCommand.Pause)
        {
            if (job.State is JobState.Queued or JobState.Interrupted) return job with { State = JobState.Paused, RequestedState = JobRequestedState.Pause, Phase = "paused" };
            if (job.State is JobState.Paused or JobState.PauseRequested) return job;
            if (job.State is JobState.Preparing or JobState.Running) return job with { State = JobState.PauseRequested, RequestedState = JobRequestedState.Pause };
        }
        if (command == JobControlCommand.Resume)
        {
            if (job.State == JobState.Queued) return job;
            if (job.State is JobState.Paused or JobState.Interrupted)
            {
                bool complete = job.CompletedUnits == job.TotalUnits;
                return job with { State = complete ? JobState.Completed : JobState.Queued, RequestedState = JobRequestedState.Run, Phase = complete ? "completed" : "queued", DiagnosticCode = null };
            }
        }
        if (command == JobControlCommand.Cancel)
        {
            if (job.State is JobState.Canceled or JobState.CancelRequested) return job;
            if (job.State is JobState.Queued or JobState.Paused or JobState.Interrupted) return job with { State = JobState.Canceled, RequestedState = JobRequestedState.Cancel, Phase = "canceled" };
            if (job.State is JobState.Preparing or JobState.Running or JobState.PauseRequested) return job with { State = JobState.CancelRequested, RequestedState = JobRequestedState.Cancel };
        }
        throw new ProjectException("job.transition.invalid");
    }
    public Task<IReadOnlyList<JobEventRecord>> ReadEventsAsync(long afterSequence, CancellationToken token) => project.JobTransactionAsync<IReadOnlyList<JobEventRecord>>(false, async (connection, transaction) =>
    { return await HasSchemaAsync(connection, transaction, token) ? await EventsAsync(connection, transaction, afterSequence, token) : []; }, token);
    public Task<long> GetEventSequenceAsync(CancellationToken token) => project.JobTransactionAsync(false, async (connection, transaction) =>
    { return await HasSchemaAsync(connection, transaction, token) ? await HeadAsync(connection, transaction, token) : 0; }, token);
    public Task<JobEventPage> ReadEventPageAsync(long? cursor, CancellationToken token) => project.JobTransactionAsync(false, async (connection, transaction) =>
    {
        if (!await HasSchemaAsync(connection, transaction, token)) return new JobEventPage(0, 0, [], [], cursor != 0);
        var jobs = await JobsAsync(connection, transaction, true, token); long head = await HeadAsync(connection, transaction, token);
        var events = await EventsAsync(connection, transaction, null, token); ValidateHistory(head, events);
        long first = events.Count == 0 ? 0 : events[0].Sequence;
        bool reset = cursor == null || cursor > head || (events.Count == 0 ? cursor < head : cursor < first - 1);
        return new JobEventPage(head, first, jobs, reset ? [] : events.Where(x => x.Sequence > cursor).ToArray(), reset);
    }, token);

    internal Task ValidatePersistentAsync(CancellationToken token) => project.JobTransactionAsync(false, async (connection, transaction) =>
    {
        if (!await HasSchemaAsync(connection, transaction, token)) return true;
        var jobs = await JobsAsync(connection, transaction, false, token);
        JobValidation.Require(jobs.Count(x => !JobValidation.Terminal(x.State)) <= 1);
        await AcceptedItemsAsync(connection, transaction, token);
        var receipts = await ReceiptsAsync(connection, transaction, token);
        foreach (var receipt in receipts)
        {
            var job = jobs.SingleOrDefault(x => x.Id == receipt.JobId); JobValidation.Require(job != null);
            if (receipt.Scope == "enqueue") JobValidation.Require(receipt.Payload == null && receipt.RequestHash == GenerationFingerprint.Request(job!.ProjectId, job.Kind, job.Input.Items.Select(x => x.SegmentId).ToArray(), job.Input.SavedVoice?.ModelId, job.Input.SavedVoxCpmVoice?.ModelId, job.Input.SavedVoxCpmVoice?.Options));
            else
            {
                var snapshot = DecodeJob(receipt.Payload ?? throw new ProjectException("project.schema.invalid"));
                JobValidation.Require(snapshot.Id == job!.Id && snapshot.InputHash == job.InputHash && snapshot.Revision <= job.Revision && Enum.GetValues<JobControlCommand>().Any(x => GenerationFingerprint.Control(job.Id, x) == receipt.RequestHash));
            }
        }
        JobValidation.Require(jobs.All(job => receipts.Any(x => x.JobId == job.Id && x.Scope == "enqueue")));
        long head = await HeadAsync(connection, transaction, token); var events = await EventsAsync(connection, transaction, null, token); ValidateHistory(head, events);
        JobValidation.Require(jobs.Count == 0 || head > 0);
        foreach (var e in events) JobValidation.Require(jobs.Any(x => x.Id == e.JobId && x.Revision >= e.JobRevision && x.TotalUnits == e.TotalUnits));
        return true;
    }, token);
    private JobRecord DecodeJob(string json)
    { var job = JsonSerializer.Deserialize<JobRecord>(json, StoredJson) ?? throw new ProjectException("project.schema.invalid"); JobValidation.Record(job, project); return job with { Input = JobValidation.Freeze(job.Input) }; }
    internal async Task<JobRecord> GetAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        using var query = Command(connection, transaction, "SELECT id,input_hash,active,payload FROM jobs WHERE id=$id", ("$id", id)); using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new ProjectException("job.missing"); return ReadJob(reader);
    }
    private JobRecord ReadJob(SqliteDataReader reader)
    {
        var job = DecodeJob(reader.GetString(3)); JobValidation.Require(reader.GetString(0) == job.Id && reader.GetString(1) == job.InputHash && reader.GetValue(2) is long active && active == (JobValidation.Terminal(job.State) ? 0 : 1)); return job;
    }
    internal async Task<IReadOnlyList<JobRecord>> JobsAsync(SqliteConnection connection, SqliteTransaction transaction, bool bounded, CancellationToken token)
    {
        using var query = Command(connection, transaction, bounded ? "SELECT id,input_hash,active,payload FROM jobs WHERE rowid IN (SELECT rowid FROM jobs WHERE active=0 ORDER BY rowid DESC LIMIT 32) OR active=1 ORDER BY rowid DESC" : "SELECT id,input_hash,active,payload FROM jobs ORDER BY rowid DESC");
        using var reader = await query.ExecuteReaderAsync(token); var result = new List<JobRecord>(); while (await reader.ReadAsync(token)) result.Add(ReadJob(reader)); return result.AsReadOnly();
    }
    internal async Task<IReadOnlyList<JobItemRecord>> ItemsAsync(SqliteConnection connection, SqliteTransaction transaction, JobRecord job, CancellationToken token)
    {
        using var query = Command(connection, transaction, "SELECT job_id,segment_id,ordinal,payload FROM job_items WHERE job_id=$id ORDER BY ordinal", ("$id", job.Id));
        using var reader = await query.ExecuteReaderAsync(token); var items = new List<JobItemRecord>();
        while (await reader.ReadAsync(token))
        {
            JobValidation.Require(items.Count < job.TotalUnits);
            var item = JsonSerializer.Deserialize<JobItemRecord>(reader.GetString(3), StoredJson) ?? throw new ProjectException("project.schema.invalid");
            var input = job.Input.Items[items.Count];
            JobValidation.Require(item.JobId == job.Id && item.SegmentId == input.SegmentId && item.Order == input.Order && reader.GetString(0) == item.JobId && reader.GetString(1) == item.SegmentId && reader.GetValue(2) is long order && order == item.Order && Enum.IsDefined(item.State) && JobValidation.Hash(item.EffectiveFingerprint) && (item.AttemptId == null || JobValidation.Id(item.AttemptId)) && (!item.CpuFallbackUsed || job.Input.Preset.AllowCpuFallback && job.Input.Preset.ProfileId == "q8-f16-hybrid-16"));
            var preset = item.CpuFallbackUsed ? job.Input.Preset with { ProfileId = "q8-f16-cpu" } : job.Input.Preset;
            JobValidation.Require(item.EffectiveFingerprint == GenerationFingerprint.ForItem(job.Input, input, preset));
            if (item.Artifact is { } artifact)
            {
                JobValidation.Require(JobValidation.Id(artifact.Id) && JobValidation.Hash(artifact.Sha256) && artifact.Length >= 44 + artifact.SampleCount * 2 && artifact.Length <= 1L << 30 && artifact.SampleCount is > 0 and <= 86400000 && artifact.ProfileId == preset.ProfileId && artifact.WorkerVersion == input.Versions.Worker && artifact.ModelVersion == input.Versions.Model && item.AttemptId != null && item.State == JobState.Completed);
                project.ResolveOwnedPath(artifact.RelativePath);
            }
            JobValidation.Require((item.State == JobState.Completed) == (item.Artifact != null)); items.Add(item);
        }
        JobValidation.Require(items.Count == job.TotalUnits && items.Count(x => x.Artifact != null) == job.CompletedUnits); return items.AsReadOnly();
    }
    private sealed record Receipt(string KeyHash, string RequestHash, string Scope, string JobId, string? Payload);
    private async Task<Receipt?> ReceiptAsync(SqliteConnection connection, SqliteTransaction transaction, string keyHash, CancellationToken token)
    {
        using var query = Command(connection, transaction, "SELECT key_hash,request_hash,scope,job_id,payload FROM job_receipts WHERE key_hash=$key", ("$key", keyHash)); using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadReceipt(reader) : null;
    }
    private static Receipt ReadReceipt(SqliteDataReader reader)
    {
        var receipt = new Receipt(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
        JobValidation.Require(JobValidation.Hash(receipt.KeyHash) && JobValidation.Hash(receipt.RequestHash) && receipt.Scope is "enqueue" or "control" && JobValidation.Id(receipt.JobId)); return receipt;
    }
    private static async Task<IReadOnlyList<Receipt>> ReceiptsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        using var query = Command(connection, transaction, "SELECT key_hash,request_hash,scope,job_id,payload FROM job_receipts"); using var reader = await query.ExecuteReaderAsync(token); var result = new List<Receipt>(); while (await reader.ReadAsync(token)) result.Add(ReadReceipt(reader)); return result;
    }
    private async Task<JobRecord> ReplayEnqueueAsync(SqliteConnection connection, SqliteTransaction transaction, Receipt receipt, string requestHash, CancellationToken token)
    { if (receipt.Scope != "enqueue" || receipt.RequestHash != requestHash) throw new ProjectException("job.idempotency.conflict"); return await GetAsync(connection, transaction, receipt.JobId, token); }
    private static Task AddReceiptAsync(SqliteConnection connection, SqliteTransaction transaction, string keyHash, string requestHash, string scope, JobRecord job, CancellationToken token) => ExecuteAsync(connection, transaction, "INSERT INTO job_receipts(key_hash,request_hash,scope,job_id,payload) VALUES($key,$request,$scope,$job,$payload)", token, ("$key", keyHash), ("$request", requestHash), ("$scope", scope), ("$job", job.Id), ("$payload", scope == "control" ? JsonSerializer.Serialize(job) : null));
    internal static async Task AddEventAsync(SqliteConnection connection, SqliteTransaction transaction, JobRecord job, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, "INSERT INTO job_events(job_id,job_revision,payload) VALUES($id,$revision,'')", token, ("$id", job.Id), ("$revision", job.Revision));
        long sequence = await HeadAsync(connection, transaction, token);
        var e = new JobEventRecord(sequence, job.Id, job.Revision, job.State, job.RequestedState, job.Phase, job.CompletedUnits, job.TotalUnits, job.DiagnosticCode, job.EffectiveProfileId);
        await ExecuteAsync(connection, transaction, "UPDATE job_events SET payload=$payload WHERE sequence=$sequence; DELETE FROM job_events WHERE sequence <= $sequence - 256", token, ("$payload", JsonSerializer.Serialize(e)), ("$sequence", sequence));
    }
    private static async Task<long> HeadAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        using var query = Command(connection, transaction, "SELECT seq FROM sqlite_sequence WHERE name='job_events'"); using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return 0;
        JobValidation.Require(reader.GetValue(0) is long head && head >= 0); long result = reader.GetInt64(0); JobValidation.Require(!await reader.ReadAsync(token)); return result;
    }
    private static async Task<IReadOnlyList<JobEventRecord>> EventsAsync(SqliteConnection connection, SqliteTransaction transaction, long? after, CancellationToken token)
    {
        // Preflight/pages read every retained row; only event consumers apply a cursor filter.
        using var query = Command(connection, transaction, "SELECT sequence,job_id,job_revision,payload FROM job_events WHERE $after IS NULL OR sequence > $after ORDER BY sequence", ("$after", after)); using var reader = await query.ExecuteReaderAsync(token); var result = new List<JobEventRecord>();
        while (await reader.ReadAsync(token))
        {
            JobValidation.Require(result.Count < 256);
            var e = JsonSerializer.Deserialize<JobEventRecord>(reader.GetString(3), StoredJson) ?? throw new ProjectException("project.schema.invalid");
            JobValidation.Require(e.Sequence > 0 && e.Sequence == reader.GetInt64(0) && JobValidation.Id(e.JobId) && e.JobId == reader.GetString(1) && e.JobRevision >= 1 && e.JobRevision == reader.GetInt64(2)); JobValidation.Summary(e.State, e.RequestedState, e.Phase, e.CompletedUnits, e.TotalUnits, e.DiagnosticCode, e.EffectiveProfileId); result.Add(e);
        }
        return result.AsReadOnly();
    }
    private static void ValidateHistory(long head, IReadOnlyList<JobEventRecord> events)
    {
        JobValidation.Require(events.Count == 0 || events[^1].Sequence == head);
        for (int i = 1; i < events.Count; i++) JobValidation.Require(events[i].Sequence == events[i - 1].Sequence + 1);
    }
    internal static async Task<bool> HasSchemaAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        using var query = Command(connection, transaction, "PRAGMA user_version"); long version = (long)(await query.ExecuteScalarAsync(token))!;
        if (version > 3) throw new ProjectException("project.schema.future"); JobValidation.Require(version is 1 or 2 or 3); return version == 3;
    }
    private static async Task RequireSchemaAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    { if (!await HasSchemaAsync(connection, transaction, token)) throw new ProjectException("project.schema.invalid"); }
    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value); return command; }
    internal static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken token, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); await command.ExecuteNonQueryAsync(token); }
}

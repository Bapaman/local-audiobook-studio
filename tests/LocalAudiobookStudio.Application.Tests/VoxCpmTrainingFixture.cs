using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Tests;

internal sealed class VoxCpmTrainingFixture : IDisposable
{
    internal ApprovedTtsCorpusFixture Corpus { get; } = new();
    internal Runtime RuntimeProvider { get; } = new();
    internal Validator Checkpoints { get; } = new();
    internal string Id => Corpus.ProjectId;
    internal string Root => Corpus.Root;
    internal VoxCpmTrainingRecordStore Store => new(Root, Id);
    internal StudioVoxCpmTrainingService Service => new(new ProjectJobAccess(new(Corpus.Box.Root), Corpus.Box.Catalog(), new()),
        new ApprovedTtsCorpusResolver(), RuntimeProvider, Checkpoints);
    internal Task SeedAsync(CancellationToken token) => Corpus.SeedAsync(token);
    internal Task<VoxCpmTrainingSnapshotDto> StartAsync(CancellationToken token, string key = "train")
        => Service.StartAsync(Id, Corpus.ProjectRevision, key, Corpus.VersionId, true, token);
    internal async Task<VoxCpmTrainingSnapshotDto> AddStepsAsync(int count, string state, CancellationToken token)
        => await Store.TransactionAsync(e =>
        {
            var r = e.Current!;
            long finalStep = checked(r.Cursor.GlobalStep + count);
            long target = Math.Max(r.TargetCompletedEpochs, (finalStep + r.Cursor.EpochSize - 1) / r.Cursor.EpochSize);
            r = r with { TargetCompletedEpochs = target, Snapshot = r.Snapshot with {
                State = "training", Phase = "training", CanResume = false, TargetCompletedEpochs = target } };
            e = e with { Current = r };
            for (int i = 0; i < count; i++)
            {
                var cursor = r.Cursor.Advance(r.Corpus.Clips.Select(x => x.Id).ToArray());
                string window = Guid.NewGuid().ToString("N"), id = Guid.NewGuid().ToString("N");
                var checkpoint = new VoxCpmCheckpointReference(id, r.Snapshot.Id, r.ActionId, r.Snapshot.ExecutionId, window,
                    $"{r.OutputRelativeRoot}/windows/{window}/checkpoints/{id}/manifest.json", new('f', 64), cursor, DateTimeOffset.UtcNow);
                e = VoxCpmTrainingRecordStore.AcceptCheckpoint(e, checkpoint); r = e.Current!;
            }
            r = r with { Snapshot = r.Snapshot with { State = state, Phase = state,
                CanResume = !VoxCpmTrainingRecordStore.Active(state) && r.Cursor.GlobalStep < 512 } };
            return (e with { Current = r }, r.Snapshot);
        }, token);
    internal sealed class Runtime : IVoxCpmTrainingRuntime
    {
        internal bool RejectAll;
        internal int Calls;
        internal bool Ready { get; set; } = true;
        internal VoxCpmTrainingIdentity Identity { get; set; } = new(new('a', 64), new('b', 40), new('c', 40),
            new('d', 64), new('e', 64), new('f', 64), new('1', 64), new('2', 64), new('3', 64), new('4', 64));
        public Task<VoxCpmTrainingReadinessDto> GetReadinessAsync(CancellationToken token = default)
        { Calls++; if (RejectAll) throw new InvalidOperationException("No runtime admission permitted"); return Task.FromResult(new VoxCpmTrainingReadinessDto(Ready, Ready ? null : "voxcpm_training.runtime.not_ready")); }
        public Task<VoxCpmTrainingIdentity> GetIdentityAsync(CancellationToken token)
        {
            Calls++;
            if (RejectAll) throw new InvalidOperationException("No runtime admission permitted");
            return Ready ? Task.FromResult(Identity) : Task.FromException<VoxCpmTrainingIdentity>(new VoxCpmTrainingExecutionException("voxcpm_training.runtime.not_ready"));
        }
    }
    internal sealed class Validator : IVoxCpmTrainingCheckpointValidator
    {
        internal bool Reject { get; set; }
        public Task VerifyAsync(string projectRoot, VoxCpmCheckpointReference checkpoint, ApprovedTtsCorpusBinding corpus, VoxCpmTrainingIdentity identity, CancellationToken token)
            => Reject ? Task.FromException(new VoxCpmTrainingExecutionException("voxcpm_training.checkpoint.invalid")) : Task.CompletedTask;
    }
    public void Dispose() => Corpus.Dispose();
}

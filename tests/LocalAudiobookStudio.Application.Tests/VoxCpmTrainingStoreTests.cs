using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.VoxCpmTraining;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class VoxCpmTrainingStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Evicted_receipts_remain_expired_and_never_restart_training()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); var start = await f.StartAsync(Token);
        await f.Store.TransactionAsync(e => {
            for (int i = 0; i < 256; i++) e = VoxCpmTrainingRecordStore.Receipt(e, e.Current!, "key-" + i, "operation-" + i);
            return (e, true); }, Token);
        Assert.Null(await f.Store.TransactionAsync(e => ((VoxCpmTrainingEnvelope?)null, f.Store.Replay(e, "never-used", "start")), Token));
        var error = await Assert.ThrowsAsync<StudioException>(() => f.StartAsync(Token)); Assert.Equal("voxcpm_training.idempotency.expired", error.Code);
        Assert.Equivalent(start, await f.Service.GetAsync(f.Id, Token));
        Assert.Equal(256, await f.Store.TransactionAsync(e => ((VoxCpmTrainingEnvelope?)null, e.Receipts.Count), Token));
    }
    [Fact]
    public async Task Failed_atomic_replacement_preserves_previous_head_and_cursor()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        var before = await f.AddStepsAsync(1, "paused", Token); string path = Path.Combine(f.Root, "voxcpm-training/state.json");
        byte[] bytes = await File.ReadAllBytesAsync(path, Token);
        using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => f.AddStepsAsync(1, "completed", Token));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token)); Assert.Equivalent(before, await f.Service.GetAsync(f.Id, Token));
    }
    [Fact]
    public async Task Corrupt_or_duplicate_fields_fail_closed_without_rewriting_state()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        string path = Path.Combine(f.Root, "voxcpm-training/state.json"); string json = await File.ReadAllTextAsync(path, Token);
        string duplicate = json.Replace("\"Format\":1", "\"Format\":1,\"Format\":1", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, duplicate, Token);
        var error = await Assert.ThrowsAsync<StudioException>(() => f.Service.GetAsync(f.Id, Token)); Assert.Equal("voxcpm_training.state.invalid", error.Code);
        Assert.Equal(duplicate, await File.ReadAllTextAsync(path, Token));
    }
    [Fact]
    public async Task Oversized_candidate_does_not_drop_history_to_fit_the_state_budget()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); await f.StartAsync(Token);
        await f.AddStepsAsync(1, "paused", Token);
        var before = await f.Service.GetAsync(f.Id, Token);
        var error = await Assert.ThrowsAsync<StudioException>(() => f.Store.TransactionAsync(e => {
            // Valid bounded text and sample metadata; its serialized size alone exceeds the store budget.
            var r = e.Current!; var giant = r.Corpus with { Clips = Enumerable.Range(0, 512).Select(i => r.Corpus.Clips[0] with {
                TrainingOrdinal = i, SourceIndex = i, Id = Guid.NewGuid().ToString("N"), StartSample = i * 100, EndSample = i * 100 + 100,
                CorrectedText = new string('я', 4000), TextSha256 = VoxCpmTrainingRecordStore.Hash(new string('я', 4000)) }).ToArray(), RetainedDurationSeconds = 3.2 };
            // Adding lineages preserves the current model, cursor and old receipt bindings.
            // Two full corpora exceed 8MiB even with unescaped UTF-8 JSON.
            giant = giant with { VersionId = Guid.NewGuid().ToString("N"), ManifestSha256 = new('5', 64), BindingSha256 = new('6', 64) };
            return (e with { Lineages = [.. e.Lineages,
                new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), giant, r.Identity),
                new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), giant, r.Identity)] }, true);
        }, Token));
        Assert.Equal("voxcpm_training.state.limit", error.Code); Assert.Equivalent(before, await f.Service.GetAsync(f.Id, Token));
    }

    [Fact]
    public async Task Lineage_step_limit_and_receipt_byte_budget_preserve_all_saved_models()
    {
        using var f = new VoxCpmTrainingFixture(); await f.SeedAsync(Token); var start = await f.StartAsync(Token);
        var completed = await f.AddStepsAsync(512, "completed", Token);
        Assert.Equal(512, completed.Models.Count); Assert.Equal(256, completed.CompletedEpochs); Assert.False(completed.CanResume);
        var error = await Assert.ThrowsAsync<StudioException>(() => f.Service.ControlAsync(f.Id, start.Id, completed.Revision, "limit", "resume", Token));
        Assert.Equal("voxcpm_training.state.limit", error.Code);
        await f.Store.TransactionAsync(e => {
            for (int i = 0; i < 60; i++) e = VoxCpmTrainingRecordStore.Receipt(e, e.Current!, "large-" + i, "read-only-test-history");
            return (e, true); }, Token);
        int count = await f.Store.TransactionAsync(e => ((VoxCpmTrainingEnvelope?)null, e.Receipts.Count), Token);
        Assert.InRange(count, 1, 59); Assert.True(new FileInfo(Path.Combine(f.Root, "voxcpm-training/state.json")).Length <= 8 * 1024 * 1024);
        Assert.Equivalent(completed, await f.Service.GetAsync(f.Id, Token));
    }
}

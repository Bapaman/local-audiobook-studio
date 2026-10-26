using System.Text.Json;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Tests;

internal static class VoxCpmTrainingApiFixture
{
    internal static Task<ApiTestHost> HostAsync(Runtime? runtime = null, Executor? executor = null, bool hosted = false, string? root = null)
        => ApiTestHost.StartAsync(root: root, deleteRootOnDispose: root == null, configureServices: services =>
        {
            services.AddSingleton<ITtsCorpusRuntime>(new TtsCorpusApiFixture.Runtime());
            services.AddSingleton<ITtsCorpusExecutor>(new TtsCorpusApiFixture.Executor());
            services.AddSingleton<IVoxCpmTrainingRuntime>(runtime ?? new());
            services.AddSingleton<IVoxCpmTrainingExecutor>(executor ?? new());
            services.AddSingleton<IVoxCpmTrainingCheckpointValidator>(new Validator());
            if (!hosted) services.RemoveAll<IHostedService>();
        });
    internal static async Task<(string Id, string ETag, string VersionId)> ApprovedAsync(ApiTestHost host)
    {
        var p = await TtsCorpusApiFixture.ProjectAsync(host); var draft = await TtsCorpusApiFixture.DraftAsync(host, p.Id, p.ETag);
        var corpus = host.Services.GetRequiredService<IStudioTtsCorpusService>(); var clip = draft.Clips[0];
        var reviewed = await corpus.UpdateClipAsync(p.Id, draft.Id, clip.Id, draft.Revision, "review", new(clip.Text, false, true), ApiTestHost.Token);
        var approved = await corpus.ApproveAsync(p.Id, draft.Id, reviewed.Revision, "approve", true, ApiTestHost.Token);
        return (p.Id, p.ETag, approved.Versions[0].Id);
    }
    internal static string PathFor(string projectId, string? trainingId = null) => $"/api/projects/{projectId}/voxcpm-training" + (trainingId == null ? "" : "/" + trainingId);
    internal static string ETag(VoxCpmTrainingSnapshotDto snapshot) => $"\"{snapshot.Revision}\"";
    internal static HttpContent Json(string value) => new StringContent(value, System.Text.Encoding.UTF8, "application/json");
    internal static HttpContent StartBody(string id, bool consent = true) => Json(JsonSerializer.Serialize(new { corpusVersionId = id, experimentalConfirmed = consent }));
    internal static async Task<JsonElement> Problem(HttpResponseMessage response, System.Net.HttpStatusCode expected, string code)
    { Assert.Equal(expected, response.StatusCode); var value = await ApiTestHost.JsonAsync(response); Assert.Equal(code, value.GetProperty("code").GetString()); return value; }
    internal sealed class Runtime : IVoxCpmTrainingRuntime
    {
        internal int Admissions;
        internal string? ErrorCode;
        public Task<VoxCpmTrainingReadinessDto> GetReadinessAsync(CancellationToken token = default)
        {
            Interlocked.Increment(ref Admissions);
            return ErrorCode == null ? Task.FromResult(new VoxCpmTrainingReadinessDto(true, null))
                : Task.FromException<VoxCpmTrainingReadinessDto>(new LocalAudiobookStudio.Application.Errors.StudioException(ErrorCode));
        }
        public Task<VoxCpmTrainingIdentity> GetIdentityAsync(CancellationToken token)
        { Interlocked.Increment(ref Admissions); return Task.FromResult(new VoxCpmTrainingIdentity(new('a', 64), new('b', 40), new('c', 40), new('d', 64), new('e', 64), new('f', 64), new('1', 64), new('2', 64), new('3', 64), new('4', 64))); }
    }
    internal sealed class Validator : IVoxCpmTrainingCheckpointValidator
    { public Task VerifyAsync(string root, VoxCpmCheckpointReference reference, ApprovedTtsCorpusBinding corpus, VoxCpmTrainingIdentity identity, CancellationToken token) => Task.CompletedTask; }
    internal sealed class Executor : IVoxCpmTrainingExecutor
    {
        internal int Calls;
        internal Func<VoxCpmTrainingExecution, Task>? Before;
        public async Task<VoxCpmTrainingWindowResult> RunAsync(VoxCpmTrainingExecution e, Func<VoxCpmTrainingProgress, Task> progress, CancellationToken token)
        {
            Assert.False(token.CanBeCanceled); Interlocked.Increment(ref Calls); if (Before != null) await Before(e);
            await progress(new("checkpointing"));
            return new(new(e.CheckpointId, e.TrainingId, e.ActionId, e.ExecutionId, e.WindowId,
                System.IO.Path.GetRelativePath(e.ProjectRoot, e.OutputRoot).Replace('\\', '/') + $"/checkpoints/{e.CheckpointId}/manifest.json",
                new('a', 64), e.Cursor.Advance(e.Corpus.Clips.Select(c => c.Id).ToArray()), DateTimeOffset.UtcNow));
        }
    }
}

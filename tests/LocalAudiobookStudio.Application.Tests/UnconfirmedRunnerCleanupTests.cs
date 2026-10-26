using System.Reflection;
using LocalAudiobookStudio.Application.Artifacts;
using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Storage;
using Microsoft.Data.Sqlite;

namespace LocalAudiobookStudio.Application.Tests;

public class UnconfirmedRunnerCleanupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unconfirmed_factory_or_terminal_cleanup_retains_actual_resources_and_never_accepts_or_reuses_slot(bool terminal)
    {
        await using var fixture = await GenerationRunnerTests.Fixture.Create(preview: true);
        fixture.Runner = new(new ProjectJobAccess(new(fixture.Box.Root), fixture.Box.Catalog(), new ProjectService()),
            new ProjectService(), new UnconfirmedFactory(fixture.Workers, terminal), TimeProvider.System);
        fixture.Workers.Release.TrySetResult(); fixture.StopFailureExpected = true;
        try
        {
            var error = await Assert.ThrowsAsync<WorkerClientException>(() => fixture.Runner.RunNextAsync(Token));
            Assert.Equal("worker.cleanup", error.Code); Assert.False(error.CleanupConfirmed); Assert.Null(error.InnerException);
            var job = await fixture.Job(); Assert.Equal(JobState.Interrupted, job.State); Assert.Equal(0, job.CompletedUnits);
            await using var db = await fixture.Open();
            var item = Assert.Single(await db.Jobs.ItemsAsync(job.Id, Token)); Assert.Null(item.Artifact);
            Assert.Equal("interrupted", Assert.Single(db.State.Attempts).Status);
            Assert.Throws<IOException>(() => File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read));
            Assert.Throws<IOException>(() => Directory.Move(fixture.Box.ProjectRoot(fixture.Id), fixture.Box.ProjectRoot(fixture.Id) + ".moved"));
            Assert.Equal("project.busy", (await Assert.ThrowsAsync<ProjectException>(async () =>
            { await using var unexpected = await db.AcquireArtifactLeaseAsync(TimeSpan.FromMilliseconds(250), Token); })).Code);
            if (terminal)
            {
                string final = fixture.Workers.LastCommand!.FinalPath; byte[] before = await File.ReadAllBytesAsync(final, Token);
                Assert.Equal(9644, before.Length);
                Assert.Equal("artifact.missing", (await Assert.ThrowsAsync<StudioException>(() =>
                    new ProjectArtifactReader(fixture.Box.Service()).OpenAsync(fixture.Id, db.State.Attempts[0].Id, Token))).Code);
                await fixture.Runner.RecoverAsync(Token); Assert.Equal(before, await File.ReadAllBytesAsync(final, Token));
                await using var count = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(fixture.Box.ProjectRoot(fixture.Id), "project.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await count.OpenAsync(Token); SqliteSidecarLifetime.Retain(count);
                using var query = count.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM staging";
                Assert.Equal(1L, await query.ExecuteScalarAsync(Token));
            }
            Assert.False(await fixture.Runner.RunNextAsync(Token)); Assert.Equal(terminal ? 1 : 0, fixture.Workers.OpenCount);
            var stopped = await Assert.ThrowsAsync<WorkerClientException>(() => fixture.Runner.StopAsync(Token));
            Assert.Equal("worker.cleanup", stopped.Code); Assert.False(stopped.CleanupConfirmed);
            var canceled = await fixture.Jobs.RequestStateAsync(fixture.Id, job.Id, job.Revision, "cancel", JobControlCommand.Cancel, Token);
            Assert.Equal(JobState.Canceled, canceled.State); Assert.False(await fixture.Runner.RunNextAsync(Token));
            Assert.Throws<IOException>(() => File.Open(fixture.Prompt, FileMode.Open, FileAccess.Write, FileShare.Read));
        }
        finally
        {
            // Test-only teardown of this exclusively owned, process-free fake.
            // Production intentionally has NO "assume exit"/unpoison operation.
            // The wrapper awaited its inner synthetic disposal before reporting
            // false; no real tree exists. Do not infer native cleanup acceptance.
            if (terminal && fixture.Workers.OpenCount > 0) await fixture.Workers.CleanupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var retained = typeof(GenerationRunner).GetField("retained", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Runner);
            if (retained != null)
            {
                var type = retained.GetType();
                ((IDisposable?)type.GetProperty("Prompt")!.GetValue(retained))?.Dispose();
                await ((ProjectArtifactLease)type.GetProperty("Lease")!.GetValue(retained)!).DisposeAsync();
                await ((IAsyncDisposable)type.GetProperty("Session")!.GetValue(retained)!).DisposeAsync();
            }
        }
    }

    private sealed class UnconfirmedFactory(GenerationRunnerTests.FakeFactory inner, bool terminal) : IWorkerClientFactory
    {
        public async Task<IWorkerClient> OpenAsync(string root, CancellationToken token)
        {
            if (!terminal) throw new WorkerClientException("worker.cleanup", false);
            return new UnconfirmedClient(await inner.OpenAsync(root, token));
        }
        private sealed class UnconfirmedClient(IWorkerClient inner) : IWorkerClient
        {
            public Task<SynthesisResult> SynthesizeAsync(string id, SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken token)
                => inner.SynthesizeAsync(id, command, progress, token);
            public Task CancelAsync(string id, CancellationToken token) => inner.CancelAsync(id, token);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); throw new WorkerClientException("worker.cleanup", false); }
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.TtsCorpus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LocalAudiobookStudio.Web.Tests;

internal static class TtsCorpusApiFixture
{
    internal static Task<ApiTestHost> HostAsync(Runtime? runtime = null, Executor? executor = null, bool hosted = false, string? root = null)
        => ApiTestHost.StartAsync(root: root, deleteRootOnDispose: root == null, configureServices: services =>
        {
            services.AddSingleton<ITtsCorpusRuntime>(runtime ?? new());
            services.AddSingleton<ITtsCorpusExecutor>(executor ?? new());
            if (!hosted) services.RemoveAll<IHostedService>();
        });

    internal static async Task<(string Id, string ETag)> ProjectAsync(ApiTestHost host, int sourceSamples = 480000)
    {
        var project = await host.CreateAsync();
        using var upload = await host.SendAsync(HttpMethod.Post, $"/api/projects/{project.Id}/voice/recording", ApiTestHost.Multipart(ApiTestHost.Wav(sourceSamples)), project.ETag);
        Assert.Equal(System.Net.HttpStatusCode.OK, upload.StatusCode);
        return (project.Id, upload.Headers.ETag!.Tag);
    }

    internal static async Task<TtsCorpusSnapshotDto> DraftAsync(ApiTestHost host, string projectId, string projectETag)
    {
        var service = host.Services.GetRequiredService<IStudioTtsCorpusService>();
        await service.StartAsync(projectId, long.Parse(projectETag.Trim('"')), "fixture-start", true, ApiTestHost.Token);
        Assert.True(await host.Services.GetRequiredService<TtsCorpusRunner>().RunNextAsync(ApiTestHost.Token));
        var snapshot = (await service.GetAsync(projectId, ApiTestHost.Token))!;
        Assert.Equal("completed", snapshot.State);
        return snapshot;
    }

    internal static string ETag(TtsCorpusSnapshotDto snapshot) => $"\"{snapshot.Revision}\"";
    internal static string PathFor(string projectId, string? corpusId = null) => $"/api/projects/{projectId}/tts-corpus" + (corpusId == null ? "" : "/" + corpusId);

    internal sealed class Runtime : ITtsCorpusRuntime
    {
        public bool Ready { get; set; } = true;
        public string Fingerprint { get; set; } = new('a', 64);
        public string? ErrorCode { get; set; }
        public Func<CancellationToken, Task>? BeforeFingerprint { get; init; }
        public Task<TtsCorpusReadinessDto> GetReadinessAsync(CancellationToken token = default)
            => ErrorCode == null ? Task.FromResult(new TtsCorpusReadinessDto(Ready, Ready ? null : "tts_corpus.runtime.not_ready"))
                : Task.FromException<TtsCorpusReadinessDto>(new LocalAudiobookStudio.Application.Errors.StudioException(ErrorCode));
        public async Task<string> GetFingerprintAsync(CancellationToken token = default)
        {
            if (BeforeFingerprint != null) await BeforeFingerprint(token);
            return Ready && ErrorCode == null ? Fingerprint : throw new TtsCorpusExecutionException(ErrorCode ?? "tts_corpus.runtime.not_ready");
        }
    }

    internal sealed class Executor : ITtsCorpusExecutor
    {
        public Func<TtsCorpusExecution, Task>? Before { get; set; }
        public string? AudioPath { get; private set; }
        public byte[]? AudioBytes { get; private set; }
        public async Task<TtsCorpusWindowResult> RunAsync(TtsCorpusExecution execution, Func<TtsCorpusProgress, Task> progress, CancellationToken token)
        {
            if (Before != null) await Before(execution);
            string relative = System.IO.Path.GetRelativePath(execution.ProjectRoot, execution.OutputRoot).Replace('\\', '/');
            if (execution.PreparedPlan == null)
            {
                int samples = checked((int)((execution.SourceSampleCount + 2) / 3));
                var bytes = ApiTestHost.Wav(samples);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 16000);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 32000);
                string id = Guid.NewGuid().ToString("N"), audio = relative + "/" + id + ".wav";
                AudioPath = System.IO.Path.Combine(execution.ProjectRoot, audio); AudioBytes = bytes;
                await File.WriteAllBytesAsync(AudioPath, bytes, token);
                var clip = new TtsCorpusPlannedClip(id, 0, 0, samples, audio, Hash(bytes));
                byte[] plan = JsonSerializer.SerializeToUtf8Bytes(new[] { clip }); string path = relative + "/plan.json";
                await File.WriteAllBytesAsync(System.IO.Path.Combine(execution.ProjectRoot, path), plan, token);
                return new(new(path, Hash(plan), execution.SourceSha256, execution.RuntimeFingerprint, samples, [clip]), []);
            }
            string textPath = relative + "/text.json"; byte[] text = Encoding.UTF8.GetBytes("Точный черновик.");
            await File.WriteAllBytesAsync(System.IO.Path.Combine(execution.ProjectRoot, textPath), text, token);
            return new(null, [new(execution.PreparedPlan.Clips[0], textPath, Hash(text), "Точный черновик.",
                ["tts_corpus.word.end_missing"], [new("черновик", 0.1, null)])]);
        }
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

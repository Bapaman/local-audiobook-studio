using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.App.TtsCorpus;
using LocalAudiobookStudio.Application;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.Voices;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Core.Projects;
using LocalAudiobookStudio.Core.Voices;
using Microsoft.Extensions.DependencyInjection;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TtsCorpusSourceHandoffTests
{
    [Fact]
    public async Task Imported_uppercase_recording_hash_crosses_real_runner_as_canonical_ipc_and_resumes_prepared_plan()
    {
        using var temporary = new CorpusTemporaryDirectory();
        var executor = new ProtocolExecutor(TtsCorpusRuntimeOptions.Read(temporary.WriteConfig()));
        var services = new ServiceCollection().AddStudioProjects(new(temporary.Child("data")));
        services.AddSingleton(new VoiceRecordingPolicy(48000));
        services.AddSingleton<ITtsCorpusRuntime>(new ReadyRuntime());
        services.AddSingleton<ITtsCorpusExecutor>(executor);
        await using var provider = services.BuildServiceProvider();
        var created = await provider.GetRequiredService<IStudioProjectService>().CreateAsync(new("Synthetic corpus handoff"), "project", TestContext.Current.CancellationToken);
        var uploaded = await provider.GetRequiredService<IStudioVoiceService>().UploadRecordingAsync(created.Snapshot.Id, created.Snapshot.Revision,
            new("source.wav", true), new MemoryStream(Wave(48000, 48000)), TestContext.Current.CancellationToken);
        string projectRoot = Path.Combine(temporary.Child("data"), "projects", uploaded.Id);
        string storedHash;
        await using (var db = await new ProjectService().OpenCurrentAsync(projectRoot, TestContext.Current.CancellationToken))
        {
            storedHash = db.State.VoiceRecording!.Sha256;
            var actual = await WavInspector.InspectAsync(db.ResolveOwnedPath(db.State.VoiceRecording.WavRelativePath), TestContext.Current.CancellationToken);
            Assert.Equal(actual.Sha256, storedHash);
            Assert.Equal(storedHash.ToUpperInvariant(), storedHash); Assert.NotEqual(storedHash.ToLowerInvariant(), storedHash);
        }
        var corpus = provider.GetRequiredService<IStudioTtsCorpusService>(); var runner = provider.GetRequiredService<TtsCorpusRunner>();
        await corpus.StartAsync(uploaded.Id, uploaded.Revision, "start", true, TestContext.Current.CancellationToken);
        Assert.True(await runner.RunNextAsync(TestContext.Current.CancellationToken));
        var paused = (await corpus.GetAsync(uploaded.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal("paused", paused.State); Assert.Null(paused.DiagnosticCode); Assert.Equal(1, paused.TotalClipCount); Assert.True(paused.CanResume);
        var resumed = await corpus.ControlAsync(uploaded.Id, paused.Id, paused.Revision, "resume", "resume", TestContext.Current.CancellationToken);
        Assert.True(await runner.RunNextAsync(TestContext.Current.CancellationToken));
        var completed = (await corpus.GetAsync(uploaded.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal("completed", completed.State); Assert.Equal(1, completed.CompletedClipCount);
        Assert.NotEqual(paused.ExecutionId, resumed.ExecutionId);
        Assert.Equal(new[] { storedHash.ToLowerInvariant(), storedHash.ToLowerInvariant() }, executor.SourceHashes);
        await using var finalDb = await new ProjectService().OpenCurrentAsync(projectRoot, TestContext.Current.CancellationToken);
        Assert.Equal(storedHash, finalDb.State.VoiceRecording!.Sha256);
    }

    private sealed class ReadyRuntime : ITtsCorpusRuntime
    {
        public Task<TtsCorpusReadinessDto> GetReadinessAsync(CancellationToken token = default) => Task.FromResult(new TtsCorpusReadinessDto(true, null));
        public Task<string> GetFingerprintAsync(CancellationToken token = default) => Task.FromResult(new string('b', 64));
    }
    private sealed class ProtocolExecutor(TtsCorpusRuntimeOptions options) : ITtsCorpusExecutor
    {
        internal List<string> SourceHashes { get; } = [];
        public async Task<TtsCorpusWindowResult> RunAsync(TtsCorpusExecution execution, Func<TtsCorpusProgress, Task> progress, CancellationToken token)
        {
            SourceHashes.Add(execution.SourceSha256);
            var request = TtsCorpusProtocol.Create(execution, options, [new("config.json", 1, new string('c', 64))], Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            string basis = request.OutputRelativeRoot; TtsCorpusPreparedPlan? prepared = null; TtsCorpusAcceptedClip[] clips = [];
            if (execution.PreparedPlan == null)
            {
                byte[] pcm = Wave(16000, 16000); string cleanSha = Write("clean.wav", pcm);
                string id = TtsCorpusProtocol.ClipId(execution.ActionId, 0); string audioSha = Write("clips/" + id + ".wav", pcm);
                var clip = new TtsCorpusPlannedClip(id, 0, 0, 16000, basis + "/clips/" + id + ".wav", audioSha);
                string sha = Write("plan.json", JsonSerializer.SerializeToUtf8Bytes(new
                { schemaVersion = 1, execution.ActionId, execution.SourceSha256, execution.RuntimeFingerprint, totalSamples = 16000,
                    clips = new[] { clip }, cleanAudioRelativePath = basis + "/clean.wav", cleanAudioSha256 = cleanSha }, TtsCorpusJson.Options));
                prepared = new(basis + "/plan.json", sha, execution.SourceSha256, execution.RuntimeFingerprint, 16000, [clip]);
            }
            else
            {
                var clip = execution.PreparedPlan.Clips[0]; string text = "Слово";
                string textSha = Write("texts/" + clip.Id + ".json", JsonSerializer.SerializeToUtf8Bytes(new
                { schemaVersion = 1, clipId = clip.Id, draftText = text, warningCodes = Array.Empty<string>(), words = Array.Empty<TtsCorpusWordDto>() }, TtsCorpusJson.Options));
                clips = [new(clip, basis + "/texts/" + clip.Id + ".json", textSha, text, [], [])];
            }
            string resultSha = Write("result.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, request.RequestId, execution.ProjectId, execution.CorpusId, execution.ActionId, execution.ExecutionId, request.WindowId,
                execution.SourceRecordingId, execution.SourceSha256, execution.RuntimeFingerprint, request.RequestSha256,
                planSha256 = (prepared ?? execution.PreparedPlan)!.Sha256, preparedPlan = prepared, clips, stopped = prepared != null
            }, TtsCorpusJson.Options));
            Write("READY", Encoding.ASCII.GetBytes(resultSha + "\n"));
            return await TtsCorpusProtocol.AcceptAsync(request, token);

            string Write(string relative, byte[] bytes)
            {
                string path = Path.Combine(execution.ProjectRoot, basis, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes); return Convert.ToHexStringLower(SHA256.HashData(bytes));
            }
        }
    }
    private static byte[] Wave(int rate, int samples)
    {
        byte[] bytes = new byte[44 + samples * 2]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), rate); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16); "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples * 2);
        return bytes;
    }
}

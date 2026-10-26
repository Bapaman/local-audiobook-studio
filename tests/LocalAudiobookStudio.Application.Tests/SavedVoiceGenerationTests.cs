using System.Security.Cryptography;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.Application.Tests;

public class SavedVoiceGenerationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saved_model_generation_never_accepts_plain_tts_and_releases_tts_before_conversion(bool conversionFails)
    {
        await using var f = await GenerationRunnerTests.Fixture.Create(preview: true);
        var old = await f.Job(); await f.Jobs.RequestStateAsync(f.Id, old.Id, old.Revision, "cancel-original", JobControlCommand.Cancel, Token);
        string id; SavedVoiceBinding binding;
        await using (var db = await f.Open())
        {
            binding = new(Guid.NewGuid().ToString("N"), "training/actions/" + Guid.NewGuid().ToString("N") + "/saved/workflow.json",
                new string('a', 64), db.State.VoiceRecording!.Id, db.State.VoiceRecording.WavRelativePath,
                db.State.VoiceRecording.Sha256.ToLowerInvariant(), 125, 1, 125, "target_epoch", "rvc-saved-voice-cpu-v1");
            var input = (await db.Jobs.GetAsync(old.Id, Token)).Input with { ProjectRevision = db.State.Revision, SavedVoice = binding };
            await using var lease = await db.AcquireArtifactLeaseAsync(cancellationToken: Token);
            id = (await db.Jobs.EnqueueAsync(lease, db.State.Revision, "saved", JobKind.Preview, input, Token)).Id;
        }
        var converter = new Converter(f, conversionFails);
        f.Runner = new(new ProjectJobAccess(new(f.Box.Root), f.Box.Catalog(), new()), new(), f.Workers, TimeProvider.System, savedVoiceConverter: converter);
        f.Workers.Release.TrySetResult(); Assert.True(await f.Runner.RunNextAsync(Token));
        await using var resultDb = await f.Open(); var job = await resultDb.Jobs.GetAsync(id, Token);
        Assert.NotNull(converter.Input); Assert.Equal(binding, converter.Input.Binding); Assert.Equal(1, converter.Calls);
        Assert.Equal(1, f.Workers.DisposeCount); Assert.EndsWith("source.wav", f.Workers.LastCommand!.FinalPath);
        Assert.Equal(conversionFails ? JobState.Failed : JobState.Completed, job.State);
        var item = Assert.Single(await resultDb.Jobs.ItemsAsync(id, Token));
        if (conversionFails) { Assert.Null(item.Artifact); Assert.Equal("worker.output.invalid", job.DiagnosticCode); }
        else
        {
            Assert.NotEqual(f.Workers.LastCommand.FinalPath, resultDb.ResolveOwnedPath(item.Artifact!.RelativePath));
            Assert.Equal(4800, item.Artifact.SampleCount);
            Assert.Equal(converter.CandidateHash, item.Artifact.Sha256);
        }
        Assert.False(File.Exists(f.Workers.LastCommand.FinalPath));
    }

    private sealed class Converter(GenerationRunnerTests.Fixture fixture, bool fail) : ISavedVoiceConverter
    {
        public int Calls; public SavedVoiceConversionExecution? Input; public string? CandidateHash;
        public async Task<ConvertedVoiceCandidate> ConvertAsync(SavedVoiceConversionExecution execution, Action<string> progress, CancellationToken token)
        {
            Calls++; Input = execution;
            Assert.Equal(1, fixture.Workers.DisposeCount);
            Assert.True(File.Exists(execution.SourcePath)); Assert.Equal("rvc-saved-voice-cpu-v1", execution.Binding.ProfileVersion);
            if (fail) throw new WorkerClientException("worker.output.invalid", true);
            string candidate = Path.Combine(execution.OutputRoot, "candidate", "final.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
            byte[] raw = GenerationRunnerTests.Wave(4800); raw[44] = 3; await File.WriteAllBytesAsync(candidate, raw, token);
            CandidateHash = Convert.ToHexStringLower(SHA256.HashData(raw));
            return new(candidate, CandidateHash, raw.Length, 4800);
        }
    }
}

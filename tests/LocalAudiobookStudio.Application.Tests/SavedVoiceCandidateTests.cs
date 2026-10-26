using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Application.Training;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.Application.Tests;

public class SavedVoiceCandidateTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-model")]
    [InlineData("tampered-audio")]
    public async Task Candidate_requires_exact_frozen_model_source_profile_and_audio(string mode)
    {
        using var box = new CatalogSandbox(); string root = box.Root;
        string workflowPath = Path.Combine(root, "training", "actions", new string('1', 32), "state", "workflow.json");
        string output = Path.Combine(root, "conversion"); string candidate = Path.Combine(output, "candidate");
        Directory.CreateDirectory(Path.GetDirectoryName(workflowPath)!); Directory.CreateDirectory(candidate);
        string wav = Path.Combine(candidate, "final.wav"), source = Path.Combine(root, "source.wav");
        byte[] audio = GenerationRunnerTests.Wave(4800); await File.WriteAllBytesAsync(wav, audio, TestContext.Current.CancellationToken); await File.WriteAllBytesAsync(source, audio, TestContext.Current.CancellationToken);
        string audioHash = Hash(audio), requestHash = new string('c', 64);
        var checkpoint = new { root = Path.Combine(root, "training", "actions", new string('1', 32), "point"), sha256 = new string('d', 64) };
        string stateHash = await Write(workflowPath, new { accepted = new { checkpoint }, run_id = new string('2', 32), global_step = 125, completed_epoch = 1, epoch_size = 125 });
        var binding = new SavedVoiceBinding(new string('3', 32), Path.GetRelativePath(root, workflowPath).Replace('\\', '/'), stateHash,
            new string('4', 32), "voice-recording/" + new string('4', 32) + ".wav", new string('a', 64), 125, 1, 125, "target_epoch", "rvc-saved-voice-cpu-v1");
        string manifestHash = await Write(Path.Combine(candidate, "manifest.json"), new { format = 1, purpose = "saved-voice-audio-conversion-candidate", request_sha256 = requestHash,
            workflow = new { path = workflowPath, sha256 = mode == "wrong-model" ? new string('f', 64) : stateHash }, source = new { path = source, sha256 = audioHash },
            profile_version = "rvc-saved-voice-cpu-v1", sample_rate = 48000, sample_count = 4800,
            selected_point = new { checkpoint, run_id = new string('2', 32), global_step = 125, completed_epoch = 1, epoch_size = 125 },
            inference_seed = 1234, central_seconds = 3, context_seconds = 1, overlap_seconds = .1, unvoiced_f0 = "preserved-zero",
            peak_normalization = false, source_mixing = false, quality_established = false, auto_activate_voice = false,
            files = new Dictionary<string, object> { ["final.wav"] = new { bytes = audio.Length, sha256 = audioHash } } });
        await File.WriteAllTextAsync(Path.Combine(candidate, "READY"), manifestHash + "\n", TestContext.Current.CancellationToken);
        if (mode == "tampered-audio") await File.WriteAllBytesAsync(wav, [1, 2, 3], TestContext.Current.CancellationToken);
        var execution = new SavedVoiceConversionExecution(root, output, binding, source, audioHash);
        if (mode == "valid")
        {
            var result = await RvcSavedVoiceConverter.ValidateCandidateAsync(execution, requestHash, candidate, manifestHash, TestContext.Current.CancellationToken);
            Assert.Equal(wav, result.Path); Assert.Equal(audioHash, result.Sha256); Assert.Equal(4800, result.SampleCount);
        }
        else Assert.Equal("worker.output.invalid", (await Assert.ThrowsAsync<WorkerClientException>(() =>
            RvcSavedVoiceConverter.ValidateCandidateAsync(execution, requestHash, candidate, manifestHash, TestContext.Current.CancellationToken))).Code);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task<string> Write(string path, object value)
    { byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value); await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken); return Hash(bytes); }
}

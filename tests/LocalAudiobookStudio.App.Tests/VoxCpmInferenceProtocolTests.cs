using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.App.VoxCpmInference;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Core.Jobs;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmInferenceProtocolTests
{
    [Fact]
    public async Task Request_preserves_text_and_binds_accepted_adapter_not_a_prompt()
    {
        using var f = new VoxInferenceFixture(); using var pins = new VoxCpmTrainingFilePins();
        var call = await f.Create(pins, "  Привет 😀\n"); using var doc = JsonDocument.Parse(call.RequestBytes);
        var r = doc.RootElement;
        Assert.Equal(12, r.EnumerateObject().Count()); Assert.Equal("  Привет 😀\n", r.GetProperty("text").GetString());
        Assert.Equal(f.Execution.Voice.Checkpoint.Id, r.GetProperty("binding").GetProperty("checkpointId").GetString());
        Assert.False(r.TryGetProperty("prompt", out _));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("  Привет 😀\n"))), r.GetProperty("textSha256").GetString());
    }
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a\0b")]
    public async Task Invalid_text_is_rejected_before_launch(string text)
    {
        using var f = new VoxInferenceFixture(); using var pins = new VoxCpmTrainingFilePins();
        await Assert.ThrowsAsync<WorkerClientException>(() => f.Create(pins, text));
    }
    [Fact]
    public async Task Unpaired_surrogate_is_rejected_without_replacement()
    {
        using var f = new VoxInferenceFixture(); using var pins = new VoxCpmTrainingFilePins();
        await Assert.ThrowsAsync<WorkerClientException>(() => f.Create(pins, new string((char)0xd800, 1)));
    }
    [Fact]
    public async Task Candidate_requires_real_wave_and_bound_completion_evidence()
    {
        using var f = new VoxInferenceFixture(); using var pins = new VoxCpmTrainingFilePins();
        var call = await f.Create(pins); f.Publish(call);
        var audio = await VoxCpmInferenceProtocol.AcceptAsync(call, TestContext.Current.CancellationToken);
        Assert.Equal(100, audio.SampleCount); Assert.Equal(244, audio.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(audio.Path))), audio.Sha256);
    }
    [Theory]
    [InlineData("request")][InlineData("binding")][InlineData("text")][InlineData("characters")]
    [InlineData("cache")][InlineData("capacity")][InlineData("limit")][InlineData("patches")]
    [InlineData("eos")][InlineData("samples")][InlineData("rate")][InlineData("peak")]
    [InlineData("float-counter")][InlineData("extra-field")][InlineData("extra-file")]
    [InlineData("ready")][InlineData("hash")][InlineData("silent")][InlineData("clipped")]
    public async Task Forged_candidate_never_becomes_audio(string fault)
    {
        using var f = new VoxInferenceFixture(); using var pins = new VoxCpmTrainingFilePins();
        var call = await f.Create(pins); f.Publish(call, fault);
        await Assert.ThrowsAsync<WorkerClientException>(() => VoxCpmInferenceProtocol.AcceptAsync(call, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task One_unicode_scalar_and_eos_on_exact_limit_are_valid()
    {
        using var f = new VoxInferenceFixture(); using var pins = new VoxCpmTrainingFilePins();
        var call = await f.Create(pins, "😀"); f.Publish(call, "boundary");
        Assert.Equal(100, (await VoxCpmInferenceProtocol.AcceptAsync(call, TestContext.Current.CancellationToken)).SampleCount);
    }
}

internal sealed class VoxInferenceFixture : IDisposable
{
    internal VoxProtocolFixture Training { get; }
    internal VoxCpmInferenceExecution Execution { get; private set; }
    internal string Scripts => Path.Combine(VoxCpmTrainingWorkerPinTests.Repository(), "src", "VoxCpmInferenceWorker");
    private readonly bool longReference;
    internal VoxInferenceFixture(bool longPath = false)
    {
        Training = new(local: true); longReference = longPath;
        Training.PrepareNative(); var e = Training.Execution; var c = e.ParentCheckpoint!;
        var b = new VoxCpmSavedVoiceBinding(c.Id, c.TrainingId, c.ManifestRelativePath, c.ManifestSha256,
            e.Corpus.VersionId, e.Corpus.ManifestSha256, e.Corpus.BindingSha256, e.Corpus.Source.Id,
            e.Corpus.Source.Sha256.ToLowerInvariant(), VoxCpmInferenceProfile.IdentityHash(e.Identity),
            c.Cursor.GlobalStep, c.Cursor.CompletedEpochs, c.Cursor.EpochSize, VoxCpmInferenceScripts.ImplementationSha256, VoxCpmInferenceProfile.Id);
        string job = new('1', 32), attempt = new('2', 32);
        Execution = new(e.ProjectRoot, Path.Combine(e.ProjectRoot, "segments", job, attempt + "-vox"), job, new('3', 32), attempt,
            "Это новая фраза.", new(b, c, e.Corpus, e.Identity));
    }
    internal string UseReference(string? fault = null)
    {
        var voice = Execution.Voice;
        string relative = "tts-corpus/actions/" + new string('a',32) + "/reference/" + new string('b',32) + ".wav";
        if (longReference) relative = "tts-corpus/actions/"+new string('a',32)+"/"+new string('x',70)+"/"+new string('y',70)+"/"+new string('b',32)+".wav";
        string path = VoxCpmTrainingFilePins.Resolve(Execution.ProjectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] wav = Wave(1000, 16000, 201680);
        if (fault == "rate") BitConverter.GetBytes(24000).CopyTo(wav,24);
        if (fault == "channels") BitConverter.GetBytes((ushort)2).CopyTo(wav,22);
        if (fault == "frames") Array.Resize(ref wav,wav.Length-2);
        File.WriteAllBytes(path,wav);
        var clip = voice.Corpus.Clips[0] with { AudioRelativePath = relative, StartSample = 0, EndSample = 201680,
            AudioSha256 = Convert.ToHexStringLower(SHA256.HashData(wav)) };
        var reference = new VoxCpmCorpusReference(clip.Id,clip.TrainingOrdinal,relative,clip.AudioSha256,201680);
        var corpus = voice.Corpus with { Clips = [clip, voice.Corpus.Clips[1]] };
        if (fault == "member") clip = clip with { AudioSha256 = new('0',64) };
        var binding = voice.Binding with { ProfileVersion = fault == "profile" ? VoxCpmInferenceProfile.Id : VoxCpmInferenceProfile.ReferenceId, Reference = reference };
        Execution = Execution with { Voice = new(binding,voice.Checkpoint,corpus,voice.Identity,referenceClip:clip) };
        voice.Dispose(); return path;
    }
    internal Task<VoxCpmInferenceInvocation> Create(VoxCpmTrainingFilePins pins, string? text = null)
    {
        if (text != null) Execution = Execution with { Text = text };
        return VoxCpmInferenceProtocol.CreateAsync(Execution, Training.Options, Training.Runtime, pins,
            VoxCpmInferenceScripts.ImplementationSha256, TestContext.Current.CancellationToken);
    }
    internal string? UseOptions(VoxCpmGenerationOptions options)
    {
        string? path = options.UseReference ? UseReference() : null;
        var voice = Execution.Voice;
        var binding = voice.Binding with { ProfileVersion = VoxCpmInferenceProfile.ControlsId, Options = options,
            Reference = options.UseReference ? voice.Binding.Reference : null };
        Execution = Execution with { Voice = new(binding,voice.Checkpoint,voice.Corpus,voice.Identity,
            referenceClip: options.UseReference ? voice.ReferenceClip : null) };
        voice.Dispose(); return path;
    }
    internal void Publish(VoxCpmInferenceInvocation call, string? fault = null)
    {
        using var request = JsonDocument.Parse(call.RequestBytes); var r = request.RootElement;
        short value = fault == "silent" ? (short)0 : fault == "clipped" ? short.MaxValue : (short)4096;
        byte[] raw = Wave(value); bool controlled = r.GetProperty("format").GetInt32() == 3;
        int finalSamples = controlled ? (int)decimal.Round(10000m/r.GetProperty("controls").GetProperty("tempoPercent").GetInt32(),0,MidpointRounding.ToEven) : 100;
        byte[] wav = controlled ? Wave(value,48000,finalSamples) : raw;
        string output = Path.Combine(Execution.OutputRoot, "candidate"); Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "final.wav"), wav);
        var evidence = JsonSerializer.SerializeToNode(new {
            profileId = VoxCpmInferenceProfile.Id, seed = 42, binding = r.GetProperty("binding"),
            textSha256 = r.GetProperty("textSha256").GetString(), tokensSha256 = new string('a',64),
            textCharacters = Execution.Text.EnumerateRunes().Count(), tokenCount = 5, cacheCapacity = 46,
            cacheBytes = 3391488, effectivePatchLimit = 40, generatedPatches = 4, naturalEos = true,
            sampleRate = 48000, samples = 100, peak = 0.125 })!;
        if (r.TryGetProperty("reference",out var reference) && reference.ValueKind == JsonValueKind.Object)
        {
            int patches = (reference.GetProperty("samples").GetInt32()+2559)/2560, prefix = patches+2;
            evidence["profileId"] = VoxCpmInferenceProfile.ReferenceId;
            evidence["reference"] = JsonNode.Parse(reference.GetRawText());
            evidence["referencePatches"] = patches; evidence["referencePrefixSlots"] = prefix;
            evidence["cacheCapacity"] = 46+prefix; evidence["cacheBytes"] = 73728*(46+prefix);
        }
        if (controlled)
        {
            var controls = r.GetProperty("controls"); int tempo = controls.GetProperty("tempoPercent").GetInt32();
            evidence["profileId"] = VoxCpmInferenceProfile.ControlsId; evidence["seed"] = controls.GetProperty("seed").GetInt64();
            evidence["controls"] = JsonNode.Parse(controls.GetRawText());
            evidence["tempo"] = JsonSerializer.SerializeToNode(new { algorithm = tempo == 100 ? "identity-v1" : "librosa-phase-vocoder-v1",
                percent = tempo, rawSamples = 100, samples = finalSamples, peak = 0.125 });
            File.WriteAllBytes(Path.Combine(output,"raw.wav"),raw);
        }
        var result = JsonSerializer.SerializeToNode(new {
            format = controlled ? 2 : 1, call.RequestSha256, projectId = Execution.Voice.Corpus.ProjectId, Execution.JobId,
            Execution.SegmentId, Execution.AttemptId, evidence,
            audio = new { relativePath = "candidate/final.wav", bytes = wav.Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(wav)), samples = finalSamples }
        }, VoxCpmTrainingJson.Options)!;
        if (controlled) result["sourceAudio"] = JsonSerializer.SerializeToNode(new { relativePath="candidate/raw.wav",bytes=raw.Length,
            sha256=Convert.ToHexStringLower(SHA256.HashData(raw)),samples=100 });
        switch (fault)
        {
            case "request": result["requestSha256"] = new string('0',64); break;
            case "binding": result["evidence"]!["binding"]!["globalStep"] = 9; break;
            case "text": result["evidence"]!["textSha256"] = new string('0',64); break;
            case "characters": result["evidence"]!["textCharacters"] = 99; break;
            case "cache": result["evidence"]!["cacheBytes"] = 1; break;
            case "capacity": result["evidence"]!["cacheCapacity"] = 45; break;
            case "limit": result["evidence"]!["effectivePatchLimit"] = 39; break;
            case "patches": result["evidence"]!["generatedPatches"] = 41; break;
            case "eos": result["evidence"]!["naturalEos"] = false; break;
            case "samples": result["evidence"]!["samples"] = 101; break;
            case "rate": result["evidence"]!["sampleRate"] = 24000; break;
            case "peak": result["evidence"]!["peak"] = 0.5; break;
            case "float-counter": result["evidence"]!["tokenCount"] = JsonNode.Parse("5.0"); break;
            case "extra-field": result["extra"] = 1; break;
            case "extra-file": File.WriteAllText(Path.Combine(output,"other"),"x"); break;
            case "hash": result["audio"]!["sha256"] = new string('0',64); break;
            case "boundary": result["evidence"]!["generatedPatches"] = 40; break;
            case "reference-sha": result["evidence"]!["reference"]!["sha256"] = new string('0',64); break;
            case "reference-id": result["evidence"]!["reference"]!["clipId"] = new string('0',32); break;
            case "reference-extra": result["evidence"]!["reference"]!["other"] = 1; break;
            case "reference-patches": result["evidence"]!["referencePatches"] = 78; break;
            case "reference-prefix": result["evidence"]!["referencePrefixSlots"] = 80; break;
            case "reference-profile": result["evidence"]!["profileId"] = VoxCpmInferenceProfile.Id; break;
            case "reference-missing": result["evidence"]!.AsObject().Remove("reference"); break;
            case "controls-echo": result["evidence"]!["controls"]!["seed"] = 43; break;
            case "controls-extra": result["evidence"]!["controls"]!["other"] = 1; break;
            case "tempo-rate": result["evidence"]!["tempo"]!["percent"] = 101; break;
            case "tempo-algorithm": result["evidence"]!["tempo"]!["algorithm"] = "other"; break;
            case "tempo-samples": result["evidence"]!["tempo"]!["samples"] = finalSamples+1; break;
            case "raw-hash": result["sourceAudio"]!["sha256"] = new string('0',64); break;
            case "raw-samples": result["sourceAudio"]!["samples"] = 99; break;
            case "raw-extra": result["sourceAudio"]!["other"] = 1; break;
            case "raw-missing": File.Delete(Path.Combine(output,"raw.wav")); break;
            case "identity-mismatch":
                byte[] other = Wave(2048,48000,finalSamples); File.WriteAllBytes(Path.Combine(output,"final.wav"),other);
                result["audio"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(other)); result["evidence"]!["tempo"]!["peak"] = 0.0625; break;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(result.ToJsonString()); File.WriteAllBytes(Path.Combine(output,"manifest.json"), bytes);
        File.WriteAllText(Path.Combine(output,"READY"), (fault == "ready" ? new string('0',64) : Convert.ToHexStringLower(SHA256.HashData(bytes))) + "\n");
    }
    private static byte[] Wave(short value, int rate = 48000, int samples = 100)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write("RIFF"u8); writer.Write(36+2*samples); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(rate); writer.Write(2*rate);
        writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(2*samples);
        for (int i = 0; i < samples; i++) writer.Write(i % 2 == 0 ? value : (short)-value);
        return bytes.ToArray();
    }
    public void Dispose() { Execution.Voice.Dispose(); Training.Dispose(); }
}

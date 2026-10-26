using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Audio;
using static LocalAudiobookStudio.App.VoxCpmTraining.VoxCpmTrainingJson;

namespace LocalAudiobookStudio.App.VoxCpmInference;

internal sealed record VoxCpmInferenceInvocation(VoxCpmInferenceExecution Execution, byte[] RequestBytes,
    string RequestSha256, string OutputRelativeRoot);

/// <summary>Accepts audio only from the exact immutable request and independently inspected PCM.</summary>
internal static class VoxCpmInferenceProtocol
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string Digest(byte[] bytes) => VoxCpmTrainingProtocol.Digest(bytes);
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    private static void Equal<T>(JsonElement actual, T expected) => Require(Canonical(actual).AsSpan().SequenceEqual(Canonical(Element(expected))));
    private static void Id(string id) => Require(id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    internal static WorkerClientException Failure(Exception? error = null) => new(
        error is OperationCanceledException ? "worker.canceled" : "worker.output.invalid", true);
    internal static async Task<VoxCpmInferenceInvocation> CreateAsync(VoxCpmInferenceExecution e, VoxCpmTrainingRuntimeOptions options,
        VoxCpmTrainingRuntimeLease runtime, VoxCpmTrainingFilePins pins, string implementation, CancellationToken token)
    {
        try
        {
            foreach (string id in new[] { e.JobId, e.SegmentId, e.AttemptId, e.Voice.Corpus.ProjectId }) Id(id);
            byte[] text = Utf8.GetBytes(e.Text); int characters = e.Text.EnumerateRunes().Count();
            Require(!string.IsNullOrWhiteSpace(e.Text) && characters is >= 1 and <= 512 && text.Length <= 2048
                && !e.Text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')));
            string output = $"segments/{e.JobId}/{e.AttemptId}-vox";
            Require(string.Equals(VoxCpmTrainingFilePins.Resolve(e.ProjectRoot, output), VoxCpmTrainingFilePins.Absolute(e.OutputRoot), StringComparison.OrdinalIgnoreCase));
            var v = e.Voice; var b = v.Binding; var c = v.Checkpoint; var corpus = v.Corpus;
            Require(v.Identity == runtime.Identity && b.InferenceImplementationSha256 == implementation && IsHash(implementation)
                && b.RuntimeIdentitySha256 == VoxCpmInferenceProfile.IdentityHash(runtime.Identity)
                && b.ModelId == c.Id && b.TrainingId == c.TrainingId && b.ManifestRelativePath == c.ManifestRelativePath
                && b.ManifestSha256 == c.ManifestSha256 && b.GlobalStep == c.Cursor.GlobalStep && b.CompletedEpochs == c.Cursor.CompletedEpochs
                && b.EpochSize == c.Cursor.EpochSize && b.CorpusVersionId == corpus.VersionId && b.CorpusManifestSha256 == corpus.ManifestSha256
                && b.CorpusBindingSha256 == corpus.BindingSha256 && b.SourceRecordingId == corpus.Source.Id && b.SourceSha256 == corpus.Source.Sha256.ToLowerInvariant());
            await VoxCpmTrainingProtocol.PinHeadAsync(pins, e.ProjectRoot, c, corpus, runtime.Identity, token);
            var reference = await VoxCpmInferenceReferenceProtocol.PinAsync(e,pins,token);
            using var manifest = Parse(await Bytes(await pins.ReadAsync(VoxCpmTrainingFilePins.Resolve(e.ProjectRoot, c.ManifestRelativePath), 65536, c.ManifestSha256, token), token));
            var adapter = manifest.RootElement.GetProperty("files").GetProperty("lora_weights.safetensors");
            string adapterSha = Hash(adapter.GetProperty("sha256")); long adapterBytes = Integer(adapter.GetProperty("bytes"), 1, 8 * 1048576);
            var identity = runtime.Identity;
            var request = new {
                format = 1, projectId = corpus.ProjectId, e.JobId, e.SegmentId, e.AttemptId, e.ProjectRoot,
                outputRelativeRoot = output, text = e.Text, textSha256 = Digest(text),
                binding = new { projectId = corpus.ProjectId, checkpointId = c.Id, globalStep = c.Cursor.GlobalStep,
                    manifestSha256 = c.ManifestSha256, adapterSha256 = adapterSha, runtimeFingerprint = identity.Fingerprint,
                    identity.RecipeSha256, identity.AdapterLayoutSha256 },
                runtime = new { options.DependencySites, options.SourceRoot, sourceFiles = Assets(runtime.SourceFiles),
                    options.ModelRoot, modelFiles = Assets(runtime.ModelFiles), options.RuntimeInventoryPath, runtime.RuntimeInventorySha256,
                    cache = new { path = options.CachePath, bytes = runtime.CacheBytes, sha256 = identity.CacheSha256, layoutSha256 = identity.CacheLayoutSha256 },
                    identity, parameterLayout = runtime.AdapterLayout },
                adapter = new { relativePath = c.ManifestRelativePath[..^"manifest.json".Length] + "lora_weights.safetensors", bytes = adapterBytes, sha256 = adapterSha }
            };
            JsonElement requestElement = Element(request);
            if (b.Options is { } controls)
                requestElement = VoxCpmInferenceControlsProtocol.Request(requestElement,reference,controls);
            else if (reference.HasValue)
            {
                var fields = requestElement.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value,StringComparer.Ordinal);
                fields["format"] = Element(2); fields["profileId"] = Element(b.ProfileVersion); fields["reference"] = reference.Value;
                requestElement = Element(fields);
            }
            byte[] bytes = Canonical(requestElement); Require(bytes.Length <= 262144);
            return new(e, bytes, Digest(bytes), output);
        }
        catch (WorkerClientException) { throw; }
        catch (Exception error) { throw Failure(error); }
    }
    private static Dictionary<string, object> Assets(IReadOnlyList<VoxCpmTrainingAssetFile> files)
        => files.ToDictionary(f => f.Filename, f => (object)new { bytes = f.Bytes, sha256 = f.Sha256 }, StringComparer.Ordinal);
    internal static async Task<VoxCpmInferenceCandidate> AcceptAsync(VoxCpmInferenceInvocation call, CancellationToken token)
    {
        try
        {
            if (call.Execution.Voice.Binding.ProfileVersion == VoxCpmInferenceProfile.ControlsId)
                return await VoxCpmInferenceControlsProtocol.AcceptAsync(call,token);
            using var pins = new VoxCpmTrainingFilePins(); var e = call.Execution;
            string root = Path.Combine(e.OutputRoot, "candidate"); pins.PinDirectory(root);
            string[] expected = ["final.wav", "manifest.json", "READY"];
            Require(expected.ToHashSet(StringComparer.Ordinal).SetEquals(Directory.EnumerateFileSystemEntries(root).Select(p=>Path.GetFileName(p)!)));
            byte[] bytes = await Bytes(await pins.ReadAsync(Path.Combine(root,"manifest.json"),16384,null,token),token);
            byte[] ready = await Bytes(await pins.ReadAsync(Path.Combine(root,"READY"),65,null,token),token);
            Require(ready.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(Digest(bytes)+"\n")));
            using var doc = Parse(bytes); using var request = Parse(call.RequestBytes); var r = doc.RootElement; var q = request.RootElement;
            Fields(r,"format","requestSha256","projectId","jobId","segmentId","attemptId","evidence","audio");
            Integer(r.GetProperty("format"),1,1); Equal(r.GetProperty("requestSha256"),call.RequestSha256);
            foreach (string key in new[] { "projectId","jobId","segmentId","attemptId" }) Equal(r.GetProperty(key),q.GetProperty(key));
            var proof = r.GetProperty("evidence");
            bool referenced = e.Voice.Binding.ProfileVersion == VoxCpmInferenceProfile.ReferenceId;
            string[] proofFields = ["profileId","seed","binding","textSha256","tokensSha256","textCharacters","tokenCount","cacheCapacity","cacheBytes","effectivePatchLimit","generatedPatches","naturalEos","sampleRate","samples","peak"];
            Fields(proof,referenced ? [..proofFields,"reference","referencePatches","referencePrefixSlots"] : proofFields);
            Equal(proof.GetProperty("profileId"),e.Voice.Binding.ProfileVersion); Integer(proof.GetProperty("seed"),42,42);
            Equal(proof.GetProperty("binding"),q.GetProperty("binding")); Equal(proof.GetProperty("textSha256"),q.GetProperty("textSha256"));
            Integer(proof.GetProperty("textCharacters"),e.Text.EnumerateRunes().Count(),e.Text.EnumerateRunes().Count()); Hash(proof.GetProperty("tokensSha256"));
            long prefix = 0;
            if (referenced)
            {
                Equal(proof.GetProperty("reference"),q.GetProperty("reference"));
                long referenceSamples = e.Voice.Binding.Reference!.SampleCount, referencePatches = (referenceSamples+2559)/2560;
                prefix = referencePatches+2;
                Integer(proof.GetProperty("referencePatches"),referencePatches,referencePatches);
                Integer(proof.GetProperty("referencePrefixSlots"),prefix,prefix);
            }
            long tokens = Integer(proof.GetProperty("tokenCount"),1,256), limit = Math.Min(256,6*tokens+10), capacity = tokens+1+prefix+limit;
            Integer(proof.GetProperty("cacheCapacity"),capacity,capacity); Integer(proof.GetProperty("cacheBytes"),73728*capacity,73728*capacity);
            Integer(proof.GetProperty("effectivePatchLimit"),limit,limit); long patches = Integer(proof.GetProperty("generatedPatches"),4,limit);
            Require(proof.GetProperty("naturalEos").ValueKind == JsonValueKind.True); Integer(proof.GetProperty("sampleRate"),48000,48000);
            long samples = Integer(proof.GetProperty("samples"),1,7680*patches);
            var peakValue = proof.GetProperty("peak"); Require(peakValue.ValueKind == JsonValueKind.Number && peakValue.TryGetDouble(out double peak) && double.IsFinite(peak) && peak > 0 && peak <= 1);
            var audio = r.GetProperty("audio"); Fields(audio,"relativePath","bytes","sha256","samples");
            Equal(audio.GetProperty("relativePath"),"candidate/final.wav"); Integer(audio.GetProperty("samples"),samples,samples);
            long length = Integer(audio.GetProperty("bytes"),44+2*samples,44+2*samples); Require(length <= 3932204);
            string sha = Hash(audio.GetProperty("sha256")), path = Path.Combine(root,"final.wav");
            var file = await pins.ReadAsync(path,length,sha,token); Require(file.Length == length);
            var wave = await WavInspector.InspectAsync(path,token);
            Require(wave.DataOffset == 44 && wave.SampleCount == samples && !wave.IsClipped && wave.Peak > 0
                && Math.Abs(wave.Peak - peakValue.GetDouble()) <= 1e-4 && wave.Sha256.ToLowerInvariant() == sha);
            Require(expected.ToHashSet(StringComparer.Ordinal).SetEquals(Directory.EnumerateFileSystemEntries(root).Select(p=>Path.GetFileName(p)!)));
            return new(path,sha,length,samples);
        }
        catch (WorkerClientException) { throw; }
        catch (Exception error) { throw Failure(error); }
    }
}

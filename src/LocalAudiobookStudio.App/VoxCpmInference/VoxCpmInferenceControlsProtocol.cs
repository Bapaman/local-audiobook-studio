using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmInference;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Core.Jobs;
using static LocalAudiobookStudio.App.VoxCpmTraining.VoxCpmTrainingJson;

namespace LocalAudiobookStudio.App.VoxCpmInference;

/// <summary>Independent acceptance of raw model output and explicitly requested export tempo.</summary>
internal static class VoxCpmInferenceControlsProtocol
{
    private static string Digest(byte[] bytes) => VoxCpmTrainingProtocol.Digest(bytes);
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value,Options);
    private static void Equal<T>(JsonElement actual,T expected)
        => Require(Canonical(actual).AsSpan().SequenceEqual(Canonical(Element(expected))));

    internal static JsonElement Request(JsonElement original,JsonElement? reference,VoxCpmGenerationOptions controls)
    {
        Require(controls.IsValid() && controls.UseReference == reference.HasValue);
        var fields = original.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value,StringComparer.Ordinal);
        fields["format"] = Element(3); fields["profileId"] = Element(VoxCpmInferenceProfile.ControlsId);
        fields["reference"] = reference ?? Element<object?>(null); fields["controls"] = Element(controls);
        return Element(fields);
    }

    internal static async Task<VoxCpmInferenceCandidate> AcceptAsync(VoxCpmInferenceInvocation call,CancellationToken token)
    {
        using var pins = new VoxCpmTrainingFilePins(); var execution = call.Execution;
        var controls = execution.Voice.Binding.Options!; Require(controls != null && controls.IsValid());
        string root = Path.Combine(execution.OutputRoot,"candidate"); pins.PinDirectory(root);
        string[] files = ["final.wav","raw.wav","manifest.json","READY"];
        void Closed() => Require(files.ToHashSet(StringComparer.Ordinal).SetEquals(Directory.EnumerateFileSystemEntries(root).Select(p=>Path.GetFileName(p)!)));
        Closed();
        byte[] bytes = await Bytes(await pins.ReadAsync(Path.Combine(root,"manifest.json"),16384,null,token),token);
        byte[] ready = await Bytes(await pins.ReadAsync(Path.Combine(root,"READY"),65,null,token),token);
        Require(ready.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(Digest(bytes)+"\n")));
        using var manifest = Parse(bytes); using var request = Parse(call.RequestBytes);
        var m = manifest.RootElement; var q = request.RootElement;
        Fields(m,"format","requestSha256","projectId","jobId","segmentId","attemptId","evidence","audio","sourceAudio");
        Integer(m.GetProperty("format"),2,2); Equal(m.GetProperty("requestSha256"),call.RequestSha256);
        Integer(q.GetProperty("format"),3,3); Equal(q.GetProperty("profileId"),VoxCpmInferenceProfile.ControlsId);
        Equal(q.GetProperty("controls"),controls);
        foreach (string key in new[] {"projectId","jobId","segmentId","attemptId"}) Equal(m.GetProperty(key),q.GetProperty(key));
        var proof = m.GetProperty("evidence");
        string[] fields = ["profileId","seed","binding","textSha256","tokensSha256","textCharacters","tokenCount","cacheCapacity","cacheBytes","effectivePatchLimit","generatedPatches","naturalEos","sampleRate","samples","peak","controls","tempo"];
        Fields(proof,controls.UseReference ? [..fields,"reference","referencePatches","referencePrefixSlots"] : fields);
        Equal(proof.GetProperty("profileId"),VoxCpmInferenceProfile.ControlsId); Equal(proof.GetProperty("controls"),controls);
        Integer(proof.GetProperty("seed"),controls.Seed,controls.Seed);
        Equal(proof.GetProperty("binding"),q.GetProperty("binding")); Equal(proof.GetProperty("textSha256"),q.GetProperty("textSha256"));
        Integer(proof.GetProperty("textCharacters"),execution.Text.EnumerateRunes().Count(),execution.Text.EnumerateRunes().Count());
        Hash(proof.GetProperty("tokensSha256")); long prefix = 0;
        if (controls.UseReference)
        {
            Equal(proof.GetProperty("reference"),q.GetProperty("reference"));
            long patches = (execution.Voice.Binding.Reference!.SampleCount+2559)/2560; prefix = patches+2;
            Integer(proof.GetProperty("referencePatches"),patches,patches);
            Integer(proof.GetProperty("referencePrefixSlots"),prefix,prefix);
        }
        else Require(q.GetProperty("reference").ValueKind == JsonValueKind.Null);
        long tokens = Integer(proof.GetProperty("tokenCount"),1,256),limit = Math.Min(256,6*tokens+10),capacity = tokens+1+prefix+limit;
        Integer(proof.GetProperty("cacheCapacity"),capacity,capacity); Integer(proof.GetProperty("cacheBytes"),73728*capacity,73728*capacity);
        Integer(proof.GetProperty("effectivePatchLimit"),limit,limit);
        long generated = Integer(proof.GetProperty("generatedPatches"),4,limit);
        Require(proof.GetProperty("naturalEos").ValueKind == JsonValueKind.True); Integer(proof.GetProperty("sampleRate"),48000,48000);
        long rawSamples = Integer(proof.GetProperty("samples"),1,7680*generated);
        double rawPeak = Peak(proof.GetProperty("peak"));
        var tempo = proof.GetProperty("tempo"); Fields(tempo,"algorithm","percent","rawSamples","samples","peak");
        Integer(tempo.GetProperty("percent"),controls.TempoPercent,controls.TempoPercent);
        Equal(tempo.GetProperty("algorithm"),controls.TempoPercent == 100 ? "identity-v1" : "librosa-phase-vocoder-v1");
        Integer(tempo.GetProperty("rawSamples"),rawSamples,rawSamples);
        long samples = checked((long)decimal.Round(rawSamples*100m/controls.TempoPercent,0,MidpointRounding.ToEven));
        Integer(tempo.GetProperty("samples"),samples,samples); Require(samples is > 0 and <= 2457600);
        var source = await Wave(pins,root,m.GetProperty("sourceAudio"),"raw.wav",rawSamples,rawPeak,3932204,token);
        var final = await Wave(pins,root,m.GetProperty("audio"),"final.wav",samples,Peak(tempo.GetProperty("peak")),4915244,token);
        if (controls.TempoPercent == 100) Require(source.Sha256 == final.Sha256);
        Closed(); return final;
    }

    private static double Peak(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double peak) && double.IsFinite(peak) && peak > 0 && peak <= 1);
        return value.GetDouble();
    }

    private static async Task<VoxCpmInferenceCandidate> Wave(VoxCpmTrainingFilePins pins,string root,JsonElement metadata,string name,
        long samples,double peak,long maximum,CancellationToken token)
    {
        Fields(metadata,"relativePath","bytes","sha256","samples"); Equal(metadata.GetProperty("relativePath"),"candidate/"+name);
        Integer(metadata.GetProperty("samples"),samples,samples); long length = Integer(metadata.GetProperty("bytes"),44+2*samples,44+2*samples);
        Require(length <= maximum); string sha = Hash(metadata.GetProperty("sha256")),path = Path.Combine(root,name);
        var stream = await pins.ReadAsync(path,length,sha,token); Require(stream.Length == length);
        var wave = await WavInspector.InspectAsync(path,token);
        Require(wave.DataOffset == 44 && wave.SampleCount == samples && !wave.IsClipped && wave.Peak > 0
            && Math.Abs(wave.Peak-peak) <= 1e-4 && wave.Sha256.ToLowerInvariant() == sha);
        return new(path,sha,length,samples);
    }
}

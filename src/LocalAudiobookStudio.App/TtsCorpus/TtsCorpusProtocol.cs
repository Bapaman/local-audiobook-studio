using LocalAudiobookStudio.Application.TtsCorpus;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static LocalAudiobookStudio.App.TtsCorpus.TtsCorpusJson;

namespace LocalAudiobookStudio.App.TtsCorpus;

internal sealed record TtsCorpusModelFile(string Filename, long Bytes, string Sha256);
internal sealed record TtsCorpusInvocation(TtsCorpusExecution Execution, string WindowId, string RequestId,
    string OutputRelativeRoot, byte[] RequestBytes, string RequestSha256);

internal static class TtsCorpusProtocol
{
    internal static TtsCorpusInvocation Create(TtsCorpusExecution execution, TtsCorpusRuntimeOptions options,
        IReadOnlyList<TtsCorpusModelFile> modelFiles, string windowId, string requestId)
    {
        foreach (string id in new[] { execution.ProjectId, execution.CorpusId, execution.ActionId, execution.ExecutionId, execution.SourceRecordingId, windowId, requestId }) Require(IsId(id));
        Require(IsHash(execution.SourceSha256) && IsHash(execution.RuntimeFingerprint) && execution.SourceSampleCount is > 0 and <= 393216000);
        string project = TtsCorpusFilePins.Absolute(execution.ProjectRoot);
        string executionRoot = $"tts-corpus/actions/{execution.ActionId}/executions/{execution.ExecutionId}";
        Require(string.Equals(TtsCorpusFilePins.Resolve(project, executionRoot), TtsCorpusFilePins.Absolute(execution.OutputRoot), StringComparison.OrdinalIgnoreCase));
        string source = Path.GetRelativePath(project, execution.SourcePath).Replace('\\', '/');
        Require(!source.StartsWith("tts-corpus/", StringComparison.OrdinalIgnoreCase));
        Require(string.Equals(TtsCorpusFilePins.Resolve(project, source), TtsCorpusFilePins.Absolute(execution.SourcePath), StringComparison.OrdinalIgnoreCase));
        if (execution.PreparedPlan == null) Require(execution.NextClipIndex == 0);
        else
        {
            _ = ReadPlan(execution, Element(execution.PreparedPlan));
            Require(execution.NextClipIndex >= 0 && execution.NextClipIndex < execution.PreparedPlan.Clips.Count);
        }
        var requested = execution.PreparedPlan?.Clips.Skip(execution.NextClipIndex).Take(8).Select(c => c.Id).ToArray() ?? [];
        string output = executionRoot + "/windows/" + windowId;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, requestId, execution.ProjectId, execution.CorpusId, execution.ActionId, execution.ExecutionId, windowId,
            projectRoot = project, outputRelativeRoot = output, sourceRelativePath = source, execution.SourceRecordingId,
            execution.SourceSha256, execution.SourceSampleCount, execution.RuntimeFingerprint, execution.PreparedPlan,
            execution.NextClipIndex, requestedClipIds = requested, options.DependencySites, options.ModelRoot, modelFiles
        }, Options);
        Require(bytes.Length <= 1048576);
        return new(execution, windowId, requestId, output, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    internal static async Task<TtsCorpusWindowResult> AcceptAsync(TtsCorpusInvocation invocation, CancellationToken token)
    {
        try
        {
            using var pins = new TtsCorpusFilePins(); var execution = invocation.Execution;
            byte[] ready = await Read(pins, execution.ProjectRoot, invocation.OutputRelativeRoot + "/READY", 65, null, token);
            Require(ready.Length == 65 && ready[64] == 10);
            string sha = Encoding.ASCII.GetString(ready, 0, 64); Require(IsHash(sha));
            using var document = Parse(await Read(pins, execution.ProjectRoot, invocation.OutputRelativeRoot + "/result.json", 8388608, sha, token));
            var result = document.RootElement;
            Fields(result, "schemaVersion", "requestId", "projectId", "corpusId", "actionId", "executionId", "windowId", "sourceRecordingId",
                "sourceSha256", "runtimeFingerprint", "requestSha256", "planSha256", "preparedPlan", "clips", "stopped");
            Require(Integer(result.GetProperty("schemaVersion"), 1, 1) == 1);
            using var request = Parse(invocation.RequestBytes);
            foreach (string field in new[] { "requestId", "projectId", "corpusId", "actionId", "executionId", "windowId", "sourceRecordingId", "sourceSha256", "runtimeFingerprint" })
                Require(Equal(result.GetProperty(field), request.RootElement.GetProperty(field)));
            Require(Text(result.GetProperty("requestSha256")) == invocation.RequestSha256);
            bool stopped = Boolean(result.GetProperty("stopped")); var clipsJson = result.GetProperty("clips");
            Require(clipsJson.ValueKind == JsonValueKind.Array && clipsJson.GetArrayLength() <= 8);
            if (execution.PreparedPlan == null)
            {
                Require(clipsJson.GetArrayLength() == 0);
                if (result.GetProperty("preparedPlan").ValueKind == JsonValueKind.Null)
                {
                    Require(stopped && result.GetProperty("planSha256").ValueKind == JsonValueKind.Null);
                    return new(null, [], true);
                }
                var plan = ReadPlan(execution, result.GetProperty("preparedPlan"));
                Require(plan.PlanRelativePath == invocation.OutputRelativeRoot + "/plan.json" && Text(result.GetProperty("planSha256")) == plan.Sha256);
                await VerifyPlan(pins, execution, plan, token);
                return new(plan, [], stopped);
            }
            Require(result.GetProperty("preparedPlan").ValueKind == JsonValueKind.Null && Text(result.GetProperty("planSha256")) == execution.PreparedPlan.Sha256);
            await VerifyPlan(pins, execution, execution.PreparedPlan, token);
            var accepted = new List<TtsCorpusAcceptedClip>(); int index = execution.NextClipIndex;
            foreach (var item in clipsJson.EnumerateArray())
            {
                Fields(item, "plan", "textRelativePath", "textSha256", "draftText", "warningCodes", "words");
                Require(index < execution.PreparedPlan.Clips.Count);
                var clip = execution.PreparedPlan.Clips[index++];
                // Validate the independently typed nested copy, then exact duplicated values.
                var nested = ReadClip(item.GetProperty("plan"));
                Require(nested == clip && Equal(item.GetProperty("plan"), Element(clip)));
                string textPath = Text(item.GetProperty("textRelativePath"));
                Require(textPath == invocation.OutputRelativeRoot + "/texts/" + clip.Id + ".json");
                string textSha = Hash(item.GetProperty("textSha256"));
                using var text = Parse(await Read(pins, execution.ProjectRoot, textPath, 8388608, textSha, token));
                var payload = text.RootElement;
                Fields(payload, "schemaVersion", "clipId", "draftText", "warningCodes", "words");
                Require(Integer(payload.GetProperty("schemaVersion"), 1, 1) == 1 && Text(payload.GetProperty("clipId")) == clip.Id);
                double duration = (clip.EndSample - clip.StartSample) / 16000d;
                ValidateTranscript(payload, duration); ValidateTranscript(item, duration);
                foreach (string field in new[] { "draftText", "warningCodes", "words" }) Require(Equal(item.GetProperty(field), payload.GetProperty(field)));
                accepted.Add(new(clip, textPath, textSha, Text(payload.GetProperty("draftText")),
                    payload.GetProperty("warningCodes").EnumerateArray().Select(Text).ToArray(),
                    payload.GetProperty("words").EnumerateArray().Select(w => new TtsCorpusWordDto(Text(w.GetProperty("text")),
                        w.GetProperty("start").GetDouble(), w.GetProperty("end").ValueKind == JsonValueKind.Null ? null : w.GetProperty("end").GetDouble())).ToArray()));
            }
            ValidatePrefix(execution.PreparedPlan.Clips.Skip(execution.NextClipIndex).Take(8).Select(c => c.Id).ToArray(), accepted.Select(c => c.Plan.Id).ToArray(), stopped);
            return new(null, accepted, stopped);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Invalid(); }
    }

    internal static void ValidatePrefix(IReadOnlyList<string> requested, IReadOnlyList<string> actual, bool stopped)
        => Require(requested.Count is > 0 and <= 8 && actual.Count <= requested.Count && actual.SequenceEqual(requested.Take(actual.Count)) && (stopped || actual.Count == requested.Count));

    internal static string ClipId(string actionId, int index)
    {
        byte[] ns = Guid.ParseExact(actionId, "N").ToByteArray(bigEndian: true);
        byte[] hash = SHA1.HashData(ns.Concat(Encoding.UTF8.GetBytes("clip:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());
        hash[6] = (byte)((hash[6] & 15) | 0x50); hash[8] = (byte)((hash[8] & 63) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString("N");
    }

    private static TtsCorpusPreparedPlan ReadPlan(TtsCorpusExecution execution, JsonElement value)
    {
        Fields(value, "planRelativePath", "sha256", "sourceSha256", "runtimeFingerprint", "totalSamples", "clips");
        string relative = Text(value.GetProperty("planRelativePath")); _ = TtsCorpusFilePins.Resolve(execution.ProjectRoot, relative);
        string prefix = $"tts-corpus/actions/{execution.ActionId}/executions/";
        Require(relative.StartsWith(prefix, StringComparison.Ordinal));
        var parts = relative[prefix.Length..].Split('/');
        Require(parts.Length == 4 && IsId(parts[0]) && parts[1] == "windows" && IsId(parts[2]) && parts[3] == "plan.json");
        string basis = relative[..^10];
        Require(Text(value.GetProperty("sourceSha256")) == execution.SourceSha256 && Text(value.GetProperty("runtimeFingerprint")) == execution.RuntimeFingerprint);
        long total = Integer(value.GetProperty("totalSamples"), 1, 131072000); Require(total == (execution.SourceSampleCount + 2) / 3);
        var array = value.GetProperty("clips"); Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() is > 0 and <= 512);
        var clips = new List<TtsCorpusPlannedClip>(); long end = 0;
        foreach (var item in array.EnumerateArray())
        {
            var clip = ReadClip(item);
            Require(clip.Id == ClipId(execution.ActionId, clips.Count) && clip.Index == clips.Count && clip.StartSample == end
                && clip.EndSample > end && clip.EndSample - end <= 256000 && clip.AudioRelativePath == basis + "/clips/" + clip.Id + ".wav");
            _ = TtsCorpusFilePins.Resolve(execution.ProjectRoot, clip.AudioRelativePath); clips.Add(clip); end = clip.EndSample;
        }
        Require(end == total);
        return new(relative, Hash(value.GetProperty("sha256")), execution.SourceSha256, execution.RuntimeFingerprint, total, clips);
    }

    private static TtsCorpusPlannedClip ReadClip(JsonElement value)
    {
        Fields(value, "id", "index", "startSample", "endSample", "audioRelativePath", "audioSha256");
        string id = Text(value.GetProperty("id")); Require(IsId(id));
        return new(id, (int)Integer(value.GetProperty("index"), 0, 511), Integer(value.GetProperty("startSample"), 0, 131072000),
            Integer(value.GetProperty("endSample"), 1, 131072000), Text(value.GetProperty("audioRelativePath")), Hash(value.GetProperty("audioSha256")));
    }

    private static async Task VerifyPlan(TtsCorpusFilePins pins, TtsCorpusExecution execution, TtsCorpusPreparedPlan plan, CancellationToken token)
    {
        _ = ReadPlan(execution, Element(plan));
        using var document = Parse(await Read(pins, execution.ProjectRoot, plan.PlanRelativePath, 1048576, plan.Sha256, token));
        var disk = document.RootElement;
        Fields(disk, "schemaVersion", "actionId", "sourceSha256", "runtimeFingerprint", "totalSamples", "clips", "cleanAudioRelativePath", "cleanAudioSha256");
        Require(Integer(disk.GetProperty("schemaVersion"), 1, 1) == 1 && Text(disk.GetProperty("actionId")) == execution.ActionId);
        var copy = new Dictionary<string, JsonElement>();
        foreach (string field in new[] { "sourceSha256", "runtimeFingerprint", "totalSamples", "clips" }) copy[field] = disk.GetProperty(field);
        copy["planRelativePath"] = Element(plan.PlanRelativePath); copy["sha256"] = Element(plan.Sha256);
        _ = ReadPlan(execution, Element(copy));
        var expected = Element(plan);
        foreach (string field in new[] { "sourceSha256", "runtimeFingerprint", "totalSamples", "clips" }) Require(Equal(disk.GetProperty(field), expected.GetProperty(field)));
        string cleanRelative = Text(disk.GetProperty("cleanAudioRelativePath"));
        Require(cleanRelative == plan.PlanRelativePath[..^10] + "/clean.wav");
        var clean = await pins.ReadAsync(TtsCorpusFilePins.Resolve(execution.ProjectRoot, cleanRelative), checked(44 + plan.TotalSamples * 2), Hash(disk.GetProperty("cleanAudioSha256")), token);
        await ValidatePcm(clean, plan.TotalSamples, token);
        byte[] a = new byte[65536], b = new byte[65536];
        foreach (var clip in plan.Clips)
        {
            long samples = clip.EndSample - clip.StartSample;
            var audio = await pins.ReadAsync(TtsCorpusFilePins.Resolve(execution.ProjectRoot, clip.AudioRelativePath), 44 + samples * 2, clip.AudioSha256, token);
            await ValidatePcm(audio, samples, token); audio.Position = 44; clean.Position = 44 + clip.StartSample * 2;
            long remaining = samples * 2;
            while (remaining > 0)
            {
                int count = (int)Math.Min(a.Length, remaining);
                await clean.ReadExactlyAsync(a.AsMemory(0, count), token); await audio.ReadExactlyAsync(b.AsMemory(0, count), token);
                Require(a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count))); remaining -= count;
            }
        }
    }

    private static async Task ValidatePcm(Stream input, long samples, CancellationToken token)
    {
        Require(input.Length == 44 + samples * 2); input.Position = 0; byte[] h = new byte[44]; await input.ReadExactlyAsync(h, token);
        Require(h.AsSpan(0, 4).SequenceEqual("RIFF"u8) && h.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8)
            && BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)) == input.Length - 8 && BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16)) == 16
            && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(20)) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(22)) == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(24)) == 16000 && BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(28)) == 32000
            && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(32)) == 2 && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(34)) == 16
            && h.AsSpan(36, 4).SequenceEqual("data"u8) && BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(40)) == samples * 2);
    }

    private static void ValidateTranscript(JsonElement value, double duration)
    {
        string text = Text(value.GetProperty("draftText"));
        Require(text.EnumerateRunes().Count() <= 4000 && text == string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        var warningArray = value.GetProperty("warningCodes"); Require(warningArray.ValueKind == JsonValueKind.Array);
        string[] warnings = warningArray.EnumerateArray().Select(Text).ToArray();
        Require(warnings.Length <= 2 && warnings.Distinct(StringComparer.Ordinal).Count() == warnings.Length
            && warnings.All(w => w is "tts_corpus.asr.final_word_end_missing" or "tts_corpus.asr.sparse_transcript"));
        var words = value.GetProperty("words"); Require(words.ValueKind == JsonValueKind.Array && words.GetArrayLength() <= 2048);
        double previous = 0; int index = 0;
        foreach (var word in words.EnumerateArray())
        {
            Fields(word, "text", "start", "end"); string w = Text(word.GetProperty("text")); Require(w.EnumerateRunes().Count() is > 0 and <= 4000);
            var startValue = word.GetProperty("start"); Require(startValue.ValueKind == JsonValueKind.Number);
            double start = startValue.GetDouble(); Require(double.IsFinite(start) && start >= previous && start <= duration); previous = start;
            var endValue = word.GetProperty("end");
            if (endValue.ValueKind == JsonValueKind.Null) Require(index == words.GetArrayLength() - 1 && warnings.Contains("tts_corpus.asr.final_word_end_missing", StringComparer.Ordinal));
            else { Require(endValue.ValueKind == JsonValueKind.Number); double end = endValue.GetDouble(); Require(double.IsFinite(end) && end >= start && end <= duration); }
            index++;
        }
    }

    private static async Task<byte[]> Read(TtsCorpusFilePins pins, string root, string relative, int maximum, string? sha, CancellationToken token)
    {
        var file = await pins.ReadAsync(TtsCorpusFilePins.Resolve(root, relative), maximum, sha, token);
        byte[] bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, token); return bytes;
    }
}

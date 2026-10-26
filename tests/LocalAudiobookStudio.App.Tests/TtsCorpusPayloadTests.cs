using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.App.TtsCorpus;
using LocalAudiobookStudio.Application.TtsCorpus;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TtsCorpusPayloadTests
{
    [Fact]
    public async Task Preparation_validates_disk_plan_clean_pcm_and_exact_clip_slice()
    {
        using var fixture = new CorpusPayloadFixture(); var request = fixture.Prepare();
        var result = await TtsCorpusProtocol.AcceptAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(16000, result.PreparedPlan!.TotalSamples);
        Assert.Equal("69314b00a4ac5cfaaae2b22660942123", Assert.Single(result.PreparedPlan.Clips).Id);
        Assert.Empty(result.Clips);
    }

    [Theory]
    [InlineData("disk-integer-alias")]
    [InlineData("wrong-pcm-rate")]
    [InlineData("wrong-pcm-slice")]
    [InlineData("clip-hash")]
    public async Task Self_consistent_preparation_hashes_do_not_bypass_independent_shape_and_plan_checks(string mutation)
    {
        using var fixture = new CorpusPayloadFixture(); var request = fixture.Prepare(mutation);
        await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => TtsCorpusProtocol.AcceptAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Asr_receipt_matches_exact_transcript_payload_and_preserves_final_null_end()
    {
        using var fixture = new CorpusPayloadFixture(); var request = fixture.Asr();
        var result = await TtsCorpusProtocol.AcceptAsync(request, TestContext.Current.CancellationToken);
        var clip = Assert.Single(result.Clips); Assert.Equal("Слово", clip.DraftText);
        Assert.Null(Assert.Single(clip.Words).End);
        Assert.Contains("tts_corpus.asr.final_word_end_missing", clip.WarningCodes);
    }

    [Theory]
    [InlineData("nested-integer-alias")]
    [InlineData("word-number-alias")]
    [InlineData("word-boolean")]
    [InlineData("text-mismatch")]
    [InlineData("foreign-text-window")]
    [InlineData("missing-end-warning")]
    public async Task Asr_rejects_nested_numeric_aliases_transcript_mismatch_and_foreign_window(string mutation)
    {
        using var fixture = new CorpusPayloadFixture(); var request = fixture.Asr(mutation);
        await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => TtsCorpusProtocol.AcceptAsync(request, TestContext.Current.CancellationToken));
    }
}

internal sealed class CorpusPayloadFixture : IDisposable
{
    private readonly CorpusProtocolFixture fixture = new();
    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private TtsCorpusPreparedPlan? plan;
    internal TtsCorpusInvocation Prepare(string? mutation = null)
    {
        var request = fixture.Invocation(); string relative = request.OutputRelativeRoot;
        string id = "69314b00a4ac5cfaaae2b22660942123";
        byte[] clean = Wave(); byte[] clip = Wave();
        if (mutation == "wrong-pcm-rate") BitConverter.GetBytes(48000).CopyTo(clip, 24);
        if (mutation == "wrong-pcm-slice") clip[44] = 1;
        string cleanSha = Write(relative + "/clean.wav", clean);
        string audioSha = Write(relative + "/clips/" + id + ".wav", clip);
        if (mutation == "clip-hash") audioSha = new string('d', 64);
        var planned = new TtsCorpusPlannedClip(id, 0, 0, 16000, relative + "/clips/" + id + ".wav", audioSha);
        var disk = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1, fixture.Execution.ActionId, fixture.Execution.SourceSha256, fixture.Execution.RuntimeFingerprint,
            totalSamples = 16000, clips = new[] { planned }, cleanAudioRelativePath = relative + "/clean.wav", cleanAudioSha256 = cleanSha
        }, options)!;
        if (mutation == "disk-integer-alias") disk["totalSamples"] = JsonNode.Parse("16000.0");
        string planSha = Write(relative + "/plan.json", Encoding.UTF8.GetBytes(disk.ToJsonString(options)));
        plan = new(relative + "/plan.json", planSha, fixture.Execution.SourceSha256, fixture.Execution.RuntimeFingerprint, 16000, [planned]);
        Publish(request, plan, [], false); return request;
    }

    internal TtsCorpusInvocation Asr(string? mutation = null)
    {
        _ = Prepare();
        var execution = fixture.Execution with { PreparedPlan = plan };
        var request = TtsCorpusProtocol.Create(execution, fixture.Options, [new("config.json", 1, new string('c', 64))],
            "88888888888888888888888888888888", "99999999999999999999999999999999");
        var payload = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1, clipId = plan!.Clips[0].Id, draftText = "Слово",
            warningCodes = new[] { "tts_corpus.asr.final_word_end_missing" }, words = new[] { new { text = "Слово", start = 0.0, end = (double?)null } }
        }, options)!;
        if (mutation == "missing-end-warning") payload["warningCodes"] = new JsonArray();
        if (mutation == "word-boolean") payload["words"]![0]!["start"] = false;
        string textPath = request.OutputRelativeRoot + "/texts/" + plan.Clips[0].Id + ".json";
        string textSha = Write(textPath, Encoding.UTF8.GetBytes(payload.ToJsonString(options)));
        var accepted = new JsonObject
        {
            ["plan"] = JsonSerializer.SerializeToNode(plan.Clips[0], options), ["textRelativePath"] = textPath, ["textSha256"] = textSha,
            ["draftText"] = payload["draftText"]!.DeepClone(), ["warningCodes"] = payload["warningCodes"]!.DeepClone(), ["words"] = payload["words"]!.DeepClone()
        };
        if (mutation == "nested-integer-alias") accepted["plan"]!["index"] = JsonNode.Parse("0.0");
        if (mutation == "word-number-alias") accepted["words"]![0]!["start"] = JsonNode.Parse("0.0");
        if (mutation == "text-mismatch") accepted["draftText"] = "Другой текст";
        if (mutation == "foreign-text-window") accepted["textRelativePath"] = plan.PlanRelativePath[..^10] + "/texts/" + plan.Clips[0].Id + ".json";
        Publish(request, null, [accepted], false); return request;
    }

    private void Publish(TtsCorpusInvocation request, TtsCorpusPreparedPlan? prepared, JsonNode[] clips, bool stopped)
    {
        using var json = JsonDocument.Parse(request.RequestBytes);
        string[] names = ["requestId", "projectId", "corpusId", "actionId", "executionId", "windowId", "sourceRecordingId", "sourceSha256", "runtimeFingerprint"];
        var receipt = names.ToDictionary(n => n, n => (object?)json.RootElement.GetProperty(n).GetString());
        receipt["schemaVersion"] = 1; receipt["requestSha256"] = request.RequestSha256; receipt["planSha256"] = plan!.Sha256;
        receipt["preparedPlan"] = prepared; receipt["clips"] = clips; receipt["stopped"] = stopped;
        string sha = Write(request.OutputRelativeRoot + "/result.json", JsonSerializer.SerializeToUtf8Bytes(receipt, options));
        _ = Write(request.OutputRelativeRoot + "/READY", Encoding.ASCII.GetBytes(sha + "\n"));
    }
    private string Write(string relative, byte[] bytes)
    {
        string path = Path.Combine(fixture.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    private static byte[] Wave()
    {
        using var file = new MemoryStream(); using var writer = new BinaryWriter(file);
        writer.Write("RIFF"u8); writer.Write(32036); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(32000); writer.Write(new byte[32000]);
        return file.ToArray();
    }
    public void Dispose() => fixture.Dispose();
}

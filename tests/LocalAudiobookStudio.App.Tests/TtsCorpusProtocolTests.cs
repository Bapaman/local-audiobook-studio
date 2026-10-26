using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.App.TtsCorpus;
using LocalAudiobookStudio.Application.TtsCorpus;

namespace LocalAudiobookStudio.App.Tests;

public sealed class TtsCorpusProtocolTests
{
    [Fact]
    public void Request_binds_distinct_action_execution_window_and_preparation_cursor()
    {
        using var fixture = new CorpusProtocolFixture();
        var request = fixture.Invocation();
        using var json = JsonDocument.Parse(request.RequestBytes);
        var root = json.RootElement;
        Assert.Equal(fixture.Execution.CorpusId, root.GetProperty("corpusId").GetString());
        Assert.Equal("44444444444444444444444444444444", root.GetProperty("actionId").GetString());
        Assert.Equal("55555555555555555555555555555555", root.GetProperty("executionId").GetString());
        Assert.Equal("66666666666666666666666666666666", root.GetProperty("windowId").GetString());
        Assert.Equal("tts-corpus/actions/44444444444444444444444444444444/executions/55555555555555555555555555555555/windows/66666666666666666666666666666666",
            root.GetProperty("outputRelativeRoot").GetString());
        Assert.Empty(root.GetProperty("requestedClipIds").EnumerateArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(request.RequestBytes)), request.RequestSha256);
    }

    [Theory]
    [InlineData("a,b,c", false, true)]
    [InlineData("a,b", true, true)]
    [InlineData("", true, true)]
    [InlineData("a,b", false, false)]
    [InlineData("b,c", true, false)]
    [InlineData("a,a", true, false)]
    [InlineData("a,b,c,d", true, false)]
    public void Only_exact_requested_prefix_can_be_accepted(string values, bool stopped, bool valid)
    {
        string[] actual = values.Length == 0 ? [] : values.Split(',');
        if (valid) TtsCorpusProtocol.ValidatePrefix(["a", "b", "c"], actual, stopped);
        else Assert.Throws<TtsCorpusExecutionException>(() => TtsCorpusProtocol.ValidatePrefix(["a", "b", "c"], actual, stopped));
    }

    [Fact]
    public async Task Preparation_stop_receipt_is_accepted_only_after_exact_ready_hash()
    {
        using var fixture = new CorpusProtocolFixture(); var request = fixture.Invocation(); fixture.WriteStopReceipt(request);
        var result = await TtsCorpusProtocol.AcceptAsync(request, TestContext.Current.CancellationToken);
        Assert.True(result.Stopped); Assert.True(result.CleanupConfirmed); Assert.Null(result.PreparedPlan); Assert.Empty(result.Clips);
    }

    [Theory]
    [InlineData("requestId")]
    [InlineData("projectId")]
    [InlineData("corpusId")]
    [InlineData("actionId")]
    [InlineData("executionId")]
    [InlineData("windowId")]
    [InlineData("sourceRecordingId")]
    [InlineData("sourceSha256")]
    [InlineData("runtimeFingerprint")]
    [InlineData("requestSha256")]
    public async Task Receipt_with_foreign_binding_is_rejected_even_with_valid_ready_hash(string field)
    {
        using var fixture = new CorpusProtocolFixture(); var request = fixture.Invocation(); fixture.WriteStopReceipt(request, field);
        var error = await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => TtsCorpusProtocol.AcceptAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal("tts_corpus.protocol.invalid", error.Code);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("duplicate")]
    [InlineData("numeric")]
    [InlineData("cleanup")]
    public async Task Receipt_rejects_tampering_numeric_aliases_and_child_cleanup_claims(string mutation)
    {
        using var fixture = new CorpusProtocolFixture(); var request = fixture.Invocation(); fixture.WriteStopReceipt(request, mutation);
        await Assert.ThrowsAsync<TtsCorpusExecutionException>(() => TtsCorpusProtocol.AcceptAsync(request, TestContext.Current.CancellationToken));
    }
}

internal sealed class CorpusProtocolFixture : IDisposable
{
    private readonly CorpusTemporaryDirectory temporary = new();
    internal string Root => temporary.Root;
    internal TtsCorpusRuntimeOptions Options => TtsCorpusRuntimeOptions.Read(temporary.WriteConfig());
    internal TtsCorpusExecution Execution => new("11111111111111111111111111111111", "33333333333333333333333333333333",
        "44444444444444444444444444444444", "55555555555555555555555555555555", Root,
        Path.Combine(Root, "tts-corpus/actions/44444444444444444444444444444444/executions/55555555555555555555555555555555"),
        Path.Combine(Root, "source.wav"), "22222222222222222222222222222222", new string('a', 64), 48000, new string('b', 64), null, 0);
    internal TtsCorpusInvocation Invocation(TtsCorpusExecution? execution = null) => TtsCorpusProtocol.Create(execution ?? Execution, Options,
        [new("config.json", 1, new string('c', 64))], "66666666666666666666666666666666", "77777777777777777777777777777777");

    internal TtsCorpusExecution CreateSource()
    {
        if (!File.Exists(Execution.SourcePath))
        {
            using var file = File.Create(Execution.SourcePath); using var writer = new BinaryWriter(file);
            writer.Write("RIFF"u8); writer.Write(96036); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(96000); writer.Write(new byte[96000]);
        }
        return Execution with { SourceSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Execution.SourcePath))) };
    }

    internal void WriteStopReceipt(TtsCorpusInvocation request, string? mutation = null)
    {
        using var json = JsonDocument.Parse(request.RequestBytes);
        string[] names = ["requestId", "projectId", "corpusId", "actionId", "executionId", "windowId", "sourceRecordingId", "sourceSha256", "runtimeFingerprint"];
        var result = names.ToDictionary(n => n, n => (object?)json.RootElement.GetProperty(n).GetString());
        result["schemaVersion"] = 1; result["requestSha256"] = request.RequestSha256;
        result["planSha256"] = null; result["preparedPlan"] = null; result["clips"] = Array.Empty<object>(); result["stopped"] = true;
        if (mutation != null && result.ContainsKey(mutation)) result[mutation] = new string('0', mutation.EndsWith("Sha256", StringComparison.Ordinal) || mutation == "runtimeFingerprint" ? 64 : 32);
        if (mutation == "cleanup") result["cleanupConfirmed"] = true;
        string text = JsonSerializer.Serialize(result);
        if (mutation == "duplicate") text = text[..^1] + ",\"stopped\":true}";
        if (mutation == "numeric") text = text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0", StringComparison.Ordinal);
        string output = Path.Combine(Root, request.OutputRelativeRoot); Directory.CreateDirectory(output);
        byte[] bytes = Encoding.UTF8.GetBytes(text); File.WriteAllBytes(Path.Combine(output, "result.json"), bytes);
        File.WriteAllText(Path.Combine(output, "READY"), (mutation == "ready" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes))) + "\n", new UTF8Encoding(false));
    }
    public void Dispose() => temporary.Dispose();
}

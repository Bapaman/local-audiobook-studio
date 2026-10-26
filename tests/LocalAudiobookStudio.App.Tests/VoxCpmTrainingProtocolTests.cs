using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmTrainingProtocolTests
{
    [Fact]
    public void Request_creation_matches_python_golden_and_uses_corrected_text()
    {
        using var fixture = new VoxProtocolFixture();
        var invocation = VoxCpmTrainingProtocol.Create(fixture.Execution, fixture.Options, fixture.Runtime);
        using var actual = JsonDocument.Parse(invocation.RequestBytes);
        var expected = JsonNode.Parse(fixture.Golden["request.json"])!;
        expected["requestId"] = actual.RootElement.GetProperty("requestId").GetString();
        using var expectedJson = JsonDocument.Parse(expected.ToJsonString());
        Assert.Equal(VoxCpmTrainingJson.Canonical(expectedJson.RootElement), VoxCpmTrainingJson.Canonical(actual.RootElement));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(invocation.RequestBytes)), invocation.RequestSha256);
        Assert.Equal("Это только синтетический пример.", actual.RootElement.GetProperty("selectedClip").GetProperty("correctedText").GetString());
    }

    [Fact]
    public async Task Golden_candidate_is_accepted_only_with_both_ready_hashes_and_exact_transition()
    {
        using var fixture = new VoxProtocolFixture(local: true);
        var invocation = fixture.Publish();
        var result = await VoxCpmTrainingProtocol.AcceptAsync(invocation, TestContext.Current.CancellationToken);
        Assert.NotNull(result.Checkpoint); Assert.Equal(2, result.Checkpoint.Cursor.GlobalStep); Assert.Equal(1, result.Checkpoint.Cursor.CompletedEpochs);
        Assert.False(result.Stopped);
    }
    [Fact]
    public async Task Checkpoint_acceptance_supports_owned_output_paths_longer_than_260()
    {
        using var fixture = new VoxProtocolFixture(local: true, longPath: true);
        Assert.True(fixture.Execution.OutputRoot.Length > 260);
        Assert.NotNull((await VoxCpmTrainingProtocol.AcceptAsync(fixture.Publish(), TestContext.Current.CancellationToken)).Checkpoint);
    }

    [Theory]
    [InlineData("window-ready")]
    [InlineData("checkpoint-ready")]
    [InlineData("extra-file")]
    [InlineData("wrong-hash")]
    [InlineData("wrong-cursor")]
    [InlineData("wrong-corpus")]
    [InlineData("wrong-parent")]
    [InlineData("wrong-request")]
    [InlineData("wrong-layout")]
    [InlineData("stale-window-ready")]
    [InlineData("stale-checkpoint-ready")]
    public async Task Tampered_bundle_never_becomes_an_accepted_step(string fault)
    {
        using var fixture = new VoxProtocolFixture(local: true); var invocation = fixture.Publish(fault);
        int acceptedCount = 0;
        await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(async () =>
        {
            var result = await VoxCpmTrainingProtocol.AcceptAsync(invocation, TestContext.Current.CancellationToken);
            if (result.Checkpoint != null) acceptedCount++;
        });
        Assert.Equal(0, acceptedCount);
    }
}

internal sealed class VoxProtocolFixture : IDisposable
{
    internal Dictionary<string, byte[]> Golden { get; } = [];
    internal VoxCpmTrainingExecution Execution { get; private set; }
    internal VoxCpmTrainingRuntimeOptions Options { get; private set; }
    internal VoxCpmTrainingRuntimeLease Runtime { get; private set; }
    private readonly string? directory;
    internal VoxProtocolFixture(bool local = false, bool longPath = false)
    {
        string golden = Path.Combine(VoxCpmTrainingWorkerPinTests.Repository(), "tests", "fixtures", "VoxCpmTrainingProtocol");
        foreach (string name in new[] { "request.json", "result.json", "checkpoint-manifest.json" }) Golden[name] = File.ReadAllBytes(Path.Combine(golden, name));
        using var doc = JsonDocument.Parse(Golden["request.json"]); var r = doc.RootElement;
        string S(string key) => r.GetProperty(key).GetString()!;
        var identity = JsonSerializer.Deserialize<VoxCpmTrainingIdentity>(r.GetProperty("identity"), VoxCpmTrainingJson.Options)!;
        var cursor = JsonSerializer.Deserialize<VoxCpmTrainingCursor>(r.GetProperty("cursor"), VoxCpmTrainingJson.Options)!;
        string root = S("projectRoot");
        if (local)
        {
            directory = root = Path.Combine(Path.GetTempPath(), "las-vox-protocol-" + Guid.NewGuid().ToString("N"));
            if (longPath) root = Path.Combine(root, new string('x', 70), new string('y', 70));
            Directory.CreateDirectory(root);
        }
        var clip = r.GetProperty("selectedClip");
        var selected = new ApprovedTtsCorpusClip(1, 2, clip.GetProperty("id").GetString()!, 16000, 32000, clip.GetProperty("correctedText").GetString()!,
            clip.GetProperty("textSha256").GetString()!, clip.GetProperty("audioRelativePath").GetString()!, clip.GetProperty("audioSha256").GetString()!, new('f', 64));
        var first = selected with { TrainingOrdinal = 0, SourceIndex = 0, Id = new('9', 32), StartSample = 0, EndSample = 16000 };
        var source = r.GetProperty("source");
        var corpus = new ApprovedTtsCorpusBinding(S("projectId"), new('b', 32), S("corpusVersionId"), new('a', 32), "tts-corpus/manifest.json", S("corpusManifestSha256"), S("corpusBindingSha256"),
            new(source.GetProperty("recordingId").GetString()!, source.GetProperty("relativePath").GetString()!, source.GetProperty("sha256").GetString()!, 192044, 96000, false, true, DateTimeOffset.UnixEpoch),
            1, new('d', 64), new('e', 64), 2.0, [first, selected]);
        var p = r.GetProperty("parentCheckpoint");
        var parent = new VoxCpmCheckpointReference(p.GetProperty("id").GetString()!, S("trainingId"), S("actionId"), p.GetProperty("executionId").GetString()!,
            p.GetProperty("windowId").GetString()!, p.GetProperty("manifestRelativePath").GetString()!, p.GetProperty("manifestSha256").GetString()!, cursor, DateTimeOffset.UnixEpoch);
        Execution = new(S("projectId"), S("trainingId"), S("actionId"), S("executionId"), S("windowId"), S("checkpointId"), root,
            Path.Combine(root, S("outputRelativeRoot").Replace('/', Path.DirectorySeparatorChar)), corpus, identity, cursor, parent, 1);
        Options = new("C:\\LAS-Protocol-Fixture\\python.exe", "C:\\LAS-Protocol-Fixture\\base", r.GetProperty("dependencySites").EnumerateArray().Select(v => v.GetString()!).ToArray(),
            "C:\\LAS-Protocol-Fixture\\worker", S("sourceRoot"), S("modelRoot"), "C:\\LAS-Protocol-Fixture\\lock.json", S("runtimeInventoryPath"), r.GetProperty("cache").GetProperty("path").GetString()!);
        IReadOnlyList<VoxCpmTrainingAssetFile> Files(string name) => r.GetProperty(name).EnumerateObject().Select(v => new VoxCpmTrainingAssetFile(v.Name, v.Value.GetProperty("bytes").GetInt64(), v.Value.GetProperty("sha256").GetString()!)).ToArray();
        var layout = JsonSerializer.Deserialize<VoxCpmTrainingAdapterLayout[]>(r.GetProperty("parameterLayout"), VoxCpmTrainingJson.Options)!;
        Runtime = new(identity, Files("modelFiles"), Files("sourceFiles"), layout, S("runtimeInventorySha256"), new Empty());
    }
    internal VoxCpmTrainingInvocation Publish(string? fault = null)
    {
        // A separate local fixture avoids writes to the schematic C:\\ path.
        if (directory == null) throw new InvalidOperationException("Use local fixture for publication.");
        var invocation = VoxCpmTrainingProtocol.Create(Execution, Options, Runtime);
        Directory.CreateDirectory(Execution.OutputRoot);
        File.WriteAllBytes(Path.Combine(Execution.OutputRoot, "request.json"), invocation.RequestBytes);
        File.WriteAllBytes(Path.Combine(Execution.OutputRoot, "START"), "START\n"u8.ToArray());
        PublishBundle(invocation, fault);
        return invocation;
    }
    internal void PublishBundle(VoxCpmTrainingInvocation invocation, string? fault = null)
    {
        var meta = JsonNode.Parse(Golden["checkpoint-manifest.json"])!;
        using var request = JsonDocument.Parse(invocation.RequestBytes);
        meta["requestId"] = request.RootElement.GetProperty("requestId").GetString(); meta["requestSha256"] = invocation.RequestSha256;
        meta["identity"] = JsonSerializer.SerializeToNode(Execution.Identity, VoxCpmTrainingJson.Options);
        meta["parentManifestSha256"] = Execution.ParentCheckpoint?.ManifestSha256;
        if (fault == "wrong-cursor") meta["afterCursor"]!["globalStep"] = 3;
        if (fault == "wrong-corpus") meta["corpusManifestSha256"] = new string('0', 64);
        if (fault == "wrong-parent") meta["parentManifestSha256"] = new string('0', 64);
        if (fault == "wrong-request") meta["requestSha256"] = new string('0', 64);
        if (fault == "wrong-layout") meta["parameterLayout"]![0]!["shape"]![1] = 3;
        string root = Path.Combine(Execution.OutputRoot, "checkpoints", Execution.CheckpointId); Directory.CreateDirectory(root);
        string payloadRoot = Path.Combine(VoxCpmTrainingWorkerPinTests.Repository(), "tests", "fixtures", "VoxCpmTrainingProtocol", "payloads");
        foreach (string name in new[] { "lora_weights.safetensors", "optimizer.pth", "scheduler.pth", "rng.pth", "metadata.json" }) File.WriteAllBytes(Path.Combine(root, name), Convert.FromBase64String(File.ReadAllText(Path.Combine(payloadRoot, name + ".b64"))));
        byte[] manifest = System.Text.Encoding.UTF8.GetBytes(meta.ToJsonString());
        string sha = Convert.ToHexStringLower(SHA256.HashData(manifest));
        File.WriteAllBytes(Path.Combine(root, "manifest.json"), manifest);
        if (fault != "checkpoint-ready") File.WriteAllText(Path.Combine(root, "READY"), sha + "\n");
        var result = JsonNode.Parse(Golden["result.json"])!;
        result["identity"] = JsonSerializer.SerializeToNode(Execution.Identity, VoxCpmTrainingJson.Options);
        result["requestId"] = request.RootElement.GetProperty("requestId").GetString(); result["requestSha256"] = invocation.RequestSha256; result["checkpoint"]!["manifestSha256"] = sha;
        byte[] resultBytes = System.Text.Encoding.UTF8.GetBytes(result.ToJsonString()); File.WriteAllBytes(Path.Combine(Execution.OutputRoot, "result.json"), resultBytes);
        if (fault != "window-ready") File.WriteAllText(Path.Combine(Execution.OutputRoot, "READY"), Convert.ToHexStringLower(SHA256.HashData(resultBytes)) + "\n");
        if (fault == "stale-window-ready") File.WriteAllText(Path.Combine(Execution.OutputRoot, "READY"), new string('0', 64) + "\n");
        if (fault == "stale-checkpoint-ready") File.WriteAllText(Path.Combine(root, "READY"), new string('0', 64) + "\n");
        if (fault == "extra-file") File.WriteAllBytes(Path.Combine(root, "unexpected.pth"), [0]);
        if (fault == "wrong-hash") File.AppendAllText(Path.Combine(root, "metadata.json"), " ");
    }
    internal void PrepareNative()
    {
        if (directory == null) throw new InvalidOperationException();
        string cache = Path.Combine(directory, "frozen.bin"); File.WriteAllBytes(cache, [1, 2, 3, 4]);
        Options = Options with { CachePath = cache };
        var identity = Execution.Identity with { CacheSha256 = VoxCpmTrainingProtocol.Digest([1, 2, 3, 4]) };
        var clips = Execution.Corpus.Clips.Select(c => c with { AudioRelativePath = "synthetic/" + c.Id + ".wav" }).ToArray();
        foreach (var clip in clips)
        {
            byte[] wav = Wav(16000, 16000); string path = Path.Combine(directory, clip.AudioRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, wav);
            clips[clip.TrainingOrdinal] = clip with { AudioSha256 = VoxCpmTrainingProtocol.Digest(wav) };
        }
        byte[] source = Wav(48000, 96000); Directory.CreateDirectory(Path.Combine(directory, "voice")); File.WriteAllBytes(Path.Combine(directory, "voice", "source.wav"), source);
        var corpus = Execution.Corpus with { Clips = clips, Source = Execution.Corpus.Source with { Sha256 = VoxCpmTrainingProtocol.Digest(source), ByteLength = source.Length } };
        var parent = Execution.ParentCheckpoint!; string parentPath = Path.Combine(directory, parent.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(parentPath)!);
        var manifest = JsonNode.Parse(Golden["checkpoint-manifest.json"])!;
        manifest["checkpointId"] = parent.Id; manifest["executionId"] = parent.ExecutionId; manifest["windowId"] = parent.WindowId;
        manifest["identity"] = JsonSerializer.SerializeToNode(identity, VoxCpmTrainingJson.Options); manifest["parentManifestSha256"] = null;
        manifest["beforeCursor"] = JsonSerializer.SerializeToNode(VoxCpmTrainingCursor.Initial(clips.Select(c => c.Id).ToArray()), VoxCpmTrainingJson.Options);
        manifest["afterCursor"] = JsonSerializer.SerializeToNode(Execution.Cursor, VoxCpmTrainingJson.Options);
        string payloadRoot = Path.Combine(VoxCpmTrainingWorkerPinTests.Repository(), "tests", "fixtures", "VoxCpmTrainingProtocol", "payloads");
        foreach (string name in VoxCpmTrainingProtocol.PayloadLimits.Keys)
        {
            byte[] payload = Convert.FromBase64String(File.ReadAllText(Path.Combine(payloadRoot, name + ".b64")));
            if (name is "optimizer.pth" or "scheduler.pth") payload = ParentArchive(payload, name == "optimizer.pth");
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(parentPath)!, name), payload);
            manifest["files"]![name]!["bytes"] = payload.Length; manifest["files"]![name]!["sha256"] = VoxCpmTrainingProtocol.Digest(payload);
        }
        byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString()); File.WriteAllBytes(parentPath, bytes);
        string sha = VoxCpmTrainingProtocol.Digest(bytes); File.WriteAllText(Path.Combine(Path.GetDirectoryName(parentPath)!, "READY"), sha + "\n");
        Execution = Execution with { Corpus = corpus, Identity = identity, ParentCheckpoint = parent with { ManifestSha256 = sha } };
        Runtime.Dispose(); Runtime = new(identity, Runtime.ModelFiles, Runtime.SourceFiles, Runtime.AdapterLayout, Runtime.RuntimeInventorySha256, new Empty(), 4);
    }
    private static byte[] ParentArchive(byte[] payload, bool optimizer)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var input = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read))
            foreach (var entry in input.Entries)
            {
                using var source = entry.Open(); using var buffer = new MemoryStream(); source.CopyTo(buffer); byte[] bytes = buffer.ToArray();
                if (optimizer && entry.FullName.StartsWith("archive/data/", StringComparison.Ordinal) && bytes.Length == 4) bytes = BitConverter.GetBytes(1f);
                if (!optimizer && entry.FullName == "archive/data.pkl")
                    foreach (var pair in new Dictionary<string, byte> { ["last_epoch"] = 1, ["_step_count"] = 2 })
                    {
                        int at = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(pair.Key)); Assert.True(at >= 0);
                        int value = at + pair.Key.Length; Assert.Equal((byte)'K', bytes[value]); bytes[value + 1] = pair.Value;
                    }
                using var destination = zip.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open(); destination.Write(bytes);
            }
        return output.ToArray();
    }
    private static byte[] Wav(int rate, int samples)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write(samples * 2); writer.Write(new byte[samples * 2]); return bytes.ToArray();
    }
    public void Dispose() { Runtime.Dispose(); if (directory != null) Directory.Delete(directory, true); }
    private sealed class Empty : IDisposable { public void Dispose() { } }
}

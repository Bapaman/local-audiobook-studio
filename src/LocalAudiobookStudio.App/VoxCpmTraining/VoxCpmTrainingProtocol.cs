using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using static LocalAudiobookStudio.App.VoxCpmTraining.VoxCpmTrainingJson;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

internal sealed record VoxCpmTrainingInvocation(VoxCpmTrainingExecution Execution, byte[] RequestBytes,
    string RequestSha256, string RequestId, string OutputRelativeRoot);

/// <summary>Parent-owned protocol. Progress messages are never checkpoint authority.</summary>
internal static class VoxCpmTrainingProtocol
{
    internal static readonly IReadOnlyDictionary<string, int> PayloadLimits = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["lora_weights.safetensors"] = 8 * 1048576, ["optimizer.pth"] = 16 * 1048576,
        ["scheduler.pth"] = 65536, ["rng.pth"] = 65536, ["metadata.json"] = 262144
    };
    private static readonly string[] IdFields = ["requestId", "projectId", "trainingId", "actionId", "executionId", "windowId", "checkpointId"];
    internal static string Digest(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    private static void Equal<T>(JsonElement actual, T expected) => Require(Canonical(actual).AsSpan().SequenceEqual(Canonical(Element(expected))));
    private static void Id(string value) => Require(value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    internal static string Output(string action, string execution, string window)
    { Id(action); Id(execution); Id(window); return $"voxcpm-training/actions/{action}/executions/{execution}/windows/{window}"; }
    private static string ManifestPath(string action, string execution, string window, string checkpoint)
    { Id(checkpoint); return Output(action, execution, window) + $"/checkpoints/{checkpoint}/manifest.json"; }

    internal static VoxCpmTrainingInvocation Create(VoxCpmTrainingExecution execution, VoxCpmTrainingRuntimeOptions options, VoxCpmTrainingRuntimeLease runtime)
    {
        foreach (string id in new[] { execution.ProjectId, execution.TrainingId, execution.CheckpointId }) Id(id);
        Require(execution.Corpus.ProjectId == execution.ProjectId && execution.Identity == runtime.Identity);
        var ids = execution.Corpus.Clips.Select(c => c.Id).ToArray();
        _ = execution.Cursor.Advance(ids);
        Require(execution.Cursor.GlobalStep < 512 && execution.TargetCompletedEpochs == execution.Cursor.CompletedEpochs + 1);
        string output = Output(execution.ActionId, execution.ExecutionId, execution.WindowId);
        Require(string.Equals(VoxCpmTrainingFilePins.Resolve(execution.ProjectRoot, output), VoxCpmTrainingFilePins.Absolute(execution.OutputRoot), StringComparison.OrdinalIgnoreCase));
        var clip = execution.Corpus.Clips[execution.Cursor.NextRetainedOrdinal];
        Require(clip.TrainingOrdinal == execution.Cursor.NextRetainedOrdinal && clip.Id == execution.Cursor.NextClipId
            && Digest(Encoding.UTF8.GetBytes(clip.CorrectedText)) == clip.TextSha256);
        var parent = execution.ParentCheckpoint;
        Require((parent == null) == (execution.Cursor.GlobalStep == 0));
        if (parent != null)
        {
            Require(parent.TrainingId == execution.TrainingId && parent.ActionId == execution.ActionId && parent.Cursor == execution.Cursor
                && parent.ManifestRelativePath == ManifestPath(parent.ActionId, parent.ExecutionId, parent.WindowId, parent.Id) && IsHash(parent.ManifestSha256));
        }
        string requestId = Guid.NewGuid().ToString("N");
        var source = execution.Corpus.Source;
        var request = new
        {
            format = 1, requestId, execution.ProjectId, execution.TrainingId, execution.ActionId, execution.ExecutionId, execution.WindowId, execution.CheckpointId,
            execution.ProjectRoot, outputRelativeRoot = output, corpusVersionId = execution.Corpus.VersionId,
            corpusManifestSha256 = execution.Corpus.ManifestSha256, corpusBindingSha256 = execution.Corpus.BindingSha256,
            execution.Identity, clipIds = ids, execution.Cursor, execution.TargetCompletedEpochs,
            selectedClip = new { clip.TrainingOrdinal, clip.SourceIndex, clip.Id, clip.StartSample, clip.EndSample, clip.CorrectedText, clip.TextSha256, clip.AudioRelativePath, clip.AudioSha256 },
            source = new { recordingId = source.Id, relativePath = source.WavRelativePath, sha256 = source.Sha256.ToLowerInvariant(), source.SampleCount, sampleRate = 48000 },
            parentCheckpoint = parent == null ? null : new { parent.Id, parent.TrainingId, parent.ActionId, parent.ExecutionId, parent.WindowId, parent.ManifestRelativePath, parent.ManifestSha256, parent.Cursor },
            options.DependencySites, options.SourceRoot, sourceFiles = Assets(runtime.SourceFiles), options.ModelRoot, modelFiles = Assets(runtime.ModelFiles),
            options.RuntimeInventoryPath, runtime.RuntimeInventorySha256,
            cache = new { path = options.CachePath, bytes = runtime.CacheBytes, sha256 = runtime.Identity.CacheSha256, layoutSha256 = runtime.Identity.CacheLayoutSha256 },
            parameterLayout = runtime.AdapterLayout
        };
        byte[] bytes = Canonical(Element(request)); Require(bytes.Length <= 262144);
        return new(execution, bytes, Digest(bytes), requestId, output);
    }
    private static Dictionary<string, object> Assets(IReadOnlyList<VoxCpmTrainingAssetFile> files)
        => files.ToDictionary(f => f.Filename, f => (object)new { bytes = f.Bytes, sha256 = f.Sha256 }, StringComparer.Ordinal);

    internal static async Task<VoxCpmTrainingWindowResult> AcceptAsync(VoxCpmTrainingInvocation invocation, CancellationToken token)
    {
        try
        {
            using var pins = new VoxCpmTrainingFilePins(); var e = invocation.Execution;
            byte[] bytes = await Read(pins, Path.Combine(e.OutputRoot, "result.json"), 1048576, null, token);
            await Ready(pins, Path.Combine(e.OutputRoot, "READY"), Digest(bytes), token);
            using var result = Parse(bytes); var value = result.RootElement;
            Fields(value, [..IdFields, "format", "corpusVersionId", "corpusManifestSha256", "corpusBindingSha256", "identity", "requestSha256", "checkpoint", "stopped"]);
            Integer(value.GetProperty("format"), 1, 1); Bind(value, e.ProjectId, e.TrainingId, e.ActionId, e.ExecutionId, e.WindowId, e.CheckpointId, e.Corpus, e.Identity);
            Equal(value.GetProperty("requestId"), invocation.RequestId); Equal(value.GetProperty("requestSha256"), invocation.RequestSha256);
            Require(value.GetProperty("stopped").ValueKind is JsonValueKind.True or JsonValueKind.False);
            bool stopped = value.GetProperty("stopped").GetBoolean(); var candidate = value.GetProperty("checkpoint");
            if (candidate.ValueKind == JsonValueKind.Null)
            {
                Require(stopped && !Directory.Exists(Path.Combine(e.OutputRoot, "checkpoints")));
                return new(null, true);
            }
            Fields(candidate, "id", "manifestRelativePath", "manifestSha256", "cursor");
            string relative = ManifestPath(e.ActionId, e.ExecutionId, e.WindowId, e.CheckpointId);
            Equal(candidate.GetProperty("id"), e.CheckpointId); Equal(candidate.GetProperty("manifestRelativePath"), relative);
            var after = e.Cursor.Advance(e.Corpus.Clips.Select(c => c.Id).ToArray()); Equal(candidate.GetProperty("cursor"), after);
            string sha = Hash(candidate.GetProperty("manifestSha256"));
            var reference = new VoxCpmCheckpointReference(e.CheckpointId, e.TrainingId, e.ActionId, e.ExecutionId, e.WindowId, relative, sha, after, DateTimeOffset.UtcNow);
            await PinHeadAsync(pins, e.ProjectRoot, reference, e.Corpus, e.Identity, token, invocation);
            return new(reference, stopped);
        }
        catch (OperationCanceledException) { throw; }
        catch (VoxCpmTrainingExecutionException) { throw; }
        catch { throw Invalid(); }
    }
    internal static async Task ValidateHeadAsync(string projectRoot, VoxCpmCheckpointReference checkpoint,
        ApprovedTtsCorpusBinding corpus, VoxCpmTrainingIdentity identity, CancellationToken token)
    {
        using var pins = new VoxCpmTrainingFilePins();
        try { await PinHeadAsync(pins, projectRoot, checkpoint, corpus, identity, token); }
        catch (OperationCanceledException) { throw; }
        catch (VoxCpmTrainingExecutionException) { throw; }
        catch { throw Invalid(); }
    }
    internal static async Task PinHeadAsync(VoxCpmTrainingFilePins pins, string projectRoot, VoxCpmCheckpointReference checkpoint,
        ApprovedTtsCorpusBinding corpus, VoxCpmTrainingIdentity identity, CancellationToken token, VoxCpmTrainingInvocation? invocation = null)
    {
        string relative = ManifestPath(checkpoint.ActionId, checkpoint.ExecutionId, checkpoint.WindowId, checkpoint.Id);
        Require(relative == checkpoint.ManifestRelativePath && IsHash(checkpoint.ManifestSha256));
        string path = VoxCpmTrainingFilePins.Resolve(projectRoot, relative), root = Path.GetDirectoryName(path)!;
        pins.PinDirectory(root);
        var names = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Require(names.SetEquals(PayloadLimits.Keys.Concat(["manifest.json", "READY"])));
        byte[] bytes = await Read(pins, path, 65536, checkpoint.ManifestSha256, token);
        await Ready(pins, Path.Combine(root, "READY"), checkpoint.ManifestSha256, token);
        using var document = Parse(bytes); var value = document.RootElement;
        Fields(value, [..IdFields, "format", "corpusVersionId", "corpusManifestSha256", "corpusBindingSha256", "identity", "parameterLayout", "requestSha256", "parentManifestSha256", "beforeCursor", "afterCursor", "files", "metrics"]);
        Integer(value.GetProperty("format"), 1, 1);
        Bind(value, corpus.ProjectId, checkpoint.TrainingId, checkpoint.ActionId, checkpoint.ExecutionId, checkpoint.WindowId, checkpoint.Id, corpus, identity);
        Id(Text(value.GetProperty("requestId"))); Hash(value.GetProperty("requestSha256"));
        var before = Cursor(value.GetProperty("beforeCursor")); var after = Cursor(value.GetProperty("afterCursor"));
        Require(before.GlobalStep < 512 && after == before.Advance(corpus.Clips.Select(c => c.Id).ToArray()) && after == checkpoint.Cursor);
        var parent = value.GetProperty("parentManifestSha256");
        if (before.GlobalStep == 0) Require(parent.ValueKind == JsonValueKind.Null); else Hash(parent);
        var layout = Layout(value.GetProperty("parameterLayout"));
        Require(Digest(Canonical(value.GetProperty("parameterLayout"))) == identity.AdapterLayoutSha256);
        if (invocation != null)
        {
            using var request = Parse(invocation.RequestBytes);
            Equal(value.GetProperty("requestId"), invocation.RequestId); Equal(value.GetProperty("requestSha256"), invocation.RequestSha256);
            Equal(value.GetProperty("beforeCursor"), invocation.Execution.Cursor);
            Equal(value.GetProperty("parentManifestSha256"), invocation.Execution.ParentCheckpoint?.ManifestSha256);
            Require(Canonical(value.GetProperty("parameterLayout")).AsSpan().SequenceEqual(Canonical(request.RootElement.GetProperty("parameterLayout"))));
        }
        var metrics = value.GetProperty("metrics"); Fields(metrics, "loss", "gradientNorm");
        Number(metrics.GetProperty("loss")); Require(Number(metrics.GetProperty("gradientNorm")) >= 0);
        var files = value.GetProperty("files"); Fields(files, PayloadLimits.Keys.ToArray());
        foreach (var file in PayloadLimits)
        {
            var metadata = files.GetProperty(file.Key); Fields(metadata, "bytes", "sha256");
            int size = (int)Integer(metadata.GetProperty("bytes"), 1, file.Value);
            byte[] payload = await Read(pins, Path.Combine(root, file.Key), size, Hash(metadata.GetProperty("sha256")), token);
            Require(payload.Length == size); VoxCpmTrainingTensorPreflight.Validate(file.Key, payload, value);
        }
        Require(names.SetEquals(Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)));
    }
    private static void Bind(JsonElement value, string project, string training, string action, string execution, string window, string checkpoint,
        ApprovedTtsCorpusBinding corpus, VoxCpmTrainingIdentity identity)
    {
        foreach (var pair in new Dictionary<string, string> { ["projectId"] = project, ["trainingId"] = training, ["actionId"] = action,
            ["executionId"] = execution, ["windowId"] = window, ["checkpointId"] = checkpoint, ["corpusVersionId"] = corpus.VersionId,
            ["corpusManifestSha256"] = corpus.ManifestSha256, ["corpusBindingSha256"] = corpus.BindingSha256 }) Equal(value.GetProperty(pair.Key), pair.Value);
        Equal(value.GetProperty("identity"), identity);
    }
    internal static IReadOnlyList<VoxCpmTrainingAdapterLayout> Layout(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is >= 1 and <= 384);
        var result = new List<VoxCpmTrainingAdapterLayout>(); string? previous = null;
        long scalars = 0;
        foreach (var item in value.EnumerateArray())
        {
            Fields(item, "name", "shape", "dtype"); string name = Text(item.GetProperty("name"));
            Require(name.Length is > 0 and <= 256 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.') && (previous == null || string.CompareOrdinal(previous, name) < 0));
            previous = name; Require(Text(item.GetProperty("dtype")) == "F32"); var shape = item.GetProperty("shape");
            Require(shape.ValueKind == JsonValueKind.Array && shape.GetArrayLength() == 2);
            int[] dimensions = shape.EnumerateArray().Select(n => (int)Integer(n, 1, 32768)).ToArray();
            scalars += (long)dimensions[0] * dimensions[1]; Require(scalars * 4 <= PayloadLimits["lora_weights.safetensors"]);
            result.Add(new(name, dimensions, "F32"));
        }
        return result;
    }
    private static VoxCpmTrainingCursor Cursor(JsonElement value)
    {
        Fields(value, "globalStep", "completedEpochs", "epochSize", "nextRetainedOrdinal", "nextClipId");
        string clip = Text(value.GetProperty("nextClipId")); Id(clip);
        return new(Integer(value.GetProperty("globalStep"), 0, 512), Integer(value.GetProperty("completedEpochs"), 0, 512),
            (int)Integer(value.GetProperty("epochSize"), 1, 512), (int)Integer(value.GetProperty("nextRetainedOrdinal"), 0, 511), clip);
    }
    private static double Number(JsonElement value)
    { Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n) && double.IsFinite(n)); return value.GetDouble(); }
    private static async Task<byte[]> Read(VoxCpmTrainingFilePins pins, string path, int maximum, string? sha, CancellationToken token)
        => await Bytes(await pins.ReadAsync(path, maximum, sha, token), token);
    private static async Task Ready(VoxCpmTrainingFilePins pins, string path, string sha, CancellationToken token)
        => Require((await Read(pins, path, 65, null, token)).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(sha + "\n")));
}

internal sealed class VoxCpmTrainingCheckpointValidator : IVoxCpmTrainingCheckpointValidator
{
    public Task VerifyAsync(string projectRoot, VoxCpmCheckpointReference checkpoint, ApprovedTtsCorpusBinding corpus,
        VoxCpmTrainingIdentity identity, CancellationToken token) => VoxCpmTrainingProtocol.ValidateHeadAsync(projectRoot, checkpoint, corpus, identity, token);
}

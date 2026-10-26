using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Training;

public sealed record TrainingRuntimeFile(string Path, long Bytes, string Sha256);
public interface ITrainingRuntimeInspector
{
    Task<IReadOnlyList<TrainingRuntimeFile>> InspectAsync(RvcTrainingOptions options, CancellationToken token);
}

/// <summary>Verify the complete pinned inventory once; poll cheap metadata and small-code hashes.
/// Native stages independently pin and rehash model bytes immediately before using them.</summary>
public sealed class TrainingRuntime(RvcTrainingOptions? options = null, ITrainingRuntimeInspector? inspector = null)
{
    private readonly SemaphoreSlim sync = new(1, 1);
    private IReadOnlyList<(TrainingRuntimeFile File, DateTime Modified)>? baseline;
    public RvcTrainingOptions? Options => options;
    public async Task<TrainingReadinessDto> GetAsync(CancellationToken token)
    {
        if (options == null) return new(false, "training.runtime.not_ready");
        await sync.WaitAsync(token);
        try
        {
            if (baseline == null)
            {
                var files = await (inspector ?? new PythonTrainingRuntimeInspector()).InspectAsync(options, token);
                if (files.Count is < 1 or > 128 || files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
                    return new(false, "training.runtime.not_ready");
                baseline = files.Select(f => (f, new FileInfo(f.Path).LastWriteTimeUtc)).ToArray();
            }
            foreach (var item in baseline)
            {
                var f = item.File;
                if (!Path.IsPathFullyQualified(f.Path) || !TrainingRecordStore.HashValid(f.Sha256) || f.Bytes < 0)
                    return new(false, "training.runtime.not_ready");
                _ = ProjectPaths.CanonicalRoot(f.Path);
                await using var input = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != f.Bytes || new FileInfo(f.Path).LastWriteTimeUtc != item.Modified)
                    return new(false, "training.runtime.changed");
                if (f.Bytes <= 1_048_576 && Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != f.Sha256)
                    return new(false, "training.runtime.changed");
            }
            return new(true, null);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, "training.runtime.not_ready"); }
        finally { sync.Release(); }
    }
}

public sealed class PythonTrainingRuntimeInspector : ITrainingRuntimeInspector
{
    public async Task<IReadOnlyList<TrainingRuntimeFile>> InspectAsync(RvcTrainingOptions options, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("training.runtime.not_ready");
        foreach (string path in new[] { options.PythonPath, options.PowerShellPath, Path.Combine(options.ScriptsRoot, "Run-RvcWorkflow.py") })
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(ProjectPaths.CanonicalRoot(path))) throw new IOException("training.runtime.not_ready");
        }
        var start = new ProcessStartInfo(options.PythonPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-I", Path.Combine(options.ScriptsRoot, "Run-RvcWorkflow.py"), "--check-runtime" }) start.ArgumentList.Add(argument);
        start.Environment["HF_HUB_OFFLINE"] = "1"; start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        using var child = new Process { StartInfo = start };
        using var job = new TrainingProcessJob();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(30));
        bool started = false, assigned = false, exited = false;
        try
        {
            if (!child.Start()) throw new IOException("training.runtime.not_ready"); started = true;
            job.Assign(child); assigned = true;
            var output = ReadBounded(child.StandardOutput, budget.Token); var errors = ReadBounded(child.StandardError, budget.Token);
            string config = JsonSerializer.Serialize(new { python_path = options.PythonPath, powershell_path = options.PowerShellPath,
                scripts_root = options.ScriptsRoot, decoder_root = options.DecoderRoot, source_root = options.SourceRoot,
                base_asset_root = options.BaseAssetRoot, feature_asset_root = options.FeatureAssetRoot });
            await child.StandardInput.WriteAsync(config.AsMemory(), budget.Token); child.StandardInput.Close();
            await child.WaitForExitAsync(budget.Token);
            exited = await job.WaitEmptyAsync(TimeSpan.FromSeconds(5));
            string raw = await output; _ = await errors;
            if (!exited || child.ExitCode != 0) throw new IOException("training.runtime.not_ready");
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!root.GetProperty("ready").GetBoolean()) throw new IOException("training.runtime.not_ready");
            var files = root.GetProperty("files").EnumerateArray().Select(f => new TrainingRuntimeFile(f.GetProperty("path").GetString()!,
                f.GetProperty("bytes").GetInt64(), f.GetProperty("sha256").GetString()!)).ToArray();
            if (files.Length is < 50 or > 128) throw new IOException("training.runtime.not_ready");
            return files;
        }
        finally
        {
            if (started && !exited)
            {
                if (assigned) { job.Terminate(); if (!await job.WaitEmptyAsync(TimeSpan.FromSeconds(5))) throw new IOException("training.runtime.cleanup"); }
                else { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var text = new System.Text.StringBuilder(); char[] buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (text.Length + count > 131_072) throw new IOException("training.runtime.not_ready");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}

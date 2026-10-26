using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Application.Jobs;
using LocalAudiobookStudio.Application.Projects;
using LocalAudiobookStudio.Application.Workers;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Core.Storage;

namespace LocalAudiobookStudio.Application.Training;

public sealed class RvcSavedVoiceConverter(TrainingRuntime runtime) : ISavedVoiceConverter
{
    public async Task<ConvertedVoiceCandidate> ConvertAsync(SavedVoiceConversionExecution execution, Action<string> progress, CancellationToken token)
    {
        if (!(await runtime.GetAsync(token)).Ready || runtime.Options is not { } options || !OperatingSystem.IsWindows())
            throw new WorkerClientException("worker.runtime.not_ready", true);
        var b = execution.Binding;
        string workflow = ProjectPaths.Resolve(execution.ProjectRoot, b.WorkflowRelativePath);
        string original = ProjectPaths.Resolve(execution.ProjectRoot, b.SourceWavRelativePath);
        using var originalPin = await PinnedWave.OpenAsync(original);
        if (originalPin.Inspection.Sha256.ToLowerInvariant() != b.SourceSha256) throw new WorkerClientException("worker.output.invalid", true);
        using var frozen = await ReadJson(workflow, b.WorkflowSha256, token);
        var state = frozen.RootElement; var priorRequest = state.GetProperty("request");
        await RvcWorkflowExecutor.ValidateReferenceAsync(new(execution.ProjectRoot, execution.OutputRoot, original, b.SourceSha256, null),
            new(workflow, b.WorkflowSha256, b.GlobalStep, b.CompletedEpoch, b.EpochSize, b.StopReason),
            priorRequest.GetProperty("path").GetString()!, priorRequest.GetProperty("sha256").GetString()!, token);
        using var sourcePin = await PinnedWave.OpenAsync(execution.SourcePath);
        if (sourcePin.Inspection.Sha256.ToLowerInvariant() != execution.SourceSha256 || sourcePin.Inspection.SampleCount is <= 0 or > 5760000)
            throw new WorkerClientException("worker.output.invalid", true);
        string requestPath = ProjectPaths.Resolve(execution.OutputRoot, "request.json");
        var request = new { format = 1, purpose = "saved-voice-audio-conversion", project_root = execution.ProjectRoot,
            workflow = new { path = workflow, sha256 = b.WorkflowSha256 }, source = new { path = execution.SourcePath, sha256 = execution.SourceSha256 },
            runtime = new { source_root = options.SourceRoot, feature_asset_root = options.FeatureAssetRoot },
            output_root = execution.OutputRoot, profile_version = b.ProfileVersion };
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(request); string hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        await using (var file = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { await file.WriteAsync(raw, token); await file.FlushAsync(token); file.Flush(true); }
        string gate = ProjectPaths.Resolve(execution.OutputRoot, "START");
        var start = new ProcessStartInfo(options.PythonPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-I", Path.Combine(options.ScriptsRoot, "Convert-RvcSavedVoice.py"), "--request", requestPath,
            "--request-sha256", hash, "--output-root", execution.OutputRoot }) start.ArgumentList.Add(arg);
        foreach (string name in new[] { "OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMBA_NUM_THREADS" }) start.Environment[name] = "2";
        start.Environment["CUDA_VISIBLE_DEVICES"] = ""; start.Environment["HF_HUB_OFFLINE"] = "1"; start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["LAS_CONVERSION_START_GATE"] = gate; start.Environment.Remove("LAS_WORKER_SESSION_SECRET"); start.Environment.Remove("LIBROSA_CACHE_DIR");
        using var child = new Process { StartInfo = start }; using var job = new TrainingProcessJob();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromMinutes(20));
        bool started = false, assigned = false, exited = false; string? candidate = null, manifest = null;
        try
        {
            if (!child.Start()) throw new WorkerClientException("worker.launch", true); started = true;
            job.Assign(child); assigned = true;
            using (var marker = new FileStream(gate, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) marker.Flush(true);
            var errors = Drain(child.StandardError);
            while (true)
            {
                string? line = await Line(child.StandardOutput, budget.Token).WaitAsync(TimeSpan.FromMinutes(5), budget.Token);
                if (line == null) break;
                using var doc = JsonDocument.Parse(line); var e = doc.RootElement; string? phase = e.GetProperty("phase").GetString();
                if (phase == "saved_voice_conversion_control")
                {
                    if (candidate != null) throw new WorkerClientException("worker.output.invalid", true);
                    candidate = e.GetProperty("candidate_root").GetString(); manifest = e.GetProperty("manifest_sha256").GetString();
                }
                else if (candidate != null || phase is not ("saved_voice_conversion_inputs_verified" or "saved_voice_conversion_window" or "saved_voice_conversion_candidate_written"))
                    throw new WorkerClientException("worker.output.invalid", true);
                else progress("generating");
            }
            await child.WaitForExitAsync(budget.Token).WaitAsync(TimeSpan.FromSeconds(5), budget.Token);
            exited = await job.WaitEmptyAsync(TimeSpan.FromSeconds(5)); await errors.WaitAsync(TimeSpan.FromSeconds(5), budget.Token);
            if (!exited) throw new WorkerClientException("worker.cleanup", false);
            if (child.ExitCode != 0 || candidate == null || manifest == null) throw new WorkerClientException("worker.output.invalid", true);
            return await ValidateCandidateAsync(execution, hash, candidate, manifest, budget.Token);
        }
        catch (Exception error)
        {
            if (started && !exited)
            {
                bool cleaned = false;
                try
                {
                    if (assigned) { job.Terminate(); cleaned = await job.WaitEmptyAsync(TimeSpan.FromSeconds(5)); }
                    else { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); cleaned = child.HasExited; }
                }
                catch { }
                if (!cleaned) throw new WorkerClientException("worker.cleanup", false);
            }
            if (error is WorkerClientException) throw;
            throw new WorkerClientException(error is OperationCanceledException ? (token.IsCancellationRequested ? "worker.canceled" : "worker.timeout") : "worker.output.invalid", true);
        }
    }

    public static async Task<ConvertedVoiceCandidate> ValidateCandidateAsync(SavedVoiceConversionExecution execution, string requestHash, string candidateRoot, string manifestHash, CancellationToken token)
    {
        try
        {
            string expectedRoot = ProjectPaths.Resolve(execution.OutputRoot, "candidate");
            if (!string.Equals(candidateRoot, expectedRoot, StringComparison.OrdinalIgnoreCase) || !TrainingRecordStore.HashValid(requestHash)
                || !TrainingRecordStore.HashValid(manifestHash)) throw new InvalidDataException();
            using var pins = new CatalogPaths.Pins(); CatalogPaths.PinDirectories(candidateRoot, pins);
            if (!Directory.EnumerateFileSystemEntries(candidateRoot).Select(Path.GetFileName).ToHashSet().SetEquals(new[] { "final.wav", "manifest.json", "READY" })) throw new InvalidDataException();
            using var readyPin = CatalogPaths.OpenFile(ProjectPaths.Resolve(candidateRoot, "READY"), 0x80000000, 1, 3);
            await using var ready = new FileStream(readyPin, FileAccess.Read);
            byte[] marker = new byte[65]; if (ready.Length != marker.Length) throw new InvalidDataException();
            await ready.ReadExactlyAsync(marker, token);
            if (System.Text.Encoding.ASCII.GetString(marker) != manifestHash + "\n") throw new InvalidDataException();
            using var manifest = await ReadJson(ProjectPaths.Resolve(candidateRoot, "manifest.json"), manifestHash, token);
            var m = manifest.RootElement; var b = execution.Binding;
            string workflowPath = ProjectPaths.Resolve(execution.ProjectRoot, b.WorkflowRelativePath);
            using var state = await ReadJson(workflowPath, b.WorkflowSha256, token);
            var selected = m.GetProperty("selected_point"); var original = state.RootElement;
            if (!m.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(new[] { "format", "purpose", "request_sha256", "workflow", "source", "profile_version", "sample_rate", "sample_count", "selected_point",
                "inference_seed", "central_seconds", "context_seconds", "overlap_seconds", "unvoiced_f0", "peak_normalization", "source_mixing", "quality_established", "auto_activate_voice", "files" })
                || m.GetProperty("format").GetInt32() != 1 || m.GetProperty("purpose").GetString() != "saved-voice-audio-conversion-candidate"
                || m.GetProperty("request_sha256").GetString() != requestHash || m.GetProperty("profile_version").GetString() != b.ProfileVersion
                || m.GetProperty("workflow").GetProperty("path").GetString() != workflowPath || m.GetProperty("workflow").GetProperty("sha256").GetString() != b.WorkflowSha256
                || m.GetProperty("source").GetProperty("path").GetString() != execution.SourcePath || m.GetProperty("source").GetProperty("sha256").GetString() != execution.SourceSha256
                || m.GetProperty("sample_rate").GetInt32() != 48000 || m.GetProperty("sample_count").GetInt64() is <= 0 or > 5760000
                || m.GetProperty("inference_seed").GetInt32() != 1234 || m.GetProperty("central_seconds").GetInt32() != 3 || m.GetProperty("context_seconds").GetInt32() != 1
                || m.GetProperty("overlap_seconds").GetDouble() != .1 || m.GetProperty("unvoiced_f0").GetString() != "preserved-zero"
                || new[] { "peak_normalization", "source_mixing", "quality_established", "auto_activate_voice" }.Any(x => m.GetProperty(x).GetBoolean())
                || selected.GetProperty("run_id").GetString() != original.GetProperty("run_id").GetString()
                || selected.GetProperty("global_step").GetInt64() != b.GlobalStep || selected.GetProperty("completed_epoch").GetInt64() != b.CompletedEpoch
                || selected.GetProperty("epoch_size").GetInt32() != b.EpochSize
                || selected.GetProperty("checkpoint").GetProperty("root").GetString() != original.GetProperty("accepted").GetProperty("checkpoint").GetProperty("root").GetString()
                || selected.GetProperty("checkpoint").GetProperty("sha256").GetString() != original.GetProperty("accepted").GetProperty("checkpoint").GetProperty("sha256").GetString()
                || m.GetProperty("files").EnumerateObject().Count() != 1) throw new InvalidDataException();
            string wav = ProjectPaths.Resolve(candidateRoot, "final.wav"); var metadata = m.GetProperty("files").GetProperty("final.wav");
            using var audio = await PinnedWave.OpenAsync(wav); using var source = await PinnedWave.OpenAsync(execution.SourcePath);
            string digest = metadata.GetProperty("sha256").GetString()!; long bytes = metadata.GetProperty("bytes").GetInt64();
            if (!TrainingRecordStore.HashValid(digest) || bytes is < 46 or > 11520044 || new FileInfo(wav).Length != bytes
                || audio.Inspection.Sha256.ToLowerInvariant() != digest || audio.Inspection.IsClipped
                || audio.Inspection.SampleCount != m.GetProperty("sample_count").GetInt64() || audio.Inspection.SampleCount != source.Inspection.SampleCount
                || source.Inspection.Sha256.ToLowerInvariant() != execution.SourceSha256) throw new InvalidDataException();
            return new(wav, digest, bytes, audio.Inspection.SampleCount);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new WorkerClientException("worker.output.invalid", true); }
    }
    private static async Task<JsonDocument> ReadJson(string path, string sha, CancellationToken token)
    {
        if (!TrainingRecordStore.HashValid(sha)) throw new InvalidDataException();
        using var handle = CatalogPaths.OpenFile(path, 0x80000000, 1, 3); await using var file = new FileStream(handle, FileAccess.Read);
        if (file.Length is <= 0 or > 65536) throw new InvalidDataException();
        byte[] bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha) throw new InvalidDataException();
        return JsonDocument.Parse(bytes);
    }
    private static async Task<string?> Line(StreamReader reader, CancellationToken token)
    {
        var line = new System.Text.StringBuilder(); char[] value = new char[1];
        while (await reader.ReadAsync(value, token) != 0)
        { if (value[0] == '\n') return line.ToString().TrimEnd('\r'); if (line.Length >= 16384) throw new InvalidDataException(); line.Append(value[0]); }
        return line.Length == 0 ? null : line.ToString();
    }
    private static async Task Drain(StreamReader reader) { char[] buffer = new char[1024]; while (await reader.ReadAsync(buffer) != 0) { } }
}

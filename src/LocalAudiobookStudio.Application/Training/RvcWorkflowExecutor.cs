using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAudiobookStudio.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace LocalAudiobookStudio.Application.Training;

/// <summary>Owns the entire workflow tree, private controls and verified immutable acceptance.</summary>
public sealed class RvcWorkflowExecutor(RvcTrainingOptions options, TrainingRuntime runtime) : ITrainingWorkflowExecutor
{
    public async Task<WorkflowReference?> RunAsync(TrainingExecution execution, Func<TrainingProgress, Task> progress,
        Func<WorkflowReference, Task> checkpoint, CancellationToken token)
    {
        if (!(await runtime.GetAsync(token)).Ready) throw new TrainingExecutionException("training.runtime.not_ready");
        if (!OperatingSystem.IsWindows()) throw new TrainingExecutionException("training.runtime.not_ready");
        if (execution.Previous != null) await ValidateStoredReferenceAsync(execution, execution.Previous, token);
        string requestPath = ProjectPaths.Resolve(execution.OutputRoot, "request.json");
        var request = new
        {
            format = 1, purpose = "local-personal-training-workflow",
            source = new { path = execution.SourcePath, sha256 = execution.SourceSha256, consent_confirmed = true },
            runtime = new { python_path = options.PythonPath, powershell_path = options.PowerShellPath, scripts_root = options.ScriptsRoot,
                decoder_root = options.DecoderRoot, source_root = options.SourceRoot, base_asset_root = options.BaseAssetRoot, feature_asset_root = options.FeatureAssetRoot },
            previous = execution.Previous == null ? null : new { path = execution.Previous.Path, sha256 = execution.Previous.Sha256 },
            output_root = execution.OutputRoot
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        string requestHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await using (var file = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(true); }
        string startGate = ProjectPaths.Resolve(execution.OutputRoot, "START");
        var start = new ProcessStartInfo(options.PythonPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = options.ScriptsRoot };
        foreach (string arg in new[] { "-I", Path.Combine(options.ScriptsRoot, "Run-RvcWorkflow.py"), "--request", requestPath,
            "--request-sha256", requestHash, "--output-root", execution.OutputRoot }) start.ArgumentList.Add(arg);
        foreach (string name in new[] { "OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMBA_NUM_THREADS" }) start.Environment[name] = "2";
        start.Environment["CUDA_VISIBLE_DEVICES"] = ""; start.Environment["RVC_CUDA_GRAPH"] = "0";
        start.Environment["HF_HUB_OFFLINE"] = "1"; start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["LAS_WORKFLOW_START_GATE"] = startGate;
        start.Environment.Remove("LIBROSA_CACHE_DIR");
        using var child = new Process { StartInfo = start };
        using var job = new TrainingProcessJob();
        bool started = false, assigned = false, treeExited = false; WorkflowReference? control = null;
        try
        {
            if (!child.Start()) throw new TrainingExecutionException("training.launch"); started = true;
            job.Assign(child); assigned = true;
            using (var file = new FileStream(startGate, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.Flush(true);
            var errors = DrainAsync(child.StandardError);
            var wall = Stopwatch.StartNew(); long step = 0, epoch = 0; int size = 0;
            while (true)
            {
                if (wall.Elapsed > TimeSpan.FromHours(2)) throw new TrainingExecutionException("training.timeout");
                string? line;
                try { line = await ReadBoundedLineAsync(child.StandardOutput, token).WaitAsync(TimeSpan.FromMinutes(5), token); }
                catch (TimeoutException) { throw new TrainingExecutionException("training.timeout"); }
                if (line == null) break;
                using var doc = JsonDocument.Parse(line); var e = doc.RootElement;
                string phase = e.GetProperty("phase").GetString() ?? throw new TrainingExecutionException("training.progress.invalid");
                if (phase == "failed") throw new TrainingExecutionException("training.failed");
                if (phase is "workflow_checkpoint" or "workflow_control")
                {
                    var point = ParseReference(e);
                    await ValidateReferenceAsync(execution, point, requestPath, requestHash, token);
                    if (phase == "workflow_checkpoint")
                    {
                        if (point.GlobalStep < 1 || control != null) throw new TrainingExecutionException("training.control.invalid");
                        await checkpoint(point);
                    }
                    else
                    {
                        if (control != null) throw new TrainingExecutionException("training.control.invalid");
                        control = point;
                    }
                    continue;
                }
                if (control != null || phase.Length is < 1 or > 80 || phase.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                    throw new TrainingExecutionException("training.progress.invalid");
                if (e.TryGetProperty("global_step", out var global)) step = global.GetInt64();
                if (e.TryGetProperty("completed_epoch", out var completed)) epoch = completed.GetInt64();
                if (e.TryGetProperty("epoch_size", out var epochSize)) size = epochSize.GetInt32();
                if (step < 0 || epoch < 0 || size < 0) throw new TrainingExecutionException("training.progress.invalid");
                await progress(new(phase, step, epoch, size));
            }
            await child.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            treeExited = await job.WaitEmptyAsync(TimeSpan.FromSeconds(5));
            await errors.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (!treeExited) throw new TrainingExecutionException("training.cleanup", false);
            if (child.ExitCode != 0 || control == null) throw new TrainingExecutionException("training.unaccepted");
            await ValidateReferenceAsync(execution, control, requestPath, requestHash, token);
            return control;
        }
        catch (Exception error)
        {
            if (started && !treeExited)
            {
                bool cleaned = false;
                try
                {
                    if (assigned) { job.Terminate(); cleaned = await job.WaitEmptyAsync(TimeSpan.FromSeconds(5)); }
                    else { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); cleaned = child.HasExited; }
                }
                catch { /* An unconfirmed owned tree retains the shared compute gate. */ }
                if (!cleaned) throw new TrainingExecutionException("training.cleanup", false);
            }
            if (error is TrainingExecutionException) throw;
            throw new TrainingExecutionException(error is OperationCanceledException ? "training.interrupted" : "training.failed");
        }
    }
    private static WorkflowReference ParseReference(JsonElement e)
        => new(e.GetProperty("state_path").GetString()!, e.GetProperty("state_sha256").GetString()!, e.GetProperty("global_step").GetInt64(),
            e.GetProperty("completed_epoch").GetInt64(), e.GetProperty("epoch_size").GetInt32(), e.GetProperty("stop_reason").GetString()!);
    private static async Task ValidateStoredReferenceAsync(TrainingExecution execution, WorkflowReference point, CancellationToken token)
    {
        using var state = await ReadKnownJson(execution.ProjectRoot, point.Path, point.Sha256, token);
        var request = state.RootElement.GetProperty("request");
        await ValidateReferenceAsync(execution, point, request.GetProperty("path").GetString()!, request.GetProperty("sha256").GetString()!, token);
    }
    public static async Task ValidateReferenceAsync(TrainingExecution execution, WorkflowReference point, string requestPath, string requestHash, CancellationToken token)
    {
        using var state = await ReadKnownJson(execution.ProjectRoot, point.Path, point.Sha256, token);
        var s = state.RootElement; var source = s.GetProperty("source"); var request = s.GetProperty("request");
        if (s.GetProperty("format").GetInt32() != 1 || s.GetProperty("purpose").GetString() != "local-personal-training-workflow-state"
            || request.GetProperty("path").GetString() != requestPath || request.GetProperty("sha256").GetString() != requestHash
            || source.GetProperty("path").GetString() != execution.SourcePath || source.GetProperty("sha256").GetString() != execution.SourceSha256
            || !source.GetProperty("consent_confirmed").GetBoolean() || !TrainingRecordStore.Id(s.GetProperty("run_id").GetString())
            || s.GetProperty("global_step").GetInt64() != point.GlobalStep || s.GetProperty("completed_epoch").GetInt64() != point.CompletedEpoch
            || s.GetProperty("epoch_size").GetInt32() != point.EpochSize || s.GetProperty("stop_reason").GetString() != point.StopReason)
            throw new TrainingExecutionException("training.control.invalid");
        string readyPath = Path.Combine(Path.GetDirectoryName(point.Path)!, "READY");
        if (!TrainingRecordStore.WithinTraining(execution.ProjectRoot, readyPath)) throw new TrainingExecutionException("training.control.invalid");
        if (new FileInfo(readyPath).Length != 65 || await File.ReadAllTextAsync(readyPath, token) != point.Sha256 + "\n")
            throw new TrainingExecutionException("training.control.invalid");
        var accepted = s.GetProperty("accepted");
        if (accepted.ValueKind == JsonValueKind.Null)
        {
            if (point.GlobalStep != 0 || point.CompletedEpoch != 0 || point.EpochSize != 0 || point.StopReason != "user_stop")
                throw new TrainingExecutionException("training.control.invalid");
            return;
        }
        var checkpoint = accepted.GetProperty("checkpoint"); string checkpointRoot = checkpoint.GetProperty("root").GetString()!;
        string manifestHash = checkpoint.GetProperty("sha256").GetString()!;
        using var manifest = await ReadKnownJson(execution.ProjectRoot, Path.Combine(checkpointRoot, "manifest.json"), manifestHash, token);
        string checkpointReady = Path.Combine(checkpointRoot, "READY");
        if (!TrainingRecordStore.WithinTraining(execution.ProjectRoot, checkpointReady) || new FileInfo(checkpointReady).Length != 65
            || await File.ReadAllTextAsync(checkpointReady, token) != manifestHash + "\n") throw new TrainingExecutionException("training.control.invalid");
        var m = manifest.RootElement; var pos = m.GetProperty("position"); string run = s.GetProperty("run_id").GetString()!;
        if (m.GetProperty("format").GetInt32() != 2 || m.GetProperty("purpose").GetString() != "complete-update-checkpoint"
            || m.GetProperty("identity").GetProperty("run_id").GetString() != run || pos.GetProperty("global_step").GetInt64() != point.GlobalStep
            || pos.GetProperty("completed_epoch").GetInt64() != point.CompletedEpoch || pos.GetProperty("epoch_size").GetInt32() != point.EpochSize
            || point.GlobalStep != checked(point.CompletedEpoch * point.EpochSize + pos.GetProperty("next_ordinal").GetInt64()))
            throw new TrainingExecutionException("training.control.invalid");
        if (point.GlobalStep < 1 || point.EpochSize is < 1 or > 2048 || point.CompletedEpoch is < 0 or > 200
            || pos.GetProperty("next_ordinal").GetInt64() < 0 || pos.GetProperty("next_ordinal").GetInt64() >= point.EpochSize)
            throw new TrainingExecutionException("training.control.invalid");
        var payloads = m.GetProperty("files");
        string[] names = ["generator.pt", "discriminator.pt", "training.pt"];
        if (payloads.EnumerateObject().Count() != names.Length) throw new TrainingExecutionException("training.control.invalid");
        foreach (string name in names)
        {
            var metadata = payloads.GetProperty(name); string payloadPath = Path.Combine(checkpointRoot, name);
            long length = metadata.GetProperty("bytes").GetInt64(); string digest = metadata.GetProperty("sha256").GetString()!;
            if (!TrainingRecordStore.WithinTraining(execution.ProjectRoot, payloadPath) || length is < 1 or > 2_147_483_648
                || !TrainingRecordStore.HashValid(digest)) throw new TrainingExecutionException("training.control.invalid");
            await using var payload = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (payload.Length != length || Convert.ToHexStringLower(await SHA256.HashDataAsync(payload, token)) != digest)
                throw new TrainingExecutionException("training.control.invalid");
        }
        var receiptRef = accepted.GetProperty("receipt");
        using var receipt = await ReadKnownJson(execution.ProjectRoot, receiptRef.GetProperty("path").GetString()!, receiptRef.GetProperty("sha256").GetString()!, token);
        var r = receipt.RootElement; string? purpose = r.GetProperty("purpose").GetString();
        if (purpose == "supervisor-accepted-personal-native-session")
        {
            string marker = Path.Combine(Path.GetDirectoryName(receiptRef.GetProperty("path").GetString()!)!, "READY");
            if (!TrainingRecordStore.WithinTraining(execution.ProjectRoot, marker) || new FileInfo(marker).Length != 65
                || await File.ReadAllTextAsync(marker, token) != receiptRef.GetProperty("sha256").GetString() + "\n")
                throw new TrainingExecutionException("training.control.invalid");
        }
        var p = r.GetProperty(purpose == "supervisor-accepted-personal-native-update" ? "point" : "selected_point");
        if (purpose is not ("supervisor-accepted-personal-native-update" or "supervisor-accepted-personal-native-session")
            || r.GetProperty("native_worker_exit_code").GetInt32() != 0 || r.GetProperty("native_worker_timed_out").GetBoolean()
            || r.GetProperty("native_worker_deadline_ms").GetInt32() != (purpose == "supervisor-accepted-personal-native-update" ? 60000 : 3600000)
            || !r.GetProperty("accepted_after_native_worker_exit").GetBoolean() || r.GetProperty("quality_established").GetBoolean()
            || r.GetProperty("auto_activate_voice").GetBoolean() || p.GetProperty("checkpoint_root").GetString() != checkpointRoot
            || p.GetProperty("checkpoint_manifest_sha256").GetString() != manifestHash || p.GetProperty("run_id").GetString() != run
            || p.GetProperty("global_step").GetInt64() != point.GlobalStep || p.GetProperty("completed_epoch").GetInt64() != point.CompletedEpoch
            || p.GetProperty("epoch_size").GetInt32() != point.EpochSize || p.GetProperty("next_ordinal").GetInt64() != pos.GetProperty("next_ordinal").GetInt64()
            || p.GetProperty("admission_sha256").GetString() != s.GetProperty("inputs").GetProperty("admission").GetProperty("sha256").GetString()
            || pos.GetProperty("admission_sha256").GetString() != p.GetProperty("admission_sha256").GetString())
            throw new TrainingExecutionException("training.control.invalid");
    }
    private static async Task<JsonDocument> ReadKnownJson(string projectRoot, string path, string sha, CancellationToken token)
    {
        if (!TrainingRecordStore.WithinTraining(projectRoot, path) || !TrainingRecordStore.HashValid(sha)) throw new TrainingExecutionException("training.control.invalid");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 1_048_576) throw new TrainingExecutionException("training.control.invalid");
        byte[] bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, token);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha) throw new TrainingExecutionException("training.control.invalid");
        return JsonDocument.Parse(bytes);
    }
    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var line = new System.Text.StringBuilder(); char[] c = new char[1];
        while (await reader.ReadAsync(c, token) != 0)
        {
            if (c[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= 16384) throw new TrainingExecutionException("training.progress.invalid");
            line.Append(c[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }
    private static async Task DrainAsync(StreamReader reader)
    { char[] buffer = new char[1024]; while (await reader.ReadAsync(buffer) != 0) { } }
}

/// <summary>Assignment precedes START, so children inherit ownership before any native work.</summary>
internal sealed class TrainingProcessJob : IDisposable
{
    private readonly SafeJobHandle handle;
    public TrainingProcessJob()
    {
        handle = CreateJobObjectW(0, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        if (handle.IsInvalid || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { handle.Dispose(); throw new TrainingExecutionException("training.launch"); }
    }
    public void Assign(Process child)
    { if (!AssignProcessToJobObject(handle, child.Handle)) throw new TrainingExecutionException("training.launch"); }
    public void Terminate()
    { if (!TerminateJobObject(handle, 1)) throw new TrainingExecutionException("training.cleanup", false); }
    public async Task<bool> WaitEmptyAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            if (!QueryInformationJobObject(handle, 1, out BasicAccounting info, (uint)Marshal.SizeOf<BasicAccounting>(), 0)) return false;
            if (info.ActiveProcesses == 0) return true;
            await Task.Delay(20);
        } while (clock.Elapsed < timeout);
        return false;
    }
    public void Dispose() => handle.Dispose();
    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public SafeJobHandle() : base(true) { } protected override bool ReleaseHandle() => CloseHandle(handle); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint LimitFlags; public nuint Minimum, Maximum; public uint ActiveLimit; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting
    { public long User, Kernel, PeriodUser, PeriodKernel; public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeJobHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeJobHandle job, int type, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeJobHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeJobHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeJobHandle job, int type, out BasicAccounting info, uint size, nint length);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}

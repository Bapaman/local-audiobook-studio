using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.WorkerHost.Engine;
using static LocalAudiobookStudio.App.VoxCpmTraining.VoxCpmTrainingJson;

namespace LocalAudiobookStudio.App.VoxCpmTraining;

internal sealed class VoxCpmTrainingNativeExecutor(VoxCpmTrainingRuntime runtime, IVoxCpmTrainingProcessLauncher? launcher = null,
    IVoxCpmTrainingMemoryReader? memory = null, TimeProvider? clock = null) : IVoxCpmTrainingExecutor, IAsyncDisposable
{
    private readonly SemaphoreSlim operation = new(1, 1);
    private Retained? retained;
    private bool disposed;

    public async Task<VoxCpmTrainingWindowResult> RunAsync(VoxCpmTrainingExecution execution, Func<VoxCpmTrainingProgress, Task> progress, CancellationToken token)
    {
        await operation.WaitAsync(token);
        VoxCpmTrainingRuntimeLease? lease = null; VoxCpmTrainingFilePins? pins = null; VoxCpmTrainingCacheLease? cache = null;
        IVoxCpmTrainingOwnedProcess? child = null;
        using var monitor = new CancellationTokenSource(); using var streamCancellation = new CancellationTokenSource();
        Task? stdout = null, stderr = null, watchdog = null; bool unknownLaunch = false, jobZero = false, cacheChecked = false;
        try
        {
            if (disposed || retained != null) throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false);
            token.ThrowIfCancellationRequested();
            var available = memory ?? new VoxCpmTrainingMemoryReader(); var time = clock ?? TimeProvider.System;
            VoxCpmTrainingResourcePolicy.CheckPreflight(available.AvailableBytes());
            lease = await runtime.AcquireAsync(token);
            if (lease.Identity != execution.Identity) throw new VoxCpmTrainingExecutionException("voxcpm_training.runtime.changed");
            cache = await VoxCpmTrainingCacheLease.AcquireAsync(runtime.Options.CachePath, lease.CacheBytes, lease.Identity.CacheSha256, token);
            pins = new VoxCpmTrainingFilePins();
            var invocation = VoxCpmTrainingProtocol.Create(execution, runtime.Options, lease);
            var source = execution.Corpus.Source;
            var sourceFile = await pins.ReadAsync(VoxCpmTrainingFilePins.Resolve(execution.ProjectRoot, source.WavRelativePath), source.ByteLength, source.Sha256.ToLowerInvariant(), token);
            Require(sourceFile.Length == source.ByteLength);
            var clip = execution.Corpus.Clips[execution.Cursor.NextRetainedOrdinal];
            var audio = await pins.ReadAsync(VoxCpmTrainingFilePins.Resolve(execution.ProjectRoot, clip.AudioRelativePath), 512044, clip.AudioSha256, token);
            await CheckClip(audio, clip.EndSample - clip.StartSample, token);
            if (execution.ParentCheckpoint != null) await VoxCpmTrainingProtocol.PinHeadAsync(pins, execution.ProjectRoot, execution.ParentCheckpoint, execution.Corpus, execution.Identity, token);
            NewOutput(pins, execution.ProjectRoot, invocation.OutputRelativeRoot);
            string requestPath = Path.Combine(execution.OutputRoot, "request.json"), gatePath = Path.Combine(execution.OutputRoot, "START");
            using (var request = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await request.WriteAsync(invocation.RequestBytes, token); request.Flush(true); }
            _ = await pins.ReadAsync(requestPath, 262144, invocation.RequestSha256, token);
            var gate = new FileStream(gatePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            pins.PinCreatedFile(gate); gate.Flush(true);
            VoxCpmTrainingResourcePolicy.CheckPreflight(available.AvailableBytes());
            long began = time.GetTimestamp();
            child = (launcher ?? new VoxCpmTrainingProcessLauncher()).Start(StartInfo(runtime.Options, requestPath, invocation.RequestSha256, gatePath), new(VoxCpmTrainingResourcePolicy.MemoryCapBytes));
            VoxCpmTrainingResourcePolicy.CheckLimits(child.ReadJobMemory());
            var gated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var state = new StreamState();
            stdout = Drain(child.StandardOutput, false, state, gated, progress, streamCancellation.Token);
            stderr = Drain(child.StandardError, true, state, gated, progress, streamCancellation.Token);
            watchdog = Watch(child, available, time, began, monitor.Token); Task<int> exit = child.WaitForExitAsync();
            while (!gated.Task.IsCompletedSuccessfully)
            {
                Task next = await NextSignal(gated.Task, exit, stdout, stderr, watchdog).WaitAsync(token);
                await next;
                CheckFaults(stdout, stderr, watchdog);
                if (!gated.Task.IsCompletedSuccessfully && (exit.IsCompleted || stdout.IsCompleted)) throw Invalid();
            }
            CheckFaults(stdout, stderr, watchdog); token.ThrowIfCancellationRequested();
            VoxCpmTrainingResourcePolicy.CheckLimits(child.ReadJobMemory());
            if (state.Error) throw new VoxCpmTrainingExecutionException("voxcpm_training.worker.failed");
            state.GateOpened = true;
            await gate.WriteAsync("START\n"u8.ToArray(), token); gate.Flush(true);
            while (!exit.IsCompleted)
            {
                Task next = await NextSignal(exit, stdout, stderr, watchdog).WaitAsync(token);
                await next;
            }
            int code = await exit;
            await Stop(child); jobZero = true; monitor.Cancel();
            await FinishWatch(watchdog);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), token);
            try { await VerifyCache(cache); } finally { cacheChecked = true; }
            if (code != 0 || state.Error || !state.Ready) throw new VoxCpmTrainingExecutionException("voxcpm_training.worker.failed");
            return await VoxCpmTrainingProtocol.AcceptAsync(invocation, token);
        }
        catch (NativeOwnedLaunchException failure)
        {
            child = VoxCpmTrainingProcessLauncher.RetainFailure(failure.Ownership); unknownLaunch = true;
            throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false);
        }
        catch (OperationCanceledException) { throw new VoxCpmTrainingExecutionException("voxcpm_training.window.cancelled"); }
        catch (VoxCpmTrainingExecutionException error)
        { if (error.Code == "voxcpm_training.cache.changed") runtime.MarkCacheChanged(); throw; }
        catch { throw new VoxCpmTrainingExecutionException("voxcpm_training.worker.failed"); }
        finally
        {
            bool zero = child == null || jobZero;
            if (child != null && !jobZero && !unknownLaunch)
            { try { await Stop(child); zero = true; } catch { zero = false; } }
            monitor.Cancel(); streamCancellation.Cancel();
            if (watchdog != null) { try { await watchdog; } catch { } }
            foreach (Task? drain in new[] { stdout, stderr }) if (drain != null) { try { await drain.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
            bool changed = false;
            if (zero)
            {
                try { if (cache != null && !cacheChecked) await VerifyCache(cache); }
                catch { changed = true; }
                finally { try { child?.Dispose(); } finally { try { pins?.Dispose(); } finally { try { cache?.Dispose(); } finally { lease?.Dispose(); } } } }
            }
            else retained = new(child!, pins, cache, lease);
            operation.Release();
            if (!zero) throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false);
            if (changed) throw new VoxCpmTrainingExecutionException("voxcpm_training.cache.changed");
        }
    }
    public async ValueTask DisposeAsync()
    {
        await operation.WaitAsync();
        try
        {
            disposed = true;
            if (retained is { } owned)
            {
                try { await Stop(owned.Child); } catch { throw new VoxCpmTrainingExecutionException("voxcpm_training.cleanup", false); }
                try { if (owned.Cache != null) await VerifyCache(owned.Cache); }
                finally { owned.Dispose(); retained = null; }
            }
        }
        finally { operation.Release(); }
    }
    private async Task VerifyCache(VoxCpmTrainingCacheLease cache)
    {
        try { await cache.VerifyUnchangedAsync(CancellationToken.None); }
        catch { runtime.MarkCacheChanged(); throw new VoxCpmTrainingExecutionException("voxcpm_training.cache.changed"); }
    }
    private static Task Stop(IVoxCpmTrainingOwnedProcess child) => child.StopTreeAsync().WaitAsync(TimeSpan.FromSeconds(12));
    // A gate/root signal may complete between the loop condition and this call.
    // Only successful optional EOF signals may be omitted to avoid a busy loop.
    internal static Task<Task> NextSignal(Task terminal, params Task[] optional)
        => Task.WhenAny(optional.Where(t => !t.IsCompletedSuccessfully).Prepend(terminal));
    private static async Task FinishWatch(Task watchdog)
    { try { await watchdog; } catch (OperationCanceledException) { } }
    private static void CheckFaults(params Task[] tasks)
    { foreach (Task task in tasks) if (task.IsFaulted || task.IsCanceled) task.GetAwaiter().GetResult(); }
    private static async Task Watch(IVoxCpmTrainingOwnedProcess child, IVoxCpmTrainingMemoryReader memory, TimeProvider time, long began, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); VoxCpmTrainingResourcePolicy.CheckLimits(child.ReadJobMemory());
            string? code = VoxCpmTrainingResourcePolicy.Violation(memory.AvailableBytes(), time.GetElapsedTime(began));
            if (code != null) throw new VoxCpmTrainingExecutionException(code);
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, token);
        }
    }
    private static void NewOutput(VoxCpmTrainingFilePins pins, string root, string relative)
    {
        pins.PinDirectory(root); string current = root;
        string[] parts = relative.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (i == parts.Length - 1) Require(!Directory.Exists(current) && !File.Exists(current));
            Directory.CreateDirectory(current); pins.PinDirectory(current);
        }
    }
    private static async Task CheckClip(FileStream input, long samples, CancellationToken token)
    {
        Require(samples is >= 1 and <= 256000 && input.Length == 44 + samples * 2);
        byte[] header = new byte[44]; await input.ReadExactlyAsync(header, token); input.Position = 0;
        Require(header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) == input.Length - 8
            && header.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8) && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)) == 16
            && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)) == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) == 16000 && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)) == 32000
            && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32)) == 2 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) == 16
            && header.AsSpan(36, 4).SequenceEqual("data"u8) && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)) == samples * 2);
    }
    private static ProcessStartInfo StartInfo(VoxCpmTrainingRuntimeOptions options, string request, string sha, string gate)
    {
        var start = new ProcessStartInfo(options.PythonPath) { WorkingDirectory = options.WorkerRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string arg in new[] { "-I", "-S", "-B", "-u", Path.Combine(options.WorkerRoot, "worker.py"), "--request", request, "--request-sha256", sha, "--gate", gate }) start.ArgumentList.Add(arg);
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["PATH"] = Path.GetDirectoryName(options.PythonPath) + ";" + options.PythonBaseRoot + ";" + Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (string key in new[] { "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_HUB_DISABLE_TELEMETRY" }) start.Environment[key] = "1";
        start.Environment["CUDA_VISIBLE_DEVICES"] = "";
        foreach (string key in new[] { "OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMEXPR_NUM_THREADS" }) start.Environment[key] = "2";
        start.Environment["TOKENIZERS_PARALLELISM"] = "false"; return start;
    }
    private static async Task Drain(StreamReader reader, bool error, StreamState state, TaskCompletionSource gated,
        Func<VoxCpmTrainingProgress, Task> progress, CancellationToken token)
    {
        char[] buffer = new char[128]; var line = new StringBuilder(); int total = 0;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), token); if (count == 0) break;
            if ((total += count) > 4096) throw Invalid();
            for (int i = 0; i < count; i++)
            {
                char c = buffer[i]; if (c != '\n') { Require(c is >= (char)0x20 and <= (char)0x7e || c == '\r'); line.Append(c); Require(line.Length <= 128); continue; }
                string value = line.ToString().TrimEnd('\r'); line.Clear();
                if (error) { Require(value == "voxcpm_training.worker.failed" && !state.Error); state.Error = true; continue; }
                if (value == "voxcpm_training.worker.gated") { Require(!state.Gated); state.Gated = true; gated.TrySetResult(); continue; }
                Require(state.Gated && state.GateOpened && !state.Ready);
                if (value == "voxcpm_training.worker.ready") { state.Ready = true; continue; }
                int phase = value switch { "voxcpm_training.worker.loading" => 1, "voxcpm_training.worker.training" => 2, "voxcpm_training.worker.checkpointing" => 3, _ => 0 };
                Require(phase > 0 && phase >= state.Phase && phase <= state.Phase + 1); state.Phase = phase;
                await progress(new(value["voxcpm_training.worker.".Length..]));
            }
        }
        Require(line.Length == 0);
    }
    private sealed class StreamState { internal volatile bool Gated, GateOpened, Ready, Error; internal int Phase; }
    private sealed record Retained(IVoxCpmTrainingOwnedProcess Child, VoxCpmTrainingFilePins? Pins, VoxCpmTrainingCacheLease? Cache, VoxCpmTrainingRuntimeLease? Runtime) : IDisposable
    { public void Dispose() { try { Child.Dispose(); } finally { try { Pins?.Dispose(); } finally { try { Cache?.Dispose(); } finally { Runtime?.Dispose(); } } } } }
}

using System.Diagnostics;
using LocalAudiobookStudio.Application.TtsCorpus;
using LocalAudiobookStudio.WorkerHost.Engine;
using System.Numerics;
using LocalAudiobookStudio.Audio;

namespace LocalAudiobookStudio.App.TtsCorpus;

internal interface ITtsCorpusOwnedProcess : IDisposable
{
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    NativeJobMemoryObservation ReadJobMemory();
    Task<int> WaitForExitAsync(CancellationToken token = default);
    Task StopTreeAsync();
}
internal interface ITtsCorpusProcessLauncher
{
    ITtsCorpusOwnedProcess Start(ProcessStartInfo start, NativeJobMemoryPolicy policy);
}
internal interface ITtsCorpusMemoryReader { ulong AvailableBytes(); }

internal sealed class TtsCorpusNativeExecutor(TtsCorpusRuntime runtime, ITtsCorpusProcessLauncher? launcher = null,
    ITtsCorpusMemoryReader? memory = null, TimeProvider? clock = null) : ITtsCorpusExecutor, IAsyncDisposable
{
    private readonly SemaphoreSlim operation = new(1, 1);
    private Retained? retained;
    private bool disposed;

    public async Task<TtsCorpusWindowResult> RunAsync(TtsCorpusExecution execution, Func<TtsCorpusProgress, Task> progress, CancellationToken token)
    {
        await operation.WaitAsync(token);
        TtsCorpusRuntimeLease? lease = null; TtsCorpusFilePins? pins = null; ITtsCorpusOwnedProcess? child = null;
        using var monitor = new CancellationTokenSource();
        using var streamCancellation = new CancellationTokenSource();
        Task? stdout = null, stderr = null, watchdog = null;
        bool launchCleanupUnknown = false;
        try
        {
            if (disposed || retained != null) throw new TtsCorpusExecutionException("tts_corpus.cleanup", false);
            token.ThrowIfCancellationRequested();
            var available = memory ?? new TtsCorpusMemoryReader(); var time = clock ?? TimeProvider.System;
            TtsCorpusResourcePolicy.CheckPreflight(available.AvailableBytes());
            lease = await runtime.AcquireAsync(token);
            if (lease.Fingerprint != execution.RuntimeFingerprint) throw new TtsCorpusExecutionException("tts_corpus.runtime.changed");
            pins = new TtsCorpusFilePins();
            var invocation = TtsCorpusProtocol.Create(execution, runtime.Options, lease.ModelFiles, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            _ = await pins.ReadAsync(execution.SourcePath, checked(execution.SourceSampleCount * 2 + 1048576), execution.SourceSha256, token);
            var source = await WavInspector.InspectAsync(execution.SourcePath, token);
            if (source.SampleCount != execution.SourceSampleCount || !source.Sha256.Equals(execution.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new TtsCorpusExecutionException("tts_corpus.source.changed");
            pins.PinDirectory(execution.ProjectRoot);
            string output = TtsCorpusFilePins.Resolve(execution.ProjectRoot, invocation.OutputRelativeRoot);
            if (Directory.Exists(output) || File.Exists(output)) throw new TtsCorpusExecutionException("tts_corpus.protocol.invalid");
            Directory.CreateDirectory(output); pins.PinDirectory(output);
            string requestPath = TtsCorpusFilePins.Resolve(execution.ProjectRoot, invocation.OutputRelativeRoot + "/request.json");
            using (var requestFile = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await requestFile.WriteAsync(invocation.RequestBytes, token); requestFile.Flush(true); }
            _ = await pins.ReadAsync(requestPath, 1048576, invocation.RequestSha256, token);
            string gatePath = TtsCorpusFilePins.Resolve(execution.ProjectRoot, invocation.OutputRelativeRoot + "/START");
            var gate = new FileStream(gatePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            pins.PinCreatedFile(gate); gate.Flush(true);
            var start = StartInfo(runtime.Options, requestPath, invocation.RequestSha256, gatePath);
            TtsCorpusResourcePolicy.CheckPreflight(available.AvailableBytes());
            long began = time.GetTimestamp();
            // The native boundary installs the aggregate cap and associates the Job at creation.
            child = (launcher ?? new TtsCorpusProcessLauncher()).Start(start, new(TtsCorpusResourcePolicy.MemoryCapBytes));
            CheckLimits(child.ReadJobMemory());
            var gated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streams = new StreamState();
            stdout = Drain(child.StandardOutput, false, streams, gated, progress, streamCancellation.Token);
            stderr = Drain(child.StandardError, true, streams, gated, progress, streamCancellation.Token);
            watchdog = Watch(child, available, time, began, monitor.Token);
            Task<int> exit = child.WaitForExitAsync();
            Task first = await Task.WhenAny(gated.Task, exit, stdout, stderr, watchdog).WaitAsync(token);
            if (!gated.Task.IsCompletedSuccessfully)
            { await first; throw new TtsCorpusExecutionException("tts_corpus.protocol.invalid"); }
            await gated.Task; token.ThrowIfCancellationRequested();
            foreach (var task in new[] { stdout, stderr, watchdog }) if (task.IsFaulted || task.IsCanceled) await task;
            CheckLimits(child.ReadJobMemory());
            // Same pre-created file, no BOM/CRLF/replacement. Only after pins and owned readback.
            await gate.WriteAsync("START\n"u8.ToArray(), token); gate.Flush(true);
            while (!exit.IsCompleted)
            {
                var waits = new[] { exit, stdout, stderr, watchdog }.Where(t => !t.IsCompletedSuccessfully).ToArray();
                Task next = await Task.WhenAny(waits).WaitAsync(token); await next;
            }
            int code = await exit;
            await Stop(child); // Required even when the root already exited successfully.
            monitor.Cancel();
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2));
            if (code != 0 || streams.Error || !streams.Ready) throw new TtsCorpusExecutionException("tts_corpus.worker.failed");
            return await TtsCorpusProtocol.AcceptAsync(invocation, token);
        }
        catch (NativeOwnedLaunchException failure)
        {
            child = TtsCorpusProcessLauncher.RetainFailure(failure.Ownership); launchCleanupUnknown = true;
            throw new TtsCorpusExecutionException("tts_corpus.cleanup", false);
        }
        catch (OperationCanceledException) { throw new TtsCorpusExecutionException("tts_corpus.window.cancelled"); }
        catch (TtsCorpusExecutionException) { throw; }
        catch { throw new TtsCorpusExecutionException("tts_corpus.worker.failed"); }
        finally
        {
            bool zero = child == null;
            if (child != null && !launchCleanupUnknown)
            {
                try { await Stop(child); zero = true; } catch { zero = false; }
            }
            monitor.Cancel();
            streamCancellation.Cancel();
            if (watchdog != null) { try { await watchdog; } catch { } }
            foreach (Task? drain in new[] { stdout, stderr })
                if (drain != null) { try { await drain.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
            if (zero)
            {
                try { child?.Dispose(); } finally { try { pins?.Dispose(); } finally { lease?.Dispose(); } }
            }
            else
            {
                // Intentionally retain every native/runtime/source identity. Runner keeps its
                // ComputeGate and artifact ownership when CleanupConfirmed is false.
                retained = new(child!, pins!, lease!);
            }
            operation.Release();
            if (!zero) throw new TtsCorpusExecutionException("tts_corpus.cleanup", false);
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
                try { await Stop(owned.Child); } catch { throw new TtsCorpusExecutionException("tts_corpus.cleanup", false); }
                owned.Dispose(); retained = null;
            }
        }
        finally { operation.Release(); }
    }

    private static Task Stop(ITtsCorpusOwnedProcess child) => child.StopTreeAsync().WaitAsync(TimeSpan.FromSeconds(12));
    private static void CheckLimits(NativeJobMemoryObservation readback)
    {
        if ((readback.LimitFlags & 0x2210) != 0x2210 || readback.AggregateJobMemoryLimitBytes != (ulong)TtsCorpusResourcePolicy.MemoryCapBytes
            || BitOperations.PopCount((ulong)readback.AffinityMask) is < 1 or > 2
            || readback.PeakJobMemoryUsedBytes > (ulong)TtsCorpusResourcePolicy.MemoryCapBytes)
            throw new TtsCorpusExecutionException("tts_corpus.memory.policy");
    }

    private static async Task Watch(ITtsCorpusOwnedProcess child, ITtsCorpusMemoryReader memory, TimeProvider time, long began, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); CheckLimits(child.ReadJobMemory());
            string? violation = TtsCorpusResourcePolicy.Violation(memory.AvailableBytes(), time.GetElapsedTime(began));
            if (violation != null) throw new TtsCorpusExecutionException(violation);
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, token);
        }
    }

    private static ProcessStartInfo StartInfo(TtsCorpusRuntimeOptions options, string request, string sha, string gate)
    {
        var start = new ProcessStartInfo(options.PythonPath) { WorkingDirectory = options.WorkerRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string arg in new[] { "-I", "-S", "-B", "-u", Path.Combine(options.WorkerRoot, "worker.py"), "--request", request, "--request-sha256", sha, "--gate", gate }) start.ArgumentList.Add(arg);
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["PATH"] = Path.GetDirectoryName(options.PythonPath) + ";" + options.PythonBaseRoot + ";" + Environment.GetFolderPath(Environment.SpecialFolder.System);
        start.Environment["HF_HUB_OFFLINE"] = "1"; start.Environment["TRANSFORMERS_OFFLINE"] = "1"; start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["CUDA_VISIBLE_DEVICES"] = ""; start.Environment["OMP_NUM_THREADS"] = "2"; start.Environment["MKL_NUM_THREADS"] = "2";
        start.Environment["OPENBLAS_NUM_THREADS"] = "2"; start.Environment["NUMEXPR_NUM_THREADS"] = "2"; start.Environment["TOKENIZERS_PARALLELISM"] = "false";
        return start;
    }

    private static async Task Drain(StreamReader reader, bool error, StreamState state, TaskCompletionSource gated,
        Func<TtsCorpusProgress, Task> progress, CancellationToken token)
    {
        char[] buffer = new char[128]; var line = new System.Text.StringBuilder(); int total = 0;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) break;
            if ((total += count) > 4096) throw new TtsCorpusExecutionException("tts_corpus.protocol.invalid");
            for (int character = 0; character < count; character++)
            {
                char c = buffer[character];
                if (c != '\n') { line.Append(c); if (line.Length > 128) throw new TtsCorpusExecutionException("tts_corpus.protocol.invalid"); continue; }
                string value = line.ToString().TrimEnd('\r'); line.Clear();
                if (error)
                {
                    if (value != "tts_corpus.worker.failed" || state.Error) throw new TtsCorpusExecutionException("tts_corpus.protocol.invalid");
                    state.Error = true;
                }
                else if (value == "tts_corpus.worker.gated" && !state.Gated && !state.Ready) { state.Gated = true; gated.TrySetResult(); }
                else if (value is "tts_corpus.worker.preparing" or "tts_corpus.worker.transcribing" && state.Gated && !state.Phase && !state.Ready)
                { state.Phase = true; await progress(new(value.EndsWith("preparing", StringComparison.Ordinal) ? "preparing" : "transcribing")); }
                else if (value == "tts_corpus.worker.ready" && state.Gated && !state.Ready) state.Ready = true;
                else throw new TtsCorpusExecutionException("tts_corpus.protocol.invalid");
            }
        }
        if (line.Length != 0) throw new TtsCorpusExecutionException("tts_corpus.protocol.invalid");
    }

    private sealed class StreamState { internal bool Gated, Phase, Ready, Error; }
    private sealed record Retained(ITtsCorpusOwnedProcess Child, TtsCorpusFilePins Pins, TtsCorpusRuntimeLease Runtime) : IDisposable
    { public void Dispose() { try { Child.Dispose(); } finally { try { Pins.Dispose(); } finally { Runtime.Dispose(); } } } }
}

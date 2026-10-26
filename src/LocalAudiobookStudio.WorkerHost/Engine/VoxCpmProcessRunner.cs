using System.Diagnostics;
using System.Globalization;
using System.Text;
using LocalAudiobookStudio.Audio;
using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.WorkerHost.Pipe;

namespace LocalAudiobookStudio.WorkerHost.Engine;

/// <summary>Publication fault-injection hooks. The runner owns the pinned durable flush and rename.</summary>
public interface IOutputPublication
{
    void Staged(string part);
    void BeforeFlush(string part);
    void Publishing(string part, string final);
    void Published(string final);
}
public sealed class OutputPublication : IOutputPublication
{
    public void Staged(string part) { }
    public void BeforeFlush(string part) { }
    public void Publishing(string part, string final) { }
    public void Published(string final) { }
}

/// <summary>Only a bounded classification window is retained; native text is never logged or emitted.</summary>
public sealed class NativeOutputClassifier
{
    private string window = "";
    public int RetainedCharacters => window.Length;
    public bool IsCudaOutOfMemory { get; private set; }
    public void Add(string text)
    {
        if (text.Length > 65536) text = text[^65536..];
        window += text;
        if (window.Contains("CUDA out of memory", StringComparison.OrdinalIgnoreCase) ||
            window.Contains("cudaMalloc failed: out of memory", StringComparison.OrdinalIgnoreCase) ||
            window.Contains("CUDA error: out of memory", StringComparison.OrdinalIgnoreCase)) IsCudaOutOfMemory = true;
        if (window.Length > 65536) window = window[^65536..];
    }
}

/// <summary>
/// Caller MUST pre-register final + final.part and hold its ProjectArtifactLease through publication
/// and SQLite acceptance. This assembly never opens, acquires, recovers or releases that lease.
/// </summary>
public sealed class VoxCpmProcessRunner : IWorkerEngine, IAsyncDisposable
{
    private readonly string project;
    private readonly Func<string, CancellationToken, Task<ResolvedArtifacts>> resolve;
    private readonly Func<ProcessStartInfo, NativeChild> launch;
    private readonly TimeSpan timeout;
    private readonly IOutputPublication publication;
    private readonly SemaphoreSlim engine = new(1, 1);
    private readonly SemaphoreSlim shutdown = new(1, 1);
    private NativeOwnedLaunchException? poison;
    private RetainedLaunch? retained;
    public VoxCpmProcessRunner(string projectRoot, ArtifactResolver resolver, TimeSpan timeout, IOutputPublication? publication = null)
        : this(projectRoot, resolver.ResolveAsync, timeout, start => WindowsNativeLauncher.Start(start), publication) { }
    internal VoxCpmProcessRunner(string projectRoot, Func<string, CancellationToken, Task<ResolvedArtifacts>> resolve, TimeSpan timeout,
        Func<ProcessStartInfo, NativeChild> launch, IOutputPublication? publication = null)
    {
        project = WorkerPaths.Root(projectRoot); this.resolve = resolve; this.launch = launch; this.timeout = timeout; this.publication = publication ?? new OutputPublication();
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1)) throw new WorkerException("worker.configuration");
    }

    public async Task<SynthesisResult> SynthesizeAsync(SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref poison) is { } unknown) throw unknown;
        Validate(command);
        if (!await engine.WaitAsync(0, cancellationToken)) throw new WorkerException("worker.arguments");
        string? part = null;
        string final = string.Empty;
        PinnedOutputFile.FileIdentity? reservedIdentity = null;
        bool owned = false, published = false, canceled = false, cleanupUnknown = false;
        FileStream? promptHold = null; ResolvedArtifacts? artifacts = null;
        var publicationGate = new object();
        using var cancellation = cancellationToken.Register(() => { lock (publicationGate) { if (!published) canceled = true; } });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string prompt = WorkerPaths.Resolve(project, command.PromptWav);
            final = WorkerPaths.Resolve(project, command.FinalPath);
            if (!command.FinalPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) throw new WorkerException("worker.path");
            part = WorkerPaths.Resolve(project, command.FinalPath + ".part");
            if (File.Exists(final) || File.Exists(part) || Directory.Exists(final) || Directory.Exists(part)) throw new WorkerException("worker.output.exists");
            // Caller owns the registration and creates the directories under the same lease.
            if (!Directory.Exists(Path.GetDirectoryName(final))) throw new WorkerException("worker.path");
            progress(new("validatingInputs"));
            promptHold = new FileStream(prompt, FileMode.Open, FileAccess.Read, FileShare.Read);
            var promptInfo = await WavInspector.InspectAsync(prompt, cancellationToken);
            if (promptInfo.IsClipped) throw new WorkerException("worker.arguments");
            artifacts = await resolve(command.ProfileId, cancellationToken);
            var start = BuildStartInfo(command, prompt, part, artifacts);
            _ = WorkerPaths.Resolve(project, command.PromptWav); _ = WorkerPaths.Resolve(project, command.FinalPath + ".part");
            // Reserve this request's staging name. A raced existing file is never deleted.
            using (var reservation = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                reservedIdentity = PinnedOutputFile.CaptureIdentity(reservation.SafeFileHandle);
            }
            publication.Staged(part);
            int exitCode; NativeOutputClassifier stdout = new(), stderr = new();
            using (var native = launch(start))
            {
                progress(new("generating"));
                using var drains = new CancellationTokenSource();
                Task outDrain = Drain(native.StandardOutput, stdout, drains.Token), errDrain = Drain(native.StandardError, stderr, drains.Token);
                using var deadline = new CancellationTokenSource(timeout);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                try { exitCode = await native.WaitForExitAsync(stop.Token); }
                catch (OperationCanceledException)
                {
                    await native.StopTreeAsync();
                    await FinishDrains(outDrain, errDrain, drains);
                    throw new WorkerException(cancellationToken.IsCancellationRequested ? "worker.canceled" : "worker.timeout");
                }
                await native.StopTreeAsync();
                await FinishDrains(outDrain, errDrain, drains);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (exitCode != 0) throw new WorkerException(stdout.IsCudaOutOfMemory || stderr.IsCudaOutOfMemory ? "worker.cuda.oom" : "worker.crash", exitCode);
            _ = WorkerPaths.Resolve(project, command.FinalPath + ".part");
            if (!File.Exists(part) || new FileInfo(part).Length == 0) throw new WorkerException("worker.output.missing");
            progress(new("validatingOutput"));
            _ = WorkerPaths.Resolve(project, command.FinalPath + ".part"); _ = WorkerPaths.Resolve(project, command.FinalPath);
            using var pinned = PinnedOutputFile.Open(part, final, reservedIdentity!.Value);
            publication.BeforeFlush(part);
            pinned.Flush();
            var inspection = await pinned.InspectAsync(cancellationToken);
            if (inspection.IsClipped) throw new WorkerException("worker.output.invalid");
            VoxCpmOutputPolicy.EnsureBelowDecodeLimit(command.Steps, inspection.SampleCount);
            long length = await pinned.VerifyIdentityAsync(inspection.Sha256, cancellationToken);
            progress(new("publishing"));
            lock (publicationGate)
            {
                if (canceled || cancellationToken.IsCancellationRequested) throw new WorkerException("worker.canceled");
                _ = WorkerPaths.Resolve(project, command.FinalPath + ".part"); _ = WorkerPaths.Resolve(project, command.FinalPath);
                publication.Publishing(part, final);
                if (canceled || cancellationToken.IsCancellationRequested) throw new WorkerException("worker.canceled");
                pinned.Rename(final); published = true;
                publication.Published(final);
            }
            return new(command.FinalPath, length, inspection.Sha256.ToLowerInvariant(), inspection.Channels, (int)inspection.SampleRate, inspection.BitsPerSample,
                inspection.Duration.TotalSeconds, command.ProfileId, ArtifactResolver.WorkerVersion, ArtifactResolver.ModelVersion);
        }
        catch (NativeOwnedLaunchException failure)
        {
            cleanupUnknown = true;
            retained = new(failure, promptHold, artifacts);
            Volatile.Write(ref poison, failure);
            promptHold = null; artifacts = null;
            throw;
        }
        catch (OperationCanceledException) { throw new WorkerException("worker.canceled"); }
        catch (WavInspectionException) { throw new WorkerException("worker.output.invalid"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new WorkerException("worker.storage"); }
        finally
        {
            try
            {
                if (!cleanupUnknown && owned && !published && part != null && reservedIdentity is { } identity)
                {
                    _ = WorkerPaths.Resolve(project, command.FinalPath + ".part");
                    PinnedOutputFile.DeleteOwnedIfPresent(part, final, identity);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or WorkerException) { throw new WorkerException("worker.cleanup"); }
            finally
            {
                if (!cleanupUnknown)
                {
                    try { artifacts?.Dispose(); } finally { try { promptHold?.Dispose(); } finally { engine.Release(); } }
                }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await shutdown.WaitAsync();
        try
        {
            if (retained is not { } owned) return;
            try { await owned.Failure.Ownership.StopTreeAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
            catch { throw owned.Failure; }
            owned.Dispose(); retained = null;
            // Poison and engine exclusion stay permanent for this host. The registered
            // staged file is left for outer ownership cleanup after host Job-zero.
        }
        finally { shutdown.Release(); }
    }
    private sealed record RetainedLaunch(NativeOwnedLaunchException Failure, FileStream? Prompt, ResolvedArtifacts? Artifacts) : IDisposable
    {
        public void Dispose()
        { try { Failure.Ownership.Dispose(); } finally { try { Artifacts?.Dispose(); } finally { Prompt?.Dispose(); } } }
    }
    public static ProcessStartInfo BuildStartInfo(SynthesizeSegmentCommand command, string prompt, string part, ResolvedArtifacts artifacts)
    {
        Validate(command);
        if ((artifacts.UseCpu && artifacts.GpuLayers != 0) || (!artifacts.UseCpu && artifacts.GpuLayers < -1)) throw new WorkerException("worker.arguments");
        var start = new ProcessStartInfo(artifacts.Executable)
        {
            WorkingDirectory = Path.GetDirectoryName(artifacts.Executable)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        // Do not forward the launch session secret to native code or its descendants.
        start.Environment.Remove("LAS_WORKER_SESSION_SECRET");
        var invariant = CultureInfo.InvariantCulture;
        string[] arguments = ["-t", command.Text, "--prompt-wav", prompt, "--prompt-text", command.Transcript.TrimEnd() + " ", "-o", part,
            "--steps", command.Steps.ToString(invariant), "--timesteps", command.Timesteps.ToString(invariant),
            "--cfg", Sampling(command.Cfg), "--temperature", Sampling(command.Temperature), "--seed", command.Seed.ToString(invariant)];
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (artifacts.UseCpu) start.ArgumentList.Add("--cpu");
        start.ArgumentList.Add("--n-gpu-layers"); start.ArgumentList.Add(artifacts.GpuLayers.ToString(invariant));
        start.ArgumentList.Add(artifacts.BaseModel); start.ArgumentList.Add(artifacts.AcousticModel); return start;
    }
    private static void Validate(SynthesizeSegmentCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Text) || string.IsNullOrWhiteSpace(command.Transcript) || command.Text.Contains('\0') || command.Transcript.Contains('\0') ||
            command.Steps is < 1 or > 200 || command.Timesteps is < 1 or > 100 || !double.IsFinite(command.Cfg) || command.Cfg <= 0 ||
            !double.IsFinite(command.Temperature) || command.Temperature <= 0 || command.Seed is < int.MinValue or > int.MaxValue) throw new WorkerException("worker.arguments");
    }
    private static string Sampling(double value)
    {
        string precise = value.ToString("R", CultureInfo.InvariantCulture);
        return precise.Contains('E') || precise.Contains('.') ? precise : precise + ".0";
    }
    private static async Task Drain(StreamReader reader, NativeOutputClassifier classification, CancellationToken token)
    {
        char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0) classification.Add(new string(buffer, 0, count));
    }
    private static async Task FinishDrains(Task output, Task error, CancellationTokenSource stop)
    {
        try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { stop.Cancel(); throw new WorkerException("worker.launch"); }
        WorkerLifetimeTrace.Report("drains_completed");
    }
}

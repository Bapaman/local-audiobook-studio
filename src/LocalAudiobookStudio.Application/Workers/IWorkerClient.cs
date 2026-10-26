using LocalAudiobookStudio.Contracts.Protocol;
using LocalAudiobookStudio.Core.Jobs;
using LocalAudiobookStudio.Core.Projects;

namespace LocalAudiobookStudio.Application.Workers;

public interface IWorkerClient : IAsyncDisposable
{
    Task<SynthesisResult> SynthesizeAsync(string requestId, SynthesizeSegmentCommand command, Action<ProgressEvent> progress, CancellationToken token);
    Task CancelAsync(string targetRequestId, CancellationToken token);
}
public interface IWorkerClientFactory { Task<IWorkerClient> OpenAsync(string projectRoot, CancellationToken token); }
public sealed record RuntimeReadiness(bool Ready, bool Experimental, string? DiagnosticCode, string ProfileId);
public interface IRuntimeReadiness { Task<RuntimeReadiness> GetAsync(CancellationToken token); }
public sealed record RuntimeDescriptor(GenerationPreset Preset, VersionPins Versions);
public interface IRuntimeDescriptor { Task<RuntimeDescriptor> GetAsync(CancellationToken token); }

/// <summary>App owns the process tree; transport may close it but cannot infer exit from a pipe ACK.</summary>
public interface IWorkerSessionLifetime
{
    /// <summary>Best-effort stop is required even at zero budget. True means owned tree exit is proved.</summary>
    Task<bool> StopAsync(TimeSpan remaining, CancellationToken token);
}

public sealed class WorkerClientException : Exception
{
    public string Code { get; }
    public bool CleanupConfirmed { get; }
    public WorkerClientException(string code, bool cleanupConfirmed) : base(SafeCode(code))
    { Code = SafeCode(code); CleanupConfirmed = cleanupConfirmed; }
    private static string SafeCode(string code) => code switch
    {
        "worker.canceled" or "worker.timeout" or "worker.crash" or "worker.cuda.oom" or "worker.output.invalid" or
        "worker.output.missing" or "worker.output.exists" or "worker.output.limit" or "worker.arguments" or "worker.path" or "worker.artifacts" or
        "worker.developer.required" or "worker.profile" or "worker.storage" or "worker.cleanup" or "worker.launch" or
        "worker.unsupported.platform" or "worker.configuration" or "worker.internal" or "worker.authentication" or
        "worker.busy" or "worker.runtime.not_ready" or "worker.interrupted" or "protocol.command" or "protocol.payload" or
        "protocol.frame" or "protocol.version" => code,
        _ => "worker.internal"
    };
}

public sealed record WorkerClientOptions
{
    public TimeSpan ConnectAndHandshake { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HeartbeatGap { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan Synthesis { get; init; } = TimeSpan.FromSeconds(1800);
    public TimeSpan CancelAck { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Shutdown { get; init; } = TimeSpan.FromSeconds(30);
    public void Validate()
    {
        if (!Valid(ConnectAndHandshake, 15) || !Valid(HeartbeatGap, 15) || !Valid(Synthesis, 1800) || !Valid(CancelAck, 5) || !Valid(Shutdown, 30))
            throw new WorkerClientException("worker.configuration", false);
    }
    private static bool Valid(TimeSpan value, int seconds) => value > TimeSpan.Zero && value <= TimeSpan.FromSeconds(seconds);
}

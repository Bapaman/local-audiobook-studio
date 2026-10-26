using System.Text.Json.Serialization;

namespace LocalAudiobookStudio.Contracts.Protocol;

public sealed record HandshakeCommand([property: JsonRequired, JsonPropertyName("sessionSecret")] string SessionSecret);
public sealed record CapabilitiesCommand;
public sealed record HealthCommand;
public sealed record ShutdownCommand;
public sealed record CancelRequestCommand([property: JsonRequired, JsonPropertyName("targetRequestId")] string TargetRequestId);
public sealed record SynthesizeSegmentCommand(
    [property: JsonRequired, JsonPropertyName("text")] string Text,
    [property: JsonRequired, JsonPropertyName("promptWav")] string PromptWav,
    [property: JsonRequired, JsonPropertyName("transcript")] string Transcript,
    [property: JsonRequired, JsonPropertyName("finalPath")] string FinalPath,
    [property: JsonRequired, JsonPropertyName("profileId")] string ProfileId,
    [property: JsonRequired, JsonPropertyName("steps")] int Steps,
    [property: JsonRequired, JsonPropertyName("timesteps")] int Timesteps,
    [property: JsonRequired, JsonPropertyName("cfg")] double Cfg,
    [property: JsonRequired, JsonPropertyName("temperature")] double Temperature,
    [property: JsonRequired, JsonPropertyName("seed")] long Seed);
public sealed record ProgressEvent([property: JsonPropertyName("phase")] string Phase);
public sealed record HeartbeatEvent([property: JsonPropertyName("state")] string State);
public sealed record ResultEvent([property: JsonPropertyName("state")] string State);
public sealed record CapabilitiesResult([property: JsonPropertyName("profiles")] string[] Profiles,
    [property: JsonPropertyName("experimental")] bool Experimental,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion);
public sealed record SynthesisResult(
    [property: JsonPropertyName("finalPath")] string FinalPath,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("channels")] int Channels,
    [property: JsonPropertyName("sampleRate")] int SampleRate,
    [property: JsonPropertyName("bitsPerSample")] int BitsPerSample,
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("workerVersion")] string WorkerVersion,
    [property: JsonPropertyName("modelVersion")] string ModelVersion);
public record Diagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("messageKey")] string MessageKey,
    [property: JsonPropertyName("actionKey")] string ActionKey,
    [property: JsonPropertyName("exitCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ExitCode = null);
public sealed record WarningEvent : Diagnostic
{
    [JsonConstructor]
    public WarningEvent(string code, string category, string messageKey, string actionKey, int? exitCode = null) : base(code, category, messageKey, actionKey, exitCode) { }
    public WarningEvent(Diagnostic value) : base(value.Code, value.Category, value.MessageKey, value.ActionKey, value.ExitCode) { }
}
public sealed record ErrorEvent : Diagnostic
{
    [JsonConstructor]
    public ErrorEvent(string code, string category, string messageKey, string actionKey, int? exitCode = null) : base(code, category, messageKey, actionKey, exitCode) { }
    public ErrorEvent(Diagnostic value) : base(value.Code, value.Category, value.MessageKey, value.ActionKey, value.ExitCode) { }
}

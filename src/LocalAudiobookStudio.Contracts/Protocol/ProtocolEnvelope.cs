using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAudiobookStudio.Contracts.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int MaximumPayloadJsonBytes = 1_048_576;
}

/// <summary>The shared JSON message metadata, independent of transport framing.</summary>
public sealed record ProtocolEnvelope<TPayload>(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("payload")] TPayload Payload)
{
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Validate before sending or after deserializing an envelope.
    /// The payload limit counts serialized JSON UTF-8 bytes, including JSON punctuation.
    /// </summary>
    public void Validate(int maxPayloadJsonBytes = ProtocolConstants.MaximumPayloadJsonBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadJsonBytes);
        if (ProtocolVersion != ProtocolConstants.Version)
        {
            throw new ArgumentException("Unsupported protocol version.", nameof(ProtocolVersion));
        }

        if (string.IsNullOrWhiteSpace(RequestId))
        {
            throw new ArgumentException("A request ID is required.", nameof(RequestId));
        }

        if (string.IsNullOrWhiteSpace(Type))
        {
            throw new ArgumentException("A message type is required.", nameof(Type));
        }
        if (!DateTimeOffset.TryParseExact(Timestamp, TimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp) || timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("An ISO 8601 UTC timestamp is required.", nameof(Timestamp));
        }

        if (JsonSerializer.SerializeToUtf8Bytes(Payload, PayloadJsonOptions).Length > maxPayloadJsonBytes)
        {
            throw new ArgumentException("The JSON payload exceeds the configured byte limit.", nameof(Payload));
        }
    }
}

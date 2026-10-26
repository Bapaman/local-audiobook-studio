using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LocalAudiobookStudio.Contracts.Protocol;

namespace LocalAudiobookStudio.Contracts.Tests;

public class ProtocolEnvelopeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [Fact]
    public void Valid_message_round_trips_wire_fields_and_utf8_cyrillic()
    {
        const string wire = """
            {"protocolVersion":1,"requestId":"request-1","type":"handshake","timestamp":"2026-10-06T12:34:56.1234567Z","payload":{"text":"Привет, ёж!","chapter":2}}
            """;

        var message = JsonSerializer.Deserialize<ProtocolEnvelope<JsonElement>>(Encoding.UTF8.GetBytes(wire));
        Assert.NotNull(message);
        message.Validate();
        Assert.Equal(1, message.ProtocolVersion);
        Assert.Equal(ProtocolConstants.Version, message.ProtocolVersion);
        Assert.Equal("request-1", message.RequestId);
        Assert.Equal("handshake", message.Type);
        Assert.Equal("2026-10-06T12:34:56.1234567Z", message.Timestamp);
        Assert.Equal("Привет, ёж!", message.Payload.GetProperty("text").GetString());
        Assert.Equal(2, message.Payload.GetProperty("chapter").GetInt32());

        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        Assert.Contains("Привет, ёж!", Encoding.UTF8.GetString(encoded));
        var restored = JsonSerializer.Deserialize<ProtocolEnvelope<JsonElement>>(encoded);
        Assert.NotNull(restored);
        restored.Validate();
        Assert.Equal(message.RequestId, restored.RequestId);
        Assert.Equal(message.Type, restored.Type);
        Assert.Equal(message.Timestamp, restored.Timestamp);
        Assert.Equal("Привет, ёж!", restored.Payload.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Unsupported_protocol_is_rejected(int version)
    {
        Assert.Throws<ArgumentException>(() => (ValidMessage() with { ProtocolVersion = version }).Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t")]
    public void Missing_request_id_is_rejected(string? requestId)
    {
        Assert.Throws<ArgumentException>(() => (ValidMessage() with { RequestId = requestId! }).Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t")]
    public void Missing_type_is_rejected(string? type)
    {
        Assert.Throws<ArgumentException>(() => (ValidMessage() with { Type = type! }).Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026-10-06")]
    [InlineData("2026-10-06T12:34:56")]
    [InlineData("2026-10-06T12:34:56+03:00")]
    [InlineData("2026-02-30T12:34:56Z")]
    public void Missing_invalid_or_non_utc_timestamp_is_rejected(string? timestamp)
    {
        Assert.Throws<ArgumentException>(() => (ValidMessage() with { Timestamp = timestamp! }).Validate());
    }

    [Theory]
    [InlineData("2026-10-06T12:34:56Z")]
    [InlineData("2026-10-06T12:34:56.1234567Z")]
    [InlineData("2026-10-06T12:34:56+00:00")]
    public void Utc_iso_timestamp_is_accepted(string timestamp)
    {
        (ValidMessage() with { Timestamp = timestamp }).Validate();
    }

    [Fact]
    public void Payload_json_above_one_mib_is_rejected()
    {
        // A JSON string has two quote bytes in addition to its content.
        var message = new ProtocolEnvelope<string>(1, "request-1", "handshake", "2026-10-06T12:34:56Z", new string('a', 1_048_575));
        Assert.Throws<ArgumentException>(() => message.Validate());
    }

    [Fact]
    public void Payload_json_at_one_mib_is_accepted()
    {
        new ProtocolEnvelope<string>(1, "request-1", "handshake", "2026-10-06T12:34:56Z", new string('a', 1_048_574)).Validate();
    }

    [Fact]
    public void Configured_limit_counts_utf8_bytes_instead_of_utf16_characters()
    {
        // 'я' is two UTF-8 bytes. Four letters plus two JSON quotes need ten bytes.
        var message = new ProtocolEnvelope<string>(1, "request-1", "handshake", "2026-10-06T12:34:56Z", "яяяя");
        message.Validate(maxPayloadJsonBytes: 10);
        Assert.Throws<ArgumentException>(() => message.Validate(maxPayloadJsonBytes: 9));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_payload_limit_is_rejected(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidMessage().Validate(limit));
    }

    private static ProtocolEnvelope<JsonElement> ValidMessage() =>
        new(1, "request-1", "handshake", "2026-10-06T12:34:56Z", JsonSerializer.SerializeToElement(new { text = "Привет" }));
}

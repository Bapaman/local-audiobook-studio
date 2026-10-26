using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LocalAudiobookStudio.Contracts.Protocol;

public sealed class ProtocolException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>One codec per duplex stream; concurrent writes are serialized. Does not own the stream.</summary>
public sealed class ProtocolCodec(Stream stream)
{
    public const int MaximumFrameBytes = 1_048_576;
    private const int FramePrefixBytes = sizeof(int);
    private const int MaximumBodyBytes = MaximumFrameBytes - FramePrefixBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 32
    };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    { "handshake", "capabilities", "health", "synthesizeSegment", "cancelRequest", "shutdown", "progress", "heartbeat", "result", "warning", "error" };
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int poisoned;

    public async Task<ProtocolEnvelope<JsonElement>?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfPoisoned();
        byte[] prefix = new byte[4];
        if (!await ReadExactlyAsync(prefix, true, cancellationToken)) return null;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length is 0 or > MaximumBodyBytes) throw new ProtocolException("protocol.frame.size");
        byte[] body = new byte[length];
        await ReadExactlyAsync(body, false, cancellationToken);
        try { _ = StrictUtf8.GetCharCount(body); }
        catch (DecoderFallbackException) { throw new ProtocolException("protocol.utf8"); }
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            RejectDuplicateProperties(root);
            if (root.ValueKind != JsonValueKind.Object) throw new ProtocolException("protocol.fields");
            string[] required = ["protocolVersion", "requestId", "type", "timestamp", "payload"];
            if (root.EnumerateObject().Count() != 5 || required.Any(name => root.EnumerateObject().Count(p => p.Name == name) != 1))
                throw new ProtocolException("protocol.fields");
            if (!root.GetProperty("protocolVersion").TryGetInt32(out int version)) throw new ProtocolException("protocol.fields");
            if (required.Skip(1).Take(3).Any(name => root.GetProperty(name).ValueKind != JsonValueKind.String) || root.GetProperty("payload").ValueKind != JsonValueKind.Object)
                throw new ProtocolException("protocol.fields");
            var envelope = new ProtocolEnvelope<JsonElement>(version, root.GetProperty("requestId").GetString()!, root.GetProperty("type").GetString()!, root.GetProperty("timestamp").GetString()!, root.GetProperty("payload").Clone());
            Validate(envelope); return envelope;
        }
        catch (JsonException) { throw new ProtocolException("protocol.json"); }
        catch (InvalidOperationException) { throw new ProtocolException("protocol.fields"); }
    }

    public async Task WriteAsync<T>(ProtocolEnvelope<T> envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfPoisoned();
        byte[] body;
        try { body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions); }
        catch (JsonException) { throw new ProtocolException("protocol.json"); }
        if (body.Length is 0 or > MaximumBodyBytes) throw new ProtocolException("protocol.frame.size");
        using (var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }))
        {
            if (doc.RootElement.GetProperty("payload").ValueKind != JsonValueKind.Object) throw new ProtocolException("protocol.fields");
            RejectDuplicateProperties(doc.RootElement);
        }
        Validate(envelope);
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfPoisoned();
            try
            {
                await stream.WriteAsync(prefix, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch
            {
                Poison();
                throw;
            }
        }
        finally { writeGate.Release(); }
    }

    private static void Validate<T>(ProtocolEnvelope<T> envelope)
    {
        if (envelope.ProtocolVersion != 1) throw new ProtocolException("protocol.version");
        if (envelope.RequestId is null || envelope.RequestId.Length > 128 || envelope.Type is null || envelope.Type.Length > 64 || envelope.Timestamp is null || envelope.Timestamp.Length > 64)
            throw new ProtocolException("protocol.fields");
        if (!Types.Contains(envelope.Type)) throw new ProtocolException("protocol.type");
        try { envelope.Validate(); }
        catch (ArgumentException) { throw new ProtocolException("protocol.fields"); }
        catch (JsonException) { throw new ProtocolException("protocol.json"); }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ProtocolException("protocol.fields");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private async Task<bool> ReadExactlyAsync(Memory<byte> buffer, bool allowEof, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read;
            try { read = await stream.ReadAsync(buffer[offset..], token); }
            catch
            {
                Poison();
                throw;
            }
            if (read == 0)
            {
                if (allowEof && offset == 0) return false;
                Poison();
                throw new ProtocolException("protocol.frame.partial");
            }
            offset += read;
        }
        return true;
    }

    private void Poison() => Interlocked.Exchange(ref poisoned, 1);
    private void ThrowIfPoisoned()
    {
        if (Volatile.Read(ref poisoned) != 0) throw new ProtocolException("protocol.connection.poisoned");
    }
}

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Contracts.Protocol;

namespace LocalAudiobookStudio.Contracts.Tests;

public class ProtocolCodecTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static ProtocolEnvelope<JsonElement> Message(string id = "one") =>
        new(1, id, "health", "2026-10-06T12:00:00Z", JsonSerializer.SerializeToElement(new { text = "Ёж, мо́ре" }));

    [Fact]
    public async Task Cyrillic_frames_use_little_endian_prefix_and_survive_single_byte_reads()
    {
        using var wire = new MemoryStream(); var codec = new ProtocolCodec(wire);
        await codec.WriteAsync(Message(), Token); await codec.WriteAsync(Message("two"), Token);
        byte[] bytes = wire.ToArray();
        int first = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        Assert.Equal((byte)'{', bytes[4]); Assert.Contains("Ёж", Encoding.UTF8.GetString(bytes));
        Assert.Equal((byte)'{', bytes[first + 8]);
        using var source = new OneByteStream(bytes); var reader = new ProtocolCodec(source);
        Assert.Equal("one", (await reader.ReadAsync(Token))!.RequestId);
        Assert.Equal("two", (await reader.ReadAsync(Token))!.RequestId);
        Assert.Null(await reader.ReadAsync(Token));
    }

    [Theory]
    [InlineData(0)] [InlineData(1048573)] [InlineData(1048577)] [InlineData(uint.MaxValue)]
    public async Task Invalid_size_is_rejected_before_body_read(uint length)
    {
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, length);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => new ProtocolCodec(new MemoryStream(prefix)).ReadAsync(Token));
        Assert.Equal("protocol.frame.size", error.Code); Assert.Equal(error.Code, error.Message);
    }

    [Theory]
    [InlineData("01", "protocol.frame.partial")]
    [InlineData("030000007B", "protocol.frame.partial")]
    [InlineData("02000000FFFF", "protocol.utf8")]
    [InlineData("010000007B", "protocol.json")]
    public async Task Truncation_and_encoding_have_private_safe_codes(string hex, string code)
    {
        var error = await Assert.ThrowsAsync<ProtocolException>(() => new ProtocolCodec(new MemoryStream(Convert.FromHexString(hex))).ReadAsync(Token));
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("{}", "protocol.fields")]
    [InlineData("{\"protocolVersion\":2,\"requestId\":\"x\",\"type\":\"health\",\"timestamp\":\"2026-10-06T12:00:00Z\",\"payload\":{}}", "protocol.version")]
    [InlineData("{\"protocolVersion\":1,\"requestId\":\"x\",\"type\":\"unknown\",\"timestamp\":\"2026-10-06T12:00:00Z\",\"payload\":{}}", "protocol.type")]
    [InlineData("{\"protocolVersion\":1,\"requestId\":3,\"type\":\"health\",\"timestamp\":\"bad\",\"payload\":{}}", "protocol.fields")]
    public async Task Invalid_envelopes_are_rejected(string json, string code)
    {
        var error = await Assert.ThrowsAsync<ProtocolException>(() => ReadJson(json)); Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Whole_frame_limit_counts_envelope_and_depth_metadata_are_bounded()
    {
        using var wire = new MemoryStream(); var codec = new ProtocolCodec(wire);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => codec.WriteAsync(Message() with { Payload = JsonSerializer.SerializeToElement(new { text = new string('a', 1048500) }) }, Token));
        Assert.Equal("protocol.frame.size", error.Code); Assert.Equal(0, wire.Length);
        Assert.Equal("protocol.fields", (await Assert.ThrowsAsync<ProtocolException>(() => codec.WriteAsync(Message(new string('x', 129)), Token))).Code);
        string nested = new string('[', 40) + "0" + new string(']', 40);
        Assert.Equal("protocol.json", (await Assert.ThrowsAsync<ProtocolException>(() => ReadJson(nested))).Code);
    }

    [Fact]
    public async Task Outbound_payload_must_be_an_object_and_oversize_is_a_frame_error()
    {
        using var wire = new MemoryStream(); var codec = new ProtocolCodec(wire);
        Assert.Equal("protocol.fields", (await Assert.ThrowsAsync<ProtocolException>(() => codec.WriteAsync(Message() with { Payload = JsonSerializer.SerializeToElement("scalar") }, Token))).Code);
        Assert.Equal("protocol.frame.size", (await Assert.ThrowsAsync<ProtocolException>(() => codec.WriteAsync(Message() with { Payload = JsonSerializer.SerializeToElement(new { text = new string('x', 1048576) }) }, Token))).Code);
        Assert.Equal(0, wire.Length);
    }
    [Fact]
    public async Task Exactly_one_mib_wire_frame_is_accepted_and_next_byte_is_rejected()
    {
        const string prefix = "{\"protocolVersion\":1,\"requestId\":\"x\",\"type\":\"health\",\"timestamp\":\"2026-10-06T12:00:00Z\",\"payload\":{\"text\":\"";
        const string suffix = "\"}}";
        string json = prefix + new string('a', 1048576 - 4 - prefix.Length - suffix.Length) + suffix;
        Assert.Equal("x", (await ReadJson(json))!.RequestId);
        Assert.Equal("protocol.frame.size", (await Assert.ThrowsAsync<ProtocolException>(() => ReadJson(prefix + "a" + json[prefix.Length..]))).Code);

        using var wire = new MemoryStream(); var writer = new ProtocolCodec(wire);
        var exact = Message("x") with { Payload = JsonSerializer.SerializeToElement(new { text = new string('a', 1048576 - 4 - prefix.Length - suffix.Length) }) };
        await writer.WriteAsync(exact, Token);
        Assert.Equal(1048576, wire.Length);
        Assert.Equal("protocol.frame.size", (await Assert.ThrowsAsync<ProtocolException>(() => writer.WriteAsync(exact with
        {
            Payload = JsonSerializer.SerializeToElement(new { text = new string('a', 1048576 - 3 - prefix.Length - suffix.Length) })
        }, Token))).Code);
        Assert.Equal(1048576, wire.Length);
    }
    [Fact]
    public async Task Null_non_object_payload_and_duplicate_fields_fail_safely()
    {
        string wire = "{\"protocolVersion\":1,\"requestId\":\"x\",\"type\":\"health\",\"timestamp\":\"2026-10-06T12:00:00Z\",\"payload\":null}";
        Assert.Equal("protocol.fields", (await Assert.ThrowsAsync<ProtocolException>(() => ReadJson(wire))).Code);
        Assert.Equal("protocol.fields", (await Assert.ThrowsAsync<ProtocolException>(() => ReadJson(wire.Replace("null", "{\"a\":1,\"a\":2}")))).Code);
    }
    [Fact]
    public async Task Concurrent_writers_never_interleave_and_cancellation_is_observed()
    {
        using var wire = new MemoryStream(); var codec = new ProtocolCodec(wire);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => codec.WriteAsync(Message(i.ToString()), Token)));
        wire.Position = 0; var ids = new HashSet<string>();
        while (await codec.ReadAsync(Token) is { } message) Assert.True(ids.Add(message.RequestId));
        Assert.Equal(100, ids.Count);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => codec.WriteAsync(Message(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => codec.ReadAsync(canceled.Token));
    }

    [Fact]
    public async Task Failed_write_after_prefix_poisoned_codec_cannot_be_reused()
    {
        using var wire = new FailAfterPrefixWriteStream();
        var codec = new ProtocolCodec(wire);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => codec.WriteAsync(Message(), Token));
        var error = await Assert.ThrowsAsync<ProtocolException>(() => codec.WriteAsync(Message("second"), Token));

        Assert.Equal("protocol.connection.poisoned", error.Code);
    }

    [Fact]
    public async Task Failed_read_after_prefix_poisoned_codec_cannot_be_reused()
    {
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Message(), ProtocolCodec.JsonOptions));
        byte[] frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length); body.CopyTo(frame, 4);
        using var wire = new FailDuringBodyReadStream(frame);
        var codec = new ProtocolCodec(wire);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => codec.ReadAsync(Token));
        var error = await Assert.ThrowsAsync<ProtocolException>(() => codec.ReadAsync(Token));

        Assert.Equal("protocol.connection.poisoned", error.Code);
    }

    [Fact]
    public async Task Cancellation_during_partial_prefix_read_poisoned_codec_cannot_be_reused()
    {
        using var wire = new CancelDuringPartialPrefixReadStream([4, 0, 0, 0]);
        var codec = new ProtocolCodec(wire);
        using var canceled = new CancellationTokenSource();

        Task<ProtocolEnvelope<JsonElement>?> read = codec.ReadAsync(canceled.Token);
        await wire.SecondReadStarted.Task.WaitAsync(Token);
        Assert.Equal(2, wire.PrefixBytesRead);
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => codec.ReadAsync(Token));

        Assert.Equal("protocol.connection.poisoned", error.Code);
    }

    private static Task<ProtocolEnvelope<JsonElement>?> ReadJson(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json), frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length); body.CopyTo(frame, 4);
        return new ProtocolCodec(new MemoryStream(frame)).ReadAsync(Token);
    }
    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }
    private sealed class FailAfterPrefixWriteStream : MemoryStream
    {
        private int writes;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                await base.WriteAsync(buffer, token);
                return;
            }
            await base.WriteAsync(buffer[..1], token);
            throw new OperationCanceledException(token);
        }
    }
    private sealed class FailDuringBodyReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        private int reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref reads) == 1) return base.ReadAsync(buffer, token);
            _ = Read(buffer.Span[..1]);
            return ValueTask.FromException<int>(new OperationCanceledException(token));
        }
    }
    private sealed class CancelDuringPartialPrefixReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        private int reads;
        public int PrefixBytesRead { get; private set; }
        public TaskCompletionSource SecondReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                PrefixBytesRead = await base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], token);
                return PrefixBytesRead;
            }

            SecondReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
    }
}

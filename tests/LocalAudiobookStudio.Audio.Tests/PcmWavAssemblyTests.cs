using System.Buffers.Binary;

namespace LocalAudiobookStudio.Audio.Tests;

public sealed class PcmWavAssemblyTests
{
    [Fact]
    public async Task Assembly_copies_only_exact_pcm_in_supplied_order_with_one_header()
    {
        using var box = new WavSandbox();
        byte[] first = File.ReadAllBytes(box.Write(samples: 48, oddChunk: true));
        byte[] second = (byte[])first.Clone();
        BinaryPrimitives.WriteInt16LittleEndian(first.AsSpan(54), 123);
        BinaryPrimitives.WriteInt16LittleEndian(second.AsSpan(54), -456);
        using var a = new MemoryStream(first); using var b = new MemoryStream(second); using var output = new MemoryStream();
        Assert.Equal(96, await PcmWavAssembly.ReadPcmLengthAsync(a, TestContext.Current.CancellationToken));
        var assembly = await PcmWavAssembly.CreateAsync([a, b], TestContext.Current.CancellationToken);
        await assembly.CopyToAsync(output, TestContext.Current.CancellationToken);
        byte[] actual = output.ToArray();
        Assert.Equal(44 + 192, actual.Length); Assert.Equal(actual.Length, assembly.Length);
        Assert.Equal((short)123, BinaryPrimitives.ReadInt16LittleEndian(actual.AsSpan(44)));
        Assert.Equal((short)-456, BinaryPrimitives.ReadInt16LittleEndian(actual.AsSpan(140)));
        Assert.Equal(192u, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(40)));
        Assert.Equal(first, a.ToArray()); Assert.Equal(second, b.ToArray()); Assert.True(a.CanRead);
    }
    [Theory]
    [InlineData("stereo")]
    [InlineData("truncated")]
    public async Task Invalid_inputs_are_rejected_before_a_response_can_be_written(string kind)
    {
        using var box = new WavSandbox(); using var input = new MemoryStream(File.ReadAllBytes(box.Write(kind: kind)));
        await Assert.ThrowsAsync<WavInspectionException>(() => PcmWavAssembly.CreateAsync([input], TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Input_count_and_cancel_are_bounded()
    {
        await Assert.ThrowsAsync<WavInspectionException>(() => PcmWavAssembly.CreateAsync([], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WavInspectionException>(() => PcmWavAssembly.CreateAsync(Enumerable.Repeat<Stream>(new MemoryStream(),257).ToArray(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PcmWavAssembly.CreateAsync([new MemoryStream()], new CancellationToken(true)));
    }

    [Fact]
    public async Task Combined_PCM_limit_is_checked_using_chunk_lengths_before_copying_audio()
    {
        using var box = new WavSandbox();
        byte[] header = File.ReadAllBytes(box.Write(samples: 48))[..44];
        const int dataLength = (1 << 29) + 2;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), dataLength + 36);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), dataLength);
        using var first = new SizedWavStream(header, dataLength + 44L);
        using var second = new SizedWavStream(header, dataLength + 44L);
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(dataLength, await PcmWavAssembly.ReadPcmLengthAsync(first, token));
        var error = await Assert.ThrowsAsync<WavInspectionException>(() => PcmWavAssembly.CreateAsync([first, second], token));
        Assert.Equal("book.byte_limit", error.Code);
    }

    private sealed class SizedWavStream(byte[] header, long declaredLength) : MemoryStream(header)
    {
        public override long Length => declaredLength;
    }
}

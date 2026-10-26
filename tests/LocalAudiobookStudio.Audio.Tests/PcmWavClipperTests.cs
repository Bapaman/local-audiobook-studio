using System.Buffers.Binary;

namespace LocalAudiobookStudio.Audio.Tests;

public sealed class PcmWavClipperTests
{
    [Fact]
    public async Task Extracts_exact_half_open_samples_and_riff_sizes_without_mutating_source()
    {
        using var box = new WavSandbox();
        string source = box.Write(samples: 1440, oddChunk: true);
        using (var writer = File.OpenWrite(source))
        {
            writer.Position = 54;
            for (short i = 0; i < 1440; i++) writer.Write(BitConverter.GetBytes(i));
        }
        byte[] before = File.ReadAllBytes(source);
        string output = Path.Combine(box.Root, "clip.wav");
        await PcmWavClipper.ExtractAsync(source, output, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        byte[] clip = File.ReadAllBytes(output);
        Assert.Equal(1004, clip.Length);
        Assert.Equal(996u, BinaryPrimitives.ReadUInt32LittleEndian(clip.AsSpan(4)));
        Assert.Equal(960u, BinaryPrimitives.ReadUInt32LittleEndian(clip.AsSpan(40)));
        Assert.Equal((short)480, BinaryPrimitives.ReadInt16LittleEndian(clip.AsSpan(44)));
        Assert.Equal((short)959, BinaryPrimitives.ReadInt16LittleEndian(clip.AsSpan(1002)));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, 10)]
    [InlineData(20, 10)]
    [InlineData(0, 31)]
    public async Task Invalid_ranges_leave_no_destination(int start, int end)
    {
        using var box = new WavSandbox(); var output = Path.Combine(box.Root, "clip.wav");
        Assert.Equal("voice.anchor.range", (await Assert.ThrowsAsync<WavInspectionException>(() => PcmWavClipper.ExtractAsync(box.Write(1440), output, TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end), TestContext.Current.CancellationToken))).Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Cancellation_and_existing_destination_preserve_other_files()
    {
        using var box = new WavSandbox(); var source = box.Write(); var output = Path.Combine(box.Root, "clip.wav");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PcmWavClipper.ExtractAsync(source, output, TimeSpan.Zero, TimeSpan.FromSeconds(1), new CancellationToken(true)));
        Assert.False(File.Exists(output));
        File.WriteAllBytes(output, [1, 2, 3]);
        await Assert.ThrowsAsync<WavInspectionException>(() => PcmWavClipper.ExtractAsync(source, output, TimeSpan.Zero, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("stereo", "wav.format")]
    [InlineData("compressed", "wav.format")]
    [InlineData("rate", "wav.format")]
    [InlineData("truncated", "wav.truncated")]
    public async Task Unsupported_source_is_rejected_without_extracting(string kind, string code)
    {
        using var box = new WavSandbox(); string destination = Path.Combine(box.Root, "clip.wav");
        Assert.Equal(code, (await Assert.ThrowsAsync<WavInspectionException>(() => PcmWavClipper.ExtractAsync(box.Write(kind: kind), destination, TimeSpan.Zero, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken))).Code);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Cancellation_after_destination_creation_removes_partial_extraction_and_preserves_source()
    {
        using var box = new WavSandbox(); var source = box.Write(samples: 48000 * 120); byte[] before = File.ReadAllBytes(source);
        var destination = Path.Combine(box.Root, "clip.wav"); using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var watcher = new FileSystemWatcher(box.Root, "clip.wav"); watcher.Created += (_, _) => canceled.Cancel(); watcher.EnableRaisingEvents = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PcmWavClipper.ExtractAsync(source, destination, TimeSpan.Zero, TimeSpan.FromSeconds(120), canceled.Token));
        Assert.False(File.Exists(destination)); Assert.Equal(before, File.ReadAllBytes(source));
    }
}

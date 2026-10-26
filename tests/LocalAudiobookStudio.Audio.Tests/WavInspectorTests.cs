using System.Security.Cryptography;
using LocalAudiobookStudio.Audio;

namespace LocalAudiobookStudio.Audio.Tests;

public class WavInspectorTests
{
    [Fact]
    public async Task Parses_chunk_offsets_odd_padding_and_actual_sample_statistics_without_prompt_duration_policy()
    {
        using var sandbox = new WavSandbox();
        string path = sandbox.Write(samples: 480, oddChunk: true);
        var result = await WavInspector.InspectAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(480, result.SampleCount);
        Assert.Equal(TimeSpan.FromMilliseconds(10), result.Duration);
        Assert.Equal(54, result.DataOffset);
        Assert.Equal(960, result.DataLength);
        Assert.Equal(1000.0 / 32768, result.Peak);
        Assert.False(result.IsClipped);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), result.Sha256);
    }

    [Theory]
    [InlineData("stereo", "wav.format")]
    [InlineData("rate", "wav.format")]
    [InlineData("compressed", "wav.format")]
    [InlineData("alignment", "wav.format")]
    [InlineData("byteRate", "wav.format")]
    [InlineData("empty", "wav.empty")]
    [InlineData("incomplete", "wav.samples")]
    [InlineData("truncated", "wav.truncated")]
    [InlineData("overflow", "wav.truncated")]
    [InlineData("riffSize", "wav.truncated")]
    public async Task Rejects_malformed_or_unsupported_files_with_private_safe_codes(string kind, string code)
    {
        using var sandbox = new WavSandbox();
        var error = await Assert.ThrowsAsync<WavInspectionException>(() => WavInspector.InspectAsync(sandbox.Write(kind: kind), TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
        Assert.Equal(code, error.Message);
    }

    [Theory]
    [InlineData(short.MinValue)]
    [InlineData(short.MaxValue)]
    public async Task Either_signed_full_scale_rail_is_clipped(short sample)
    {
        using var sandbox = new WavSandbox();
        Assert.True((await WavInspector.InspectAsync(sandbox.Write(sample: sample), TestContext.Current.CancellationToken)).IsClipped);
    }

    [Fact]
    public async Task Invalid_source_path_reports_only_stable_code()
    {
        var error = await Assert.ThrowsAsync<WavInspectionException>(() => WavInspector.InspectAsync("invalid\0path", TestContext.Current.CancellationToken));
        Assert.Equal("wav.io", error.Code); Assert.Equal(error.Code, error.Message);
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        using var sandbox = new WavSandbox();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WavInspector.InspectAsync(sandbox.Write(), new CancellationToken(true)));
    }
}

internal sealed class WavSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "las-wav-" + Guid.NewGuid().ToString("N"));
    public WavSandbox() => Directory.CreateDirectory(Root);
    public string Write(int samples = 144000, short sample = 1000, bool oddChunk = false, string kind = "valid")
    {
        string path = Path.Combine(Root, Guid.NewGuid() + ".wav");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            int length = kind == "empty" ? 0 : samples * 2;
            if (kind == "incomplete") length++;
            writer.Write("RIFF"u8); writer.Write((uint)(36 + length + (length & 1) + (oddChunk ? 10 : 0))); writer.Write("WAVE"u8);
            if (oddChunk) { writer.Write("JUNK"u8); writer.Write(1u); writer.Write((byte)7); writer.Write((byte)0); }
            writer.Write("fmt "u8); writer.Write(16u); writer.Write((ushort)(kind == "compressed" ? 3 : 1));
            writer.Write((ushort)(kind == "stereo" ? 2 : 1)); writer.Write(kind == "rate" ? 44100u : 48000u);
            writer.Write(kind == "byteRate" ? 1u : 96000u); writer.Write((ushort)(kind == "alignment" ? 4 : 2)); writer.Write((ushort)16);
            writer.Write("data"u8); writer.Write(kind == "overflow" ? uint.MaxValue : (uint)length);
            for (int i = 0; i < length / 2; i++) writer.Write(sample);
            if (length % 2 != 0) { writer.Write((byte)1); writer.Write((byte)0); }
        }
        if (kind == "truncated") { using var stream = File.OpenWrite(path); stream.SetLength(stream.Length - 1); }
        if (kind == "riffSize") { using var stream = File.OpenWrite(path); stream.Position = 4; stream.Write(BitConverter.GetBytes(uint.MaxValue)); }
        return path;
    }
    public void Dispose() => Directory.Delete(Root, true);
}

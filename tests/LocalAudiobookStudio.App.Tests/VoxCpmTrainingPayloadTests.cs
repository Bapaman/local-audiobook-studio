using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmTraining;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmTrainingPayloadTests
{
    [Theory]
    [InlineData("lora_weights.safetensors")]
    [InlineData("optimizer.pth")]
    [InlineData("scheduler.pth")]
    [InlineData("rng.pth")]
    [InlineData("metadata.json")]
    public void Complete_synthetic_payload_grammar_matches_python_fixture(string name)
    {
        using var manifest = Manifest();
        VoxCpmTrainingTensorPreflight.Validate(name, Payload(name), manifest.RootElement);
    }
    [Fact]
    public void Nonfinite_adapter_and_unlimited_header_allocation_are_rejected()
    {
        using var manifest = Manifest();
        byte[] value = Payload("lora_weights.safetensors");
        BitConverter.GetBytes(float.PositiveInfinity).CopyTo(value, value.Length - 4);
        Assert.Throws<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingTensorPreflight.Validate("lora_weights.safetensors", value, manifest.RootElement));
        BitConverter.GetBytes(ulong.MaxValue).CopyTo(value, 0);
        Assert.Throws<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingTensorPreflight.Validate("lora_weights.safetensors", value, manifest.RootElement));
    }
    [Fact]
    public void Arbitrary_pickle_global_and_huge_announced_storage_are_rejected_without_execution()
    {
        using var manifest = Manifest();
        foreach (byte[] pickle in new[]
        {
            new byte[] { 0x80, 2 }.Concat("cos\nsystem\n."u8.ToArray()).ToArray(),
            StorageBomb()
        })
        {
            byte[] value = Archive(pickle);
            Assert.Throws<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingTensorPreflight.Validate("optimizer.pth", value, manifest.RootElement));
        }
    }
    [Theory]
    [InlineData("optimizer.pth")]
    [InlineData("scheduler.pth")]
    [InlineData("rng.pth")]
    public void Torch_streaming_descriptors_optional_zip64_footer_and_version_zero_are_supported(string name)
    {
        using var manifest = Manifest();
        byte[] archive = TorchArchive(Payload(name));
        VoxCpmTrainingTensorPreflight.Validate(name, archive, manifest.RootElement);
        // Neither the complete outer ZIP nor correct descriptor metadata can
        // authorize bytes whose CRC no longer matches.
        archive[64] ^= 1;
        Assert.Throws<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingTensorPreflight.Validate(name, archive, manifest.RootElement));
    }
    [Fact]
    public void Memo_larger_than_2048_is_valid_but_index_32768_is_rejected()
    {
        using var manifest = Manifest();
        foreach (int count in new[] { 7709, 32769 })
        {
            byte[] archive = Rewrite(Payload("scheduler.pth"), (name, bytes) =>
            {
                if (name != "archive/data.pkl") return bytes;
                using var output = new MemoryStream(); output.Write(bytes.AsSpan(0, bytes.Length - 1));
                using var writer = new BinaryWriter(output, Encoding.UTF8, true);
                for (int i = 0; i < count; i++) { writer.Write((byte)'r'); writer.Write(i); }
                writer.Write((byte)'.'); return output.ToArray();
            });
            if (count == 7709) VoxCpmTrainingTensorPreflight.Validate("scheduler.pth", archive, manifest.RootElement);
            else Assert.Throws<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingTensorPreflight.Validate("scheduler.pth", archive, manifest.RootElement));
        }
    }
    private static byte[] Rewrite(byte[] original, Func<string, byte[], byte[]> change)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var input = new ZipArchive(new MemoryStream(original), ZipArchiveMode.Read))
            foreach (var entry in input.Entries)
            { using var source = entry.Open(); using var data = new MemoryStream(); source.CopyTo(data); using var target = zip.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open(); target.Write(change(entry.FullName, data.ToArray())); }
        return output.ToArray();
    }
    private static byte[] TorchArchive(byte[] original)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        using var input = new ZipArchive(new MemoryStream(original), ZipArchiveMode.Read);
        var entries = new List<(byte[] Name, int Offset, int Length, uint Crc)>();
        foreach (var entry in input.Entries)
        {
            using var source = entry.Open(); using var bytes = new MemoryStream(); source.CopyTo(bytes); byte[] data = bytes.ToArray(), name = Encoding.ASCII.GetBytes(entry.FullName);
            uint crc = uint.MaxValue; foreach (byte b in data) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320; } crc = ~crc;
            int offset = (int)output.Position, extra = (64 - (offset + 30 + name.Length) % 64) % 64; if (extra < 4) extra += 64;
            writer.Write(0x04034b50u); writer.Write((ushort)0); writer.Write((ushort)0x808); writer.Write((ushort)0); writer.Write(0u);
            writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write((ushort)name.Length); writer.Write((ushort)extra); writer.Write(name);
            writer.Write((ushort)0x4246); writer.Write((ushort)(extra - 4)); writer.Write(Enumerable.Repeat((byte)0x5a, extra - 4).ToArray()); writer.Write(data);
            writer.Write(0x08074b50u); writer.Write(crc); writer.Write(data.Length); writer.Write(data.Length); entries.Add((name, offset, data.Length, crc));
        }
        int central = (int)output.Position;
        foreach (var entry in entries)
        {
            writer.Write(0x02014b50u); writer.Write((ushort)0x031e); writer.Write((ushort)0); writer.Write((ushort)0x808); writer.Write((ushort)0); writer.Write(0u);
            writer.Write(entry.Crc); writer.Write(entry.Length); writer.Write(entry.Length); writer.Write((ushort)entry.Name.Length); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u); writer.Write(entry.Offset); writer.Write(entry.Name);
        }
        int size = (int)output.Position - central; long footer = output.Position;
        writer.Write(0x06064b50u); writer.Write(44ul); writer.Write((ushort)0x031e); writer.Write((ushort)45); writer.Write(0u); writer.Write(0u);
        writer.Write((ulong)entries.Count); writer.Write((ulong)entries.Count); writer.Write((ulong)size); writer.Write((ulong)central);
        writer.Write(0x07064b50u); writer.Write(0u); writer.Write((ulong)footer); writer.Write(1u);
        writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)entries.Count); writer.Write((ushort)entries.Count); writer.Write(size); writer.Write(central); writer.Write((ushort)0);
        return output.ToArray();
    }
    private static byte[] StorageBomb()
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        void Text(string value) { writer.Write((byte)'X'); writer.Write(Encoding.UTF8.GetByteCount(value)); writer.Write(Encoding.UTF8.GetBytes(value)); }
        writer.Write(new byte[] { 0x80, 2, (byte)'(' }); Text("storage"); writer.Write("ctorch\nFloatStorage\n"u8.ToArray()); Text("0"); Text("cpu");
        writer.Write(new byte[] { 0x8a, 6, 0, 0, 0, 0, 0, 1, (byte)'t', (byte)'Q', (byte)'.' });
        return bytes.ToArray();
    }
    private static byte[] Archive(byte[] pickle)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var pair in new Dictionary<string, byte[]> { ["data.pkl"] = pickle, [".format_version"] = "1"u8.ToArray(), [".storage_alignment"] = "64"u8.ToArray(),
                ["byteorder"] = "little"u8.ToArray(), ["version"] = "3\n"u8.ToArray(), [".data/serialization_id"] = "0"u8.ToArray() })
            { using var stream = archive.CreateEntry("archive/" + pair.Key, CompressionLevel.NoCompression).Open(); stream.Write(pair.Value); }
        return bytes.ToArray();
    }
    private static JsonDocument Manifest() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(VoxCpmTrainingWorkerPinTests.Repository(), "tests", "fixtures", "VoxCpmTrainingProtocol", "checkpoint-manifest.json")));
    private static byte[] Payload(string name) => Convert.FromBase64String(File.ReadAllText(Path.Combine(VoxCpmTrainingWorkerPinTests.Repository(), "tests", "fixtures", "VoxCpmTrainingProtocol", "payloads", name + ".b64")));
}

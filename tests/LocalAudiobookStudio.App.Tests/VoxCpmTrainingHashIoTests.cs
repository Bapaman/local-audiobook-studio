using System.Diagnostics;
using System.Runtime.InteropServices;
using LocalAudiobookStudio.App.VoxCpmTraining;

namespace LocalAudiobookStudio.App.Tests;

[CollectionDefinition("VoxPinnedHashIo", DisableParallelization = true)]
public sealed class VoxPinnedHashIoCollection;

[Collection("VoxPinnedHashIo")]
public sealed class VoxCpmTrainingHashIoTests
{
    [Theory]
    [InlineData(0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData(1, "4bf5122f344554c53bde2ebb8cd2b7e3d1600ad631c385a5d7cce23c7785459a")]
    [InlineData(3, "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81")]
    public async Task Small_seekable_hashes_bound_reads_to_remaining_bytes_and_hash_all_short_reads(int length, string sha)
    {
        using var stream = new ShortReadStream(new byte[] { 1, 2, 3 }[..length]);
        Assert.Equal(sha, await VoxCpmTrainingFileHash.Sha256Async(stream, TestContext.Current.CancellationToken));
        Assert.InRange(stream.LargestRequest, 1, Math.Max(1, length));
    }

    [Theory]
    [InlineData(2, "084fed08b978af4d7d196a7446a86b58009e636b611db16211b65a9aadff29c5")]
    [InlineData(4, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public async Task Seekable_hashes_start_at_current_position_even_beyond_the_end(int position, string sha)
    {
        using var stream = new ShortReadStream([1, 2, 3]) { Position = position };
        Assert.Equal(sha, await VoxCpmTrainingFileHash.Sha256Async(stream, TestContext.Current.CancellationToken));
        Assert.InRange(stream.LargestRequest, 1, Math.Max(1, 3 - position));
    }

    [Fact]
    public async Task Nonseekable_streams_are_fully_hashed_without_querying_length_or_position()
    {
        using var stream = new ShortReadStream([1, 2, 3], seekable: false);
        Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81",
            await VoxCpmTrainingFileHash.Sha256Async(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_file_verification_uses_bounded_disk_reads(bool cache)
    {
        using var fixture = new VoxRuntimeFixture();
        string path = fixture.Child("payload.bin");
        const int length = 32 * 1024 * 1024;
        // Independently calculated SHA256 of 32 MiB of zero bytes.
        const string sha = "83ee47245398adee79bd9c0a8bc57b821e92aba10f5f9ade8a5d1fae4d8c4302";
        using (var output = File.Create(path)) { output.SetLength(length); output.Flush(true); }
        using var process = Process.GetCurrentProcess();
        ulong before = Reads(process.Handle);
        if (cache)
        {
            using var lease = await VoxCpmTrainingCacheLease.AcquireAsync(path, length, sha, TestContext.Current.CancellationToken);
            await lease.VerifyUnchangedAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            using var pins = new VoxCpmTrainingFilePins();
            var stream = await pins.ReadAsync(path, length, sha, TestContext.Current.CancellationToken);
            Assert.Equal(0, stream.Position);
            Assert.Equal(length, stream.Length);
        }
        ulong reads = Reads(process.Handle) - before;
        Assert.InRange(reads, 1UL, 256UL);
    }

    private static ulong Reads(nint process)
    {
        Assert.True(GetProcessIoCounters(process, out var counters));
        return counters.ReadOperations;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint process, out IoCounters counters);

    private sealed class ShortReadStream(byte[] bytes, bool seekable = true) : MemoryStream(bytes)
    {
        internal int LargestRequest { get; private set; }
        public override bool CanSeek => seekable;
        public override long Length => seekable ? base.Length : throw new NotSupportedException();
        public override long Position { get => seekable ? base.Position : throw new NotSupportedException(); set => base.Position = value; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestRequest = Math.Max(LargestRequest, buffer.Length);
            return base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }
    }
}

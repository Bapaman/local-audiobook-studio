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
}

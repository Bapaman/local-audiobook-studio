using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Diagnostics;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmTraining;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmTrainingCacheLeaseTests
{
    private static readonly byte[] Payload = [1, 2, 3, 4];
    private static string Sha => Convert.ToHexStringLower(SHA256.HashData(Payload));

    [Fact]
    public async Task Shared_mapping_compatible_cache_lease_excludes_second_owner()
    {
        using var fixture = new VoxRuntimeFixture(); string path = fixture.Child("frozen.bin"); File.WriteAllBytes(path, Payload);
        using (var lease = await VoxCpmTrainingCacheLease.AcquireAsync(path, 4, Sha, TestContext.Current.CancellationToken))
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var mapping = MemoryMappedFile.CreateFromFile(input, null, 4, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
            await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingCacheLease.AcquireAsync(path, 4, Sha, TestContext.Current.CancellationToken));
            await lease.VerifyUnchangedAsync(TestContext.Current.CancellationToken);
            Assert.Throws<IOException>(() => File.Move(path, path + ".replaced"));
        }
        using var next = await VoxCpmTrainingCacheLease.AcquireAsync(path, 4, Sha, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cache_changed_after_failed_window_is_detected()
    {
        using var fixture = new VoxRuntimeFixture(); string path = fixture.Child("frozen.bin"); File.WriteAllBytes(path, Payload);
        using var lease = await VoxCpmTrainingCacheLease.AcquireAsync(path, 4, Sha, TestContext.Current.CancellationToken);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { writer.WriteByte(9); writer.Flush(true); }
        var error = await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => lease.VerifyUnchangedAsync(TestContext.Current.CancellationToken));
        Assert.Equal("voxcpm_training.cache.changed", error.Code);
    }

    [Fact]
    public async Task Nonempty_or_reparse_lock_is_not_truncated()
    {
        using var fixture = new VoxRuntimeFixture(); string path = fixture.Child("frozen.bin"); File.WriteAllBytes(path, Payload);
        string marker = path + ".voxcpm-training.lock"; File.WriteAllBytes(marker, [8]);
        await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => VoxCpmTrainingCacheLease.AcquireAsync(path, 4, Sha, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(marker));
    }

    [Fact]
    public async Task Reparse_parent_cannot_redirect_the_lock_or_truncate_its_target()
    {
        using var fixture = new VoxRuntimeFixture();
        string target = fixture.Child("target"), alias = fixture.Child("alias"); Directory.CreateDirectory(target);
        string cache = Path.Combine(target, "frozen.bin"), marker = cache + ".voxcpm-training.lock";
        File.WriteAllBytes(cache, Payload); File.WriteAllBytes(marker, [8]);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string item in new[] { "/d", "/c", "mklink", "/J", alias, target }) start.ArgumentList.Add(item);
        using var child = Process.Start(start)!;
        await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, child.ExitCode);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => VoxCpmTrainingCacheLease.AcquireAsync(Path.Combine(alias, "frozen.bin"), 4, Sha, TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(marker)); Assert.Equal(Payload, File.ReadAllBytes(cache));
        }
        finally { Directory.Delete(alias); }
    }
}

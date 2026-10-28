using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using LocalAudiobookStudio.App.VoxCpmTraining;
using LocalAudiobookStudio.Application.VoxCpmTraining;
using LocalAudiobookStudio.WorkerHost.Pipe;
using Microsoft.Win32.SafeHandles;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmTrainingFilePinsTests
{
    [Fact]
    public void Failed_directory_pin_is_not_reused_and_successful_pin_blocks_moves()
    {
        using var fixture = new VoxRuntimeFixture();
        string directory = fixture.Child("created-later"), moved = fixture.Child("moved");
        using (var pins = new VoxCpmTrainingFilePins())
        {
            Assert.Throws<VoxCpmTrainingExecutionException>(() => pins.PinDirectory(directory));
            Directory.CreateDirectory(directory);
            pins.PinDirectory(directory);
            Assert.Throws<IOException>(() => Directory.Move(directory, moved));
        }
        Directory.Move(directory, moved);
    }

    [Fact]
    public async Task Retained_ancestry_blocks_moves_until_disposal_and_a_new_owner_rechecks_it()
    {
        using var fixture = new VoxRuntimeFixture();
        string parent = fixture.Child("parent"), nested = Path.Combine(parent, "nested"), moved = fixture.Child("moved");
        Directory.CreateDirectory(nested); File.WriteAllBytes(Path.Combine(nested, "asset.bin"), [1]);
        using (var pins = new VoxCpmTrainingFilePins())
        {
            _ = await pins.ReadAsync(Path.Combine(nested, "asset.bin"), 1, null, TestContext.Current.CancellationToken);
            pins.PinDirectory(nested);
            Assert.Throws<IOException>(() => Directory.Move(nested, nested + ".moved"));
            Assert.Throws<IOException>(() => Directory.Move(parent, moved));
        }
        Directory.Move(parent, moved);
        CreateJunction(parent, moved);
        try
        {
            using var pins = new VoxCpmTrainingFilePins();
            await Assert.ThrowsAnyAsync<Exception>(() => pins.ReadAsync(Path.Combine(nested, "asset.bin"), 1, null, TestContext.Current.CancellationToken));
            Assert.Throws<WorkerException>(() => VoxCpmTrainingFilePins.Absolute(Path.Combine(nested, "asset.bin")));
        }
        finally { Directory.Delete(parent); }
    }

    [Fact]
    public void Reparse_child_is_rejected_even_when_its_parent_is_already_pinned()
    {
        using var fixture = new VoxRuntimeFixture();
        string target = fixture.Child("target"), alias = fixture.Child("alias"); Directory.CreateDirectory(target);
        CreateJunction(alias, target);
        try
        {
            using var pins = new VoxCpmTrainingFilePins(); pins.PinDirectory(fixture.Root);
            Assert.Throws<VoxCpmTrainingExecutionException>(() => pins.PinDirectory(alias));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public async Task Case_distinct_directory_spellings_cannot_reuse_a_pin_to_follow_a_junction()
    {
        using var fixture = new VoxRuntimeFixture();
        string root = fixture.Child("case-sensitive"), target = fixture.Child("target");
        Directory.CreateDirectory(root); Directory.CreateDirectory(target);
        using (var handle = CreateFileW(root, 0x100, 7, 0, 3, 0x02200000, 0))
        {
            Assert.False(handle.IsInvalid);
            uint flags = 1;
            Assert.True(SetFileInformationByHandle(handle, 23, ref flags, sizeof(uint)),
                $"Cannot enable case sensitivity on the owned temporary directory: Win32 {Marshal.GetLastWin32Error()}.");
        }
        string safe = Path.Combine(root, "Runtime"), alias = Path.Combine(root, "runtime");
        Directory.CreateDirectory(safe);
        File.WriteAllBytes(Path.Combine(safe, "asset.bin"), [1]);
        File.WriteAllBytes(Path.Combine(target, "asset.bin"), [9]);
        CreateJunction(alias, target);
        try
        {
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(safe, "asset.bin")));
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(alias, "asset.bin")));
            using var pins = new VoxCpmTrainingFilePins();
            pins.PinDirectory(safe);
            var error = await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() =>
                pins.ReadAsync(Path.Combine(alias, "asset.bin"), 1, null, TestContext.Current.CancellationToken));
            Assert.Equal("voxcpm_training.file.invalid", error.Code);
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void Retained_directory_blocks_write_handles_that_could_change_it_to_a_reparse_point()
    {
        using var fixture = new VoxRuntimeFixture();
        string directory = fixture.Child("directory"), target = fixture.Child("target");
        Directory.CreateDirectory(directory); Directory.CreateDirectory(target);
        using (var pins = new VoxCpmTrainingFilePins())
        {
            pins.PinDirectory(directory);
            using var writer = CreateFileW(directory, 2, 3, 0, 3, 0x02200000, 0);
            Assert.True(writer.IsInvalid);
            using var reader = CreateFileW(directory, 0, 3, 0, 3, 0x02200000, 0);
            Assert.False(reader.IsInvalid);
            byte[] junction = JunctionData(target);
            Assert.False(DeviceIoControl(reader, 0x000900a4, junction, junction.Length, 0, 0, out _, 0));
            string child = Path.Combine(directory, "child.bin"), childDirectory = Path.Combine(directory, "child");
            File.WriteAllBytes(child, [1]); Directory.CreateDirectory(childDirectory);
            File.Delete(child); Directory.Delete(childDirectory);
        }
        CreateJunction(directory, target);
        Directory.Delete(directory);
    }

    [Fact]
    public async Task Files_reject_directory_and_hardlink_targets_and_directories_reject_files()
    {
        using var fixture = new VoxRuntimeFixture();
        string path = fixture.Child("asset.bin"), alias = fixture.Child("alias.bin"); File.WriteAllBytes(path, [1]);
        Assert.True(CreateHardLinkW(alias, path, 0));
        using var pins = new VoxCpmTrainingFilePins();
        Assert.Throws<VoxCpmTrainingExecutionException>(() => pins.PinDirectory(path));
        await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => pins.ReadAsync(fixture.Root, 1, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<VoxCpmTrainingExecutionException>(() => pins.ReadAsync(path, 1, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Large_inventory_pins_do_not_retain_per_file_64KiB_buffers()
    {
        using var fixture = new VoxRuntimeFixture();
        const int count = 1024;
        for (int i = 0; i < count; i++) File.WriteAllBytes(fixture.Child(i + ".bin"), [1]);
        long before = GC.GetTotalMemory(true);
        using (var pins = new VoxCpmTrainingFilePins())
        {
            for (int i = 0; i < count; i++)
                await pins.ReadAsync(fixture.Child(i + ".bin"), 1, "4bf5122f344554c53bde2ebb8cd2b7e3d1600ad631c385a5d7cce23c7785459a", TestContext.Current.CancellationToken);
            Assert.True(GC.GetTotalMemory(true) - before < 16 * 1024 * 1024);
            Assert.Throws<IOException>(() => File.Move(fixture.Child("0.bin"), fixture.Child("moved.bin")));
        }
        File.Move(fixture.Child("0.bin"), fixture.Child("moved.bin"));
    }

    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        byte[] data = JunctionData(target);
        using var handle = CreateFileW(path, 0x40000000, 0, 0, 3, 0x02200000, 0);
        Assert.False(handle.IsInvalid);
        Assert.True(DeviceIoControl(handle, 0x000900a4, data, data.Length, 0, 0, out _, 0));
    }

    private static byte[] JunctionData(string target)
    {
        byte[] substitute = Encoding.Unicode.GetBytes("\\??\\" + target), display = Encoding.Unicode.GetBytes(target);
        byte[] data = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xa0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)display.Length));
        substitute.CopyTo(data, 16); display.CopyTo(data, 16 + substitute.Length + 2);
        return data;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string target, nint security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref uint information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, nint output, int outputLength, out int returned, nint overlapped);
}

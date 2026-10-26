using System.Security.Cryptography;
using LocalAudiobookStudio.App.VoxCpmTraining;

namespace LocalAudiobookStudio.App.Tests;

public sealed class VoxCpmTrainingFilePinsTests
{
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
}

using LocalAudiobookStudio.Application.Errors;
using LocalAudiobookStudio.Application.VoxCpmTraining;

namespace LocalAudiobookStudio.Application.Tests;

public sealed class VoxCpmTrainingCursorTests
{
    private static readonly string[] Ids = [new('a', 32), new('b', 32)];
    [Fact]
    public void Advances_next_clip_and_wraps_only_after_the_last_retained_clip()
    {
        var cursor = VoxCpmTrainingCursor.Initial(Ids);
        Assert.Equal(new(0, 0, 2, 0, Ids[0]), cursor);
        cursor = cursor.Advance(Ids); Assert.Equal(new(1, 0, 2, 1, Ids[1]), cursor);
        cursor = cursor.Advance(Ids); Assert.Equal(new(2, 1, 2, 0, Ids[0]), cursor);
        cursor = cursor.Advance(Ids); Assert.Equal(new(3, 1, 2, 1, Ids[1]), cursor);
    }
    [Theory]
    [InlineData(0, 0, 2, 1, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData(1, 0, 2, 1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(0, 0, 3, 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(-1, 0, 2, 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Rejects_inconsistent_saved_cursor(long step, long epoch, int size, int ordinal, string id)
        => Assert.Equal("voxcpm_training.cursor.invalid", Assert.Throws<StudioException>(() => new VoxCpmTrainingCursor(step, epoch, size, ordinal, id).Advance(Ids)).Code);
    [Fact]
    public void Rejects_empty_duplicate_ids_and_checked_overflow()
    {
        Assert.Throws<StudioException>(() => VoxCpmTrainingCursor.Initial([]));
        Assert.Throws<StudioException>(() => VoxCpmTrainingCursor.Initial([Ids[0], Ids[0]]));
        Assert.Throws<StudioException>(() => new VoxCpmTrainingCursor(long.MaxValue, (long.MaxValue - 1) / 2, 2, 1, Ids[1]).Advance(Ids));
    }
}

using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;

namespace BeBoosted.Tests.Domain;

public sealed class ConflictDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 8, 11);

    private static BlockOccurrence At(int startHour, int startMinute, int endHour, int endMinute, DateOnly? date = null)
    {
        var block = CalendarBlock.CreateForTask(
            TaskId.New(),
            date ?? Date,
            new TimeOnly(startHour, startMinute),
            new TimeOnly(endHour, endMinute),
            Now);
        return new BlockOccurrence(block, date ?? Date);
    }

    [Fact]
    public void OverlappingBlocks_AreBothFlagged()
    {
        var a = At(9, 0, 10, 30);
        var b = At(10, 0, 11, 0);
        var c = At(13, 0, 14, 0);

        var conflicts = ConflictDetector.FindConflicts([a, b, c]);

        Assert.Equal(2, conflicts.Count);
        Assert.Contains(a.Block.Id, conflicts);
        Assert.Contains(b.Block.Id, conflicts);
        Assert.DoesNotContain(c.Block.Id, conflicts);
    }

    [Fact]
    public void TouchingBlocks_DoNotConflict()
    {
        var a = At(9, 0, 10, 0);
        var b = At(10, 0, 11, 0);

        Assert.Empty(ConflictDetector.FindConflicts([a, b]));
    }

    [Fact]
    public void SameTimes_OnDifferentDays_DoNotConflict()
    {
        var a = At(9, 0, 10, 0);
        var b = At(9, 0, 10, 0, Date.AddDays(1));

        Assert.Empty(ConflictDetector.FindConflicts([a, b]));
    }

    [Fact]
    public void ChainedOverlaps_FlagEveryOverlappingPairMember()
    {
        var a = At(9, 0, 11, 0);
        var b = At(10, 0, 12, 0);
        var c = At(11, 30, 13, 0);
        var d = At(14, 0, 15, 0);

        var conflicts = ConflictDetector.FindConflicts([a, b, c, d]);

        Assert.Equal(3, conflicts.Count);
        Assert.DoesNotContain(d.Block.Id, conflicts);
    }
}

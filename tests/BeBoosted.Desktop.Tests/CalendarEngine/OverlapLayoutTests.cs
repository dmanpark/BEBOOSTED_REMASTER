using BeBoosted.Desktop.CalendarEngine;

namespace BeBoosted.Desktop.Tests.CalendarEngine;

public sealed class OverlapLayoutTests
{
    private static LayoutInterval At(int key, int startHour, int startMinute, int endHour, int endMinute)
        => new(key, (startHour * 60) + startMinute, (endHour * 60) + endMinute);

    [Fact]
    public void DisjointIntervals_EachGetFullWidth()
    {
        var slots = OverlapLayout.Arrange([At(0, 9, 0, 10, 0), At(1, 11, 0, 12, 0)]);

        Assert.Equal(new LayoutSlot(0, 1), slots[0]);
        Assert.Equal(new LayoutSlot(0, 1), slots[1]);
    }

    [Fact]
    public void TouchingIntervals_DoNotShareColumns()
    {
        var slots = OverlapLayout.Arrange([At(0, 9, 0, 10, 0), At(1, 10, 0, 11, 0)]);

        Assert.Equal(new LayoutSlot(0, 1), slots[0]);
        Assert.Equal(new LayoutSlot(0, 1), slots[1]);
    }

    [Fact]
    public void OverlappingPair_SplitsIntoTwoColumns()
    {
        var slots = OverlapLayout.Arrange([At(0, 9, 0, 10, 30), At(1, 10, 0, 11, 0)]);

        Assert.Equal(new LayoutSlot(0, 2), slots[0]);
        Assert.Equal(new LayoutSlot(1, 2), slots[1]);
    }

    [Fact]
    public void ChainedCluster_ReusesFreedColumns()
    {
        // A 9–11, B 10–12, C 11–13: C fits back into A's column; cluster spans 2 columns.
        var slots = OverlapLayout.Arrange([At(0, 9, 0, 11, 0), At(1, 10, 0, 12, 0), At(2, 11, 0, 13, 0)]);

        Assert.Equal(new LayoutSlot(0, 2), slots[0]);
        Assert.Equal(new LayoutSlot(1, 2), slots[1]);
        Assert.Equal(new LayoutSlot(0, 2), slots[2]);
    }

    [Fact]
    public void TripleOverlap_UsesThreeColumns()
    {
        var slots = OverlapLayout.Arrange(
            [At(0, 9, 0, 12, 0), At(1, 9, 30, 11, 0), At(2, 10, 0, 11, 30)]);

        Assert.Equal(3, slots[0].ColumnCount);
        Assert.Equal([0, 1, 2], new[] { slots[0].Column, slots[1].Column, slots[2].Column });
    }

    [Fact]
    public void SeparateClusters_GetIndependentColumnCounts()
    {
        var slots = OverlapLayout.Arrange(
            [At(0, 9, 0, 10, 30), At(1, 10, 0, 11, 0), At(2, 14, 0, 15, 0)]);

        Assert.Equal(2, slots[0].ColumnCount);
        Assert.Equal(2, slots[1].ColumnCount);
        Assert.Equal(new LayoutSlot(0, 1), slots[2]);
    }
}

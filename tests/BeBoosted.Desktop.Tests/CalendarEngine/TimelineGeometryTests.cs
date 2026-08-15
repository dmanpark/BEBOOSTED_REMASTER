using BeBoosted.Desktop.CalendarEngine;

namespace BeBoosted.Desktop.Tests.CalendarEngine;

public sealed class TimelineGeometryTests
{
    private static readonly TimelineGeometry Geometry = new(6 * 60, 23 * 60, 56);

    [Fact]
    public void TotalHeight_CoversVisibleRange() => Assert.Equal(17 * 56, Geometry.TotalHeight);

    [Theory]
    [InlineData(6, 0, 0)]
    [InlineData(7, 0, 56)]
    [InlineData(8, 30, 140)]
    [InlineData(14, 10, 457.333)]
    public void YFromTime_IsLinear(int hour, int minute, double expected)
        => Assert.Equal(expected, Geometry.YFromTime(new TimeOnly(hour, minute)), 2);

    [Fact]
    public void HeightForDuration_ScalesWithHourHeight()
        => Assert.Equal(84, Geometry.HeightForDuration(TimeSpan.FromMinutes(90)));

    [Theory]
    [InlineData(140, 15, 8, 30)]     // exact position
    [InlineData(147, 15, 8, 30)]     // rounds down to nearest 15
    [InlineData(151, 15, 8, 45)]     // rounds up
    [InlineData(147, 5, 8, 40)]      // fine snap
    [InlineData(-40, 15, 6, 0)]      // clamped to visible start
    [InlineData(99999, 15, 23, 0)]   // clamped to visible end
    public void TimeFromY_SnapsAndClamps(double y, int snap, int hour, int minute)
        => Assert.Equal(new TimeOnly(hour, minute), Geometry.TimeFromY(y, snap));

    [Fact]
    public void HourMarks_CoverVisibleHours()
    {
        var marks = Geometry.HourMarks().ToList();
        Assert.Equal(17, marks.Count);
        Assert.Equal(new TimeOnly(6, 0), marks[0]);
        Assert.Equal(new TimeOnly(22, 0), marks[^1]);
    }

    // ---- Full 00:00–24:00 day (BB-QA-001) ----

    private static readonly TimelineGeometry FullDay = new(0, 24 * 60, 56);

    [Fact]
    public void FullDay_TotalHeight_CoversTwentyFourHours() => Assert.Equal(24 * 56, FullDay.TotalHeight);

    [Fact]
    public void FullDay_HourMarks_CoverEveryHourOfTheDay()
    {
        var marks = FullDay.HourMarks().ToList();
        Assert.Equal(24, marks.Count);
        Assert.Equal(new TimeOnly(0, 0), marks[0]);
        Assert.Equal(new TimeOnly(23, 0), marks[^1]);
    }

    [Theory]
    [InlineData(0, 0, 0)]          // midnight at the very top
    [InlineData(12, 0, 672)]
    [InlineData(23, 0, 1288)]
    [InlineData(23, 59, 1343.067)] // last minute stays inside the 1344 px extent
    public void FullDay_YFromTime_MapsWholeDayLinearly(int hour, int minute, double expected)
        => Assert.Equal(expected, FullDay.YFromTime(new TimeOnly(hour, minute)), 2);

    [Fact]
    public void FullDay_TimeFromY_ClampsBottomToLastRepresentableMinute()
        => Assert.Equal(new TimeOnly(23, 59), FullDay.TimeFromY(99999, 15));

    [Fact]
    public void FullDay_TimeFromY_ClampsTopToMidnight()
        => Assert.Equal(new TimeOnly(0, 0), FullDay.TimeFromY(-40, 15));
}

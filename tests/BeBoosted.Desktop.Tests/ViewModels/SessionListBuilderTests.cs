using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// The pure schedule-list builder both editors share: (date, start, created, id)
/// ordering, one-off-only "SESSION X OF N" numbering, repeating rows without
/// numbers, status chips, and word-spelled automation names.
/// </summary>
public sealed class SessionListBuilderTests
{
    private static readonly FakeClock Clock = new(TestShell.DesignDate);
    private static readonly TaskId Task = TaskId.New();

    private static CalendarBlock OneOff(
        DateOnly date, TimeOnly start, TimeOnly end, DateTimeOffset? created = null)
        => CalendarBlock.CreateTaskSession(Task, date, start, end, created ?? Clock.Now);

    private static CalendarBlock Repeating(
        DateOnly anchor, TimeOnly start, TimeOnly end, params DayOfWeek[] days)
        => CalendarBlock.CreateTaskSession(
            Task, anchor, start, end, Clock.Now, RecurrenceRule.Weekly(1, days));

    [Fact]
    public void Build_OrdersByDateStartCreatedThenId()
    {
        var later = OneOff(new DateOnly(2026, 8, 26), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var early = OneOff(new DateOnly(2026, 8, 25), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var midday = OneOff(new DateOnly(2026, 8, 25), new TimeOnly(10, 0), new TimeOnly(11, 0));
        var earlyTwin = OneOff(
            new DateOnly(2026, 8, 25), new TimeOnly(9, 0), new TimeOnly(10, 0), Clock.Now.AddMinutes(5));

        var rows = SessionListBuilder.Build([later, earlyTwin, midday, early]);

        Assert.Equal([early.Id, earlyTwin.Id, midday.Id, later.Id], rows.Select(r => r.Id).ToArray());
        Assert.Equal("SESSION 1 OF 4", rows[0].PositionText);
        Assert.Equal("Tue, Aug 25", rows[0].PrimaryText);
        Assert.Equal("9:00 – 10:00 AM · 1 h", rows[0].SecondaryText);
    }

    [Fact]
    public void Build_NumbersOneOffsOnly_SkippingRepeatingRows()
    {
        var first = OneOff(new DateOnly(2026, 8, 25), new TimeOnly(16, 0), new TimeOnly(16, 45));
        var series = Repeating(
            new DateOnly(2026, 8, 26), new TimeOnly(16, 0), new TimeOnly(17, 0),
            DayOfWeek.Wednesday, DayOfWeek.Saturday);
        var second = OneOff(new DateOnly(2026, 8, 30), new TimeOnly(10, 0), new TimeOnly(11, 0));

        var rows = SessionListBuilder.Build([first, series, second]);

        Assert.Equal(
            new string?[] { "SESSION 1 OF 2", null, "SESSION 2 OF 2" },
            rows.Select(r => r.PositionText));
        Assert.True(rows[1].IsRepeating);
        Assert.Equal("Wed · Sat", rows[1].PrimaryText);
    }

    [Fact]
    public void Build_ResolvedHistoryKeepsItsPosition_AndChips()
    {
        var resolved = OneOff(new DateOnly(2026, 8, 10), new TimeOnly(9, 0), new TimeOnly(10, 0));
        resolved.RecordOutcome(BlockOutcome.DidntHappen, Clock.Now);
        var pending = OneOff(new DateOnly(2026, 8, 12), new TimeOnly(9, 0), new TimeOnly(10, 0));

        var rows = SessionListBuilder.Build([resolved, pending]);

        Assert.Equal("SESSION 1 OF 2", rows[0].PositionText);
        Assert.Equal("DIDN'T HAPPEN", rows[0].StatusChip);
        Assert.Null(rows[1].StatusChip);
    }

    [Fact]
    public void Build_AccessibleNames_SpellDatesAndTimes_WithoutGlyphs()
    {
        var done = OneOff(new DateOnly(2026, 8, 26), new TimeOnly(9, 0), new TimeOnly(10, 0));
        done.RecordOutcome(BlockOutcome.Done, Clock.Now);
        var series = Repeating(
            new DateOnly(2026, 8, 24), new TimeOnly(9, 0), new TimeOnly(10, 0),
            DayOfWeek.Monday, DayOfWeek.Wednesday);

        var rows = SessionListBuilder.Build([done, series]);

        var doneRow = rows.Single(r => !r.IsRepeating);
        Assert.Equal(
            "Session 1 of 1 — Wednesday, August 26, 9:00 AM to 10:00 AM, done", doneRow.AccessibleName);
        Assert.Equal("Edit session 1 of 1", doneRow.EditControlName);
        Assert.Equal("Remove session 1 of 1", doneRow.RemoveControlName);
        var seriesRow = rows.Single(r => r.IsRepeating);
        Assert.Equal(
            "Repeating schedule — Monday, Wednesday, 9:00 AM to 10:00 AM", seriesRow.AccessibleName);
        Assert.Equal("Edit repeating schedule", seriesRow.EditControlName);
        Assert.Equal("Remove repeating schedule", seriesRow.RemoveControlName);
        Assert.All(rows, r => Assert.DoesNotContain("·", r.AccessibleName));
    }

    [Fact]
    public void Build_TimeRange_ShowsBothMeridiems_WhenTheyDiffer()
    {
        var overnight = OneOff(new DateOnly(2026, 9, 30), new TimeOnly(23, 30), new TimeOnly(23, 59));
        var crossing = OneOff(new DateOnly(2026, 10, 1), new TimeOnly(11, 30), new TimeOnly(12, 45));

        var rows = SessionListBuilder.Build([overnight, crossing]);

        Assert.Equal("11:30 – 11:59 PM · 29 min", rows[0].SecondaryText);
        Assert.Equal("11:30 AM – 12:45 PM · 1 h 15 min", rows[1].SecondaryText);
    }

    [Fact]
    public void PositionOf_ReturnsTheOneOffOrdinal_AndZeroForRepeating()
    {
        var first = OneOff(new DateOnly(2026, 8, 25), new TimeOnly(16, 0), new TimeOnly(16, 45));
        var series = Repeating(
            new DateOnly(2026, 8, 26), new TimeOnly(16, 0), new TimeOnly(17, 0), DayOfWeek.Wednesday);
        var second = OneOff(new DateOnly(2026, 8, 30), new TimeOnly(10, 0), new TimeOnly(11, 0));
        var sessions = new[] { first, series, second };

        Assert.Equal((2, 2), SessionListBuilder.PositionOf(sessions, second.Id));
        Assert.Equal((0, 2), SessionListBuilder.PositionOf(sessions, series.Id));
    }

    [Fact]
    public void SummaryFor_CoversEveryShape()
    {
        var one = OneOff(new DateOnly(2026, 8, 25), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var ninety = OneOff(new DateOnly(2026, 8, 26), new TimeOnly(15, 30), new TimeOnly(17, 0));
        var anotherNinety = OneOff(new DateOnly(2026, 8, 27), new TimeOnly(9, 0), new TimeOnly(10, 30));
        var thirtyMinSeries = Repeating(
            new DateOnly(2026, 8, 24), new TimeOnly(7, 0), new TimeOnly(7, 30), DayOfWeek.Monday);
        var hourSeries = Repeating(
            new DateOnly(2026, 8, 26), new TimeOnly(16, 0), new TimeOnly(17, 0), DayOfWeek.Wednesday);

        Assert.Equal("0 sessions", SessionListBuilder.SummaryFor([]));
        Assert.Equal("1 session · 1 h", SessionListBuilder.SummaryFor([one]));
        Assert.Equal("3 sessions · 4 h", SessionListBuilder.SummaryFor([one, ninety, anotherNinety]));
        Assert.Equal("repeating · 30 min", SessionListBuilder.SummaryFor([thirtyMinSeries]));
        Assert.Equal("2 one-off · repeating", SessionListBuilder.SummaryFor([one, ninety, thirtyMinSeries]));
        Assert.Equal(
            "2 one-off · 2 repeating",
            SessionListBuilder.SummaryFor([one, ninety, thirtyMinSeries, hourSeries]));

        one.RecordOutcome(BlockOutcome.Done, Clock.Now);
        ninety.RecordOutcome(BlockOutcome.Done, Clock.Now);
        anotherNinety.RecordOutcome(BlockOutcome.Done, Clock.Now);
        Assert.Equal("3 sessions · all done", SessionListBuilder.SummaryFor([one, ninety, anotherNinety]));
    }

    [Fact]
    public void Build_RepeatingPrimaryText_ListsWeekdaysMondayFirst()
    {
        var series = Repeating(
            new DateOnly(2026, 8, 24), new TimeOnly(7, 0), new TimeOnly(7, 30),
            DayOfWeek.Friday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Wednesday);

        var rows = SessionListBuilder.Build([series]);

        Assert.Equal("Mon · Wed · Fri · Sun", rows[0].PrimaryText);
    }
}

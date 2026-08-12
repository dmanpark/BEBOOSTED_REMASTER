using BeBoosted.Domain;
using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Tests.Domain;

public sealed class RecurrenceRuleTests
{
    private static readonly DateOnly Start = new(2026, 8, 11); // Tuesday

    [Fact]
    public void Daily_OccursEveryIntervalFromStart()
    {
        var rule = RecurrenceRule.Daily(2);

        Assert.True(rule.OccursOn(Start, Start));
        Assert.False(rule.OccursOn(Start.AddDays(1), Start));
        Assert.True(rule.OccursOn(Start.AddDays(2), Start));
        Assert.True(rule.OccursOn(Start.AddDays(10), Start));
        Assert.False(rule.OccursOn(Start.AddDays(-2), Start)); // never before the start
    }

    [Fact]
    public void Weekly_OccursOnSelectedDays()
    {
        var rule = RecurrenceRule.Weekly(1, DayOfWeek.Monday, DayOfWeek.Wednesday);

        Assert.False(rule.OccursOn(Start, Start)); // Tuesday not selected
        Assert.True(rule.OccursOn(new DateOnly(2026, 8, 12), Start)); // Wednesday
        Assert.True(rule.OccursOn(new DateOnly(2026, 8, 17), Start)); // next Monday
        Assert.False(rule.OccursOn(new DateOnly(2026, 8, 10), Start)); // Monday before start
    }

    [Fact]
    public void BiweeklyRule_SkipsAlternateWeeks()
    {
        var rule = RecurrenceRule.Weekly(2, DayOfWeek.Friday);

        Assert.True(rule.OccursOn(new DateOnly(2026, 8, 14), Start)); // Friday of the start week
        Assert.False(rule.OccursOn(new DateOnly(2026, 8, 21), Start)); // following week skipped
        Assert.True(rule.OccursOn(new DateOnly(2026, 8, 28), Start)); // two weeks after
    }

    [Fact]
    public void InvalidRules_AreRejected()
    {
        Assert.Throws<DomainException>(() => RecurrenceRule.Daily(0));
        Assert.Throws<DomainException>(() => RecurrenceRule.Weekly(1));
    }
}

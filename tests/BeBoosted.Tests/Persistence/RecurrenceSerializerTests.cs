using BeBoosted.Domain.Scheduling;
using BeBoosted.Infrastructure.Persistence;

namespace BeBoosted.Tests.Persistence;

public sealed class RecurrenceSerializerTests
{
    [Fact]
    public void Daily_RoundTrips()
    {
        var rule = RecurrenceSerializer.Deserialize(RecurrenceSerializer.Serialize(RecurrenceRule.Daily(3)));

        Assert.Equal(RecurrenceFrequency.Daily, rule.Frequency);
        Assert.Equal(3, rule.Interval);
    }

    [Fact]
    public void Weekly_RoundTripsWithDays()
    {
        var original = RecurrenceRule.Weekly(2, DayOfWeek.Friday, DayOfWeek.Monday);
        var serialized = RecurrenceSerializer.Serialize(original);
        var rule = RecurrenceSerializer.Deserialize(serialized);

        Assert.Equal("W:2:MO,FR", serialized);
        Assert.Equal(RecurrenceFrequency.Weekly, rule.Frequency);
        Assert.Equal(2, rule.Interval);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], rule.DaysOfWeek.OrderBy(d => d));
    }

    [Fact]
    public void UnknownEncoding_Throws()
        => Assert.Throws<FormatException>(() => RecurrenceSerializer.Deserialize("X:1"));
}

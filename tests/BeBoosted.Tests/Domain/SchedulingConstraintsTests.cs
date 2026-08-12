using BeBoosted.Domain;
using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Tests.Domain;

public sealed class SchedulingConstraintsTests
{
    [Fact]
    public void RejectsInvertedTimeWindow()
        => Assert.Throws<DomainException>(
            () => new SchedulingConstraints(earliestTime: new TimeOnly(17, 0), latestTime: new TimeOnly(9, 0)));

    [Fact]
    public void EmptyConstraints_ReportEmpty()
    {
        Assert.True(new SchedulingConstraints().IsEmpty);
        Assert.False(new SchedulingConstraints(notBefore: new DateOnly(2026, 8, 12)).IsEmpty);
    }
}

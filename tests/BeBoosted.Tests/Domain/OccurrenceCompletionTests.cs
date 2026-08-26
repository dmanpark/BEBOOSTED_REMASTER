using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Tests.Domain;

/// <summary>
/// Occurrence completion is an explicit per-occurrence operation: valid only for
/// repeating local task sessions on a date the session actually occurs. It never
/// reuses the one-off outcome rules and never mutates the session itself.
/// </summary>
public sealed class OccurrenceCompletionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));
    private static readonly DateOnly Date = new(2026, 8, 11);

    private static CalendarBlock RepeatingSession(params DayOfWeek[] days)
        => CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now,
            RecurrenceRule.Weekly(1, days));

    [Fact]
    public void Create_RecordsTheOccurrence_WithoutMutatingTheSession()
    {
        var session = RepeatingSession(DayOfWeek.Tuesday);

        var completion = OccurrenceCompletion.Create(session, Date, Now.AddHours(2));

        Assert.Equal(session.Id, completion.BlockId);
        Assert.Equal(Date, completion.OccurrenceDate);
        Assert.Equal(Now.AddHours(2), completion.CompletedAt);
        // Completing never mutates the session itself.
        Assert.Equal(BlockOutcome.None, session.Outcome);
        Assert.Equal(Now, session.ModifiedAt);
    }

    [Fact]
    public void Create_AcceptsAnyOccurrenceOfTheSeries_ButNotOtherDates()
    {
        var session = RepeatingSession(DayOfWeek.Tuesday, DayOfWeek.Wednesday);

        var wednesday = OccurrenceCompletion.Create(session, Date.AddDays(1), Now);
        Assert.Equal(Date.AddDays(1), wednesday.OccurrenceDate);

        // Thursday is not an occurrence of this series.
        Assert.Throws<DomainException>(() => OccurrenceCompletion.Create(session, Date.AddDays(2), Now));
    }

    [Fact]
    public void Create_RejectsOneOffSessionsAndExternalEvents()
    {
        // A one-off session completes its Task, never an occurrence.
        var oneOff = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(15, 0), new TimeOnly(16, 0), Now);
        Assert.Throws<DomainException>(() => OccurrenceCompletion.Create(oneOff, Date, Now));

        var external = CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, "External", Date, new TimeOnly(9, 0), new TimeOnly(10, 0),
            BlockKind.ExternalEvent, RecurrenceRule.Weekly(1, DayOfWeek.Tuesday),
            "google", "evt-1", 0, BlockOutcome.None, null, Now, Now);
        Assert.Throws<DomainException>(() => OccurrenceCompletion.Create(external, Date, Now));
        Assert.Equal(Now, external.ModifiedAt);
    }
}

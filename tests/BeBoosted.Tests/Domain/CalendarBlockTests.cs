using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Tests.Domain;

public sealed class CalendarBlockTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));
    private static readonly DateOnly Date = new(2026, 8, 11);

    private static CalendarBlock ExternalEvent(RecurrenceRule? recurrence = null)
        => CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, "External", Date, new TimeOnly(9, 0), new TimeOnly(10, 0),
            BlockKind.ExternalEvent, recurrence, "google", "evt-1", 0, BlockOutcome.None, null, Now, Now);

    [Fact]
    public void CreateTaskSession_ValidatesTimes_AndIsAlwaysTaskBacked()
    {
        var taskId = TaskId.New();
        Assert.Throws<DomainException>(() => CalendarBlock.CreateTaskSession(
            taskId, Date, new TimeOnly(9, 45), new TimeOnly(8, 30), Now));

        var session = CalendarBlock.CreateTaskSession(
            taskId, Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now);
        Assert.Equal(taskId, session.TaskId);
        Assert.Null(session.Title); // the Task owns the title
        Assert.Equal(BlockKind.TaskSession, session.Kind);
        Assert.Equal(CalendarBlock.LocalProvider, session.Provider);
        Assert.False(session.IsExternal);
        Assert.Equal(TimeSpan.FromMinutes(75), session.Duration);
    }

    [Fact]
    public void CreateTaskSession_SupportsARepeatingSchedule()
    {
        var recurrence = RecurrenceRule.Weekly(1, DayOfWeek.Tuesday);
        var session = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now, recurrence);
        Assert.Same(recurrence, session.Recurrence);
    }

    [Fact]
    public void OneOffSessions_TakeOutcomes_RepeatingAndExternalDoNot()
    {
        var oneOff = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(15, 30), new TimeOnly(17, 0), Now);
        oneOff.RecordOutcome(BlockOutcome.Done, Now.AddHours(4));
        Assert.Equal(BlockOutcome.Done, oneOff.Outcome);
        Assert.Equal(Now.AddHours(4), oneOff.OutcomeRecordedAt);
        Assert.Throws<DomainException>(() => oneOff.RecordOutcome(BlockOutcome.None, Now));

        var repeating = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        Assert.Throws<DomainException>(() => repeating.RecordOutcome(BlockOutcome.Done, Now));

        Assert.Throws<DomainException>(() => ExternalEvent().RecordOutcome(BlockOutcome.Done, Now));
    }

    [Fact]
    public void ClearOutcome_ResetsAOneOffSession()
    {
        var session = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(15, 30), new TimeOnly(17, 0), Now);
        session.RecordOutcome(BlockOutcome.Done, Now.AddHours(4));

        session.ClearOutcome(Now.AddHours(5));

        Assert.Equal(BlockOutcome.None, session.Outcome);
        Assert.Null(session.OutcomeRecordedAt);
    }

    [Fact]
    public void Reschedule_MovesBlock_AndRejectsExternalEdits()
    {
        var block = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(15, 30), new TimeOnly(17, 0), Now);
        block.Reschedule(Date.AddDays(2), new TimeOnly(9, 0), new TimeOnly(10, 30), Now.AddHours(1));

        Assert.Equal(Date.AddDays(2), block.Date);
        Assert.Equal(new TimeOnly(9, 0), block.StartTime);

        var external = ExternalEvent();
        Assert.True(external.IsExternal);
        Assert.Throws<DomainException>(
            () => external.Reschedule(Date, new TimeOnly(10, 0), new TimeOnly(11, 0), Now));
    }

    [Fact]
    public void OccursOn_ExpandsRecurrenceFromAnchorDate()
    {
        var block = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday));

        Assert.True(block.OccursOn(Date));                 // anchor Tuesday
        Assert.True(block.OccursOn(Date.AddDays(1)));      // Wednesday
        Assert.False(block.OccursOn(Date.AddDays(4)));     // Saturday
        Assert.True(block.OccursOn(Date.AddDays(7)));      // next Tuesday
        Assert.False(block.OccursOn(Date.AddDays(-1)));    // before the anchor

        var single = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(15, 0), new TimeOnly(16, 0), Now);
        Assert.True(single.OccursOn(Date));
        Assert.False(single.OccursOn(Date.AddDays(1)));
    }

    [Fact]
    public void SetRecurrence_RejectsExternalEvents_WithoutMutating()
    {
        var session = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(15, 0), new TimeOnly(16, 0), Now);
        var recurrence = RecurrenceRule.Weekly(1, DayOfWeek.Monday);
        session.SetRecurrence(recurrence, Now.AddHours(1));
        Assert.Same(recurrence, session.Recurrence);
        session.SetRecurrence(null, Now.AddHours(2));
        Assert.Null(session.Recurrence);

        var external = ExternalEvent(RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        Assert.Throws<DomainException>(() => external.SetRecurrence(null, Now.AddHours(1)));
        Assert.Throws<DomainException>(() => external.SetRecurrence(
            RecurrenceRule.Weekly(1, DayOfWeek.Friday), Now.AddHours(1)));
        Assert.Equal([DayOfWeek.Tuesday], external.Recurrence!.DaysOfWeek);
        Assert.Equal(Now, external.ModifiedAt);
    }

    [Fact]
    public void EnsureOccurrenceCompletable_RepeatingLocalSessionsOnly()
    {
        var repeating = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        repeating.EnsureOccurrenceCompletable(Date);              // anchor Tuesday
        repeating.EnsureOccurrenceCompletable(Date.AddDays(7));   // next Tuesday
        Assert.Throws<DomainException>(
            () => repeating.EnsureOccurrenceCompletable(Date.AddDays(1))); // Wednesday

        // One-off sessions complete their Task; external events complete nothing.
        var oneOff = CalendarBlock.CreateTaskSession(
            TaskId.New(), Date, new TimeOnly(15, 0), new TimeOnly(16, 0), Now);
        Assert.Throws<DomainException>(() => oneOff.EnsureOccurrenceCompletable(Date));
        Assert.Throws<DomainException>(() => ExternalEvent().EnsureOccurrenceCompletable(Date));
    }

    [Fact]
    public void ExternalEvents_KeepProviderIdentifiers()
    {
        var external = ExternalEvent();
        Assert.Equal("google", external.Provider);
        Assert.Equal("evt-1", external.ExternalId);
        Assert.Equal("External", external.Title);
        Assert.Null(external.TaskId);
    }
}

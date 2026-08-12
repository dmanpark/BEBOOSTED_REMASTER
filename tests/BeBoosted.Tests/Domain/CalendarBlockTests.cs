using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Tests.Domain;

public sealed class CalendarBlockTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));
    private static readonly DateOnly Date = new(2026, 8, 11);

    [Fact]
    public void CreateFixedCommitment_ValidatesTitleAndTimes()
    {
        Assert.Throws<DomainException>(() => CalendarBlock.CreateFixedCommitment(
            " ", Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now));
        Assert.Throws<DomainException>(() => CalendarBlock.CreateFixedCommitment(
            "AP Economics", Date, new TimeOnly(9, 45), new TimeOnly(8, 30), Now));

        var block = CalendarBlock.CreateFixedCommitment(
            " AP Economics ", Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now);
        Assert.Equal("AP Economics", block.Title);
        Assert.Equal(BlockKind.FixedCommitment, block.Kind);
        Assert.Equal(CalendarBlock.LocalProvider, block.Provider);
        Assert.False(block.IsExternal);
        Assert.Equal(TimeSpan.FromMinutes(75), block.Duration);
    }

    [Fact]
    public void TaskBlocks_TakeOutcomes_FixedCommitmentsDoNot()
    {
        var taskBlock = CalendarBlock.CreateForTask(
            TaskId.New(), Date, new TimeOnly(15, 30), new TimeOnly(17, 0), Now);
        var fixedBlock = CalendarBlock.CreateFixedCommitment(
            "Lunch", Date, new TimeOnly(12, 0), new TimeOnly(12, 45), Now);

        taskBlock.RecordOutcome(BlockOutcome.Done, Now.AddHours(4));
        Assert.Equal(BlockOutcome.Done, taskBlock.Outcome);
        Assert.Equal(Now.AddHours(4), taskBlock.OutcomeRecordedAt);

        Assert.Throws<DomainException>(() => taskBlock.RecordOutcome(BlockOutcome.None, Now));
        Assert.Throws<DomainException>(() => fixedBlock.RecordOutcome(BlockOutcome.Done, Now));
    }

    [Fact]
    public void Reschedule_MovesBlock_AndRejectsExternalEdits()
    {
        var block = CalendarBlock.CreateForTask(
            TaskId.New(), Date, new TimeOnly(15, 30), new TimeOnly(17, 0), Now);
        block.Reschedule(Date.AddDays(2), new TimeOnly(9, 0), new TimeOnly(10, 30), Now.AddHours(1));

        Assert.Equal(Date.AddDays(2), block.Date);
        Assert.Equal(new TimeOnly(9, 0), block.StartTime);

        var external = CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, "External", Date, new TimeOnly(9, 0), new TimeOnly(10, 0),
            BlockKind.FixedCommitment, null, "google", "evt-1", 0, BlockOutcome.None, null, Now, Now);
        Assert.True(external.IsExternal);
        Assert.Throws<DomainException>(
            () => external.Reschedule(Date, new TimeOnly(10, 0), new TimeOnly(11, 0), Now));
    }

    [Fact]
    public void OccursOn_ExpandsRecurrenceFromAnchorDate()
    {
        var block = CalendarBlock.CreateFixedCommitment(
            "AP Economics", Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday));

        Assert.True(block.OccursOn(Date));                 // anchor Tuesday
        Assert.True(block.OccursOn(Date.AddDays(1)));      // Wednesday
        Assert.False(block.OccursOn(Date.AddDays(4)));     // Saturday
        Assert.True(block.OccursOn(Date.AddDays(7)));      // next Tuesday
        Assert.False(block.OccursOn(Date.AddDays(-1)));    // before the anchor

        var single = CalendarBlock.CreateForTask(TaskId.New(), Date, new TimeOnly(15, 0), new TimeOnly(16, 0), Now);
        Assert.True(single.OccursOn(Date));
        Assert.False(single.OccursOn(Date.AddDays(1)));
    }

    [Fact]
    public void Rename_OnlyAppliesToFixedCommitments()
    {
        var fixedBlock = CalendarBlock.CreateFixedCommitment(
            "Lunch", Date, new TimeOnly(12, 0), new TimeOnly(12, 45), Now);
        fixedBlock.Rename("Team lunch", Now);
        Assert.Equal("Team lunch", fixedBlock.Title);

        var taskBlock = CalendarBlock.CreateForTask(
            TaskId.New(), Date, new TimeOnly(15, 0), new TimeOnly(16, 0), Now);
        Assert.Throws<DomainException>(() => taskBlock.Rename("Nope", Now));
    }
}

using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Tests.Domain;

/// <summary>
/// Fixed-commitment completion is an explicit per-occurrence operation: valid only for
/// local fixed commitments on a date the block actually occurs. It never reuses the
/// task-block outcome rules and never mutates the block itself.
/// </summary>
public sealed class CommitmentCompletionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(-7));
    private static readonly DateOnly Date = new(2026, 8, 11);

    [Fact]
    public void Create_RecordsTheOccurrenceForALocalCommitment()
    {
        var block = CalendarBlock.CreateFixedCommitment(
            "Stats HW", Date, new TimeOnly(16, 0), new TimeOnly(17, 0), Now);

        var completion = CommitmentCompletion.Create(block, Date, Now.AddHours(2));

        Assert.Equal(block.Id, completion.BlockId);
        Assert.Equal(Date, completion.OccurrenceDate);
        Assert.Equal(Now.AddHours(2), completion.CompletedAt);
        // Completing never mutates the block itself.
        Assert.Equal(BlockOutcome.None, block.Outcome);
        Assert.Equal(Now, block.ModifiedAt);
    }

    [Fact]
    public void Create_AcceptsAnyOccurrenceOfARecurringSeries_ButNotOtherDates()
    {
        var block = CalendarBlock.CreateFixedCommitment(
            "AP Economics", Date, new TimeOnly(8, 30), new TimeOnly(9, 45), Now,
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));

        var wednesday = CommitmentCompletion.Create(block, Date.AddDays(1), Now);
        Assert.Equal(Date.AddDays(1), wednesday.OccurrenceDate);

        // Thursday is not an occurrence of this series.
        Assert.Throws<DomainException>(() => CommitmentCompletion.Create(block, Date.AddDays(2), Now));
    }

    [Fact]
    public void Create_RejectsTaskBlocksAndExternalCommitments()
    {
        var taskBlock = CalendarBlock.CreateForTask(
            TaskId.New(), Date, new TimeOnly(15, 0), new TimeOnly(16, 0), Now);
        Assert.Throws<DomainException>(() => CommitmentCompletion.Create(taskBlock, Date, Now));

        var external = CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, null, "External", Date, new TimeOnly(9, 0), new TimeOnly(10, 0),
            BlockKind.FixedCommitment, null, "google", "evt-1", 0, BlockOutcome.None, null, Now, Now);
        Assert.Throws<DomainException>(() => CommitmentCompletion.Create(external, Date, Now));
        Assert.Equal(Now, external.ModifiedAt);
    }

    [Fact]
    public void EnsureOccurrenceCompletable_GuardsReopenTheSameWay()
    {
        var local = CalendarBlock.CreateFixedCommitment(
            "Stats HW", Date, new TimeOnly(16, 0), new TimeOnly(17, 0), Now);
        local.EnsureOccurrenceCompletable(Date); // does not throw

        Assert.Throws<DomainException>(() => local.EnsureOccurrenceCompletable(Date.AddDays(1)));

        var external = CalendarBlock.Rehydrate(
            CalendarBlockId.New(), null, null, "External", Date, new TimeOnly(9, 0), new TimeOnly(10, 0),
            BlockKind.FixedCommitment, null, "google", "evt-1", 0, BlockOutcome.None, null, Now, Now);
        Assert.Throws<DomainException>(() => external.EnsureOccurrenceCompletable(Date));
    }
}

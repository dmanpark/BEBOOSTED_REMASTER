using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Prioritization;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;

namespace BeBoosted.Application.Planning;

public sealed record PlanDraftResult(PlanningProposal Proposal, IReadOnlyList<UnplacedTask> Unplaced);

/// <summary>
/// Plan-draft use cases. Drafts never touch the approved calendar; approving a block
/// materializes it as a calendar block with the same id, which keeps approval undoable.
/// Fixed events are never mutated by planning.
/// </summary>
public sealed class PlanningService(
    IPlanningProposalRepository proposals,
    ICalendarBlockRepository blocks,
    InboxQueryService inbox,
    IPrioritizationRepository prioritization,
    CalendarService calendar,
    IClock clock)
{
    /// <summary>Creates a fresh draft for the period, replacing any existing draft.</summary>
    public PlanDraftResult CreateDraft(PlanningPeriod period)
    {
        if (GetActiveDraft() is { } existing)
        {
            existing.Discard(clock.Now);
            proposals.Save(existing);
        }

        var (from, to) = period.Kind == PlanningPeriodKind.Today
            ? (period.Anchor, period.Anchor)
            : (period.Anchor, period.Anchor.AddDays(6));

        var candidates = inbox.GetInboxTasks();
        var ranks = prioritization.GetRanks(period.Key).ToDictionary(r => r.TaskId);
        var occurrences = calendar.GetOccurrences(from, to);
        var now = clock.Now;

        var result = DeterministicScheduler.Plan(
            candidates,
            occurrences,
            ranks,
            from,
            to,
            DateOnly.FromDateTime(now.LocalDateTime),
            TimeOnly.FromDateTime(now.LocalDateTime));

        var proposal = PlanningProposal.CreateDraft(period, result.Blocks, now);
        proposals.Save(proposal);
        return new PlanDraftResult(proposal, result.Unplaced);
    }

    public PlanningProposal? GetActiveDraft() => proposals.GetActiveDraft();

    public void MoveBlock(PlanningProposalId proposalId, CalendarBlockId blockId, DateOnly date, TimeOnly start)
    {
        var proposal = Require(proposalId);
        proposal.MoveBlock(blockId, date, start, clock.Now);
        proposals.Save(proposal);
    }

    public void ResizeBlock(PlanningProposalId proposalId, CalendarBlockId blockId, TimeOnly end)
    {
        var proposal = Require(proposalId);
        proposal.ResizeBlock(blockId, end, clock.Now);
        proposals.Save(proposal);
    }

    public void RemoveBlock(PlanningProposalId proposalId, CalendarBlockId blockId)
    {
        var proposal = Require(proposalId);
        proposal.RemoveBlock(blockId, clock.Now);
        proposals.Save(proposal);
    }

    /// <summary>Approves one draft block into a real calendar block. Returns its id.</summary>
    public CalendarBlockId ApproveBlock(PlanningProposalId proposalId, CalendarBlockId blockId)
    {
        var proposal = Require(proposalId);
        var block = proposal.GetBlock(blockId);
        proposal.ApproveBlock(blockId, clock.Now);

        var calendarBlock = CalendarBlock.Rehydrate(
            block.Id,
            block.TaskId,
            title: null,
            block.Date,
            block.StartTime,
            block.EndTime,
            BlockKind.TaskBlock,
            recurrence: null,
            CalendarBlock.LocalProvider,
            externalId: null,
            syncState: 0,
            BlockOutcome.None,
            outcomeRecordedAt: null,
            clock.Now,
            clock.Now);
        blocks.Add(calendarBlock);
        proposals.Save(proposal);
        return block.Id;
    }

    /// <summary>Approves every pending block. Returns the created calendar block ids.</summary>
    public IReadOnlyList<CalendarBlockId> ApproveAll(PlanningProposalId proposalId)
    {
        var proposal = Require(proposalId);
        var pending = proposal.PendingBlocks.Select(b => b.Id).ToList();
        var created = new List<CalendarBlockId>();
        foreach (var blockId in pending)
        {
            created.Add(ApproveBlock(proposalId, blockId));
        }

        return created;
    }

    /// <summary>Undoes an approval: removes the created calendar blocks and reverts the draft.</summary>
    public void UndoApproval(PlanningProposalId proposalId, IReadOnlyList<CalendarBlockId> blockIds)
    {
        var proposal = Require(proposalId);
        foreach (var blockId in blockIds)
        {
            blocks.Delete(blockId);
            proposal.RevertBlock(blockId, clock.Now);
        }

        proposals.Save(proposal);
    }

    public void DiscardDraft(PlanningProposalId proposalId)
    {
        var proposal = Require(proposalId);
        proposal.Discard(clock.Now);
        proposals.Save(proposal);
    }

    private PlanningProposal Require(PlanningProposalId id)
        => proposals.GetById(id) ?? throw new DomainException("That plan draft no longer exists.");
}

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
/// Existing schedules and synced events are never mutated by planning.
/// </summary>
public sealed class PlanningService(
    IPlanningProposalRepository proposals,
    InboxQueryService inbox,
    IPrioritizationRepository prioritization,
    CalendarService calendar,
    ICalendarMutations mutations,
    IClock clock)
{
    /// <summary>
    /// Creates a fresh draft for the period, replacing any existing draft. The
    /// replacement is planned first without touching persistence; then one
    /// transaction discards the current active draft (re-read through the
    /// transaction-bound repository) and saves the new draft together, so a
    /// failure anywhere leaves the previous draft as the unchanged active draft.
    /// </summary>
    public PlanDraftResult CreateDraft(PlanningPeriod period)
    {
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
        if (result.Blocks.Count > 0)
        {
            mutations.Execute((_, _, _, proposalRepo) =>
            {
                if (proposalRepo.GetActiveDraft() is { } existing)
                {
                    existing.Discard(now);
                    proposalRepo.Save(existing);
                }

                proposalRepo.Save(proposal);
            });
        }

        // An empty plan writes nothing: it must not discard the draft the user can
        // see, and a zero-block draft must never survive as the invisible active one.
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
        => ApproveAtomically(proposalId, [blockId]).Single();

    /// <summary>Approves every pending block. Returns the created calendar block ids.</summary>
    public IReadOnlyList<CalendarBlockId> ApproveAll(PlanningProposalId proposalId)
        => ApproveAtomically(proposalId, blockIds: null);

    /// <summary>
    /// One transaction for the whole approval: load the proposal through the
    /// transaction-bound repository, validate everything (the proposal still an
    /// active draft, every target still pending, every Task existing and not
    /// completed) before the first write, materialize every calendar block and
    /// approve every draft block, save the proposal once, and commit once. The
    /// created ids are returned only after the commit succeeds — a failure anywhere
    /// leaves every block and status untouched, so a retry never collides.
    /// </summary>
    private IReadOnlyList<CalendarBlockId> ApproveAtomically(
        PlanningProposalId proposalId, IReadOnlyList<CalendarBlockId>? blockIds)
    {
        var created = new List<CalendarBlockId>();
        mutations.Execute((blockRepo, _, taskRepo, proposalRepo) =>
        {
            var proposal = proposalRepo.GetById(proposalId)
                ?? throw new DomainException("That plan draft no longer exists.");
            if (proposal.State != ProposalState.Draft)
            {
                throw new DomainException(
                    "That plan draft is no longer active — only an active draft can be approved.");
            }

            var targets = blockIds is null
                ? proposal.PendingBlocks.ToList()
                : blockIds.Select(proposal.GetBlock).ToList();

            foreach (var block in targets)
            {
                if (block.Status != ProposedBlockStatus.Pending)
                {
                    throw new DomainException("Only pending draft blocks can be approved.");
                }

                var task = taskRepo.GetById(block.TaskId)
                    ?? throw new DomainException(
                        "A proposed block's task no longer exists — re-plan to refresh the draft.");
                if (task.IsCompleted)
                {
                    throw new DomainException(
                        $"\"{task.Title}\" is already complete — reopen it or re-plan before scheduling it.");
                }
            }

            if (targets.Count == 0)
            {
                return;
            }

            var now = clock.Now;
            foreach (var block in targets)
            {
                proposal.ApproveBlock(block.Id, now);
                blockRepo.Add(CalendarBlock.Rehydrate(
                    block.Id,
                    block.TaskId,
                    title: null,
                    block.Date,
                    block.StartTime,
                    block.EndTime,
                    BlockKind.TaskSession,
                    recurrence: null,
                    CalendarBlock.LocalProvider,
                    externalId: null,
                    syncState: 0,
                    BlockOutcome.None,
                    outcomeRecordedAt: null,
                    now,
                    now));
                created.Add(block.Id);
            }

            proposalRepo.Save(proposal);
        });
        return created;
    }

    /// <summary>
    /// Undoes an approval as one transaction: the complete request is validated
    /// before any write — non-empty, no duplicates, the proposal still undoable
    /// against the live active draft, every id an approved block of the proposal,
    /// and every physical calendar block still the one-off local session that its
    /// approval created. Only then are the calendar blocks removed and the draft
    /// blocks reverted, with one proposal save. A failure restores the approved
    /// proposal and every calendar block together.
    /// </summary>
    public void UndoApproval(PlanningProposalId proposalId, IReadOnlyList<CalendarBlockId> blockIds)
    {
        if (blockIds.Count == 0)
        {
            throw new DomainException("There is nothing to undo.");
        }

        if (blockIds.Distinct().Count() != blockIds.Count)
        {
            throw new DomainException("An undo request can't name the same block twice.");
        }

        mutations.Execute((blockRepo, _, _, proposalRepo) =>
        {
            var proposal = proposalRepo.GetById(proposalId)
                ?? throw new DomainException("That plan draft no longer exists.");
            RequireUndoable(proposal, proposalRepo.GetActiveDraft());

            var targets = blockIds.Select(proposal.GetBlock).ToList();
            foreach (var block in targets)
            {
                if (block.Status != ProposedBlockStatus.Approved)
                {
                    throw new DomainException("Only an approved block can be reverted.");
                }

                var physical = blockRepo.GetById(block.Id)
                    ?? throw new DomainException(
                        "An approved block is no longer on the calendar — it can't be reverted.");
                if (physical.Kind != BlockKind.TaskSession
                    || physical.Provider != CalendarBlock.LocalProvider
                    || physical.TaskId != block.TaskId)
                {
                    throw new DomainException(
                        "A calendar block no longer matches its approval — it can't be reverted.");
                }

                // The approval created a one-off session. Once that session stands
                // for a repeating series, deleting it would take the whole series
                // with it — far more than the approval ever added.
                if (physical.Recurrence is not null)
                {
                    throw new DomainException(
                        "That block now repeats — undoing its approval would delete the whole series.");
                }
            }

            var now = clock.Now;
            foreach (var block in targets)
            {
                blockRepo.Delete(block.Id);
                proposal.RevertBlock(block.Id, now);
            }

            proposalRepo.Save(proposal);
        });
    }

    /// <summary>
    /// Undo may never leave two active plans behind, and never resurrects a plan the
    /// user has already replaced. Checked against the live active draft read through
    /// the same transaction, before the first delete or revert: a replaced plan is
    /// gone for good, a plan still in Draft must be the current one, and a fully
    /// approved plan may only reopen while no newer draft is waiting. The newer plan
    /// is never discarded or altered to make room.
    /// </summary>
    private static void RequireUndoable(PlanningProposal proposal, PlanningProposal? activeDraft)
    {
        var rejection = proposal.State switch
        {
            ProposalState.Discarded =>
                "That plan was replaced — its approvals can no longer be undone.",
            ProposalState.Draft when activeDraft?.Id != proposal.Id =>
                "That plan is no longer the active one — its approvals can no longer be undone.",
            ProposalState.Approved when activeDraft is not null =>
                "A newer plan draft is active — discard it before undoing this approval.",
            _ => null,
        };

        if (rejection is not null)
        {
            throw new DomainException(rejection);
        }
    }

    /// <summary>Discards the draft — loaded and validated live inside the transaction,
    /// so only a still-active draft can be discarded.</summary>
    public void DiscardDraft(PlanningProposalId proposalId)
    {
        mutations.Execute((_, _, _, proposalRepo) =>
        {
            var proposal = proposalRepo.GetById(proposalId)
                ?? throw new DomainException("That plan draft no longer exists.");
            if (proposal.State != ProposalState.Draft)
            {
                throw new DomainException("Only an active plan draft can be discarded.");
            }

            proposal.Discard(clock.Now);
            proposalRepo.Save(proposal);
        });
    }

    private PlanningProposal Require(PlanningProposalId id)
        => proposals.GetById(id) ?? throw new DomainException("That plan draft no longer exists.");
}

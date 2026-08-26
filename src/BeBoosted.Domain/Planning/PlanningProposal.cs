using BeBoosted.Domain.Prioritization;

namespace BeBoosted.Domain.Planning;

public enum ProposalState
{
    Draft = 0,
    Approved = 1,
    Discarded = 2,
}

public enum ProposedBlockStatus
{
    Pending = 0,
    Approved = 1,
    Removed = 2,
}

/// <summary>
/// User-relevant evidence for a proposed block — deadline, duration, priority,
/// availability, and (later) a source citation. Never chain-of-thought.
/// </summary>
public sealed record WhyEvidence(
    string? Deadline,
    string Duration,
    string? Priority,
    string Availability,
    string? Source);

/// <summary>One suggested calendar operation inside a draft. Its id becomes the
/// calendar block's id when approved, so approval is traceable and undoable.</summary>
public sealed class ProposedBlock
{
    public ProposedBlock(
        CalendarBlockId id,
        TaskId taskId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        WhyEvidence why,
        string? sessionLabel = null,
        ProposedBlockStatus status = ProposedBlockStatus.Pending)
    {
        if (endTime <= startTime)
        {
            throw new DomainException("A proposed block must end after it starts.");
        }

        Id = id;
        TaskId = taskId;
        Date = date;
        StartTime = startTime;
        EndTime = endTime;
        Why = why;
        SessionLabel = sessionLabel;
        Status = status;
    }

    public CalendarBlockId Id { get; }

    public TaskId TaskId { get; }

    public DateOnly Date { get; private set; }

    public TimeOnly StartTime { get; private set; }

    public TimeOnly EndTime { get; private set; }

    public WhyEvidence Why { get; }

    /// <summary>"Session 1 of 2" when a long task was split.</summary>
    public string? SessionLabel { get; }

    public ProposedBlockStatus Status { get; internal set; }

    public TimeSpan Duration => EndTime - StartTime;

    internal void Reschedule(DateOnly date, TimeOnly startTime, TimeOnly endTime)
    {
        if (endTime <= startTime)
        {
            throw new DomainException("A proposed block must end after it starts.");
        }

        Date = date;
        StartTime = startTime;
        EndTime = endTime;
    }
}

/// <summary>
/// A plan draft: suggested calendar operations that never mutate the approved calendar
/// until each one is explicitly approved. Draft blocks stay movable, resizable, and
/// individually removable/approvable.
/// </summary>
public sealed class PlanningProposal
{
    private readonly List<ProposedBlock> _blocks;

    public PlanningProposal(
        PlanningProposalId id,
        PlanningPeriod period,
        IEnumerable<ProposedBlock> blocks,
        ProposalState state,
        DateTimeOffset createdAt,
        DateTimeOffset modifiedAt)
    {
        Id = id;
        Period = period;
        _blocks = [.. blocks];
        State = state;
        CreatedAt = createdAt;
        ModifiedAt = modifiedAt;
    }

    public PlanningProposalId Id { get; }

    public PlanningPeriod Period { get; }

    public IReadOnlyList<ProposedBlock> Blocks => _blocks;

    public ProposalState State { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ModifiedAt { get; private set; }

    public IEnumerable<ProposedBlock> PendingBlocks
        => _blocks.Where(b => b.Status == ProposedBlockStatus.Pending);

    public static PlanningProposal CreateDraft(
        PlanningPeriod period, IEnumerable<ProposedBlock> blocks, DateTimeOffset now)
        => new(PlanningProposalId.New(), period, blocks, ProposalState.Draft, now, now);

    public ProposedBlock GetBlock(CalendarBlockId id)
        => _blocks.FirstOrDefault(b => b.Id == id)
            ?? throw new DomainException("That proposed block is no longer part of the draft.");

    public void MoveBlock(CalendarBlockId id, DateOnly date, TimeOnly startTime, DateTimeOffset now)
    {
        var block = RequirePending(id);
        var duration = block.Duration;
        block.Reschedule(date, startTime, startTime.Add(duration));
        Touch(now);
    }

    public void ResizeBlock(CalendarBlockId id, TimeOnly endTime, DateTimeOffset now)
    {
        var block = RequirePending(id);
        block.Reschedule(block.Date, block.StartTime, endTime);
        Touch(now);
    }

    /// <summary>
    /// Withdraws a pending block. A draft left with nothing pending is normalized
    /// rather than surviving as an empty active draft: approved work marks it
    /// approved, otherwise it is discarded.
    /// </summary>
    public void RemoveBlock(CalendarBlockId id, DateTimeOffset now)
    {
        RequirePending(id).Status = ProposedBlockStatus.Removed;
        SettleEmptiedDraft();
        Touch(now);
    }

    public void ApproveBlock(CalendarBlockId id, DateTimeOffset now)
    {
        RequirePending(id).Status = ProposedBlockStatus.Approved;
        if (!PendingBlocks.Any())
        {
            State = ProposalState.Approved;
        }

        Touch(now);
    }

    /// <summary>
    /// Reverts an approved block back to pending (undo support). A discarded plan
    /// is never resurrected this way — it was replaced, and reviving it would put a
    /// second active draft beside the newer plan.
    /// </summary>
    public void RevertBlock(CalendarBlockId id, DateTimeOffset now)
    {
        var block = GetBlock(id);
        if (State == ProposalState.Discarded)
        {
            throw new DomainException(
                "That plan was replaced — its approvals can no longer be undone.");
        }

        if (block.Status != ProposedBlockStatus.Approved)
        {
            throw new DomainException("Only an approved block can be reverted.");
        }

        block.Status = ProposedBlockStatus.Pending;
        State = ProposalState.Draft;
        Touch(now);
    }

    public void Discard(DateTimeOffset now)
    {
        State = ProposalState.Discarded;
        Touch(now);
    }

    /// <summary>
    /// Deleting a Task withdraws every one of its blocks from this proposal —
    /// pending, approved, or removed alike, so no row can outlive the Task. A draft
    /// left with nothing pending is normalized: approved work marks it approved,
    /// otherwise it is discarded rather than surviving as an empty active draft.
    /// Returns whether anything changed.
    /// </summary>
    public bool PruneBlocksForTask(TaskId taskId, DateTimeOffset now)
    {
        if (_blocks.RemoveAll(b => b.TaskId == taskId) == 0)
        {
            return false;
        }

        SettleEmptiedDraft();
        Touch(now);
        return true;
    }

    /// <summary>
    /// A draft with nothing left to decide stops being the active draft: approved
    /// work settles it as Approved, anything else as Discarded — so the active-draft
    /// lookup stops returning it.
    /// </summary>
    private void SettleEmptiedDraft()
    {
        if (State != ProposalState.Draft || PendingBlocks.Any())
        {
            return;
        }

        State = _blocks.Any(b => b.Status == ProposedBlockStatus.Approved)
            ? ProposalState.Approved
            : ProposalState.Discarded;
    }

    private ProposedBlock RequirePending(CalendarBlockId id)
    {
        var block = GetBlock(id);
        return block.Status == ProposedBlockStatus.Pending
            ? block
            : throw new DomainException("Only pending draft blocks can be changed.");
    }

    private void Touch(DateTimeOffset now) => ModifiedAt = now;
}

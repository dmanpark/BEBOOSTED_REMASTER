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

    public void RemoveBlock(CalendarBlockId id, DateTimeOffset now)
    {
        RequirePending(id).Status = ProposedBlockStatus.Removed;
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

    /// <summary>Reverts an approved block back to pending (undo support).</summary>
    public void RevertBlock(CalendarBlockId id, DateTimeOffset now)
    {
        var block = GetBlock(id);
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

    private ProposedBlock RequirePending(CalendarBlockId id)
    {
        var block = GetBlock(id);
        return block.Status == ProposedBlockStatus.Pending
            ? block
            : throw new DomainException("Only pending draft blocks can be changed.");
    }

    private void Touch(DateTimeOffset now) => ModifiedAt = now;
}

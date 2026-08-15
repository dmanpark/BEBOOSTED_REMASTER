using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Application.Calendar;

/// <summary>Calendar use cases: commitments, task scheduling, movement, and outcomes.</summary>
public sealed class CalendarService(
    ICalendarBlockRepository blocks,
    ICommitmentCompletionRepository completions,
    ICalendarMutations mutations,
    ITaskRepository tasks,
    IClock clock)
{
    /// <summary>Default length for scheduling a task without an estimate.</summary>
    public static readonly TimeSpan DefaultTaskBlockDuration = TimeSpan.FromMinutes(30);

    public CalendarBlock CreateFixedCommitment(
        string title,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        RecurrenceRule? recurrence = null,
        ProjectId? projectId = null)
    {
        var block = CalendarBlock.CreateFixedCommitment(
            title, date, startTime, endTime, clock.Now, recurrence, projectId);
        blocks.Add(block);
        return block;
    }

    /// <summary>
    /// Edits a local fixed commitment in one step: title, date, times, recurrence
    /// (whole series), project link, and — when <paramref name="completion"/> is given —
    /// the requested completion state. Everything persists in one atomic mutation, and
    /// completion rows are reconciled with the edited schedule:
    /// a completed one-off follows its date; completions on dates the edited block no
    /// longer occurs on are removed (so recurring↔one-off conversions and weekday or
    /// anchor changes never leave resurrectable rows); requesting completion for an
    /// occurrence the edit removes is rejected, never silently ignored.
    /// </summary>
    public CalendarBlock UpdateFixedCommitment(
        CalendarBlockId id,
        string title,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        RecurrenceRule? recurrence,
        ProjectId? projectId,
        CommitmentCompletionRequest? completion = null)
    {
        var block = Require(id);
        var previousDate = block.Date;
        var previouslyOneOff = block.Recurrence is null;

        // A one-off's completion follows its (possibly new) date; a series completes
        // the occurrence the editor was opened for. Validate before mutating anything.
        var completionTarget = completion is null
            ? (DateOnly?)null
            : recurrence is null ? date : completion.OpenedOccurrence;
        if (completion is { Completed: true }
            && !WillOccurOn(date, recurrence, completionTarget!.Value))
        {
            throw new DomainException(
                "That occurrence no longer exists after this change — untick Completed or keep its weekday.");
        }

        block.UpdateCommitment(title, date, startTime, endTime, recurrence, projectId, clock.Now);
        mutations.Execute((blockRepo, completionRepo) =>
        {
            blockRepo.Update(block);
            CarryOneOffCompletion(completionRepo, block, previousDate, previouslyOneOff);
            RemoveObsoleteCompletions(completionRepo, block);
            if (completionTarget is { } target && block.OccursOn(target))
            {
                ApplyCompletion(completionRepo, block, target, completion!.Completed);
            }
        });
        return block;
    }

    private static bool WillOccurOn(DateOnly date, RecurrenceRule? recurrence, DateOnly occurrence)
        => recurrence is null ? occurrence == date : recurrence.OccursOn(occurrence, date);

    /// <summary>No completion row may outlive its occurrence after a schedule edit.</summary>
    private static void RemoveObsoleteCompletions(
        ICommitmentCompletionRepository completionRepo, CalendarBlock block)
    {
        foreach (var completion in completionRepo.GetForBlock(block.Id))
        {
            if (!block.OccursOn(completion.OccurrenceDate))
            {
                completionRepo.Remove(block.Id, completion.OccurrenceDate);
            }
        }
    }

    private void ApplyCompletion(
        ICommitmentCompletionRepository completionRepo,
        CalendarBlock block,
        DateOnly occurrenceDate,
        bool completed)
    {
        var existing = completionRepo.Get(block.Id, occurrenceDate);
        if (completed && existing is null)
        {
            completionRepo.Add(CommitmentCompletion.Create(block, occurrenceDate, clock.Now));
        }
        else if (!completed && existing is not null)
        {
            completionRepo.Remove(block.Id, occurrenceDate);
        }
    }

    // ---- Per-occurrence commitment completion ----

    /// <summary>
    /// Checks off one occurrence of a local fixed commitment. Returns false when the
    /// occurrence was already complete (a quiet no-op, so callers announce no change).
    /// </summary>
    public bool CompleteCommitmentOccurrence(CalendarBlockId id, DateOnly occurrenceDate)
    {
        var block = Require(id);
        block.EnsureOccurrenceCompletable(occurrenceDate);
        if (completions.Get(id, occurrenceDate) is not null)
        {
            return false;
        }

        completions.Add(CommitmentCompletion.Create(block, occurrenceDate, clock.Now));
        return true;
    }

    /// <summary>Reopens a checked-off occurrence. Returns false when it was not complete.</summary>
    public bool ReopenCommitmentOccurrence(CalendarBlockId id, DateOnly occurrenceDate)
    {
        var block = Require(id);
        block.EnsureOccurrenceCompletable(occurrenceDate);
        if (completions.Get(id, occurrenceDate) is null)
        {
            return false;
        }

        completions.Remove(id, occurrenceDate);
        return true;
    }

    /// <summary>Applies a requested completion state; returns whether anything changed.</summary>
    public bool SetCommitmentOccurrenceCompletion(CalendarBlockId id, DateOnly occurrenceDate, bool completed)
        => completed
            ? CompleteCommitmentOccurrence(id, occurrenceDate)
            : ReopenCommitmentOccurrence(id, occurrenceDate);

    public bool IsCommitmentOccurrenceCompleted(CalendarBlockId id, DateOnly occurrenceDate)
        => completions.Get(id, occurrenceDate) is not null;

    /// <summary>
    /// A completed one-off commitment keeps its done state when its date changes —
    /// the completion record follows the block to its new (only) occurrence. This
    /// applies only when the previous schedule was ALSO a one-off: converting a
    /// recurring series into a one-off must never let an old anchor-occurrence
    /// completion masquerade as the new date's completion.
    /// </summary>
    private static void CarryOneOffCompletion(
        ICommitmentCompletionRepository completionRepo,
        CalendarBlock block,
        DateOnly previousDate,
        bool previouslyOneOff)
    {
        if (!previouslyOneOff
            || block.Kind != BlockKind.FixedCommitment || block.Recurrence is not null
            || block.Date == previousDate
            || completionRepo.Get(block.Id, previousDate) is not { } done)
        {
            return;
        }

        completionRepo.Remove(block.Id, previousDate);
        completionRepo.Add(done with { OccurrenceDate = block.Date });
    }

    /// <summary>Deletes a local fixed commitment (whole series when recurring).</summary>
    public void DeleteLocalCommitment(CalendarBlockId id)
    {
        var block = Require(id);
        if (block.Kind != BlockKind.FixedCommitment || block.IsExternal)
        {
            throw new DomainException("Only local commitments are deleted here.");
        }

        blocks.Delete(id);
    }

    /// <summary>Removes an approved task block from the calendar (its task stays open).</summary>
    public void UnscheduleTaskBlock(CalendarBlockId id)
    {
        var block = Require(id);
        if (block.Kind != BlockKind.TaskBlock || block.IsExternal)
        {
            throw new DomainException("Only local task blocks can be unscheduled.");
        }

        blocks.Delete(id);
    }

    /// <summary>Directly schedules a task (drag from the Inbox onto the calendar).</summary>
    public CalendarBlock ScheduleTask(TaskId taskId, DateOnly date, TimeOnly startTime)
    {
        var task = tasks.GetById(taskId) ?? throw new DomainException($"Task {taskId} no longer exists.");
        var duration = task.EstimatedDuration ?? DefaultTaskBlockDuration;
        var endTime = ClampEnd(startTime, duration);
        var block = CalendarBlock.CreateForTask(taskId, date, startTime, endTime, clock.Now);
        blocks.Add(block);
        return block;
    }

    public CalendarBlock MoveBlock(CalendarBlockId id, DateOnly date, TimeOnly startTime)
    {
        var block = Require(id);
        var previousDate = block.Date;
        var previouslyOneOff = block.Recurrence is null;
        var endTime = ClampEnd(startTime, block.Duration);
        block.Reschedule(date, startTime, endTime, clock.Now);
        mutations.Execute((blockRepo, completionRepo) =>
        {
            blockRepo.Update(block);
            CarryOneOffCompletion(completionRepo, block, previousDate, previouslyOneOff);
        });
        return block;
    }

    public CalendarBlock ResizeBlock(CalendarBlockId id, TimeOnly endTime)
    {
        var block = Require(id);
        block.Reschedule(block.Date, block.StartTime, endTime, clock.Now);
        blocks.Update(block);
        return block;
    }

    public CalendarBlock? GetBlock(CalendarBlockId id) => blocks.GetById(id);

    /// <summary>
    /// Records a block outcome and applies its task effect:
    /// Done completes the task; Needs more time updates the remaining estimate and sends
    /// the task back to the Inbox; Didn't happen leaves the task open for replanning.
    /// </summary>
    public void RecordOutcome(CalendarBlockId id, BlockOutcome outcome, TimeSpan? remaining = null)
    {
        var block = Require(id);
        block.RecordOutcome(outcome, clock.Now);
        blocks.Update(block);

        if (block.TaskId is not { } taskId || tasks.GetById(taskId) is not { } task)
        {
            return;
        }

        switch (outcome)
        {
            case BlockOutcome.Done:
                task.Complete(clock.Now);
                tasks.Update(task);
                break;
            case BlockOutcome.NeedsMoreTime:
                task.RecordNeedsMoreTime(
                    remaining ?? task.EstimatedDuration ?? DefaultTaskBlockDuration,
                    clock.Now);
                tasks.Update(task);
                break;
            case BlockOutcome.DidntHappen:
                break;
        }
    }

    /// <summary>Expands recurring blocks into concrete occurrences for the visible range.</summary>
    public IReadOnlyList<BlockOccurrence> GetOccurrences(DateOnly from, DateOnly to)
    {
        var completed = completions.GetBetween(from, to)
            .Select(c => (c.BlockId, c.OccurrenceDate))
            .ToHashSet();
        var occurrences = new List<BlockOccurrence>();
        foreach (var block in blocks.GetCandidatesBetween(from, to))
        {
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (block.OccursOn(date))
                {
                    occurrences.Add(new BlockOccurrence(
                        block, date, completed.Contains((block.Id, date))));
                }
            }
        }

        return occurrences
            .OrderBy(o => o.Date)
            .ThenBy(o => o.StartTime)
            .ToList();
    }

    /// <summary>Elapsed task blocks that still need an outcome (the quiet review notice).</summary>
    public IReadOnlyList<CalendarBlock> GetBlocksNeedingOutcome()
    {
        var now = clock.Now;
        return blocks.GetElapsedWithoutOutcome(DateOnly.FromDateTime(now.LocalDateTime), TimeOnly.FromDateTime(now.LocalDateTime));
    }

    private CalendarBlock Require(CalendarBlockId id)
        => blocks.GetById(id) ?? throw new DomainException($"Calendar block {id} no longer exists.");

    private static TimeOnly ClampEnd(TimeOnly startTime, TimeSpan duration)
    {
        // Blocks never cross midnight; clamp the end to 23:59.
        var minutesToMidnight = (24 * 60) - (int)startTime.ToTimeSpan().TotalMinutes - 1;
        var minutes = Math.Min((int)duration.TotalMinutes, minutesToMidnight);
        if (minutes < 5)
        {
            minutes = Math.Min(5, minutesToMidnight);
        }

        return startTime.AddMinutes(minutes);
    }
}

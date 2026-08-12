using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Application.Calendar;

/// <summary>Calendar use cases: commitments, task scheduling, movement, and outcomes.</summary>
public sealed class CalendarService(
    ICalendarBlockRepository blocks,
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
        RecurrenceRule? recurrence = null)
    {
        var block = CalendarBlock.CreateFixedCommitment(title, date, startTime, endTime, clock.Now, recurrence);
        blocks.Add(block);
        return block;
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
        var endTime = ClampEnd(startTime, block.Duration);
        block.Reschedule(date, startTime, endTime, clock.Now);
        blocks.Update(block);
        return block;
    }

    public CalendarBlock ResizeBlock(CalendarBlockId id, TimeOnly endTime)
    {
        var block = Require(id);
        block.Reschedule(block.Date, block.StartTime, endTime, clock.Now);
        blocks.Update(block);
        return block;
    }

    public void DeleteBlock(CalendarBlockId id) => blocks.Delete(id);

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
        var occurrences = new List<BlockOccurrence>();
        foreach (var block in blocks.GetCandidatesBetween(from, to))
        {
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (block.OccursOn(date))
                {
                    occurrences.Add(new BlockOccurrence(block, date));
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

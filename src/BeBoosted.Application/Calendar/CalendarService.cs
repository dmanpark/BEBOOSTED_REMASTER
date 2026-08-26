using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Application.Calendar;

/// <summary>A Task's requested schedule: one session, optionally repeating.</summary>
public sealed record TaskScheduleRequest(
    DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, RecurrenceRule? Recurrence);

/// <summary>
/// The editor's requested completion state, applied atomically with the other fields.
/// <paramref name="OpenedOccurrence"/> is the occurrence the editor was opened for —
/// a repeating schedule completes per occurrence; everything else completes the Task.
/// </summary>
public sealed record TaskCompletionRequest(DateOnly OpenedOccurrence, bool Completed);

/// <summary>The details half of the Task editor: everything except the schedule.</summary>
public sealed record TaskDetailsRequest(
    string Title, ProjectId? ProjectId, DateOnly? Deadline, TimeSpan? EstimatedDuration);

/// <summary>
/// Calendar and task-schedule use cases: the unified Task editor's persistence path,
/// direct scheduling, movement, outcomes, and per-occurrence completion.
/// </summary>
public sealed class CalendarService(
    ICalendarBlockRepository blocks,
    IOccurrenceCompletionRepository completions,
    ICalendarMutations mutations,
    ITaskRepository tasks,
    IClock clock)
{
    /// <summary>Default length for scheduling a task without an estimate.</summary>
    public static readonly TimeSpan DefaultTaskBlockDuration = TimeSpan.FromMinutes(30);

    // ---- The unified Task editor's one persistence path ----

    /// <summary>Creates a Task (optionally scheduled) in one atomic save.</summary>
    public TaskItem CreateTask(TaskDetailsRequest details, TaskScheduleRequest? schedule = null)
    {
        var task = TaskItem.Create(
            details.Title, clock.Now,
            estimatedDuration: details.EstimatedDuration,
            deadline: details.Deadline,
            projectId: details.ProjectId);
        var session = schedule is null ? null : BuildSession(task.Id, schedule);
        mutations.Execute((blockRepo, _, taskRepo) =>
        {
            taskRepo.Add(task);
            if (session is not null)
            {
                blockRepo.Add(session);
            }
        });
        return task;
    }

    /// <summary>
    /// The whole-task editor's save: task fields plus the aggregate completion
    /// transition. Never changes any session's date, time, or recurrence —
    /// aggregate completion may still resolve or reopen one-off siblings'
    /// outcomes, exactly like the task-row checkbox.
    /// </summary>
    public TaskItem UpdateTaskDetails(
        TaskId taskId, TaskDetailsRequest details, TaskCompletionRequest? completion = null)
    {
        var task = RequireTask(taskId);
        var sessions = blocks.GetForTask(taskId);

        if (string.IsNullOrWhiteSpace(details.Title))
        {
            throw new DomainException("A task needs a title.");
        }

        if (details.EstimatedDuration is { } estimate && estimate <= TimeSpan.Zero)
        {
            throw new DomainException("An estimated duration must be positive.");
        }

        if (completion is { Completed: true } && sessions.Any(s => s.Recurrence is not null))
        {
            throw new DomainException("A repeating task completes per occurrence, not as a whole.");
        }

        var now = clock.Now;
        task.Rename(details.Title, now);
        task.SetEstimatedDuration(details.EstimatedDuration, now);
        task.SetDeadline(details.Deadline, now);
        task.AssignToProject(details.ProjectId, now);

        List<CalendarBlock> touched = completion is { } request
            ? ApplyAggregateCompletion(task, sessions, request.Completed, now, out _)
            : [];

        mutations.Execute((blockRepo, _, taskRepo) =>
        {
            taskRepo.Update(task);
            foreach (var session in touched)
            {
                blockRepo.Update(session);
            }
        });
        return task;
    }

    /// <summary>
    /// The session editor's save: one block's schedule plus the staged occurrence
    /// completion, atomically. Task detail fields are never touched here; the only
    /// task effect is conversion reconciliation (a completed one-off converted to
    /// repeating reopens the Task — a repeating task is never globally complete).
    /// </summary>
    public CalendarBlock UpdateSessionSchedule(
        TaskId taskId,
        CalendarBlockId sessionId,
        TaskScheduleRequest schedule,
        TaskCompletionRequest? occurrenceCompletion = null)
    {
        var task = RequireTask(taskId);
        var session = Require(sessionId);
        if (session.TaskId != taskId)
        {
            throw new DomainException("That session belongs to a different task.");
        }

        if (schedule.EndTime <= schedule.StartTime)
        {
            throw new DomainException("A block must end after it starts.");
        }

        if (occurrenceCompletion is { Completed: true } && schedule.Recurrence is { } recurrence
            && !recurrence.OccursOn(occurrenceCompletion.OpenedOccurrence, schedule.Date))
        {
            throw new DomainException(
                "That occurrence no longer exists after this change — untick Completed or keep its weekday.");
        }

        var now = clock.Now;
        session.Reschedule(schedule.Date, schedule.StartTime, schedule.EndTime, now);
        session.SetRecurrence(schedule.Recurrence, now);

        // Conversion reconciliation: a repeating schedule forbids global completion,
        // and a conversion never completes anything.
        var taskTouched = false;
        if (schedule.Recurrence is not null)
        {
            if (task.IsCompleted)
            {
                task.Reopen(now);
                taskTouched = true;
            }

            if (session.Outcome != BlockOutcome.None)
            {
                session.ClearOutcome(now);
            }
        }

        mutations.Execute((blockRepo, completionRepo, taskRepo) =>
        {
            if (taskTouched)
            {
                taskRepo.Update(task);
            }

            blockRepo.Update(session);
            RemoveObsoleteCompletions(completionRepo, session);
            if (occurrenceCompletion is { } request && session.Recurrence is not null
                && session.OccursOn(request.OpenedOccurrence))
            {
                ApplyOccurrenceCompletion(
                    completionRepo, session, request.OpenedOccurrence, request.Completed);
            }
        });
        return session;
    }

    /// <summary>A new session for an existing task; the only entry that may create a second repeating schedule.</summary>
    public CalendarBlock AddSession(TaskId taskId, TaskScheduleRequest schedule)
    {
        var task = RequireTask(taskId);
        if (task.IsCompleted)
        {
            throw new DomainException(
                "That task is already complete — reopen it before scheduling more work.");
        }

        if (schedule.EndTime <= schedule.StartTime)
        {
            throw new DomainException("A block must end after it starts.");
        }

        var block = CalendarBlock.CreateTaskSession(
            taskId, schedule.Date, schedule.StartTime, schedule.EndTime, clock.Now,
            schedule.Recurrence);
        blocks.Add(block);
        return block;
    }

    /// <summary>Removes every session of a task (with completion rows) in one transaction; the task survives.</summary>
    public void UnscheduleAllSessions(TaskId taskId)
    {
        _ = RequireTask(taskId);
        mutations.Execute((blockRepo, completionRepo, _) =>
        {
            foreach (var session in blockRepo.GetForTask(taskId))
            {
                RemoveSession(blockRepo, completionRepo, session);
            }
        });
    }

    /// <summary>
    /// Deletes a Task with its schedule sessions, their completion records, and every
    /// planning-proposal block that references it — one transaction, so a failure
    /// leaves the whole aggregate (proposals included) exactly as it was. Drafts left
    /// with nothing pending are normalized rather than surviving as empty drafts.
    /// </summary>
    public void DeleteTask(TaskId taskId)
    {
        _ = RequireTask(taskId);
        var now = clock.Now;
        mutations.Execute((blockRepo, completionRepo, taskRepo, proposalRepo) =>
        {
            foreach (var session in blockRepo.GetForTask(taskId))
            {
                RemoveSession(blockRepo, completionRepo, session);
            }

            foreach (var proposal in proposalRepo.GetAll())
            {
                if (proposal.PruneBlocksForTask(taskId, now))
                {
                    proposalRepo.Save(proposal);
                }
            }

            taskRepo.Delete(taskId);
        });
    }

    private CalendarBlock BuildSession(TaskId taskId, TaskScheduleRequest schedule)
        => CalendarBlock.CreateTaskSession(
            taskId, schedule.Date, schedule.StartTime, schedule.EndTime, clock.Now,
            schedule.Recurrence);

    private static void RemoveSession(
        ICalendarBlockRepository blockRepo,
        IOccurrenceCompletionRepository completionRepo,
        CalendarBlock session)
    {
        foreach (var completion in completionRepo.GetForBlock(session.Id))
        {
            completionRepo.Remove(session.Id, completion.OccurrenceDate);
        }

        blockRepo.Delete(session.Id);
    }

    /// <summary>No completion row may outlive its occurrence after a schedule edit.</summary>
    private static void RemoveObsoleteCompletions(
        IOccurrenceCompletionRepository completionRepo, CalendarBlock session)
    {
        foreach (var completion in completionRepo.GetForBlock(session.Id))
        {
            if (session.Recurrence is null || !session.OccursOn(completion.OccurrenceDate))
            {
                completionRepo.Remove(session.Id, completion.OccurrenceDate);
            }
        }
    }

    private void ApplyOccurrenceCompletion(
        IOccurrenceCompletionRepository completionRepo,
        CalendarBlock session,
        DateOnly occurrenceDate,
        bool completed)
    {
        var existing = completionRepo.Get(session.Id, occurrenceDate);
        if (completed && existing is null)
        {
            completionRepo.Add(OccurrenceCompletion.Create(session, occurrenceDate, clock.Now));
        }
        else if (!completed && existing is not null)
        {
            completionRepo.Remove(session.Id, occurrenceDate);
        }
    }

    // ---- Task-level completion (the product's one authoritative path) ----

    /// <summary>
    /// The product's one authoritative task-completion path (Inbox, Daily, and
    /// Project rows): completes the Task and resolves every still-pending one-off
    /// session as Done in the same transaction. Sessions resolved another way keep
    /// their history; repeating tasks complete per occurrence, never here. Returns
    /// false when the task was already complete (a quiet no-op).
    /// </summary>
    public bool CompleteTask(TaskId taskId) => SetTaskCompletion(taskId, completed: true);

    /// <summary>Reopens a Task, clearing every matching Done session outcome atomically.</summary>
    public bool ReopenTask(TaskId taskId) => SetTaskCompletion(taskId, completed: false);

    private bool SetTaskCompletion(TaskId taskId, bool completed)
    {
        var task = RequireTask(taskId);
        var sessions = blocks.GetForTask(taskId);

        // Completing while any series repeats is forbidden; reopening is always
        // allowed so a corrupted globally-completed repeating Task can recover.
        if (completed && sessions.Any(s => s.Recurrence is not null))
        {
            throw new DomainException("A repeating task completes per occurrence, not as a whole.");
        }

        var touched = ApplyAggregateCompletion(task, sessions, completed, clock.Now, out var taskChanged);

        // "Already there" is an aggregate statement: only when neither the Task nor
        // any one-off session needs a change is the request a quiet no-op.
        if (!taskChanged && touched.Count == 0)
        {
            return false;
        }

        mutations.Execute((blockRepo, _, taskRepo) =>
        {
            taskRepo.Update(task);
            foreach (var session in touched)
            {
                blockRepo.Update(session);
            }
        });
        return true;
    }

    /// <summary>
    /// The one aggregate completion transition: completing resolves every pending
    /// one-off session as Done; reopening clears every Done outcome. Sessions
    /// resolved another way — and repeating series — keep their state. Callers
    /// enforce the repeating-forbids-completion rule and persist the returned
    /// sessions inside their own single transaction.
    /// </summary>
    private static List<CalendarBlock> ApplyAggregateCompletion(
        TaskItem task,
        IEnumerable<CalendarBlock> sessions,
        bool completed,
        DateTimeOffset now,
        out bool taskChanged)
    {
        taskChanged = task.IsCompleted != completed;
        var touched = new List<CalendarBlock>();
        if (completed)
        {
            task.Complete(now);
            foreach (var session in sessions)
            {
                if (session is { Recurrence: null, Outcome: BlockOutcome.None })
                {
                    session.RecordOutcome(BlockOutcome.Done, now);
                    touched.Add(session);
                }
            }
        }
        else
        {
            task.Reopen(now);
            foreach (var session in sessions)
            {
                if (session is { Recurrence: null, Outcome: BlockOutcome.Done })
                {
                    session.ClearOutcome(now);
                    touched.Add(session);
                }
            }
        }

        return touched;
    }

    // ---- Per-occurrence completion (repeating sessions) ----

    /// <summary>
    /// Checks off one occurrence of a repeating task session. Returns false when the
    /// occurrence was already complete (a quiet no-op, so callers announce no change).
    /// </summary>
    public bool CompleteOccurrence(CalendarBlockId id, DateOnly occurrenceDate)
    {
        var block = Require(id);
        block.EnsureOccurrenceCompletable(occurrenceDate);
        if (completions.Get(id, occurrenceDate) is not null)
        {
            return false;
        }

        completions.Add(OccurrenceCompletion.Create(block, occurrenceDate, clock.Now));
        return true;
    }

    /// <summary>Reopens a checked-off occurrence. Returns false when it was not complete.</summary>
    public bool ReopenOccurrence(CalendarBlockId id, DateOnly occurrenceDate)
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
    public bool SetOccurrenceCompletion(CalendarBlockId id, DateOnly occurrenceDate, bool completed)
        => completed
            ? CompleteOccurrence(id, occurrenceDate)
            : ReopenOccurrence(id, occurrenceDate);

    public bool IsOccurrenceCompleted(CalendarBlockId id, DateOnly occurrenceDate)
        => completions.Get(id, occurrenceDate) is not null;

    // ---- Direct scheduling and movement ----

    /// <summary>Removes a session from the calendar (its task stays open).</summary>
    public void UnscheduleSession(CalendarBlockId id)
    {
        var block = Require(id);
        if (block.Kind != BlockKind.TaskSession || block.IsExternal)
        {
            throw new DomainException("External events are never edited by BeBoosted.");
        }

        mutations.Execute((blockRepo, completionRepo, _) =>
            RemoveSession(blockRepo, completionRepo, block));
    }

    /// <summary>Directly schedules a task (drag from the Inbox onto the calendar).</summary>
    public CalendarBlock ScheduleTask(TaskId taskId, DateOnly date, TimeOnly startTime)
    {
        var task = RequireTask(taskId);
        return ScheduleTask(taskId, date, startTime, task.EstimatedDuration ?? DefaultTaskBlockDuration);
    }

    /// <summary>Schedules a task with an explicit duration (manual Schedule flyout).</summary>
    public CalendarBlock ScheduleTask(TaskId taskId, DateOnly date, TimeOnly startTime, TimeSpan duration)
    {
        var task = RequireTask(taskId);

        // Direct scheduling schedules active work; a Done session for a completed
        // Task is only ever added deliberately through the editor's atomic
        // UpdateTask path — never as a silent pending session.
        if (task.IsCompleted)
        {
            throw new DomainException(
                "That task is already complete — reopen it before scheduling more work.");
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new DomainException("A block needs a positive duration.");
        }

        var endTime = ClampEnd(startTime, duration);
        var block = CalendarBlock.CreateTaskSession(taskId, date, startTime, endTime, clock.Now);
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

    /// <summary>
    /// Reschedules a session's date, start, and duration together — one validated
    /// repository write, so a failure can never leave a torn half-reschedule.
    /// </summary>
    public CalendarBlock RescheduleSession(
        CalendarBlockId id, DateOnly date, TimeOnly startTime, TimeSpan duration)
    {
        var block = Require(id);
        if (duration <= TimeSpan.Zero)
        {
            throw new DomainException("A block needs a positive duration.");
        }

        var endTime = ClampEnd(startTime, duration);
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

    public CalendarBlock? GetBlock(CalendarBlockId id) => blocks.GetById(id);

    /// <summary>The task's schedule sessions, earliest first.</summary>
    public IReadOnlyList<CalendarBlock> GetSessionsForTask(TaskId taskId)
        => blocks.GetForTask(taskId);

    public TaskItem? GetTask(TaskId taskId) => tasks.GetById(taskId);

    /// <summary>
    /// Records a one-off session outcome and applies its task effect as one atomic
    /// mutation. Done resolves that session alone — the Task stays open and its
    /// sibling sessions stay pending, so a Task with several sessions is finished
    /// one sitting at a time. Needs more time updates the remaining estimate and
    /// sends the task back to the Inbox; Didn't happen leaves the task open for
    /// replanning. Everything validates before anything persists.
    /// </summary>
    public void RecordOutcome(CalendarBlockId id, BlockOutcome outcome, TimeSpan? remaining = null)
    {
        var block = Require(id);

        // An orphaned local session must fail without changing the block.
        TaskItem? task = null;
        if (block is { IsExternal: false, Kind: BlockKind.TaskSession })
        {
            task = block.TaskId is { } taskId ? tasks.GetById(taskId) : null;
            if (task is null)
            {
                throw new DomainException("That session's task no longer exists.");
            }
        }

        block.RecordOutcome(outcome, clock.Now); // local one-off validation in the domain

        switch (outcome)
        {
            case BlockOutcome.Done:
                // A session's Done is local to that session. Whole-task completion
                // stays with CompleteTask / UpdateTaskDetails.
                break;
            case BlockOutcome.NeedsMoreTime:
                task!.RecordNeedsMoreTime(
                    remaining ?? task.EstimatedDuration ?? DefaultTaskBlockDuration,
                    clock.Now);
                break;
            case BlockOutcome.DidntHappen:
                break;
        }

        mutations.Execute((blockRepo, _, taskRepo) =>
        {
            blockRepo.Update(block);
            if (outcome == BlockOutcome.NeedsMoreTime)
            {
                taskRepo.Update(task!);
            }
        });
    }

    /// <summary>
    /// Takes back one session's outcome, leaving its siblings and its Task alone —
    /// the per-session inverse of RecordOutcome. Returns false when the session
    /// carried no outcome, so callers announce nothing.
    /// </summary>
    public bool ClearSessionOutcome(CalendarBlockId id)
    {
        var block = Require(id);
        if (block.Outcome == BlockOutcome.None)
        {
            return false;
        }

        block.ClearOutcome(clock.Now); // rejects external events in the domain
        mutations.Execute((blockRepo, _, _) => blockRepo.Update(block));
        return true;
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

    /// <summary>Elapsed one-off sessions that still need an outcome (the quiet review notice).</summary>
    public IReadOnlyList<CalendarBlock> GetBlocksNeedingOutcome()
    {
        var now = clock.Now;
        return blocks.GetElapsedWithoutOutcome(
            DateOnly.FromDateTime(now.LocalDateTime), TimeOnly.FromDateTime(now.LocalDateTime));
    }

    private CalendarBlock Require(CalendarBlockId id)
        => blocks.GetById(id) ?? throw new DomainException($"Calendar block {id} no longer exists.");

    private TaskItem RequireTask(TaskId id)
        => tasks.GetById(id) ?? throw new DomainException($"Task {id} no longer exists.");

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

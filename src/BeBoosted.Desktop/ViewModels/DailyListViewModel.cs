using System.Collections.ObjectModel;
using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Prioritization;
using BeBoosted.Application.Projects;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// The Today view's priority-first Daily list: what cannot move, what is most important,
/// what is scheduled, what remains unscheduled, and what has been completed. Rebuilt by
/// <see cref="CalendarViewModel.Reload"/>; every mutation refreshes dependents exactly once
/// through the owner.
/// </summary>
public sealed partial class DailyListViewModel : ViewModelBase
{
    private readonly CalendarViewModel _owner;
    private readonly InboxQueryService _inboxQuery;
    private readonly PrioritySortService _prioritySort;
    private readonly AiService _ai;
    private readonly CalendarService _calendar;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IClock _clock;

    private DateOnly _date;
    private Dictionary<ProjectId, string> _projectNames = [];

    internal DailyListViewModel(
        CalendarViewModel owner,
        InboxQueryService inboxQuery,
        PrioritySortService prioritySort,
        AiService ai,
        CalendarService calendar,
        ITaskRepository tasks,
        IProjectRepository projects,
        IClock clock)
    {
        _owner = owner;
        _inboxQuery = inboxQuery;
        _prioritySort = prioritySort;
        _ai = ai;
        _calendar = calendar;
        _tasks = tasks;
        _projects = projects;
        _clock = clock;
        _date = clock.Today;
    }

    /// <summary>The draft banner and its actions bind through the owner.</summary>
    public CalendarViewModel Owner => _owner;

    /// <summary>Raised after a manual schedule so the view can move focus to the new row.</summary>
    public event Action<TaskId>? RowFocusRequested;

    [ObservableProperty]
    public partial bool IsActive { get; internal set; }

    public ObservableCollection<DailyRowViewModel> ScheduledRows { get; } = [];

    public ObservableCollection<DailyRowViewModel> UnscheduledRows { get; } = [];

    public ObservableCollection<DailyRowViewModel> CompletedRows { get; } = [];

    // ---- Heading, progress, counts, empty states ----

    public string HeadingText => _date == _clock.Today
        ? "Today's tasks"
        : $"Tasks for {_date.ToString("dddd", CultureInfo.CurrentCulture)}";

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    public int ScheduledCount => ScheduledRows.Count;

    public int UnscheduledCount => UnscheduledRows.Count;

    public int CompletedCount => CompletedRows.Count;

    public bool HasCompleted => CompletedRows.Count > 0;

    public bool IsScheduledEmpty => ScheduledRows.Count == 0;

    public bool IsUnscheduledEmpty => UnscheduledRows.Count == 0;

    public bool IsAllComplete
        => ScheduledRows.Count == 0 && UnscheduledRows.Count == 0 && CompletedRows.Count > 0;

    [ObservableProperty]
    public partial bool IsCompletedExpanded { get; set; }

    // ---- Draft banner (compact inline summary above Scheduled) ----

    public bool ShowDraftBanner => _owner.HasDraft;

    public string DraftTitle => _owner.DraftTitle;

    public string DraftSummaryText => _owner.DraftSummaryText;

    // ---- Rebuild ----

    /// <summary>
    /// Rebuilds every section for the visible date. <paramref name="proposals"/> carries the
    /// active draft's pending blocks for ALL dates: same-date ones render in Scheduled and
    /// every proposal's task id is subtracted from Unscheduled, so no task shows twice.
    /// </summary>
    internal void Rebuild(
        DateOnly date,
        IReadOnlyList<BlockOccurrence> occurrences,
        IReadOnlyList<ProposedBlock> proposals,
        IReadOnlySet<CalendarBlockId> conflicts)
    {
        if (date != _date)
        {
            _date = date;
            IsCompletedExpanded = false;
        }

        _projectNames = _projects.GetAll().ToDictionary(p => p.Id, p => p.Name);

        var ranks = _prioritySort.GetRankLookup(PlanningPeriod.ForToday(date));
        var today = _clock.Today;
        var now = TimeOnly.FromDateTime(_clock.Now.LocalDateTime);

        var scheduled = new List<DailyRowViewModel>();
        var completed = new List<DailyRowViewModel>();
        // Suppresses the later completed-task sweep: a task already represented by a
        // done session today must not be counted a second time as a completed task.
        var doneTaskIds = new HashSet<TaskId>();
        var openOccurrenceCount = 0;
        var doneOccurrenceCount = 0;
        var openSessionCount = 0;
        var doneSessionCount = 0;

        foreach (var occurrence in occurrences.Where(o => o.Date == date))
        {
            var block = occurrence.Block;
            var task = block.TaskId is { } taskId ? _tasks.GetById(taskId) : null;
            var row = BuildOccurrenceRow(occurrence, task, ranks, conflicts, today, now);
            if (row.IsDone)
            {
                completed.Add(row);
                if (block.Recurrence is not null && !block.IsExternal)
                {
                    doneOccurrenceCount++;
                }
                else if (!block.IsExternal)
                {
                    // Every done session is its own unit of work — a task with two
                    // done sessions today is two units, not one.
                    doneSessionCount++;
                    if (block.TaskId is { } doneId)
                    {
                        doneTaskIds.Add(doneId);
                    }
                }
            }
            else if (row.HasRecordedOutcome)
            {
                // "Needs more time" / "Didn't happen": the session is settled history
                // for this day, and the task itself continues in Unscheduled — a task
                // never sits in Scheduled and Unscheduled at once. The open work is
                // counted through the task's other rows, not this resolved session.
                completed.Add(row);
            }
            else
            {
                scheduled.Add(row);
                if (block.IsExternal)
                {
                    // External events never count toward the day's task progress.
                }
                else if (block.Recurrence is not null)
                {
                    openOccurrenceCount++;
                }
                else
                {
                    // Every unresolved session is its own unit of remaining work.
                    openSessionCount++;
                }
            }
        }

        foreach (var proposal in proposals.Where(p => p.Date == date))
        {
            var task = _tasks.GetById(proposal.TaskId);
            scheduled.Add(DailyRowViewModel.ForProposal(
                this,
                proposal,
                task?.Title ?? "(deleted task)",
                ProjectNameFor(task?.ProjectId),
                task is null ? null : ranks.GetValueOrDefault(task.Id),
                task,
                conflicts.Contains(proposal.Id)));
        }

        var proposalTaskIds = proposals.Select(p => p.TaskId).ToHashSet();
        var unscheduled = new List<DailyRowViewModel>();
        var openTaskCount = 0;
        foreach (var task in _inboxQuery.GetInboxTasks())
        {
            if (proposalTaskIds.Contains(task.Id))
            {
                continue; // represented by an active proposal — never show twice
            }

            unscheduled.Add(BuildTaskRow(task, ranks, isDone: false));
            // A task only reaches Unscheduled once every session is resolved, so it
            // never overlaps with a session counted above — its own unit of work.
            openTaskCount++;
        }

        var completedTaskCount = 0;
        foreach (var task in _tasks.GetAll())
        {
            if (task.IsCompleted
                && task.CompletedAt is { } at
                && DateOnly.FromDateTime(at.LocalDateTime) == date
                && !doneTaskIds.Contains(task.Id))
            {
                completed.Add(BuildTaskRow(task, ranks, isDone: true));
                doneTaskIds.Add(task.Id);
                completedTaskCount++;
            }
        }

        var done = doneOccurrenceCount + doneSessionCount + completedTaskCount;
        var total = done + openOccurrenceCount + openSessionCount + openTaskCount;
        ProgressText = $"{done} of {total} complete";

        Replace(ScheduledRows, SortScheduled(scheduled));
        Replace(UnscheduledRows, SortUnscheduled(unscheduled));
        Replace(CompletedRows, SortCompleted(completed));
        SetEditingTask(_owner.EditingTaskId);

        if (CompletedRows.Count == 0)
        {
            IsCompletedExpanded = false;
        }

        OnPropertyChanged(nameof(HeadingText));
        OnPropertyChanged(nameof(ScheduledCount));
        OnPropertyChanged(nameof(UnscheduledCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(IsScheduledEmpty));
        OnPropertyChanged(nameof(IsUnscheduledEmpty));
        OnPropertyChanged(nameof(IsAllComplete));
        OnPropertyChanged(nameof(ShowDraftBanner));
        OnPropertyChanged(nameof(DraftTitle));
        OnPropertyChanged(nameof(DraftSummaryText));
    }

    private DailyRowViewModel BuildOccurrenceRow(
        BlockOccurrence occurrence,
        TaskItem? task,
        IReadOnlyDictionary<TaskId, PriorityRank> ranks,
        IReadOnlySet<CalendarBlockId> conflicts,
        DateOnly today,
        TimeOnly now)
    {
        var block = occurrence.Block;
        var title = block.Title ?? task?.Title ?? "(deleted task)";
        // Only a session with its own title needs its parent named beneath it.
        var parentTitle = block.Title is not null && !block.IsExternal ? task?.Title : null;
        var projectName = ProjectNameFor(task?.ProjectId);
        // A repeating session is done per occurrence; a one-off through its Task.
        var isDone = block.Recurrence is not null
            ? occurrence.IsCompleted
            : block.Outcome == BlockOutcome.Done || task?.IsCompleted == true;
        var elapsed = occurrence.Date < today || (occurrence.Date == today && block.EndTime <= now);
        var needsOutcome = block is { IsExternal: false, Recurrence: null }
            && block.Outcome == BlockOutcome.None && task?.IsCompleted != true && elapsed;

        var row = DailyRowViewModel.ForOccurrence(
            this,
            occurrence,
            title,
            parentTitle,
            projectName,
            task is null ? null : ranks.GetValueOrDefault(task.Id),
            task,
            conflicts.Contains(block.Id),
            isDone,
            needsOutcome);

        if (task is not null && block is { IsExternal: false, Recurrence: null })
        {
            row.TaskRow = CreateTaskRow(task);
            if (!isDone)
            {
                row.ScheduleEditor = new ScheduleFlyoutViewModel(
                    this, row, "Change time", "Change time",
                    occurrence.Date, block.StartTime, (int)block.Duration.TotalMinutes);
            }
        }

        return row;
    }

    private DailyRowViewModel BuildTaskRow(
        TaskItem task, IReadOnlyDictionary<TaskId, PriorityRank> ranks, bool isDone)
    {
        var row = DailyRowViewModel.ForTask(
            this,
            task,
            _date,
            ProjectNameFor(task.ProjectId),
            ranks.GetValueOrDefault(task.Id),
            CreateTaskRow(task),
            isDone,
            needsReview: !isDone && _ai.TaskNeedsReview(task));
        if (!isDone)
        {
            row.ScheduleEditor = new ScheduleFlyoutViewModel(
                this, row, "Schedule", "Schedule",
                _date,
                ScheduleFlyoutViewModel.DefaultStartFor(_date, _clock),
                (int)(task.EstimatedDuration ?? CalendarService.DefaultTaskBlockDuration).TotalMinutes);
        }

        return row;
    }

    private TaskRowViewModel CreateTaskRow(TaskItem task)
        => new(
            task, _calendar, _clock,
            onRemoved: _ => _owner.NotifyTasksMutated(),
            ProjectNameFor(task.ProjectId),
            onEditRequested: row => _owner.OpenTaskEditorForTask(row.Task.Id));

    private string? ProjectNameFor(ProjectId? projectId)
        => projectId is { } id ? _projectNames.GetValueOrDefault(id) : null;

    private static void Replace(ObservableCollection<DailyRowViewModel> target, IEnumerable<DailyRowViewModel> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    // ---- Ordering ----

    /// <summary>Time obligations by start; ranked tasks by tier and ordinal; unranked by start.</summary>
    private static IEnumerable<DailyRowViewModel> SortScheduled(List<DailyRowViewModel> rows)
        => rows
            .OrderBy(ScheduledGroup)
            .ThenBy(r => ScheduledGroup(r) == 1 ? (int)r.Rank!.Tier : 0)
            .ThenBy(r => ScheduledGroup(r) == 1 ? r.Rank!.Rank : 0)
            .ThenBy(r => ScheduledGroup(r) == 1 ? TimeOnly.MinValue : r.StartTime ?? TimeOnly.MinValue)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.BlockId?.ToString() ?? string.Empty);

    private static int ScheduledGroup(DailyRowViewModel row)
        => row.Kind == DailyRowKind.Obligation ? 0 : row.Rank is not null ? 1 : 2;

    /// <summary>Tier, ordinal rank, deadline (overdue/today first), then capture order.</summary>
    private IEnumerable<DailyRowViewModel> SortUnscheduled(List<DailyRowViewModel> rows)
        => rows
            .OrderBy(r => r.Rank is null ? 1 : 0)
            .ThenBy(r => r.Rank is { } rank ? (int)rank.Tier : int.MaxValue)
            .ThenBy(r => r.Rank is { } rank ? rank.Rank : int.MaxValue)
            .ThenBy(r => DeadlineBucket(r.Task?.Deadline))
            .ThenBy(r => r.Task?.Deadline ?? DateOnly.MaxValue)
            .ThenBy(r => r.Task?.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.TaskId?.ToString() ?? string.Empty);

    private int DeadlineBucket(DateOnly? deadline)
        => deadline is null ? 2 : deadline <= _clock.Today ? 0 : 1;

    private static IEnumerable<DailyRowViewModel> SortCompleted(List<DailyRowViewModel> rows)
        => rows
            .OrderBy(r => r.Kind switch
            {
                DailyRowKind.Obligation => 0,
                DailyRowKind.Session => 1,
                _ => 2,
            })
            .ThenBy(r => r.StartTime ?? TimeOnly.MinValue)
            .ThenBy(r => r.CompletedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase);

    // ---- Row mutations (each announces exactly once through the owner) ----

    internal void CompleteTask(DailyRowViewModel row)
    {
        // The authoritative path reconciles any one-off session outcome with the
        // Task in one transaction; a no-op announces nothing.
        if (row.TaskId is { } id && _calendar.CompleteTask(id))
        {
            _owner.NotifyTasksMutated();
        }
    }

    internal void ReopenRow(DailyRowViewModel row)
    {
        if (row.Kind == DailyRowKind.Obligation && row.BlockId is { } occurrenceId)
        {
            _owner.SetOccurrenceDone(occurrenceId, row.Date, done: false);
        }
        else if (row.Kind == DailyRowKind.Session && row.BlockId is { } sessionId)
        {
            // Per-session: this session's outcome only. Its siblings and its Task
            // are untouched.
            _owner.ClearSessionOutcome(sessionId);
        }
        else if (row.Kind == DailyRowKind.Task
            && row.TaskId is { } taskId
            && _calendar.ReopenTask(taskId))
        {
            // Reopening a Task still clears every Done one-off session of it — the
            // inverse of completing the Task, which resolves them all together.
            _owner.NotifyTasksMutated();
        }
    }

    internal void ToggleOccurrenceDone(DailyRowViewModel row)
    {
        if (row.BlockId is { } id)
        {
            _owner.SetOccurrenceDone(id, row.Date, !row.IsDone);
        }
    }

    internal void Unschedule(DailyRowViewModel row)
    {
        if (row.BlockId is { } id)
        {
            _owner.UnscheduleBlock(id);
        }
    }

    /// <summary>Marks the source row "Editing" while its task's editor is open (frame 3a).</summary>
    internal void SetEditingTask(TaskId? taskId)
    {
        foreach (var row in ScheduledRows.Concat(UnscheduledRows).Concat(CompletedRows))
        {
            row.IsEditing = taskId is not null && row.TaskId == taskId;
        }
    }

    internal void EditRow(DailyRowViewModel row)
    {
        // A list row is task-scoped even when it shows a session (spec, Approach).
        if (row.TaskId is { } taskId)
        {
            _owner.OpenTaskEditorForTask(taskId);
        }
    }

    internal void Rerank(DailyRowViewModel row)
    {
        if (row.CanRerank && row.TaskId is { } taskId)
        {
            _owner.RequestRerank(taskId);
        }
    }

    internal void RecordOutcome(DailyRowViewModel row, BlockOutcome outcome, TimeSpan? remaining)
    {
        if (row.BlockId is { } id)
        {
            _owner.RecordOutcome(id, outcome, remaining);
        }
    }

    internal void ApproveProposal(DailyRowViewModel row)
    {
        if (row.BlockId is { } id)
        {
            _owner.ApproveProposalBlock(id);
        }
    }

    internal void RemoveProposal(DailyRowViewModel row)
    {
        if (row.BlockId is { } id)
        {
            _owner.RemoveProposalBlock(id);
        }
    }

    // ---- Manual scheduling ----

    /// <summary>
    /// Applies a schedule form: new block for a task row, move+resize for a block row.
    /// Success refreshes once and asks the view to focus the scheduled row; a domain
    /// error keeps the task where it was and surfaces inline.
    /// </summary>
    internal bool ConfirmSchedule(DailyRowViewModel row, ScheduleFlyoutViewModel editor)
    {
        if (row.TaskId is not { } taskId)
        {
            return false;
        }

        if (editor.Date is not { } date || editor.Start is not { } start)
        {
            editor.Error = "Pick a date and start time.";
            return false;
        }

        var day = DateOnly.FromDateTime(date.Date);
        var startTime = TimeOnly.FromTimeSpan(start);
        var duration = TimeSpan.FromMinutes((double)Math.Max(5, editor.DurationMinutes));
        try
        {
            if (row.Kind == DailyRowKind.Session && row.BlockId is { } blockId)
            {
                // One operation: date, start, and end move together or not at all.
                _calendar.RescheduleSession(blockId, day, startTime, duration);
            }
            else
            {
                _calendar.ScheduleTask(taskId, day, startTime, duration);
            }
        }
        catch (DomainException exception)
        {
            editor.Error = exception.Message;
            return false;
        }

        _owner.NotifyTasksMutated();
        RowFocusRequested?.Invoke(taskId);
        return true;
    }

    /// <summary>Overlap and constraint warnings for the schedule form. Informative only.</summary>
    internal string? DescribeScheduleWarnings(
        DailyRowViewModel row, DateOnly date, TimeOnly start, TimeSpan duration)
    {
        var end = ClampedEnd(start, duration);
        var warnings = new List<string>(2);

        var overlap = _calendar.GetOccurrences(date, date)
            .FirstOrDefault(o => o.Block.Id != row.BlockId && o.StartTime < end && start < o.EndTime);
        if (overlap is not null)
        {
            var title = overlap.Block.Title
                ?? (overlap.Block.TaskId is { } id ? _tasks.GetById(id)?.Title : null)
                ?? "another block";
            warnings.Add($"Overlaps {title} ({DailyRowViewModel.FormatRange(overlap.StartTime, overlap.EndTime)}).");
        }

        if (row.Task?.Constraints is { } constraints
            && ((constraints.NotBefore is { } notBefore && date < notBefore)
                || (constraints.EarliestTime is { } earliest && start < earliest)
                || (constraints.LatestTime is { } latest && end > latest)))
        {
            warnings.Add("Outside this task's scheduling constraints.");
        }

        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    /// <summary>Blocks never cross midnight (matches the calendar service's clamp).</summary>
    private static TimeOnly ClampedEnd(TimeOnly start, TimeSpan duration)
    {
        var minutesToMidnight = (24 * 60) - (int)start.ToTimeSpan().TotalMinutes - 1;
        var minutes = Math.Clamp((int)duration.TotalMinutes, Math.Min(5, minutesToMidnight), minutesToMidnight);
        return start.AddMinutes(minutes);
    }

    // ---- Add task: both affordances open the one canonical Task editor ----

    /// <summary>
    /// New scheduled task for the visible date, prefilled with the sensible default
    /// slot (next quarter hour today, 9:00 elsewhere; 30 minutes). Saving persists
    /// the task and its session in one atomic operation.
    /// </summary>
    [RelayCommand]
    private void AddScheduledTask()
    {
        var start = ScheduleFlyoutViewModel.DefaultStartFor(_date, _clock);
        var endMinutes = Math.Min(
            start.ToTimeSpan().TotalMinutes + CalendarService.DefaultTaskBlockDuration.TotalMinutes,
            (24 * 60.0) - 1);
        _owner.OpenNewTaskEditorAt(
            _date, start, TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(endMinutes)));
    }

    /// <summary>New unscheduled task; the visible date prefills if scheduling is enabled.</summary>
    [RelayCommand]
    private void AddUnscheduledTask() => _owner.OpenNewUnscheduledTaskEditor(_date);
}

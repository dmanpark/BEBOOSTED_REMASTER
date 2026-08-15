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
    private readonly TaskService _taskService;
    private readonly InboxQueryService _inboxQuery;
    private readonly PrioritySortService _prioritySort;
    private readonly AiService _ai;
    private readonly CalendarService _calendar;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IClock _clock;

    private DateOnly _date;
    private List<ProjectChoiceViewModel> _projectChoices = [ProjectChoiceViewModel.None];
    private Dictionary<ProjectId, string> _projectNames = [];

    internal DailyListViewModel(
        CalendarViewModel owner,
        TaskService taskService,
        InboxQueryService inboxQuery,
        PrioritySortService prioritySort,
        AiService ai,
        CalendarService calendar,
        ITaskRepository tasks,
        IProjectRepository projects,
        IClock clock)
    {
        _owner = owner;
        _taskService = taskService;
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
            CancelAddUnscheduled();
            CancelAddScheduled();
            ScheduledAddNotice = null;
        }

        var projects = _projects.GetAll();
        _projectNames = projects.ToDictionary(p => p.Id, p => p.Name);
        _projectChoices = [ProjectChoiceViewModel.None];
        _projectChoices.AddRange(projects.Select(p => new ProjectChoiceViewModel(p.Id, p.Name)));

        var ranks = _prioritySort.GetRankLookup(PlanningPeriod.ForToday(date));
        var today = _clock.Today;
        var now = TimeOnly.FromDateTime(_clock.Now.LocalDateTime);

        var scheduled = new List<DailyRowViewModel>();
        var completed = new List<DailyRowViewModel>();
        var doneTaskIds = new HashSet<TaskId>();
        var openOccurrenceCount = 0;
        var doneOccurrenceCount = 0;
        var openTaskIds = new HashSet<TaskId>();

        foreach (var occurrence in occurrences.Where(o => o.Date == date))
        {
            var block = occurrence.Block;
            var task = block.TaskId is { } taskId ? _tasks.GetById(taskId) : null;
            var row = BuildOccurrenceRow(occurrence, task, ranks, conflicts, today, now);
            if (row.IsDone)
            {
                completed.Add(row);
                if (block.Kind == BlockKind.FixedCommitment)
                {
                    doneOccurrenceCount++;
                }
                else if (block.TaskId is { } doneId)
                {
                    doneTaskIds.Add(doneId);
                }
            }
            else
            {
                scheduled.Add(row);
                if (block.Kind == BlockKind.FixedCommitment)
                {
                    if (!block.IsExternal)
                    {
                        openOccurrenceCount++;
                    }
                }
                else if (block.TaskId is { } openId)
                {
                    openTaskIds.Add(openId);
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
        foreach (var task in _inboxQuery.GetInboxTasks())
        {
            if (proposalTaskIds.Contains(task.Id))
            {
                continue; // represented by an active proposal — never show twice
            }

            unscheduled.Add(BuildTaskRow(task, ranks, isDone: false));
            openTaskIds.Add(task.Id);
        }

        foreach (var task in _tasks.GetAll())
        {
            if (task.IsCompleted
                && task.CompletedAt is { } at
                && DateOnly.FromDateTime(at.LocalDateTime) == date
                && !doneTaskIds.Contains(task.Id))
            {
                completed.Add(BuildTaskRow(task, ranks, isDone: true));
                doneTaskIds.Add(task.Id);
            }
        }

        openTaskIds.ExceptWith(doneTaskIds);
        var done = doneOccurrenceCount + doneTaskIds.Count;
        var total = done + openOccurrenceCount + openTaskIds.Count;
        ProgressText = $"{done} of {total} complete";

        Replace(ScheduledRows, SortScheduled(scheduled));
        Replace(UnscheduledRows, SortUnscheduled(unscheduled));
        Replace(CompletedRows, SortCompleted(completed));

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
        var projectName = block.Kind == BlockKind.FixedCommitment
            ? ProjectNameFor(block.ProjectId)
            : ProjectNameFor(task?.ProjectId);
        var isDone = block.Outcome == BlockOutcome.Done || occurrence.IsCompleted;
        var elapsed = occurrence.Date < today || (occurrence.Date == today && block.EndTime <= now);
        var needsOutcome = block.Kind == BlockKind.TaskBlock && block.Outcome == BlockOutcome.None && elapsed;

        var row = DailyRowViewModel.ForOccurrence(
            this,
            occurrence,
            title,
            projectName,
            task is null ? null : ranks.GetValueOrDefault(task.Id),
            task,
            conflicts.Contains(block.Id),
            isDone,
            needsOutcome);

        if (task is not null && block.Kind == BlockKind.TaskBlock)
        {
            row.TaskRow = CreateTaskRow(task);
            if (!isDone && !block.IsExternal)
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
            task, _taskService, _clock,
            onRemoved: _ => _owner.NotifyTasksMutated(),
            _projectChoices,
            ProjectNameFor(task.ProjectId),
            onEdited: _ => _owner.NotifyTasksMutated());

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

    /// <summary>Fixed obligations by start; ranked flex by tier and ordinal; unranked flex by start.</summary>
    private static IEnumerable<DailyRowViewModel> SortScheduled(List<DailyRowViewModel> rows)
        => rows
            .OrderBy(ScheduledGroup)
            .ThenBy(r => ScheduledGroup(r) == 1 ? (int)r.Rank!.Tier : 0)
            .ThenBy(r => ScheduledGroup(r) == 1 ? r.Rank!.Rank : 0)
            .ThenBy(r => ScheduledGroup(r) == 1 ? TimeOnly.MinValue : r.StartTime ?? TimeOnly.MinValue)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.BlockId?.ToString() ?? string.Empty);

    private static int ScheduledGroup(DailyRowViewModel row)
        => row.Kind == DailyRowKind.FixedCommitment ? 0 : row.Rank is not null ? 1 : 2;

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
                DailyRowKind.FixedCommitment => 0,
                DailyRowKind.TaskBlock => 1,
                _ => 2,
            })
            .ThenBy(r => r.StartTime ?? TimeOnly.MinValue)
            .ThenBy(r => r.CompletedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase);

    // ---- Row mutations (each announces exactly once through the owner) ----

    internal void CompleteTask(DailyRowViewModel row)
    {
        if (row.TaskId is { } id)
        {
            _taskService.Complete(id);
            _owner.NotifyTasksMutated();
        }
    }

    internal void ReopenRow(DailyRowViewModel row)
    {
        if (row.Kind == DailyRowKind.FixedCommitment && row.BlockId is { } blockId)
        {
            _owner.SetCommitmentOccurrenceDone(blockId, row.Date, done: false);
        }
        else if (row.Kind == DailyRowKind.Task && row.TaskId is { } taskId)
        {
            _taskService.Reopen(taskId);
            _owner.NotifyTasksMutated();
        }
    }

    internal void ToggleCommitmentDone(DailyRowViewModel row)
    {
        if (row.BlockId is { } id)
        {
            _owner.SetCommitmentOccurrenceDone(id, row.Date, !row.IsDone);
        }
    }

    internal void Unschedule(DailyRowViewModel row)
    {
        if (row.BlockId is { } id)
        {
            _owner.UnscheduleBlock(id);
        }
    }

    internal void EditCommitment(DailyRowViewModel row)
    {
        if (row.CanEditCommitment && row.BlockId is { } id)
        {
            _owner.OpenCommitmentEditorFor(id, row.Date);
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
            if (row.Kind == DailyRowKind.TaskBlock && row.BlockId is { } blockId)
            {
                _calendar.MoveBlock(blockId, day, startTime);
                _calendar.ResizeBlock(blockId, ClampedEnd(startTime, duration));
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

    // ---- Add task (Unscheduled): inline capture ----

    [ObservableProperty]
    public partial bool IsAddingUnscheduled { get; set; }

    [ObservableProperty]
    public partial string NewUnscheduledTitle { get; set; } = string.Empty;

    [RelayCommand]
    private void BeginAddUnscheduled()
    {
        NewUnscheduledTitle = string.Empty;
        IsAddingUnscheduled = true;
    }

    /// <summary>Enter captures; blank input does nothing (the field stays for typing).</summary>
    [RelayCommand]
    public void ConfirmAddUnscheduled()
    {
        var title = NewUnscheduledTitle.Trim();
        if (title.Length == 0)
        {
            return;
        }

        _taskService.Capture(title);
        NewUnscheduledTitle = string.Empty;
        IsAddingUnscheduled = false;
        _owner.NotifyTasksMutated();
    }

    [RelayCommand]
    public void CancelAddUnscheduled()
    {
        NewUnscheduledTitle = string.Empty;
        IsAddingUnscheduled = false;
    }

    // ---- Add task (Scheduled): compact create-and-schedule editor ----

    [ObservableProperty]
    public partial bool IsAddingScheduled { get; set; }

    [ObservableProperty]
    public partial string NewScheduledTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TimeSpan? NewScheduledStart { get; set; }

    [ObservableProperty]
    public partial decimal NewScheduledDurationMinutes { get; set; } = 30;

    [ObservableProperty]
    public partial ProjectChoiceViewModel? NewScheduledProject { get; set; }

    public IReadOnlyList<ProjectChoiceViewModel> ProjectChoices => _projectChoices;

    [ObservableProperty]
    public partial string? ScheduledAddError { get; private set; }

    /// <summary>"Added but not scheduled" report — a captured task is never silently lost.</summary>
    [ObservableProperty]
    public partial string? ScheduledAddNotice { get; private set; }

    [RelayCommand]
    private void BeginAddScheduled()
    {
        NewScheduledTitle = string.Empty;
        NewScheduledStart = ScheduleFlyoutViewModel.DefaultStartFor(_date, _clock).ToTimeSpan();
        NewScheduledDurationMinutes = 30;
        NewScheduledProject = _projectChoices[0];
        ScheduledAddError = null;
        ScheduledAddNotice = null;
        OnPropertyChanged(nameof(ProjectChoices));
        IsAddingScheduled = true;
    }

    [RelayCommand]
    public void ConfirmAddScheduled()
    {
        var title = NewScheduledTitle.Trim();
        if (title.Length == 0)
        {
            ScheduledAddError = "A task needs a title.";
            return;
        }

        if (NewScheduledStart is not { } start)
        {
            ScheduledAddError = "Pick a start time.";
            return;
        }

        var duration = TimeSpan.FromMinutes((double)Math.Max(5, NewScheduledDurationMinutes));
        var task = _taskService.Capture(title, duration, null, NewScheduledProject?.Id);
        try
        {
            _calendar.ScheduleTask(task.Id, _date, TimeOnly.FromTimeSpan(start), duration);
            ScheduledAddNotice = null;
        }
        catch (DomainException exception)
        {
            ScheduledAddNotice = $"Added “{title}” to Unscheduled — {exception.Message}";
        }

        ScheduledAddError = null;
        IsAddingScheduled = false;
        _owner.NotifyTasksMutated();
    }

    [RelayCommand]
    public void CancelAddScheduled()
    {
        ScheduledAddError = null;
        IsAddingScheduled = false;
    }
}

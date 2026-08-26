using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Prioritization;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// The whole-task editor's core: every session listed with positions, task-field
/// saves that never touch a session's date, time, or recurrence, completion
/// gating with the exact spec sentences, truthful Cancel, and the push/return
/// leg into the session editor.
/// </summary>
public sealed class WholeTaskEditorViewModelTests
{
    private static readonly DateOnly Date = TestShell.DesignDate;

    private sealed record Context(
        CalendarViewModel Calendar,
        InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks,
        InMemoryOccurrenceCompletionRepository Completions,
        FakeClock Clock,
        CalendarService Service);

    /// <summary>A delegating task repository whose chosen writes throw SqliteException.</summary>
    private sealed class ThrowingTaskRepository(InMemoryTaskRepository inner) : ITaskRepository
    {
        public InMemoryTaskRepository Inner => inner;

        public bool ThrowOnUpdate { get; set; }

        public bool ThrowOnAdd { get; set; }

        public void Add(TaskItem task)
        {
            if (ThrowOnAdd)
            {
                throw new SqliteException("database is locked", 5);
            }

            inner.Add(task);
        }

        public void Update(TaskItem task)
        {
            if (ThrowOnUpdate)
            {
                throw new SqliteException("database is locked", 5);
            }

            inner.Update(task);
        }

        public void Delete(TaskId id) => inner.Delete(id);

        public TaskItem? GetById(TaskId id) => inner.GetById(id);

        public IReadOnlyList<TaskItem> GetAll() => inner.GetAll();

        public IReadOnlyList<TaskItem> GetOpen() => inner.GetOpen();
    }

    /// <summary>A delegating block repository whose chosen writes throw SqliteException.</summary>
    private sealed class ThrowingBlockRepository(InMemoryCalendarBlockRepository inner) : ICalendarBlockRepository
    {
        public InMemoryCalendarBlockRepository Inner => inner;

        public bool ThrowOnAdd { get; set; }

        public bool ThrowOnDelete { get; set; }

        public void Add(CalendarBlock block)
        {
            if (ThrowOnAdd)
            {
                throw new SqliteException("database is locked", 5);
            }

            inner.Add(block);
        }

        public void Update(CalendarBlock block) => inner.Update(block);

        public void Delete(CalendarBlockId id)
        {
            if (ThrowOnDelete)
            {
                throw new SqliteException("database is locked", 5);
            }

            inner.Delete(id);
        }

        public CalendarBlock? GetById(CalendarBlockId id) => inner.GetById(id);

        public IReadOnlyList<CalendarBlock> GetAll() => inner.GetAll();

        public IReadOnlyList<CalendarBlock> GetCandidatesBetween(DateOnly from, DateOnly to)
            => inner.GetCandidatesBetween(from, to);

        public IReadOnlyList<CalendarBlock> GetForTask(TaskId taskId) => inner.GetForTask(taskId);

        public IReadOnlyList<CalendarBlock> GetElapsedWithoutOutcome(DateOnly today, TimeOnly now)
            => inner.GetElapsedWithoutOutcome(today, now);

        public IReadOnlySet<TaskId> GetTaskIdsWithPendingBlocks() => inner.GetTaskIdsWithPendingBlocks();
    }

    private static Context Create(
        ThrowingTaskRepository? throwingTasks = null, ThrowingBlockRepository? throwingBlocks = null)
    {
        var clock = new FakeClock(Date);
        var tasks = throwingTasks?.Inner ?? new InMemoryTaskRepository();
        ITaskRepository taskPort = throwingTasks is null ? tasks : throwingTasks;
        var blocks = throwingBlocks?.Inner ?? new InMemoryCalendarBlockRepository();
        ICalendarBlockRepository blockPort = throwingBlocks is null ? blocks : throwingBlocks;
        blocks.Tasks = tasks;
        var completions = new InMemoryOccurrenceCompletionRepository();
        var proposals = new InMemoryPlanningProposalRepository();
        var mutations = new InMemoryCalendarMutations(blockPort, completions, taskPort, proposals);
        var service = new CalendarService(blockPort, completions, mutations, taskPort, clock);
        var ranks = new InMemoryPrioritizationRepository();
        var planning = new PlanningService(
            proposals, new InboxQueryService(tasks, blocks), ranks, service, mutations, clock);
        var calendar = TestShell.CreateCalendarViewModel(
            new InMemorySettingsStore(), clock, tasks, blocks, new InMemoryProjectRepository(),
            service, planning, ranks);
        return new Context(calendar, tasks, blocks, completions, clock, service);
    }

    private static TaskItem AddTask(Context context, string title)
    {
        var task = TaskItem.Create(title, context.Clock.Now);
        context.Tasks.Add(task);
        return task;
    }

    private static CalendarBlock AddSession(
        Context context, TaskItem task, DateOnly date, TimeOnly start, TimeOnly end,
        RecurrenceRule? recurrence = null)
    {
        var session = CalendarBlock.CreateTaskSession(
            task.Id, date, start, end, context.Clock.Now, recurrence);
        context.Blocks.Add(session);
        return session;
    }

    private static WholeTaskEditorViewModel Open(Context context, TaskItem task)
    {
        var editor = context.Calendar.OpenWholeTaskEditor(task.Id);
        Assert.NotNull(editor);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        return editor;
    }

    [Fact]
    public void OpenWholeTaskEditor_ListsEverySession_Ordered_WithPositions()
    {
        var context = Create();
        var task = AddTask(context, "Practice DECA role-play");
        AddSession(context, task, new DateOnly(2026, 8, 16), new TimeOnly(9, 0), new TimeOnly(10, 30));
        AddSession(context, task, new DateOnly(2026, 8, 11), new TimeOnly(15, 30), new TimeOnly(17, 0));
        AddSession(context, task, new DateOnly(2026, 8, 15), new TimeOnly(16, 0), new TimeOnly(17, 0));

        var editor = Open(context, task);

        Assert.Equal("WHOLE TASK", editor.ScopeLabel);
        Assert.Equal(
            new[] { "SESSION 1 OF 3", "SESSION 2 OF 3", "SESSION 3 OF 3" },
            editor.Sessions.Select(r => r.Data.PositionText!).ToArray());
        Assert.Equal("3 sessions · 4 h", editor.ScheduleSummary);
        Assert.True(editor.ShowUnscheduleAll);
        Assert.False(editor.ShowEmptyState);
        Assert.Equal("Save task", editor.SaveButtonText);
    }

    [Fact]
    public void SaveTask_PersistsFields_AnnouncesOnce_ClosesAndNeverTouchesSchedules()
    {
        var context = Create();
        var task = AddTask(context, "Draft essay");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Title = "Draft essay v2";
        editor.Deadline = new DateTimeOffset(new DateOnly(2026, 8, 30).ToDateTime(TimeOnly.MinValue));
        editor.DurationMinutes = 90;
        editor.SaveCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.Null(context.Calendar.ActiveTaskEditor);
        var saved = context.Tasks.GetById(task.Id)!;
        Assert.Equal("Draft essay v2", saved.Title);
        Assert.Equal(new DateOnly(2026, 8, 30), saved.Deadline);
        Assert.Equal(TimeSpan.FromMinutes(90), saved.EstimatedDuration);
        var untouched = context.Blocks.GetById(session.Id)!;
        Assert.Equal(Date, untouched.Date);
        Assert.Equal(new TimeOnly(9, 0), untouched.StartTime);
        Assert.Null(untouched.Recurrence);
    }

    [Fact]
    public void CompletionControl_AbsentUnderRepeating_WithTheSentences()
    {
        var context = Create();
        var repeatingTask = AddTask(context, "Morning reading");
        AddSession(
            context, repeatingTask, new DateOnly(2026, 8, 4), new TimeOnly(7, 0), new TimeOnly(7, 30),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var repeatingEditor = Open(context, repeatingTask);

        Assert.False(repeatingEditor.ShowCompletion);
        Assert.Equal(
            ["This task repeats — complete each occurrence from the calendar or its session view."],
            repeatingEditor.ScheduleNotes);

        var mixedTask = AddTask(context, "SAT vocabulary drill");
        AddSession(context, mixedTask, new DateOnly(2026, 8, 25), new TimeOnly(16, 0), new TimeOnly(16, 45));
        AddSession(
            context, mixedTask, new DateOnly(2026, 8, 5), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        var mixedEditor = Open(context, mixedTask);

        Assert.False(mixedEditor.ShowCompletion);
        Assert.Equal(
            [
                "This task repeats — complete each occurrence from the calendar or its session view.",
                "One-off sessions can't be marked Done while a repeating schedule remains.",
                "Session numbers count one-off sessions only; the repeating schedule has no number.",
            ],
            mixedEditor.ScheduleNotes);
    }

    [Fact]
    public void AggregateNote_AppearsAtTwoOrMoreOneOffs()
    {
        var context = Create();
        var single = AddTask(context, "Solo");
        AddSession(context, single, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        Assert.Null(Open(context, single).AggregateNote);

        var split = AddTask(context, "Split");
        AddSession(context, split, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, split, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        Assert.Equal(
            "Completing or reopening applies to all 2 sessions.",
            Open(context, split).AggregateNote);
    }

    [Fact]
    public void SaveTask_Completing_UsesAggregateAuthority()
    {
        var context = Create();
        var task = AddTask(context, "Split work");
        var first = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var second = AddSession(context, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        Assert.True(editor.ShowCompletion);
        editor.IsCompleted = true;
        editor.SaveCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.True(context.Tasks.GetById(task.Id)!.IsCompleted);
        Assert.Equal(BlockOutcome.Done, context.Blocks.GetById(first.Id)!.Outcome);
        Assert.Equal(BlockOutcome.Done, context.Blocks.GetById(second.Id)!.Outcome);
    }

    [Fact]
    public void Cancel_NeverPersistsAnything()
    {
        var context = Create();
        var task = AddTask(context, "Keep me");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        var modifiedAt = context.Tasks.GetById(task.Id)!.ModifiedAt;

        editor.Title = "Renamed";
        editor.IsCompleted = true;
        editor.CancelCommand.Execute(null);

        Assert.Null(context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
        var unchanged = context.Tasks.GetById(task.Id)!;
        Assert.Equal("Keep me", unchanged.Title);
        Assert.False(unchanged.IsCompleted);
        Assert.Equal(modifiedAt, unchanged.ModifiedAt);
    }

    [Fact]
    public void SaveTask_ValidationFailure_KeepsTheEditorOpenWithTheError_NoNavigation()
    {
        var context = Create();
        var task = AddTask(context, "Keep me");
        var editor = Open(context, task);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Title = "   ";
        editor.SaveCommand.Execute(null);

        Assert.Equal("A task needs a title.", editor.Error);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
        Assert.Equal("Keep me", context.Tasks.GetById(task.Id)!.Title);
    }

    [Fact]
    public void SaveTask_SqliteFailure_MapsToTheGenericLine_AndStaysOpen()
    {
        var throwing = new ThrowingTaskRepository(new InMemoryTaskRepository());
        var context = Create(throwing);
        var task = AddTask(context, "Keep me");
        var editor = Open(context, task);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        throwing.ThrowOnUpdate = true;

        editor.Title = "Renamed";
        editor.SaveCommand.Execute(null);

        // The editor reports the failure without navigating or announcing.
        // (The nothing-persists guarantee is proven against real SQLite in
        // CalendarMutationAtomicityTests — the in-memory double has no rollback.)
        Assert.Equal("Couldn't save — nothing was changed. Try again.", editor.Error);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
    }

    [Fact]
    public void ExistingUnscheduledTask_ShowsTheEmptyState_WithCompletionAvailable()
    {
        var context = Create();
        var task = AddTask(context, "Never scheduled");

        var editor = Open(context, task);

        Assert.True(editor.ShowEmptyState);
        Assert.Equal(
            "No sessions scheduled. The task stays in your Inbox until you add one.",
            editor.EmptyStateText);
        Assert.Equal("0 sessions", editor.ScheduleSummary);
        Assert.True(editor.ShowCompletion);
        Assert.False(editor.ShowUnscheduleAll);
        Assert.True(editor.CanAddSession);
    }

    [Fact]
    public void CompletedTask_DisablesAddSession_WithTheNote()
    {
        var context = Create();
        var task = AddTask(context, "Finished");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        context.Service.CompleteTask(task.Id);

        var editor = Open(context, task);

        Assert.True(editor.IsCompleted);
        Assert.False(editor.CanAddSession);
        Assert.Equal("Task complete — reopen it to schedule more sessions.", editor.AddSessionBlockedNote);
        Assert.Equal("DONE", editor.Sessions.Single().Data.StatusChip);
    }

    [Fact]
    public void EditRow_WithACleanDraft_PushesTheSessionEditor()
    {
        var context = Create();
        var task = AddTask(context, "Split work");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var second = AddSession(context, task, Date.AddDays(2), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);

        editor.Sessions.Single(r => r.Data.Id == second.Id).EditCommand.Execute(null);

        var pushed = Assert.IsType<SessionEditorViewModel>(context.Calendar.ActiveTaskEditor);
        Assert.Equal(second.Id, pushed.SessionId);
        Assert.Same(editor, context.Calendar.Navigation!.Parent);
        Assert.Equal(second.Id, context.Calendar.Navigation.ReturnRowId);
    }

    [Fact]
    public void EditRow_OnARepeatingRow_ResolvesTheOccurrence_ByTheF15Rule()
    {
        // Occurs today (Tuesday) → today's occurrence.
        var context = Create();
        var occursToday = AddTask(context, "Tuesday series");
        AddSession(
            context, occursToday, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        Open(context, occursToday).Sessions.Single().EditCommand.Execute(null);
        Assert.Equal(
            Date, ((SessionEditorViewModel)context.Calendar.ActiveTaskEditor!).OccurrenceDate);

        // No occurrence today → the most recent elapsed one.
        var elapsed = AddTask(context, "Wednesday series");
        AddSession(
            context, elapsed, new DateOnly(2026, 8, 5), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        Open(context, elapsed).Sessions.Single().EditCommand.Execute(null);
        Assert.Equal(
            new DateOnly(2026, 8, 5),
            ((SessionEditorViewModel)context.Calendar.ActiveTaskEditor!).OccurrenceDate);

        // A series that only starts in the future → its anchor.
        var future = AddTask(context, "Future series");
        AddSession(
            context, future, new DateOnly(2026, 8, 19), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        Open(context, future).Sessions.Single().EditCommand.Execute(null);
        Assert.Equal(
            new DateOnly(2026, 8, 19),
            ((SessionEditorViewModel)context.Calendar.ActiveTaskEditor!).OccurrenceDate);
    }

    // ---- Schedule mutations (Task 8) ----

    [Fact]
    public void RemoveRow_Confirms_ThenRemovesOneBlock_KeepingSiblings_AndRenumbering()
    {
        var context = Create();
        var task = AddTask(context, "Practice DECA role-play");
        AddSession(context, task, new DateOnly(2026, 8, 25), new TimeOnly(15, 30), new TimeOnly(17, 0));
        var second = AddSession(
            context, task, new DateOnly(2026, 8, 26), new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, task, new DateOnly(2026, 8, 30), new TimeOnly(9, 0), new TimeOnly(10, 30));
        var editor = Open(context, task);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Sessions.Single(r => r.Data.Id == second.Id).RemoveCommand.Execute(null);
        Assert.Equal(
            "Remove this session — Wed, Aug 26 · 9:00–10:00 AM? The task keeps its other 2 sessions.",
            editor.Confirmation!.Message);
        Assert.Equal("Remove session", editor.Confirmation.ConfirmLabel);
        editor.ConfirmPromptCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Null(context.Blocks.GetById(second.Id));
        Assert.Equal(
            new[] { "SESSION 1 OF 2", "SESSION 2 OF 2" },
            editor.Sessions.Select(r => r.Data.PositionText!).ToArray());
    }

    [Fact]
    public void RemoveRow_LastSession_UsesTheUnscheduledVariant()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        AddSession(context, task, new DateOnly(2026, 8, 26), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);

        editor.Sessions.Single().RemoveCommand.Execute(null);

        Assert.Equal(
            "Remove this session — Wed, Aug 26 · 9:00–10:00 AM? The task stays, unscheduled.",
            editor.Confirmation!.Message);
    }

    [Fact]
    public void RemoveRow_Repeating_UsesTheScheduleCopy_AndRemovesCompletionHistory()
    {
        var context = Create();
        var task = AddTask(context, "SAT vocabulary drill");
        AddSession(context, task, new DateOnly(2026, 8, 25), new TimeOnly(16, 0), new TimeOnly(16, 45));
        var series = AddSession(
            context, task, new DateOnly(2026, 8, 5), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        context.Service.CompleteOccurrence(series.Id, new DateOnly(2026, 8, 5));
        var editor = Open(context, task);

        editor.Sessions.Single(r => r.Data.IsRepeating).RemoveCommand.Execute(null);
        Assert.Equal(
            "Remove the repeating schedule? Every occurrence and its completion history go with it. The task stays.",
            editor.Confirmation!.Message);
        Assert.Equal("Remove schedule", editor.Confirmation.ConfirmLabel);
        editor.ConfirmPromptCommand.Execute(null);

        Assert.Null(context.Blocks.GetById(series.Id));
        Assert.Empty(context.Completions.GetForBlock(series.Id));
        Assert.Single(editor.Sessions);
    }

    [Fact]
    public void UnscheduleAll_CopyVariants_AndFullRemoval()
    {
        // One-offs only.
        var context = Create();
        var oneOffs = AddTask(context, "Split");
        AddSession(context, oneOffs, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, oneOffs, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var oneOffEditor = Open(context, oneOffs);
        oneOffEditor.RequestUnscheduleAllCommand.Execute(null);
        Assert.Equal(
            "Remove all 2 sessions? The task itself stays.", oneOffEditor.Confirmation!.Message);
        Assert.Equal("Remove all", oneOffEditor.Confirmation.ConfirmLabel);

        // Repeating only (Unschedule all needs two rows, so R is always >= 2 here).
        var repeatingOnly = AddTask(context, "Two series");
        AddSession(
            context, repeatingOnly, new DateOnly(2026, 8, 4), new TimeOnly(7, 0), new TimeOnly(7, 30),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        AddSession(
            context, repeatingOnly, new DateOnly(2026, 8, 5), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        var repeatingEditor = Open(context, repeatingOnly);
        repeatingEditor.RequestUnscheduleAllCommand.Execute(null);
        Assert.Equal(
            "Remove 2 repeating schedules? Their completion history goes with them. The task stays.",
            repeatingEditor.Confirmation!.Message);

        // Mixed — and the confirmed removal itself.
        var mixed = AddTask(context, "SAT vocabulary drill");
        AddSession(context, mixed, new DateOnly(2026, 8, 25), new TimeOnly(16, 0), new TimeOnly(16, 45));
        AddSession(context, mixed, new DateOnly(2026, 8, 30), new TimeOnly(10, 0), new TimeOnly(11, 0));
        AddSession(
            context, mixed, new DateOnly(2026, 8, 5), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        var mixedEditor = Open(context, mixed);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        mixedEditor.RequestUnscheduleAllCommand.Execute(null);
        Assert.Equal(
            "Remove 2 one-off sessions and the repeating schedule? The schedule's completion history goes with it. The task stays.",
            mixedEditor.Confirmation!.Message);
        mixedEditor.ConfirmPromptCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.Empty(context.Blocks.GetForTask(mixed.Id));
        Assert.NotNull(context.Tasks.GetById(mixed.Id));
        Assert.Same(mixedEditor, context.Calendar.ActiveTaskEditor);
        Assert.True(mixedEditor.ShowEmptyState);
    }

    [Fact]
    public void RemovingTheFinalRepeatingSchedule_MakesWholeTaskCompletionAvailable()
    {
        var context = Create();
        var task = AddTask(context, "SAT vocabulary drill");
        AddSession(context, task, new DateOnly(2026, 8, 25), new TimeOnly(16, 0), new TimeOnly(16, 45));
        var series = AddSession(
            context, task, new DateOnly(2026, 8, 5), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        var editor = Open(context, task);
        Assert.False(editor.ShowCompletion);
        Assert.NotEmpty(editor.ScheduleNotes);

        editor.Sessions.Single(r => r.Data.Id == series.Id).RemoveCommand.Execute(null);
        editor.ConfirmPromptCommand.Execute(null);

        // Every schedule-derived property recomputes while the editor stays open.
        Assert.True(editor.ShowCompletion);
        Assert.Empty(editor.ScheduleNotes);
        Assert.Equal("1 session · 45 min", editor.ScheduleSummary);
        Assert.False(editor.ShowUnscheduleAll);
    }

    [Fact]
    public void DeleteConfirmation_MatchesEveryScheduleShape_ThenDeletesAndCloses()
    {
        var context = Create();

        var bare = AddTask(context, "Bare");
        var bareEditor = Open(context, bare);
        bareEditor.RequestDeleteCommand.Execute(null);
        Assert.Equal("Delete this task?", bareEditor.Confirmation!.Message);
        Assert.True(bareEditor.Confirmation.IsTaskDeletion);
        Assert.Equal("Delete task", bareEditor.Confirmation.ConfirmLabel);

        var single = AddTask(context, "Single");
        AddSession(context, single, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var singleEditor = Open(context, single);
        singleEditor.RequestDeleteCommand.Execute(null);
        Assert.Equal("Delete this task? Its session goes with it.", singleEditor.Confirmation!.Message);

        var split = AddTask(context, "Split");
        AddSession(context, split, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, split, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var splitEditor = Open(context, split);
        splitEditor.RequestDeleteCommand.Execute(null);
        Assert.Equal("Delete this task? Its 2 sessions go with it.", splitEditor.Confirmation!.Message);

        var repeating = AddTask(context, "Series");
        AddSession(
            context, repeating, new DateOnly(2026, 8, 4), new TimeOnly(7, 0), new TimeOnly(7, 30),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var repeatingEditor = Open(context, repeating);
        repeatingEditor.RequestDeleteCommand.Execute(null);
        Assert.Equal(
            "Delete this task? Its repeating schedule and completed occurrences go with it.",
            repeatingEditor.Confirmation!.Message);

        var mixed = AddTask(context, "Mixed");
        AddSession(context, mixed, new DateOnly(2026, 8, 25), new TimeOnly(16, 0), new TimeOnly(16, 45));
        AddSession(context, mixed, new DateOnly(2026, 8, 30), new TimeOnly(10, 0), new TimeOnly(11, 0));
        AddSession(
            context, mixed, new DateOnly(2026, 8, 5), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        var mixedEditor = Open(context, mixed);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        mixedEditor.RequestDeleteCommand.Execute(null);
        Assert.Equal(
            "Delete this task? Its repeating schedule and 2 one-off sessions go with it.",
            mixedEditor.Confirmation!.Message);
        mixedEditor.ConfirmPromptCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.Null(context.Calendar.ActiveTaskEditor);
        Assert.Null(context.Tasks.GetById(mixed.Id));
        Assert.Empty(context.Blocks.GetForTask(mixed.Id));
    }

    [Fact]
    public void StaleRowRemove_ShowsTheNotice_Refreshes_NoAnnouncement()
    {
        var context = Create();
        var task = AddTask(context, "Split");
        var first = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        context.Blocks.Delete(first.Id);

        editor.Sessions.First(r => r.Data.Id == first.Id).RemoveCommand.Execute(null);
        editor.ConfirmPromptCommand.Execute(null);

        Assert.Equal("That session was already removed — the list has been updated.", editor.ScheduleNotice);
        Assert.Equal(0, announcements);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal("SESSION 1 OF 1", editor.Sessions.Single().Data.PositionText);
    }

    [Fact]
    public void SqliteFailure_OnRowRemove_OnUnscheduleAll_AndOnDeleteTask_MapsToTheGenericLine()
    {
        foreach (var operate in new Action<WholeTaskEditorViewModel>[]
        {
            editor => editor.Sessions.First().RemoveCommand.Execute(null),
            editor => editor.RequestUnscheduleAllCommand.Execute(null),
            editor => editor.RequestDeleteCommand.Execute(null),
        })
        {
            var throwing = new ThrowingBlockRepository(new InMemoryCalendarBlockRepository());
            var context = Create(throwingBlocks: throwing);
            var task = AddTask(context, "Target");
            AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
            AddSession(context, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
            var editor = Open(context, task);
            var announcements = 0;
            context.Calendar.DataChanged += () => announcements++;
            throwing.ThrowOnDelete = true;

            operate(editor);
            editor.ConfirmPromptCommand.Execute(null);

            Assert.Equal("Couldn't save — nothing was changed. Try again.", editor.Error);
            Assert.Same(editor, context.Calendar.ActiveTaskEditor);
            Assert.Equal(0, announcements);
        }
    }

    // ---- Add mode and create mode (Task 8) ----

    [Fact]
    public void AddSession_EditMode_OpensTheNewModeEditor_AndFocusesTheCreatedRow()
    {
        var context = Create();
        var task = AddTask(context, "Split");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        CalendarBlockId? focusedRow = null;
        context.Calendar.EditorRowFocusRequested += id => focusedRow = id;

        editor.AddSessionCommand.Execute(null);
        var added = Assert.IsType<SessionEditorViewModel>(context.Calendar.ActiveTaskEditor);
        Assert.Equal(SessionEditorMode.New, added.Mode);
        Assert.Equal("NEW SESSION", added.ScopeLabel);
        Assert.Equal("Add session", added.SaveButtonText);
        Assert.Null(added.RemoveButtonText);
        Assert.False(added.ShowEditWholeTask);
        Assert.Same(editor, context.Calendar.Navigation!.Parent);

        added.Schedule.Date = new DateTimeOffset(Date.AddDays(3).ToDateTime(TimeOnly.MinValue));
        added.SaveCommand.Execute(null);

        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.NotNull(focusedRow);
        Assert.Contains(editor.Sessions, r => r.Data.Id == focusedRow);
        Assert.Equal("2 sessions · 2 h", editor.ScheduleSummary);
    }

    [Fact]
    public void AddSession_NewMode_SqliteFailure_KeepsTheNewModeEditorOpen()
    {
        var throwing = new ThrowingBlockRepository(new InMemoryCalendarBlockRepository());
        var context = Create(throwingBlocks: throwing);
        var task = AddTask(context, "Split");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        editor.AddSessionCommand.Execute(null);
        var added = (SessionEditorViewModel)context.Calendar.ActiveTaskEditor!;
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        throwing.ThrowOnAdd = true;

        added.SaveCommand.Execute(null);

        Assert.Equal("Couldn't save — nothing was changed. Try again.", added.Error);
        Assert.Same(added, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
    }

    [Fact]
    public void CreateMode_AddTask_WithRevealedFields_CreatesTaskAndSessionAtomically()
    {
        var context = Create();
        var editor = context.Calendar.OpenNewWholeTaskEditor(
            Date, new TimeOnly(13, 0), new TimeOnly(14, 0), scheduled: true)!;
        Assert.True(editor.IsCreateMode);
        Assert.Equal("Add task", editor.SaveButtonText);
        Assert.True(editor.ShowInlineSchedule);

        editor.Title = "Evening workout";
        editor.SaveCommand.Execute(null);

        Assert.Null(context.Calendar.ActiveTaskEditor);
        var task = context.Tasks.GetAll().Single();
        Assert.Equal("Evening workout", task.Title);
        var session = context.Blocks.GetForTask(task.Id).Single();
        Assert.Equal(new TimeOnly(13, 0), session.StartTime);

        // Without the inline session the task lands alone in the Inbox.
        var bare = context.Calendar.OpenNewWholeTaskEditor(Date, null, null, scheduled: false)!;
        bare.Title = "Just a task";
        bare.SaveCommand.Execute(null);
        var bareTask = context.Tasks.GetAll().Single(t => t.Title == "Just a task");
        Assert.Empty(context.Blocks.GetForTask(bareTask.Id));
    }

    [Fact]
    public void CreateMode_PrefilledEntry_ArrivesRevealed_UnscheduledEntry_ArrivesEmpty()
    {
        var context = Create();

        var prefilled = context.Calendar.OpenNewWholeTaskEditor(
            Date, new TimeOnly(13, 0), new TimeOnly(14, 0), scheduled: true)!;
        Assert.True(prefilled.ShowInlineSchedule);
        Assert.False(prefilled.ShowEmptyState);
        Assert.Equal(new TimeSpan(13, 0, 0), prefilled.InlineSchedule.Start);

        var unscheduled = context.Calendar.OpenNewWholeTaskEditor(Date, null, null, scheduled: false)!;
        Assert.False(unscheduled.ShowInlineSchedule);
        Assert.True(unscheduled.ShowEmptyState);

        // Add session reveals the inline fields; the quiet Remove link clears back.
        unscheduled.AddSessionCommand.Execute(null);
        Assert.True(unscheduled.ShowInlineSchedule);
        Assert.False(unscheduled.ShowEmptyState);
        unscheduled.ClearInlineScheduleCommand.Execute(null);
        Assert.False(unscheduled.ShowInlineSchedule);
        Assert.True(unscheduled.ShowEmptyState);
    }

    // ---- The save-or-discard gate (Task 9) ----

    private static (Context Context, WholeTaskEditorViewModel Editor, CalendarBlock Second) DirtySplit()
    {
        var context = Create();
        var task = AddTask(context, "Split work");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var second = AddSession(context, task, Date.AddDays(2), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        editor.Title = "Split work v2";
        return (context, editor, second);
    }

    [Fact]
    public void Gate_DirtyDraft_PrecedesEveryScheduleOperation_AndDeleteIsExempt()
    {
        var (context, editor, second) = DirtySplit();
        foreach (var operate in new Action[]
        {
            () => editor.Sessions.First(r => r.Data.Id == second.Id).EditCommand.Execute(null),
            () => editor.AddSessionCommand.Execute(null),
            () => editor.Sessions.First().RemoveCommand.Execute(null),
            () => editor.RequestUnscheduleAllCommand.Execute(null),
        })
        {
            operate();
            Assert.NotNull(editor.Gate);
            Assert.Equal("You have unsaved task changes.", editor.Gate!.Title);
            Assert.Equal("Save task and continue", editor.Gate.SaveLabel);
            Assert.Null(editor.Confirmation);
            Assert.Same(editor, context.Calendar.ActiveTaskEditor);
            editor.GateKeepEditingCommand.Execute(null);
        }

        // Delete task is the one exception: its confirmation supersedes the draft.
        editor.RequestDeleteCommand.Execute(null);
        Assert.Null(editor.Gate);
        Assert.NotNull(editor.Confirmation);
        Assert.True(editor.Confirmation!.IsTaskDeletion);
    }

    [Fact]
    public void GateSaveAndContinue_KeepsTheEditorActive_ThenRunsThePending()
    {
        var (context, editor, second) = DirtySplit();
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Sessions.First(r => r.Data.Id == second.Id).EditCommand.Execute(null);
        editor.GateSaveAndContinueCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.Equal("Split work v2", context.Tasks.GetById(editor.TaskId!.Value)!.Title);
        var pushed = Assert.IsType<SessionEditorViewModel>(context.Calendar.ActiveTaskEditor);
        Assert.Equal(second.Id, pushed.SessionId);
        Assert.Same(editor, context.Calendar.Navigation!.Parent);
    }

    [Fact]
    public void GateSaveAndContinue_BeforeARemove_ShowsTheConfirmationAfterTheSave()
    {
        var (context, editor, second) = DirtySplit();
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Sessions.First(r => r.Data.Id == second.Id).RemoveCommand.Execute(null);
        Assert.NotNull(editor.Gate);
        Assert.Null(editor.Confirmation);
        editor.GateSaveAndContinueCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.NotNull(editor.Confirmation);
        Assert.Equal("Remove session", editor.Confirmation!.ConfirmLabel);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.NotNull(context.Blocks.GetById(second.Id));
    }

    [Fact]
    public void GateSave_Failure_DiscardsPending_NoNavigationNoAnnouncement()
    {
        var (context, editor, second) = DirtySplit();
        editor.Title = "   ";
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Sessions.First(r => r.Data.Id == second.Id).RemoveCommand.Execute(null);
        editor.GateSaveAndContinueCommand.Execute(null);

        Assert.Equal("A task needs a title.", editor.Error);
        Assert.Null(editor.Gate);
        Assert.Null(editor.Confirmation);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
    }

    [Fact]
    public void GateDiscardAndContinue_ResetsTheDraft_ThenRunsThePending()
    {
        var (context, editor, second) = DirtySplit();
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Sessions.First(r => r.Data.Id == second.Id).EditCommand.Execute(null);
        editor.GateDiscardAndContinueCommand.Execute(null);

        Assert.Equal("Split work", editor.Title);
        Assert.Equal(0, announcements);
        Assert.IsType<SessionEditorViewModel>(context.Calendar.ActiveTaskEditor);
    }

    [Fact]
    public void GateKeepEditing_DropsThePending()
    {
        var (context, editor, _) = DirtySplit();
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.AddSessionCommand.Execute(null);
        editor.GateKeepEditingCommand.Execute(null);

        Assert.Null(editor.Gate);
        Assert.Equal("Split work v2", editor.Title);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
    }

    [Fact]
    public void GateSaveAndContinue_LeavesACleanSnapshot_NoRegating()
    {
        var (context, editor, second) = DirtySplit();
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Sessions.First(r => r.Data.Id == second.Id).RemoveCommand.Execute(null);
        editor.GateSaveAndContinueCommand.Execute(null);
        Assert.NotNull(editor.Confirmation);
        editor.KeepPromptCommand.Execute(null);

        // No new edit: the next operation opens its confirmation directly, no gate.
        editor.RequestUnscheduleAllCommand.Execute(null);
        Assert.Null(editor.Gate);
        Assert.NotNull(editor.Confirmation);
        Assert.Equal("Remove all", editor.Confirmation!.ConfirmLabel);
        Assert.Equal(1, announcements);
    }

    // ---- Escape depth (Task 9) ----

    [Fact]
    public void Escape_DismissesAnOpenConfirmation_BeforeAnyNavigation()
    {
        var context = Create();
        var task = AddTask(context, "Split");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        editor.RequestUnscheduleAllCommand.Execute(null);

        context.Calendar.EscapeTaskEditor();

        Assert.Null(editor.Confirmation);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
    }

    [Fact]
    public void Escape_DismissesTheGate_KeepingDraftAndEditor()
    {
        var (context, editor, second) = DirtySplit();
        editor.Sessions.First(r => r.Data.Id == second.Id).EditCommand.Execute(null);
        Assert.NotNull(editor.Gate);

        context.Calendar.EscapeTaskEditor();

        Assert.Null(editor.Gate);
        Assert.Equal("Split work v2", editor.Title);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
    }

    [Fact]
    public void Escape_ThenLeavesThePush_ThenCloses()
    {
        var context = Create();
        var task = AddTask(context, "Split");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        editor.Sessions.Single().EditCommand.Execute(null);
        Assert.IsType<SessionEditorViewModel>(context.Calendar.ActiveTaskEditor);

        context.Calendar.EscapeTaskEditor();
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);

        context.Calendar.EscapeTaskEditor();
        Assert.Null(context.Calendar.ActiveTaskEditor);
    }

    [Fact]
    public void CreateMode_SqliteFailure_KeepsTheDraft_AndCreatesNothing()
    {
        var throwing = new ThrowingTaskRepository(new InMemoryTaskRepository());
        var context = Create(throwing);
        var editor = context.Calendar.OpenNewWholeTaskEditor(Date, null, null, scheduled: false)!;
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        throwing.ThrowOnAdd = true;

        editor.Title = "Evening workout";
        editor.SaveCommand.Execute(null);

        Assert.Equal("Couldn't save — nothing was changed. Try again.", editor.Error);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
        Assert.Empty(context.Tasks.GetAll());
    }

    // ---- Obsolete errors never outlive a later success ----

    /// <summary>A failed live op's error clears when the retried op succeeds.</summary>
    [Fact]
    public void ASuccessfulScheduleOperation_ClearsAnEarlierError()
    {
        var throwing = new ThrowingBlockRepository(new InMemoryCalendarBlockRepository());
        var context = Create(throwingBlocks: throwing);
        var task = AddTask(context, "Split");
        var first = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        var row = editor.Sessions.Single(r => r.Data.Id == first.Id);

        throwing.ThrowOnDelete = true;
        row.RemoveCommand.Execute(null);
        editor.ConfirmPromptCommand.Execute(null);
        Assert.Equal("Couldn't save — nothing was changed. Try again.", editor.Error);

        throwing.ThrowOnDelete = false;
        editor.Sessions.Single(r => r.Data.Id == first.Id).RemoveCommand.Execute(null);
        editor.ConfirmPromptCommand.Execute(null);

        Assert.Null(editor.Error);
        Assert.Single(editor.Sessions);
    }

    /// <summary>
    /// A direct create-mode Save with an invalid inline END performs nothing:
    /// no task created, no announcement, no navigation, no generic error.
    /// </summary>
    [Fact]
    public void DirectCreateSave_WithInvalidInlineEnd_DoesNothing()
    {
        var context = Create();
        var editor = context.Calendar.OpenNewWholeTaskEditor(
            Date, new TimeOnly(9, 0), new TimeOnly(10, 0), scheduled: true);
        editor.Title = "Evening workout";
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.InlineSchedule.End = new TimeSpan(8, 0, 0);
        editor.SaveCommand.Execute(null);

        Assert.Same(editor, context.Calendar.ActiveTaskEditor); // no navigation
        Assert.Empty(context.Tasks.GetAll());
        Assert.Empty(context.Blocks.GetAll());
        Assert.Equal(0, announcements);
        Assert.Null(editor.Error); // never replaced by a generic line
        Assert.Equal(
            "A block must end after it starts.", editor.InlineSchedule.EndFieldError);
    }

    // ---- Parent errors never outlive a successful child mutation ----

    private const string StaleParentError = "Couldn't save — nothing was changed. Try again.";

    private sealed record PushContext(
        Context Context, WholeTaskEditorViewModel Parent, CalendarBlock First);

    /// <summary>A parent editor carrying a live-operation error, ready to push.</summary>
    private static PushContext CreateParentWithError()
    {
        var context = Create();
        var task = AddTask(context, "Split");
        var first = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var parent = Open(context, task);
        parent.Error = StaleParentError;
        return new PushContext(context, parent, first);
    }

    [Fact]
    public void ChildSaveSuccess_ClearsTheParentError()
    {
        var fixture = CreateParentWithError();
        fixture.Parent.Sessions.Single(r => r.Data.Id == fixture.First.Id)
            .EditCommand.Execute(null);
        var child = (SessionEditorViewModel)fixture.Context.Calendar.ActiveTaskEditor!;

        child.SaveCommand.Execute(null);

        Assert.Same(fixture.Parent, fixture.Context.Calendar.ActiveTaskEditor);
        Assert.Null(fixture.Parent.Error);
    }

    [Fact]
    public void ChildRemoveSuccess_ClearsTheParentError()
    {
        var fixture = CreateParentWithError();
        fixture.Parent.Sessions.Single(r => r.Data.Id == fixture.First.Id)
            .EditCommand.Execute(null);
        var child = (SessionEditorViewModel)fixture.Context.Calendar.ActiveTaskEditor!;

        child.RequestRemoveCommand.Execute(null);
        child.ConfirmPromptCommand.Execute(null);

        Assert.Same(fixture.Parent, fixture.Context.Calendar.ActiveTaskEditor);
        Assert.Null(fixture.Parent.Error);
    }

    [Fact]
    public void ChildAddSuccess_ClearsTheParentError()
    {
        var fixture = CreateParentWithError();
        fixture.Parent.AddSessionCommand.Execute(null);
        var child = (SessionEditorViewModel)fixture.Context.Calendar.ActiveTaskEditor!;
        child.Schedule.Date = new DateTimeOffset(Date.AddDays(3).ToDateTime(TimeOnly.MinValue));

        child.SaveCommand.Execute(null);

        Assert.Same(fixture.Parent, fixture.Context.Calendar.ActiveTaskEditor);
        Assert.Null(fixture.Parent.Error);
    }

    [Fact]
    public void ChildCancel_KeepsTheParentError()
    {
        var fixture = CreateParentWithError();
        fixture.Parent.Sessions.Single(r => r.Data.Id == fixture.First.Id)
            .EditCommand.Execute(null);
        var child = (SessionEditorViewModel)fixture.Context.Calendar.ActiveTaskEditor!;

        child.CancelCommand.Execute(null);

        Assert.Same(fixture.Parent, fixture.Context.Calendar.ActiveTaskEditor);
        Assert.Equal(StaleParentError, fixture.Parent.Error);
    }

    [Fact]
    public void FailedChildSave_KeepsTheParentError()
    {
        var fixture = CreateParentWithError();
        fixture.Parent.Sessions.Single(r => r.Data.Id == fixture.First.Id)
            .EditCommand.Execute(null);
        var child = (SessionEditorViewModel)fixture.Context.Calendar.ActiveTaskEditor!;
        child.Schedule.Date = null;

        child.SaveCommand.Execute(null); // fails, the child stays put

        Assert.Same(child, fixture.Context.Calendar.ActiveTaskEditor);
        Assert.Equal(StaleParentError, fixture.Parent.Error);
    }

    // ---- Singular confirmation copy (spec amendment 2026-08-21) ----

    [Fact]
    public void MixedDeleteConfirmation_SingularOneOff_ReadsNaturally()
    {
        var context = Create();
        var task = AddTask(context, "Mixed");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(
            context, task, Date.AddDays(-7), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var editor = Open(context, task);

        editor.RequestDeleteCommand.Execute(null);

        Assert.Equal(
            "Delete this task? Its repeating schedule and 1 one-off session go with it.",
            editor.Confirmation!.Message);
    }

    [Fact]
    public void RowRemoveConfirmation_SingularRemainder_ReadsNaturally()
    {
        var context = Create();
        var task = AddTask(context, "Pair");
        var first = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);

        editor.Sessions.Single(r => r.Data.Id == first.Id).RemoveCommand.Execute(null);

        Assert.EndsWith("The task keeps its other 1 session.", editor.Confirmation!.Message);
    }

    // ---- Vanished Schedule rows stay safe at every entry ----

    private const string VanishedRowNotice =
        "That session was already removed — the list has been updated.";

    private sealed record VanishedRowContext(
        Context Context, TaskItem Task, WholeTaskEditorViewModel Editor, SessionRowViewModel Row);

    /// <summary>Two sessions; the second's row is targeted after its block vanishes.</summary>
    private static VanishedRowContext CreateVanishedRowContext()
    {
        var context = Create();
        var task = AddTask(context, "Split");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var gone = AddSession(context, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, task);
        var row = editor.Sessions.Single(r => r.Data.Id == gone.Id);
        return new VanishedRowContext(context, task, editor, row);
    }

    [Fact]
    public void CleanEditOnAVanishedRow_ShowsTheQuietNotice_AndRefreshes()
    {
        var fixture = CreateVanishedRowContext();
        fixture.Context.Blocks.Delete(fixture.Row.Data.Id); // removed elsewhere

        fixture.Row.EditCommand.Execute(null);

        Assert.Same(fixture.Editor, fixture.Context.Calendar.ActiveTaskEditor); // no push
        Assert.Equal(VanishedRowNotice, fixture.Editor.ScheduleNotice);
        Assert.Single(fixture.Editor.Sessions);
    }

    [Fact]
    public void PendingEditResumedAfterGateSave_OnAVanishedRow_ShowsTheQuietNotice()
    {
        var fixture = CreateVanishedRowContext();
        fixture.Editor.Title = "Split renamed";
        fixture.Row.EditCommand.Execute(null); // dirty → gate holds the pending edit
        Assert.NotNull(fixture.Editor.Gate);
        fixture.Context.Blocks.Delete(fixture.Row.Data.Id); // vanishes under the gate

        fixture.Editor.GateSaveAndContinueCommand.Execute(null);

        Assert.Same(fixture.Editor, fixture.Context.Calendar.ActiveTaskEditor);
        Assert.Equal(VanishedRowNotice, fixture.Editor.ScheduleNotice);
        Assert.Single(fixture.Editor.Sessions);
        // The gated save itself still landed.
        Assert.Equal("Split renamed", fixture.Context.Tasks.GetById(fixture.Task.Id)!.Title);
    }

    [Fact]
    public void PendingRemoveResumedAfterGateSave_OnAVanishedRow_NeverThrows()
    {
        var fixture = CreateVanishedRowContext();
        fixture.Editor.Title = "Split renamed";
        fixture.Row.RemoveCommand.Execute(null); // dirty → gate holds the pending remove
        Assert.NotNull(fixture.Editor.Gate);
        fixture.Context.Blocks.Delete(fixture.Row.Data.Id);

        fixture.Editor.GateSaveAndContinueCommand.Execute(null);

        Assert.Null(fixture.Editor.Confirmation); // nothing left to confirm
        Assert.Equal(VanishedRowNotice, fixture.Editor.ScheduleNotice);
        Assert.Single(fixture.Editor.Sessions);
    }
}

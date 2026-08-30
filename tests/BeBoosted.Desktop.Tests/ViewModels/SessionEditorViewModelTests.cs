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
/// The session editor's block-entry modes: one block's schedule and (for a
/// repeating schedule) the opened occurrence's completion — never task fields,
/// never task deletion, and every failure stays inside the editor.
/// </summary>
public sealed class SessionEditorViewModelTests
{
    private static readonly DateOnly Date = TestShell.DesignDate;

    private sealed record Context(
        CalendarViewModel Calendar,
        InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks,
        InMemoryOccurrenceCompletionRepository Completions,
        FakeClock Clock,
        CalendarService Service);

    /// <summary>A delegating block repository whose chosen writes throw SqliteException.</summary>
    private sealed class ThrowingBlockRepository(InMemoryCalendarBlockRepository inner) : ICalendarBlockRepository
    {
        public InMemoryCalendarBlockRepository Inner => inner;

        public bool ThrowOnUpdate { get; set; }

        public bool ThrowOnDelete { get; set; }

        public void Add(CalendarBlock block) => inner.Add(block);

        public void Update(CalendarBlock block)
        {
            if (ThrowOnUpdate)
            {
                throw new SqliteException("database is locked", 5);
            }

            inner.Update(block);
        }

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

    private static Context Create(ThrowingBlockRepository? throwingBlocks = null)
    {
        var clock = new FakeClock(Date);
        var tasks = new InMemoryTaskRepository();
        var blocks = throwingBlocks?.Inner ?? new InMemoryCalendarBlockRepository();
        blocks.Tasks = tasks;
        ICalendarBlockRepository blockPort = throwingBlocks is null ? blocks : throwingBlocks;
        var completions = new InMemoryOccurrenceCompletionRepository();
        var proposals = new InMemoryPlanningProposalRepository();
        var mutations = new InMemoryCalendarMutations(blockPort, completions, tasks, proposals);
        var service = new CalendarService(blockPort, completions, mutations, tasks, clock);
        var ranks = new InMemoryPrioritizationRepository();
        var planning = new PlanningService(
            proposals, new InboxQueryService(tasks, blockPort), ranks, service, mutations, clock);
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

    private static SessionEditorViewModel Open(
        Context context, CalendarBlock session, DateOnly? occurrence = null)
    {
        var editor = context.Calendar.OpenSessionEditorForBlock(session.Id, occurrence ?? session.Date);
        Assert.NotNull(editor);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        return editor;
    }

    [Fact]
    public void BlockEntry_BuildsTheOneOffEditor_WithPositionLabel()
    {
        var context = Create();
        var task = AddTask(context, "Practice DECA role-play");
        AddSession(context, task, new DateOnly(2026, 8, 25), new TimeOnly(15, 30), new TimeOnly(17, 0));
        var second = AddSession(
            context, task, new DateOnly(2026, 8, 26), new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, task, new DateOnly(2026, 8, 30), new TimeOnly(9, 0), new TimeOnly(10, 30));

        var editor = Open(context, second);

        Assert.Equal(SessionEditorMode.OneOff, editor.Mode);
        Assert.Equal("THIS SESSION · 2 OF 3", editor.ScopeLabel);
        Assert.Equal("Practice DECA role-play", editor.TaskTitle);
        Assert.Equal("Save session", editor.SaveButtonText);
        Assert.Equal("Remove this session", editor.RemoveButtonText);
        Assert.True(editor.ShowEditWholeTask);
    }

    [Fact]
    public void BlockEntry_SingleSession_Shows1Of1()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));

        Assert.Equal("THIS SESSION · 1 OF 1", Open(context, only).ScopeLabel);
    }

    [Fact]
    public void OneOffEditor_ExposesNoCompletionControl()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));

        var editor = Open(context, only);

        Assert.False(editor.ShowOccurrenceSection);
        Assert.True(editor.ShowDateField);
    }

    [Fact]
    public void RepeatingEditor_StagesOccurrenceCompletion_AndSavesItAtomically()
    {
        var context = Create();
        var task = AddTask(context, "Stats HW");
        var series = AddSession(
            context, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var editor = Open(context, series, Date);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        Assert.Equal(SessionEditorMode.Repeating, editor.Mode);
        Assert.Equal("REPEATING SCHEDULE", editor.ScopeLabel);
        Assert.Equal("THIS OCCURRENCE · TUE, AUG 11", editor.OccurrenceSectionLabel);
        Assert.Equal("Only Tue, Aug 11. Other occurrences aren't affected.", editor.OccurrenceNote);
        Assert.Equal("Save schedule", editor.SaveButtonText);
        Assert.Equal("Remove schedule", editor.RemoveButtonText);
        Assert.False(editor.ShowDateField);

        editor.IsOccurrenceCompleted = true;
        editor.Schedule.Days.Single(d => d.Day == DayOfWeek.Thursday).IsSelected = true;
        editor.SaveCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.Null(context.Calendar.ActiveTaskEditor);
        Assert.True(context.Service.IsOccurrenceCompleted(series.Id, Date));
        Assert.Equal(
            [DayOfWeek.Tuesday, DayOfWeek.Thursday],
            context.Blocks.GetById(series.Id)!.Recurrence!.DaysOfWeek);
    }

    [Fact]
    public void UntickingRepeats_HidesTheOccurrenceSection_AndDiscardsItsValue()
    {
        var context = Create();
        var task = AddTask(context, "Stats HW");
        var series = AddSession(
            context, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var editor = Open(context, series, Date);

        editor.IsOccurrenceCompleted = true;
        editor.Schedule.RepeatsWeekly = false;

        Assert.False(editor.ShowOccurrenceSection);
        Assert.True(editor.ShowDateField);
        // The date field reveals prefilled with the opened occurrence.
        Assert.Equal(Date, DateOnly.FromDateTime(editor.Schedule.Date!.Value.Date));

        editor.SaveCommand.Execute(null);

        var converted = context.Blocks.GetById(series.Id)!;
        Assert.Null(converted.Recurrence);
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
        Assert.Empty(context.Completions.GetForBlock(series.Id));
    }

    [Fact]
    public void CompletedOneOffMadeRepeating_ReopensTheTask()
    {
        var context = Create();
        var task = AddTask(context, "Vocab review");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        context.Service.CompleteTask(task.Id);
        var editor = Open(context, session);

        editor.Schedule.RepeatsWeekly = true;
        editor.SaveCommand.Execute(null);

        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
        var converted = context.Blocks.GetById(session.Id)!;
        Assert.NotNull(converted.Recurrence);
        Assert.Equal(BlockOutcome.None, converted.Outcome);
    }

    [Fact]
    public void RemoveThisSession_Confirms_ThenKeepsTaskAndSiblings()
    {
        var context = Create();
        var task = AddTask(context, "Practice DECA role-play");
        AddSession(context, task, new DateOnly(2026, 8, 25), new TimeOnly(15, 30), new TimeOnly(17, 0));
        var second = AddSession(
            context, task, new DateOnly(2026, 8, 26), new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, task, new DateOnly(2026, 8, 30), new TimeOnly(9, 0), new TimeOnly(10, 30));
        var editor = Open(context, second);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.RequestRemoveCommand.Execute(null);
        Assert.Equal(
            "Remove this session — Wed, Aug 26 · 9:00–10:00 AM? The task keeps its other 2 sessions.",
            editor.Confirmation!.Message);
        Assert.Equal("Remove session", editor.Confirmation.ConfirmLabel);
        Assert.False(editor.Confirmation.IsTaskDeletion);

        editor.ConfirmPromptCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.Null(context.Calendar.ActiveTaskEditor);
        Assert.Null(context.Blocks.GetById(second.Id));
        Assert.Equal(2, context.Blocks.GetForTask(task.Id).Count);
        Assert.NotNull(context.Tasks.GetById(task.Id));
    }

    [Fact]
    public void RemoveThisSession_LastSession_UsesTheUnscheduledVariant()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(
            context, task, new DateOnly(2026, 8, 26), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, only);

        editor.RequestRemoveCommand.Execute(null);

        Assert.Equal(
            "Remove this session — Wed, Aug 26 · 9:00–10:00 AM? The task stays, unscheduled.",
            editor.Confirmation!.Message);
    }

    [Fact]
    public void RemoveSchedule_Confirms_ThenKeepsTaskAndUnrelatedSessions()
    {
        var context = Create();
        var task = AddTask(context, "SAT vocabulary drill");
        var oneOff = AddSession(
            context, task, new DateOnly(2026, 8, 25), new TimeOnly(16, 0), new TimeOnly(16, 45));
        var series = AddSession(
            context, task, new DateOnly(2026, 8, 5), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));
        context.Service.CompleteOccurrence(series.Id, new DateOnly(2026, 8, 5));
        var editor = Open(context, series, new DateOnly(2026, 8, 5));

        editor.RequestRemoveCommand.Execute(null);
        Assert.Equal(
            "Remove the repeating schedule? Every occurrence and its completion history go with it. The task stays.",
            editor.Confirmation!.Message);
        Assert.Equal("Remove schedule", editor.Confirmation.ConfirmLabel);

        editor.ConfirmPromptCommand.Execute(null);

        Assert.Null(context.Blocks.GetById(series.Id));
        Assert.Empty(context.Completions.GetForBlock(series.Id));
        Assert.NotNull(context.Blocks.GetById(oneOff.Id));
        Assert.NotNull(context.Tasks.GetById(task.Id));
    }

    [Fact]
    public void MixedTask_OneOffEditor_ShowsTheNumberingNote()
    {
        var context = Create();
        var task = AddTask(context, "SAT vocabulary drill");
        var oneOff = AddSession(
            context, task, new DateOnly(2026, 8, 25), new TimeOnly(16, 0), new TimeOnly(16, 45));
        AddSession(
            context, task, new DateOnly(2026, 8, 5), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday));

        var editor = Open(context, oneOff);

        Assert.Equal(
            "Session numbers count one-off sessions only; the repeating schedule has no number.",
            editor.NumberingNote);
    }

    [Fact]
    public void SaveSession_Success_ClosesAndAnnouncesOnce()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, only);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Schedule.End = new TimeSpan(11, 0, 0);
        editor.SaveCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.Null(context.Calendar.ActiveTaskEditor);
        Assert.Equal(new TimeOnly(11, 0), context.Blocks.GetById(only.Id)!.EndTime);
    }

    [Fact]
    public void StaleSave_ShowsTheFixedCopy_AndGoesInert()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, only);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        context.Blocks.Delete(only.Id);

        editor.SaveCommand.Execute(null);

        Assert.True(editor.IsStale);
        Assert.Equal(
            "This session no longer exists — it was removed elsewhere. Cancel to go back.",
            editor.StaleNotice);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
    }

    [Fact]
    public void StaleRemove_BehavesTheSameWay()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, only);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        context.Blocks.Delete(only.Id);

        editor.RequestRemoveCommand.Execute(null);
        editor.ConfirmPromptCommand.Execute(null);

        Assert.True(editor.IsStale);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
    }

    /// <summary>Failure → correction → success must not leave the old error visible.</summary>
    [Fact]
    public void ALaterSuccessfulSave_ClearsTheEarlierError()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, session);

        editor.Schedule.Date = null;
        editor.SaveCommand.Execute(null);
        Assert.Equal("Pick a date, start, and end.", editor.Error);

        editor.Schedule.Date = new DateTimeOffset(Date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        editor.SaveCommand.Execute(null);

        Assert.Null(editor.Error);
        Assert.Null(context.Calendar.ActiveTaskEditor); // saved and closed
    }

    /// <summary>Spec amendment 2026-08-21: one remaining session reads singular.</summary>
    [Fact]
    public void RemoveConfirmation_SingularRemainder_ReadsNaturally()
    {
        var context = Create();
        var task = AddTask(context, "Pair");
        var first = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        AddSession(context, task, Date.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, first);

        editor.RequestRemoveCommand.Execute(null);

        Assert.EndsWith("The task keeps its other 1 session.", editor.Confirmation!.Message);
    }

    /// <summary>
    /// The removal names what will actually be removed: the persisted block —
    /// unsaved draft edits to date or time never leak into the confirmation.
    /// </summary>
    [Fact]
    public void RemoveConfirmation_NamesThePersistedSession_NotTheDraft()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, session);

        editor.Schedule.Date = new DateTimeOffset(Date.AddDays(3).ToDateTime(TimeOnly.MinValue));
        editor.Schedule.Start = new TimeSpan(14, 0, 0);
        editor.Schedule.End = new TimeSpan(15, 0, 0);

        editor.RequestRemoveCommand.Execute(null);

        Assert.Equal(
            "Remove this session — Tue, Aug 11 · 9:00–10:00 AM? The task stays, unscheduled.",
            editor.Confirmation!.Message);
    }

    /// <summary>
    /// Unticking Repeats hides the occurrence section, so its staged value is
    /// discarded at once — rechecking must not resurrect it.
    /// </summary>
    [Fact]
    public void UntickingRepeats_DiscardsTheStagedOccurrenceCompletion_Immediately()
    {
        var context = Create();
        var task = AddTask(context, "Stats HW");
        var session = AddSession(
            context, task, Date.AddDays(-7), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        context.Service.CompleteOccurrence(session.Id, Date);
        var editor = context.Calendar.OpenSessionEditorForBlock(session.Id, Date)!;
        Assert.True(editor.IsOccurrenceCompleted);

        editor.Schedule.RepeatsWeekly = false;
        Assert.False(editor.IsOccurrenceCompleted);

        editor.Schedule.RepeatsWeekly = true;
        Assert.False(editor.IsOccurrenceCompleted);
    }

    /// <summary>
    /// A direct Save with an invalid END performs nothing at all: no repository
    /// write, no announcement, no navigation, and no generic footer error —
    /// the END-pinned message stays authoritative.
    /// </summary>
    [Fact]
    public void DirectSave_WithInvalidEnd_DoesNothing()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, session);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Schedule.End = new TimeSpan(8, 0, 0);
        editor.SaveCommand.Execute(null);

        Assert.Same(editor, context.Calendar.ActiveTaskEditor); // no navigation
        Assert.Equal(new TimeOnly(10, 0), context.Blocks.GetById(session.Id)!.EndTime);
        Assert.Equal(0, announcements);
        Assert.Null(editor.Error); // never replaced by a generic line
        Assert.Equal("A block must end after it starts.", editor.Schedule.EndFieldError);
    }

    /// <summary>
    /// An invalid END gates the gate too: "Save session and continue" must not
    /// run, the gate stays, and the pinned field error is never copied into the
    /// generic footer Error.
    /// </summary>
    [Fact]
    public void GateSaveAndContinue_WithInvalidEnd_DoesNotSaveOrNavigate()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, session);
        editor.Schedule.End = new TimeSpan(8, 0, 0); // dirty and invalid

        editor.EditWholeTaskCommand.Execute(null);
        Assert.NotNull(editor.Gate);
        editor.GateSaveAndContinueCommand.Execute(null);

        Assert.Same(editor, context.Calendar.ActiveTaskEditor); // no promotion
        Assert.NotNull(editor.Gate);                            // the gate holds
        Assert.Equal(new TimeOnly(10, 0), context.Blocks.GetById(session.Id)!.EndTime);
        Assert.Null(editor.Error);                              // no footer copy
        Assert.Equal("A block must end after it starts.", editor.Schedule.EndFieldError);
    }

    [Fact]
    public void GateSaveAndContinue_RepeatingMode_WithInvalidEnd_DoesNotSaveOrNavigate()
    {
        var context = Create();
        var task = AddTask(context, "Stats HW");
        var session = AddSession(
            context, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var editor = context.Calendar.OpenSessionEditorForBlock(session.Id, Date)!;
        editor.Schedule.End = new TimeSpan(15, 0, 0); // dirty and invalid (start 16:00)

        editor.EditWholeTaskCommand.Execute(null);
        Assert.NotNull(editor.Gate);
        editor.GateSaveAndContinueCommand.Execute(null);

        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.NotNull(editor.Gate);
        Assert.Equal(new TimeOnly(17, 0), context.Blocks.GetById(session.Id)!.EndTime);
        Assert.Null(editor.Error);
        Assert.Equal("A block must end after it starts.", editor.Schedule.EndFieldError);
    }

    /// <summary>
    /// The persisted series anchor is never silently rebased: unticking Repeats
    /// may show the opened occurrence as the prospective one-off date, but
    /// rechecking restores the anchor, and saving keeps every valid completion.
    /// </summary>
    [Fact]
    public void RecheckingRepeats_RestoresThePersistedAnchor_AndKeepsHistory()
    {
        var context = Create();
        var task = AddTask(context, "Stats HW");
        var session = AddSession(
            context, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        context.Service.CompleteOccurrence(session.Id, new DateOnly(2026, 8, 11));
        var opened = new DateOnly(2026, 8, 25);
        var editor = context.Calendar.OpenSessionEditorForBlock(session.Id, opened)!;

        // Two full untick → recheck cycles: the anchor round-trips every time.
        for (var cycle = 0; cycle < 2; cycle++)
        {
            editor.Schedule.RepeatsWeekly = false; // prospective one-off = the occurrence
            Assert.Equal(opened, DateOnly.FromDateTime(editor.Schedule.Date!.Value.Date));
            editor.Schedule.RepeatsWeekly = true;  // back to the series: the anchor returns
            Assert.Equal(
                new DateOnly(2026, 8, 4),
                DateOnly.FromDateTime(editor.Schedule.Date!.Value.Date));
        }

        editor.SaveCommand.Execute(null);

        var saved = context.Blocks.GetById(session.Id)!;
        Assert.Equal(new DateOnly(2026, 8, 4), saved.Date); // never rebased
        Assert.NotNull(saved.Recurrence);
        Assert.True(
            context.Service.IsOccurrenceCompleted(session.Id, new DateOnly(2026, 8, 11)),
            "earlier completion history must survive the round trip");
        Assert.False(editor.IsOccurrenceCompleted); // the staged value still never resurrects
    }

    /// <summary>Untick → save converts to a one-off without the discarded completion.</summary>
    [Fact]
    public void UntickingRepeats_ThenSaving_NeverPersistsTheDiscardedCompletion()
    {
        var context = Create();
        var task = AddTask(context, "Stats HW");
        var session = AddSession(
            context, task, Date.AddDays(-7), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var editor = context.Calendar.OpenSessionEditorForBlock(session.Id, Date)!;
        editor.IsOccurrenceCompleted = true; // staged, then abandoned by the untick

        editor.Schedule.RepeatsWeekly = false;
        editor.SaveCommand.Execute(null);

        Assert.Null(context.Calendar.ActiveTaskEditor); // saved and closed
        Assert.Null(context.Blocks.GetById(session.Id)!.Recurrence);
        Assert.Empty(context.Completions.GetForBlock(session.Id));
        Assert.False(context.Tasks.GetById(task.Id)!.IsCompleted);
    }

    /// <summary>
    /// The occurrence-guard validation happens while the block still exists, so
    /// it is an ordinary editable error — never the inert stale state. Stale is
    /// decided by block existence, not by matching exception text.
    /// </summary>
    [Fact]
    public void OccurrenceValidationFailure_IsAnEditableError_NeverStale()
    {
        var context = Create();
        var task = AddTask(context, "Stats HW");
        var session = AddSession(
            context, task, Date.AddDays(-7), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var editor = context.Calendar.OpenSessionEditorForBlock(session.Id, Date)!;
        editor.IsOccurrenceCompleted = true;

        // Swap Tuesday for Wednesday: the checked occurrence would no longer occur.
        foreach (var day in editor.Schedule.Days)
        {
            day.IsSelected = day.Day == DayOfWeek.Wednesday;
        }

        editor.SaveCommand.Execute(null);

        Assert.False(editor.IsStale);
        Assert.Equal(
            "That occurrence no longer exists after this change — untick Completed or keep its weekday.",
            editor.Error);
        // The draft stays editable exactly as typed, ready to correct.
        Assert.Equal(
            DayOfWeek.Wednesday, editor.Schedule.Days.Single(d => d.IsSelected).Day);
        Assert.True(editor.IsOccurrenceCompleted);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
    }

    [Fact]
    public void ValidationError_PinsInsideTheSessionEditor()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, only);

        editor.Schedule.End = new TimeSpan(8, 0, 0);
        editor.SaveCommand.Execute(null);

        // Frame 4n: the message pins to the END field inside the editor; the
        // generic footer line stays empty and nothing persists or navigates.
        Assert.Equal("A block must end after it starts.", editor.Schedule.EndFieldError);
        Assert.Null(editor.Error);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal(new TimeOnly(10, 0), context.Blocks.GetById(only.Id)!.EndTime);
    }

    [Fact]
    public void SqliteFailure_OnSave_MapsToTheGenericLine_NoNavigationNoAnnouncement()
    {
        var throwing = new ThrowingBlockRepository(new InMemoryCalendarBlockRepository());
        var context = Create(throwing);
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, only);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        throwing.ThrowOnUpdate = true;

        editor.Schedule.End = new TimeSpan(11, 0, 0);
        editor.SaveCommand.Execute(null);

        // The editor reports the failure without navigating or announcing.
        // (The nothing-persists guarantee is proven against real SQLite in
        // CalendarMutationAtomicityTests — the in-memory double has no rollback.)
        Assert.Equal("Couldn't save — nothing was changed. Try again.", editor.Error);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
    }

    [Fact]
    public void SqliteFailure_OnRemoveThisSession_AndOnRemoveSchedule_BehaveTheSameWay()
    {
        foreach (var recurrence in new RecurrenceRule?[]
            { null, RecurrenceRule.Weekly(1, DayOfWeek.Tuesday) })
        {
            var throwing = new ThrowingBlockRepository(new InMemoryCalendarBlockRepository());
            var context = Create(throwing);
            var task = AddTask(context, "Target");
            var session = AddSession(
                context, task, new DateOnly(2026, 8, 4), new TimeOnly(9, 0), new TimeOnly(10, 0),
                recurrence);
            var editor = Open(context, session, recurrence is null ? session.Date : Date);
            var announcements = 0;
            context.Calendar.DataChanged += () => announcements++;
            throwing.ThrowOnDelete = true;

            editor.RequestRemoveCommand.Execute(null);
            editor.ConfirmPromptCommand.Execute(null);

            Assert.Equal("Couldn't save — nothing was changed. Try again.", editor.Error);
            Assert.Same(editor, context.Calendar.ActiveTaskEditor);
            Assert.Equal(0, announcements);
            Assert.NotNull(context.Blocks.GetById(session.Id));
        }
    }

    [Fact]
    public void SaveInAPushedSessionEditor_ReturnsToTheRefreshedParent_AndRequestsRowFocus()
    {
        var context = Create();
        var task = AddTask(context, "Split work");
        AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var second = AddSession(
            context, task, Date.AddDays(2), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var parent = context.Calendar.OpenWholeTaskEditor(task.Id)!;
        parent.Sessions.Single(r => r.Data.Id == second.Id).EditCommand.Execute(null);
        var pushed = (SessionEditorViewModel)context.Calendar.ActiveTaskEditor!;
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        CalendarBlockId? focusedRow = null;
        context.Calendar.EditorRowFocusRequested += id => focusedRow = id;

        // Move the second session ahead of the first: the parent's list renumbers.
        pushed.Schedule.Date = new DateTimeOffset(Date.AddDays(-3).ToDateTime(TimeOnly.MinValue));
        pushed.SaveCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.Same(parent, context.Calendar.ActiveTaskEditor);
        Assert.Equal(second.Id, focusedRow);
        Assert.Equal(
            second.Id, parent.Sessions.First(r => r.Data.PositionText == "SESSION 1 OF 2").Data.Id);
        Assert.Null(context.Calendar.Navigation);
    }

    [Fact]
    public void CancelInAPushedSessionEditor_ReturnsWithoutPersisting()
    {
        var context = Create();
        var task = AddTask(context, "Split work");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var parent = context.Calendar.OpenWholeTaskEditor(task.Id)!;
        parent.Sessions.Single().EditCommand.Execute(null);
        var pushed = (SessionEditorViewModel)context.Calendar.ActiveTaskEditor!;
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        pushed.Schedule.Start = new TimeSpan(6, 0, 0);
        pushed.CancelCommand.Execute(null);

        Assert.Same(parent, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
        Assert.Equal(new TimeOnly(9, 0), context.Blocks.GetById(only.Id)!.StartTime);
    }

    [Fact]
    public void BlockEntrySessionEditor_StillClosesToTheInvoker()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, only);

        editor.Schedule.End = new TimeSpan(11, 0, 0);
        editor.SaveCommand.Execute(null);

        Assert.Null(context.Calendar.ActiveTaskEditor);
        Assert.Null(context.Calendar.Navigation);
    }

    // ---- Edit whole task: gated promotion (Task 9) ----

    [Fact]
    public void EditWholeTask_CleanDraft_PromotesImmediately_NoReturnLeg()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, only);

        editor.EditWholeTaskCommand.Execute(null);

        var promoted = Assert.IsType<WholeTaskEditorViewModel>(context.Calendar.ActiveTaskEditor);
        Assert.Equal(task.Id, promoted.TaskId);
        Assert.Null(context.Calendar.Navigation);

        context.Calendar.EscapeTaskEditor();
        Assert.Null(context.Calendar.ActiveTaskEditor);
    }

    [Fact]
    public void EditWholeTask_DirtyDraft_GatesWithTheModeSpecificSaveLabel()
    {
        var context = Create();
        var task = AddTask(context, "Mixed labels");
        var oneOff = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var series = AddSession(
            context, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));

        var oneOffEditor = Open(context, oneOff);
        oneOffEditor.Schedule.Start = new TimeSpan(8, 0, 0);
        oneOffEditor.EditWholeTaskCommand.Execute(null);
        Assert.Equal("You have unsaved session changes.", oneOffEditor.Gate!.Title);
        Assert.Equal("Save session and continue", oneOffEditor.Gate.SaveLabel);
        oneOffEditor.GateKeepEditingCommand.Execute(null);
        Assert.Same(oneOffEditor, context.Calendar.ActiveTaskEditor);

        var repeatingEditor = Open(context, series, Date);
        repeatingEditor.Schedule.Start = new TimeSpan(6, 0, 0);
        repeatingEditor.EditWholeTaskCommand.Execute(null);
        Assert.Equal("Save schedule and continue", repeatingEditor.Gate!.SaveLabel);
    }

    [Fact]
    public void SessionGateSaveAndContinue_Promotes_WithoutTheNormalReturn()
    {
        var context = Create();
        var task = AddTask(context, "Split work");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var parent = context.Calendar.OpenWholeTaskEditor(task.Id)!;
        parent.Sessions.Single().EditCommand.Execute(null);
        var pushed = (SessionEditorViewModel)context.Calendar.ActiveTaskEditor!;
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;
        var rowFocusEvents = 0;
        context.Calendar.EditorRowFocusRequested += _ => rowFocusEvents++;

        pushed.Schedule.End = new TimeSpan(11, 0, 0);
        pushed.EditWholeTaskCommand.Execute(null);
        pushed.GateSaveAndContinueCommand.Execute(null);

        Assert.Equal(1, announcements);
        Assert.Equal(0, rowFocusEvents);
        Assert.False(pushed.IsDirty);
        var promoted = Assert.IsType<WholeTaskEditorViewModel>(context.Calendar.ActiveTaskEditor);
        Assert.NotSame(parent, promoted);
        Assert.Null(context.Calendar.Navigation);
        Assert.Equal(new TimeOnly(11, 0), context.Blocks.GetById(only.Id)!.EndTime);
    }

    [Fact]
    public void SessionGateSave_Failure_StaysInTheSessionEditor_NoPromotion()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, only);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Schedule.End = new TimeSpan(8, 0, 0);
        editor.EditWholeTaskCommand.Execute(null);
        editor.GateSaveAndContinueCommand.Execute(null);

        // Frame 4n: the invalid END pins its own error and holds the gate; the
        // footer never repeats the field message, nothing announces, nothing
        // navigates.
        Assert.Equal("A block must end after it starts.", editor.Schedule.EndFieldError);
        Assert.Null(editor.Error);
        Assert.NotNull(editor.Gate);
        Assert.Same(editor, context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
    }

    [Fact]
    public void SessionSave_AlsoAdvancesItsSnapshot()
    {
        var context = Create();
        var task = AddTask(context, "Solo");
        var only = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, only);

        editor.Schedule.End = new TimeSpan(11, 0, 0);
        Assert.True(editor.IsDirty);
        editor.EditWholeTaskCommand.Execute(null);
        editor.GateSaveAndContinueCommand.Execute(null);

        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Cancel_NeverPersists()
    {
        var context = Create();
        var task = AddTask(context, "Stats HW");
        var series = AddSession(
            context, task, new DateOnly(2026, 8, 4), new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        var editor = Open(context, series, Date);
        var announcements = 0;
        context.Calendar.DataChanged += () => announcements++;

        editor.Schedule.Start = new TimeSpan(6, 0, 0);
        editor.IsOccurrenceCompleted = true;
        editor.CancelCommand.Execute(null);

        Assert.Null(context.Calendar.ActiveTaskEditor);
        Assert.Equal(0, announcements);
        Assert.Equal(new TimeOnly(16, 0), context.Blocks.GetById(series.Id)!.StartTime);
        Assert.False(context.Service.IsOccurrenceCompleted(series.Id, Date));
    }

    // ---- Session title (Task 8) ----

    [Fact]
    public void SavingASessionTitle_PersistsIt_AndBlankClearsIt()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));

        var editor = Open(context, session);
        editor.SessionTitle = "Jane Eyre 1-10";
        editor.SaveCommand.Execute(null);

        Assert.Equal("Jane Eyre 1-10", context.Blocks.GetById(session.Id)!.Title);

        var reopened = Open(context, context.Blocks.GetById(session.Id)!);
        Assert.Equal("Jane Eyre 1-10", reopened.SessionTitle);
        reopened.SessionTitle = "   ";
        reopened.SaveCommand.Execute(null);

        Assert.Null(context.Blocks.GetById(session.Id)!.Title);
    }

    [Fact]
    public void TheTitlePlaceholder_IsTheParentTaskTitle_SoTheFieldReadsAsOptional()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));

        var editor = Open(context, session);

        Assert.Equal("Read Jane Eyre 1-20", editor.TitlePlaceholder);
        Assert.Equal(string.Empty, editor.SessionTitle); // untitled session
    }

    /// <summary>
    /// Trap 2: the title lives outside <see cref="ScheduleFieldsViewModel"/>, so
    /// IsDirty and MarkSaved must be extended by hand. A title-only edit — no
    /// schedule field touched — must register as dirty before save, actually
    /// persist, and clear back to clean after save.
    /// </summary>
    [Fact]
    public void SessionTitleOnlyChange_IsDirty_AndPersists_AndClearsDirtyAfterSave()
    {
        var context = Create();
        var task = AddTask(context, "Read Jane Eyre 1-20");
        var session = AddSession(context, task, Date, new TimeOnly(9, 0), new TimeOnly(10, 0));
        var editor = Open(context, session);
        Assert.False(editor.IsDirty);

        editor.SessionTitle = "Jane Eyre 1-10";

        Assert.True(editor.IsDirty);

        editor.SaveCommand.Execute(null);

        Assert.Equal("Jane Eyre 1-10", context.Blocks.GetById(session.Id)!.Title);
        var reopened = Open(context, context.Blocks.GetById(session.Id)!);
        Assert.False(reopened.IsDirty);
    }
}

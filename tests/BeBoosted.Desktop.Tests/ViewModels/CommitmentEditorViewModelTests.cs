using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// The one reusable commitment editor: New and Edit share the same state, validation,
/// and persistence paths. Opened as a centered modal from the calendar.
/// </summary>
public sealed class CommitmentEditorViewModelTests
{
    private sealed record Context(
        CalendarViewModel Calendar,
        InMemoryCalendarBlockRepository Blocks,
        InMemoryProjectRepository Projects,
        FakeClock Clock,
        CalendarService Service);

    private static Context Create()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var projects = new InMemoryProjectRepository();
        var service = TestShell.CreateCalendarService(blocks, tasks, clock);
        var planning = new PlanningService(
            new InMemoryPlanningProposalRepository(), blocks,
            new InboxQueryService(tasks, blocks), new InMemoryPrioritizationRepository(),
            service, clock);
        var calendar = new CalendarViewModel(
            new AppSettings(new InMemorySettingsStore()), clock, service, tasks, planning, projects);
        return new Context(calendar, blocks, projects, clock, service);
    }

    private static Project AddProject(Context context, string name, string accent = "#5B8DEF")
    {
        var project = Project.Create(name, accent, context.Clock.Now);
        context.Projects.Add(project);
        return project;
    }

    [Fact]
    public void OpenNew_DefaultsCleanState_EvenAfterAPreviousSession()
    {
        var context = Create();
        AddProject(context, "Schoolwork");

        context.Calendar.OpenNewCommitmentEditorCommand.Execute(null);
        var first = context.Calendar.CommitmentEditor!;
        first.Title = "Stale";
        first.RepeatsWeekly = true;
        first.SelectedProject = first.ProjectOptions[^1];
        first.CancelCommand.Execute(null);
        Assert.Null(context.Calendar.CommitmentEditor);

        context.Calendar.OpenNewCommitmentEditorCommand.Execute(null);
        var editor = context.Calendar.CommitmentEditor!;
        Assert.False(editor.IsEditMode);
        Assert.Equal("New commitment", editor.Heading);
        Assert.Equal("Add commitment", editor.SaveButtonText);
        Assert.Equal(string.Empty, editor.Title);
        Assert.False(editor.RepeatsWeekly);
        Assert.All(editor.Days, day => Assert.False(day.IsSelected));
        Assert.Equal("No project", editor.SelectedProject!.Name);
        Assert.Null(editor.SelectedProject.Id);
        Assert.Equal(new TimeSpan(9, 0, 0), editor.Start);
        Assert.Equal(new TimeSpan(10, 0, 0), editor.End);
        Assert.Equal(
            TestShell.DesignDate,
            DateOnly.FromDateTime(editor.Date!.Value.Date));
    }

    [Fact]
    public void ProjectOptions_SortByName_AfterTheNoProjectDefault()
    {
        var context = Create();
        AddProject(context, "Math");
        AddProject(context, "DECA");

        context.Calendar.OpenNewCommitmentEditorCommand.Execute(null);
        var editor = context.Calendar.CommitmentEditor!;

        Assert.Equal(["No project", "DECA", "Math"], editor.ProjectOptions.Select(o => o.Name));
        Assert.True(editor.HasProjects);
    }

    [Fact]
    public void WithoutProjects_KeepsNoProjectSelected_AndOffersHelperCopy()
    {
        var context = Create();
        context.Calendar.OpenNewCommitmentEditorCommand.Execute(null);
        var editor = context.Calendar.CommitmentEditor!;

        Assert.False(editor.HasProjects);
        Assert.Equal("No project", editor.SelectedProject!.Name);
        Assert.Single(editor.ProjectOptions);
    }

    [Fact]
    public void SaveNew_PersistsTheCommitmentWithItsProject_AndCloses()
    {
        var context = Create();
        var project = AddProject(context, "Schoolwork");

        context.Calendar.OpenNewCommitmentEditorCommand.Execute(null);
        var editor = context.Calendar.CommitmentEditor!;
        editor.Title = "Stats HW";
        editor.Start = new TimeSpan(16, 0, 0);
        editor.End = new TimeSpan(17, 0, 0);
        editor.SelectedProject = editor.ProjectOptions.Single(o => o.Name == "Schoolwork");
        editor.SaveCommand.Execute(null);

        Assert.Null(context.Calendar.CommitmentEditor);
        var block = context.Blocks.GetAll().Single();
        Assert.Equal("Stats HW", block.Title);
        Assert.Equal(project.Id, block.ProjectId);
        Assert.Equal(new TimeOnly(16, 0), block.StartTime);
    }

    [Fact]
    public void Save_WithInvalidInput_KeepsTheEditorOpenWithTheError()
    {
        var context = Create();
        context.Calendar.OpenNewCommitmentEditorCommand.Execute(null);
        var editor = context.Calendar.CommitmentEditor!;
        editor.Title = "";
        editor.SaveCommand.Execute(null);

        Assert.NotNull(context.Calendar.CommitmentEditor);
        Assert.NotNull(editor.Error);
        Assert.Empty(context.Blocks.GetAll());

        // Fixing the input clears the failure on the next save.
        editor.Title = "Lunch";
        editor.SaveCommand.Execute(null);
        Assert.Null(context.Calendar.CommitmentEditor);
        Assert.Single(context.Blocks.GetAll());
    }

    [Fact]
    public void OpenForEdit_LoadsThePersistedValues()
    {
        var context = Create();
        var project = AddProject(context, "Math");
        var block = context.Service.CreateFixedCommitment(
            "Stats HW", TestShell.DesignDate.AddDays(1), new TimeOnly(16, 0), new TimeOnly(17, 30),
            projectId: project.Id);
        context.Calendar.Reload();

        context.Calendar.OpenCommitmentEditorFor(block.Id);
        var editor = context.Calendar.CommitmentEditor!;

        Assert.True(editor.IsEditMode);
        Assert.Equal("Edit commitment", editor.Heading);
        Assert.Equal("Save changes", editor.SaveButtonText);
        Assert.Equal("Delete commitment", editor.DeleteButtonText);
        Assert.Equal("Stats HW", editor.Title);
        Assert.Equal(project.Id, editor.SelectedProject!.Id);
        Assert.Equal(new TimeSpan(16, 0, 0), editor.Start);
        Assert.Equal(new TimeSpan(17, 30, 0), editor.End);
        Assert.Equal(
            TestShell.DesignDate.AddDays(1),
            DateOnly.FromDateTime(editor.Date!.Value.Date));
        Assert.False(editor.RepeatsWeekly);
    }

    [Fact]
    public void OpenForEdit_RecurringSeries_UsesSeriesLanguage()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "AP Economics", TestShell.DesignDate, new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Monday, DayOfWeek.Friday));

        context.Calendar.OpenCommitmentEditorFor(block.Id);
        var editor = context.Calendar.CommitmentEditor!;

        Assert.Equal("Edit repeating commitment", editor.Heading);
        Assert.Equal("Delete series", editor.DeleteButtonText);
        Assert.True(editor.RepeatsWeekly);
        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Friday],
            editor.Days.Where(d => d.IsSelected).Select(d => d.Day));
    }

    [Fact]
    public void SaveEdit_PersistsEveryChangedField()
    {
        var context = Create();
        var project = AddProject(context, "Schoolwork");
        var block = context.Service.CreateFixedCommitment(
            "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));

        context.Calendar.OpenCommitmentEditorFor(block.Id);
        var editor = context.Calendar.CommitmentEditor!;
        editor.Title = "Study hall";
        editor.Date = new DateTimeOffset(TestShell.DesignDate.AddDays(2).ToDateTime(TimeOnly.MinValue));
        editor.Start = new TimeSpan(13, 0, 0);
        editor.End = new TimeSpan(14, 0, 0);
        editor.SelectedProject = editor.ProjectOptions.Single(o => o.Name == "Schoolwork");
        editor.RepeatsWeekly = true;
        editor.Days.Single(d => d.Day == DayOfWeek.Thursday).IsSelected = true;
        editor.SaveCommand.Execute(null);

        Assert.Null(context.Calendar.CommitmentEditor);
        var saved = context.Blocks.GetById(block.Id)!;
        Assert.Equal("Study hall", saved.Title);
        Assert.Equal(TestShell.DesignDate.AddDays(2), saved.Date);
        Assert.Equal(new TimeOnly(13, 0), saved.StartTime);
        Assert.Equal(new TimeOnly(14, 0), saved.EndTime);
        Assert.Equal(project.Id, saved.ProjectId);
        Assert.Equal([DayOfWeek.Thursday], saved.Recurrence!.DaysOfWeek);
    }

    [Fact]
    public void Delete_RequiresConfirmation_BeforeRemovingTheCommitment()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));

        context.Calendar.OpenCommitmentEditorFor(block.Id);
        var editor = context.Calendar.CommitmentEditor!;
        editor.RequestDeleteCommand.Execute(null);

        Assert.True(editor.IsConfirmingDelete);
        Assert.NotNull(context.Blocks.GetById(block.Id));
        Assert.NotNull(context.Calendar.CommitmentEditor);

        editor.CancelDeleteCommand.Execute(null);
        Assert.False(editor.IsConfirmingDelete);
        Assert.NotNull(context.Blocks.GetById(block.Id));

        editor.RequestDeleteCommand.Execute(null);
        editor.ConfirmDeleteCommand.Execute(null);
        Assert.Null(context.Blocks.GetById(block.Id));
        Assert.Null(context.Calendar.CommitmentEditor);
    }

    [Fact]
    public void NewMode_StartsIncomplete()
    {
        var context = Create();
        context.Calendar.OpenNewCommitmentEditorCommand.Execute(null);
        var editor = context.Calendar.CommitmentEditor!;

        Assert.False(editor.IsEditMode);
        Assert.False(editor.IsCompleted);

        editor.Title = "Stats HW";
        editor.SaveCommand.Execute(null);
        var block = context.Blocks.GetAll().Single();
        Assert.False(context.Service.IsCommitmentOccurrenceCompleted(block.Id, block.Date));
    }

    [Fact]
    public void EditMode_ShowsCompletion_AndSavesItAtomicallyWithOtherFields()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "Stats HW", TestShell.DesignDate, new TimeOnly(16, 0), new TimeOnly(17, 0));
        context.Service.CompleteCommitmentOccurrence(block.Id, TestShell.DesignDate);

        context.Calendar.OpenCommitmentEditorFor(block.Id);
        var editor = context.Calendar.CommitmentEditor!;
        Assert.True(editor.IsCompleted);

        // Unchecking and renaming in one save applies both.
        editor.IsCompleted = false;
        editor.Title = "Stats homework";
        editor.SaveCommand.Execute(null);
        Assert.Null(context.Calendar.CommitmentEditor);
        Assert.Equal("Stats homework", context.Blocks.GetById(block.Id)!.Title);
        Assert.False(context.Service.IsCommitmentOccurrenceCompleted(block.Id, TestShell.DesignDate));

        // Checking it during an edit that also moves the date completes the new occurrence.
        context.Calendar.OpenCommitmentEditorFor(block.Id);
        var reopened = context.Calendar.CommitmentEditor!;
        Assert.False(reopened.IsCompleted);
        reopened.IsCompleted = true;
        reopened.Date = new DateTimeOffset(TestShell.DesignDate.AddDays(1).ToDateTime(TimeOnly.MinValue));
        reopened.SaveCommand.Execute(null);
        Assert.True(context.Service.IsCommitmentOccurrenceCompleted(
            block.Id, TestShell.DesignDate.AddDays(1)));
    }

    [Fact]
    public void Cancel_NeverPersistsACompletionChange()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "Stats HW", TestShell.DesignDate, new TimeOnly(16, 0), new TimeOnly(17, 0));

        context.Calendar.OpenCommitmentEditorFor(block.Id);
        context.Calendar.CommitmentEditor!.IsCompleted = true;
        context.Calendar.CommitmentEditor.CancelCommand.Execute(null);
        Assert.False(context.Service.IsCommitmentOccurrenceCompleted(block.Id, TestShell.DesignDate));

        context.Service.CompleteCommitmentOccurrence(block.Id, TestShell.DesignDate);
        context.Calendar.OpenCommitmentEditorFor(block.Id);
        context.Calendar.CommitmentEditor!.IsCompleted = false;
        context.Calendar.CloseCommitmentEditor(); // the Escape path
        Assert.True(context.Service.IsCommitmentOccurrenceCompleted(block.Id, TestShell.DesignDate));
    }

    [Fact]
    public void RecurringEdit_TargetsTheOpenedOccurrenceOnly()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "AP Economics", TestShell.DesignDate.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        context.Calendar.Reload();

        // Open from today's (Tuesday's) occurrence and check it off.
        context.Calendar.OpenCommitmentEditorFor(block.Id, TestShell.DesignDate);
        var editor = context.Calendar.CommitmentEditor!;
        Assert.NotNull(editor.CompletionScopeNote); // series edits flag the per-occurrence scope
        editor.IsCompleted = true;
        editor.SaveCommand.Execute(null);

        Assert.True(context.Service.IsCommitmentOccurrenceCompleted(block.Id, TestShell.DesignDate));
        Assert.False(context.Service.IsCommitmentOccurrenceCompleted(
            block.Id, TestShell.DesignDate.AddDays(1)));
    }

    [Fact]
    public void Save_RejectsCompletingAnOccurrenceTheEditRemoves()
    {
        var context = Create();
        var series = context.Service.CreateFixedCommitment(
            "AP Economics", TestShell.DesignDate.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        context.Calendar.Reload();

        // Open Tuesday's occurrence, check Completed, and remove Tuesdays in one edit.
        context.Calendar.OpenCommitmentEditorFor(series.Id, TestShell.DesignDate);
        var editor = context.Calendar.CommitmentEditor!;
        editor.IsCompleted = true;
        editor.Days.Single(d => d.Day == DayOfWeek.Tuesday).IsSelected = false;
        editor.SaveCommand.Execute(null);

        // Never silently ignored: the save is rejected inside the dialog and
        // nothing — recurrence or completion — is persisted.
        Assert.NotNull(context.Calendar.CommitmentEditor);
        Assert.NotNull(editor.Error);
        var saved = context.Blocks.GetById(series.Id)!;
        Assert.Equal(
            [DayOfWeek.Tuesday, DayOfWeek.Wednesday],
            saved.Recurrence!.DaysOfWeek.OrderBy(d => d));
        Assert.False(context.Service.IsCommitmentOccurrenceCompleted(series.Id, TestShell.DesignDate));
    }

    [Fact]
    public void Save_UncheckingWhileRemovingTheWeekday_PurgesTheCompletion()
    {
        var context = Create();
        var series = context.Service.CreateFixedCommitment(
            "AP Economics", TestShell.DesignDate.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        context.Service.CompleteCommitmentOccurrence(series.Id, TestShell.DesignDate);
        context.Calendar.Reload();

        context.Calendar.OpenCommitmentEditorFor(series.Id, TestShell.DesignDate);
        var editor = context.Calendar.CommitmentEditor!;
        Assert.True(editor.IsCompleted);
        editor.IsCompleted = false;
        editor.Days.Single(d => d.Day == DayOfWeek.Tuesday).IsSelected = false;
        editor.SaveCommand.Execute(null);

        Assert.Null(context.Calendar.CommitmentEditor);
        var saved = context.Blocks.GetById(series.Id)!;
        Assert.Equal([DayOfWeek.Wednesday], saved.Recurrence!.DaysOfWeek);
        Assert.False(context.Service.IsCommitmentOccurrenceCompleted(series.Id, TestShell.DesignDate));
    }

    [Fact]
    public void Cancel_ClosesWithoutTouchingTheCommitment()
    {
        var context = Create();
        var block = context.Service.CreateFixedCommitment(
            "Lunch", TestShell.DesignDate, new TimeOnly(12, 0), new TimeOnly(12, 45));
        var modified = block.ModifiedAt;

        context.Calendar.OpenCommitmentEditorFor(block.Id);
        var editor = context.Calendar.CommitmentEditor!;
        editor.Title = "Changed but discarded";
        editor.CancelCommand.Execute(null);

        Assert.Null(context.Calendar.CommitmentEditor);
        var loaded = context.Blocks.GetById(block.Id)!;
        Assert.Equal("Lunch", loaded.Title);
        Assert.Equal(modified, loaded.ModifiedAt);
    }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The Projects surface is a full editor entry point: task rows AND
/// scheduled-session rows are task-scoped and open the whole-task editor
/// (the F-03 rule), and completing from the project detail refreshes every
/// surface through one notification chain.
/// </summary>
public sealed class ProjectEntryPointTests
{
    private sealed record Fixture(
        MainWindow Window,
        ShellViewModel Shell,
        InMemoryTaskRepository Tasks,
        InMemoryCalendarBlockRepository Blocks,
        ProjectId ProjectId,
        TaskId OpenTaskId,
        CalendarBlockId RepeatingSessionId);

    /// <summary>
    /// A "Schoolwork" project (detail open) with an unscheduled open task, a
    /// completed task, and a repeating Tuesday session anchored on the design date.
    /// </summary>
    private static Fixture CreateProjectShell()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        var projects = new InMemoryProjectRepository();
        var schoolwork = Project.Create("Schoolwork", "#5B8DEF", clock.Now);
        projects.Add(schoolwork);

        var open = TaskItem.Create("PIQ2", clock.Now, projectId: schoolwork.Id);
        tasks.Add(open);
        var done = TaskItem.Create("Vocab review", clock.Now, projectId: schoolwork.Id);
        done.Complete(clock.Now);
        tasks.Add(done);
        var repeatingTask = TaskItem.Create("Stats HW", clock.Now, projectId: schoolwork.Id);
        tasks.Add(repeatingTask);
        var session = CalendarBlock.CreateTaskSession(
            repeatingTask.Id, TestShell.DesignDate, new TimeOnly(16, 0), new TimeOnly(17, 0),
            clock.Now, RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        blocks.Add(session);

        var shell = TestShell.Create(tasks: tasks, blocks: blocks, projects: projects);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        shell.NavigateCommand.Execute(AppSection.Projects);
        shell.Projects.OpenProject(schoolwork.Id);
        window.CaptureRenderedFrame();
        window.CaptureRenderedFrame();
        return new Fixture(window, shell, tasks, blocks, schoolwork.Id, open.Id, session.Id);
    }

    private static void ClickByName(MainWindow window, string automationName)
    {
        var button = window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.IsEffectivelyVisible
                && AutomationProperties.GetName(b) == automationName);
        var point = button.TranslatePoint(
            new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();
    }

    [AvaloniaFact]
    public void OpenTaskRow_EditButton_OpensTheCanonicalEditor()
    {
        var fixture = CreateProjectShell();

        ClickByName(fixture.Window, "Edit task PIQ2");

        var editor = Assert.IsType<WholeTaskEditorViewModel>(fixture.Shell.Calendar.ActiveTaskEditor);
        Assert.Equal(fixture.OpenTaskId, editor.TaskId);
        Assert.Equal("PIQ2", editor.Title);
        Assert.True(editor.ShowEmptyState); // unscheduled: no sessions listed

        // The modal renders over the Projects surface.
        var scrim = fixture.Window.GetVisualDescendants()
            .OfType<Border>().First(b => b.Name == "TaskModalScrim");
        Assert.True(scrim.IsEffectivelyVisible);
        fixture.Window.Close();
    }

    [AvaloniaFact]
    public void CompletedTaskRow_EditButton_OpensTheCanonicalEditor()
    {
        var fixture = CreateProjectShell();

        ClickByName(fixture.Window, "Edit task Vocab review");

        var editor = Assert.IsType<WholeTaskEditorViewModel>(fixture.Shell.Calendar.ActiveTaskEditor);
        Assert.Equal("Vocab review", editor.Title);
        Assert.True(editor.IsCompleted);
        fixture.Window.Close();
    }

    /// <summary>A scheduled-session row in a list is still task-scoped (F-03).</summary>
    [AvaloniaFact]
    public void ScheduledSessionRow_EditButton_OpensTheWholeTaskEditor()
    {
        var fixture = CreateProjectShell();

        // The repeating Stats HW session's upcoming occurrence row (today).
        ClickByName(fixture.Window, "Edit session Stats HW");

        var editor = Assert.IsType<WholeTaskEditorViewModel>(fixture.Shell.Calendar.ActiveTaskEditor);
        Assert.Equal("Stats HW", editor.Title);
        Assert.Single(editor.Sessions);

        // Occurrence completion lives in the pushed session editor (F-15 resolves today).
        editor.Sessions.Single().EditCommand.Execute(null);
        fixture.Window.CaptureRenderedFrame();
        var session = Assert.IsType<SessionEditorViewModel>(fixture.Shell.Calendar.ActiveTaskEditor);
        Assert.Equal(TestShell.DesignDate, session.OccurrenceDate);
        Assert.False(session.IsOccurrenceCompleted);
        session.IsOccurrenceCompleted = true;
        session.SaveCommand.Execute(null);
        fixture.Window.CaptureRenderedFrame();
        var detail = fixture.Shell.Projects.Detail!;
        Assert.Contains(
            detail.ScheduledBlocks.Concat(detail.CompletedScheduledBlocks),
            r => r.IsDone && r.Date == TestShell.DesignDate);
        fixture.Window.Close();
    }

    [AvaloniaFact]
    public void CompletedScheduledRow_EditButton_OpensTheEditor()
    {
        var fixture = CreateProjectShell();
        fixture.Shell.Calendar.SetOccurrenceDone(
            fixture.RepeatingSessionId, TestShell.DesignDate, done: true);
        fixture.Window.CaptureRenderedFrame();
        fixture.Window.CaptureRenderedFrame();

        ClickByName(fixture.Window, "Edit session Stats HW");

        var editor = Assert.IsType<WholeTaskEditorViewModel>(fixture.Shell.Calendar.ActiveTaskEditor);
        Assert.Equal("Stats HW", editor.Title);
        fixture.Window.Close();
    }

    /// <summary>
    /// Completing a Task from the project detail announces through one chain:
    /// Calendar, Inbox, the open detail, and (on return) the card counts all agree.
    /// </summary>
    [AvaloniaFact]
    public void CompletingATask_FromProjectDetail_RefreshesEverySurface_Once()
    {
        var fixture = CreateProjectShell();
        var shell = fixture.Shell;
        Assert.Contains(shell.Inbox.Tasks, r => r.Title == "PIQ2");
        var changes = 0;
        shell.Calendar.DataChanged += () => changes++;

        var row = shell.Projects.Detail!.OpenTasks.Single(t => t.Title == "PIQ2");
        row.CompleteCommand.Execute(null);
        fixture.Window.CaptureRenderedFrame();

        Assert.Equal(1, changes); // exactly one announcement
        Assert.True(fixture.Tasks.GetById(fixture.OpenTaskId)!.IsCompleted);
        // The Inbox queue dropped it without a manual reload.
        Assert.DoesNotContain(shell.Inbox.Tasks, r => r.Title == "PIQ2");
        // The open detail moved it to recently completed.
        Assert.Contains(shell.Projects.Detail!.RecentlyCompleted, t => t.Title == "PIQ2");
        // Navigating back to Calendar shows fresh completion state immediately.
        shell.NavigateCommand.Execute(AppSection.Calendar);
        fixture.Window.CaptureRenderedFrame();
        Assert.Contains(shell.Calendar.Daily.CompletedRows, r => r.Title == "PIQ2");
        fixture.Window.Close();
    }

    /// <summary>Reassignment through the modal moves the task between projects at once.</summary>
    [AvaloniaFact]
    public void ReassigningThroughTheModal_MovesTheTaskBetweenProjects()
    {
        var fixture = CreateProjectShell();
        var shell = fixture.Shell;
        shell.Projects.CloseDetailCommand.Execute(null);
        shell.Projects.NewProjectName = "CAPPs";
        Assert.True(shell.Projects.TryCreateProject()); // CAPPs detail now open
        fixture.Window.CaptureRenderedFrame();

        shell.Calendar.OpenTaskEditorForTask(fixture.OpenTaskId);
        var editor = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
        editor.SelectedProject = editor.ProjectOptions.Single(o => o.Name == "CAPPs");
        editor.SaveCommand.Execute(null);

        // Added to the open new project immediately…
        Assert.Contains(shell.Projects.Detail!.OpenTasks, t => t.Title == "PIQ2");
        // …and gone from the old one.
        shell.Projects.CloseDetailCommand.Execute(null);
        shell.Projects.Projects.Single(p => p.Name == "Schoolwork").OpenCommand.Execute(null);
        Assert.DoesNotContain(shell.Projects.Detail!.OpenTasks, t => t.Title == "PIQ2");
        fixture.Window.Close();
    }
}

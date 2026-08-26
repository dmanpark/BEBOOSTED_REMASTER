using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// Targeted review screenshots for the scope-led editors (whole task / session,
/// frames 3a/3b/4a–4p), editable sessions, locked external events, and the
/// centered Projects empty state. Runs only when BEBOOSTED_SCREENSHOT_DIR is set.
/// </summary>
public sealed class FeatureScreenshotCaptureTests
{
    [AvaloniaFact]
    public void CaptureTaskEditorAndProjectScreens()
    {
        var directory = Environment.GetEnvironmentVariable("BEBOOSTED_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(directory), "BEBOOSTED_SCREENSHOT_DIR is not set");
        Directory.CreateDirectory(directory!);

        foreach (var (width, height) in new[] { (1440, 960), (1280, 800) })
        {
            var clock = new FakeClock(TestShell.DesignDate);
            var tasks = new InMemoryTaskRepository();
            var blocks = new InMemoryCalendarBlockRepository();
            TestShell.SeedDesignCalendar(tasks, blocks, clock);
            blocks.Add(CalendarBlock.Rehydrate(
                CalendarBlockId.New(), null, "Imported standup", TestShell.DesignDate,
                new TimeOnly(13, 30), new TimeOnly(14, 0), BlockKind.ExternalEvent, null,
                "google", "evt-1", 0, BlockOutcome.None, null, clock.Now, clock.Now));
            var projects = new InMemoryProjectRepository();
            var schoolwork = Project.Create("Schoolwork", "#5B8DEF", clock.Now);
            projects.Add(schoolwork);
            projects.Add(Project.Create("Math", "#C2803F", clock.Now));

            var shell = TestShell.Create(tasks: tasks, blocks: blocks, projects: projects);
            var window = new MainWindow { DataContext = shell, Width = width, Height = height };
            window.Show();
            window.CaptureRenderedFrame();

            // The Today view's priority-first Daily list (no FIXED/FLEX badges).
            Capture(window, directory!, $"daily-list-{width}x{height}.png");

            // Create mode: the whole-task editor, unscheduled (frame 4a).
            shell.Calendar.OpenNewTaskEditorCommand.Execute(null);
            window.CaptureRenderedFrame();
            Capture(window, directory!, $"whole-task-editor-new-{width}x{height}.png");
            shell.Calendar.CloseActiveEditor();

            // Timeline block states render on the Week surface.
            shell.Calendar.ViewKind = CalendarViewKind.Week;
            window.CaptureRenderedFrame();

            // A project-linked repeating task used across the completion states.
            var statsTask = BeBoosted.Domain.Tasks.TaskItem.Create(
                "Stats HW", clock.Now, projectId: schoolwork.Id);
            tasks.Add(statsTask);
            var stats = CalendarBlock.CreateTaskSession(
                statsTask.Id, TestShell.DesignDate, new TimeOnly(16, 0), new TimeOnly(17, 0),
                clock.Now, BeBoosted.Domain.Scheduling.RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
            blocks.Add(stats);
            var vocabTask = BeBoosted.Domain.Tasks.TaskItem.Create(
                "Vocab review", clock.Now, projectId: schoolwork.Id);
            tasks.Add(vocabTask);
            blocks.Add(CalendarBlock.CreateTaskSession(
                vocabTask.Id, TestShell.DesignDate.AddDays(-3), new TimeOnly(16, 0), new TimeOnly(17, 0),
                clock.Now)); // stays incomplete → overdue

            // A three-one-off task (frame 3a) and a mixed one-off + repeating task (4f).
            var binderTask = BeBoosted.Domain.Tasks.TaskItem.Create(
                "Prepare regional qualifier binder", clock.Now, projectId: schoolwork.Id);
            tasks.Add(binderTask);
            var binderFirst = CalendarBlock.CreateTaskSession(
                binderTask.Id, TestShell.DesignDate, new TimeOnly(19, 0), new TimeOnly(20, 0),
                clock.Now);
            blocks.Add(binderFirst);
            for (var i = 1; i < 3; i++)
            {
                blocks.Add(CalendarBlock.CreateTaskSession(
                    binderTask.Id, TestShell.DesignDate.AddDays(i),
                    new TimeOnly(19, 0), new TimeOnly(20, 0), clock.Now));
            }

            var mixedTask = BeBoosted.Domain.Tasks.TaskItem.Create(
                "Practice DECA role-play", clock.Now);
            tasks.Add(mixedTask);
            blocks.Add(CalendarBlock.CreateTaskSession(
                mixedTask.Id, TestShell.DesignDate.AddDays(1), new TimeOnly(9, 0),
                new TimeOnly(10, 0), clock.Now));
            blocks.Add(CalendarBlock.CreateTaskSession(
                mixedTask.Id, TestShell.DesignDate.AddDays(-4), new TimeOnly(7, 0),
                new TimeOnly(7, 45), clock.Now,
                BeBoosted.Domain.Scheduling.RecurrenceRule.Weekly(1, DayOfWeek.Friday)));
            shell.Calendar.Reload();

            var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
            var scroller = surface.FindControl<ScrollViewer>("Scroller")!;

            // Incomplete repeating session with its per-occurrence completion circle.
            scroller.Offset = new Vector(0, 800);
            Capture(window, directory!, $"calendar-stats-incomplete-{width}x{height}.png");

            // The repeating session editor, scoped to today's occurrence (frame 4g).
            shell.Calendar.OpenTaskEditorForBlock(stats.Id, TestShell.DesignDate);
            window.CaptureRenderedFrame();
            Capture(window, directory!, $"session-editor-repeating-{width}x{height}.png");
            shell.Calendar.CloseActiveEditor();

            // The one-off session editor (frame 3b).
            shell.Calendar.OpenTaskEditorForBlock(binderFirst.Id, TestShell.DesignDate);
            window.CaptureRenderedFrame();
            Capture(window, directory!, $"session-editor-oneoff-{width}x{height}.png");
            shell.Calendar.CloseActiveEditor();

            // Frame 4n + gate: the invalid END pins its error beneath the field
            // and the gate's save-and-continue is disabled.
            shell.Calendar.OpenTaskEditorForBlock(binderFirst.Id, TestShell.DesignDate);
            var invalidEditor = (SessionEditorViewModel)shell.Calendar.ActiveTaskEditor!;
            invalidEditor.Schedule.End = new TimeSpan(8, 0, 0);
            invalidEditor.EditWholeTaskCommand.Execute(null);
            window.CaptureRenderedFrame();
            Capture(window, directory!, $"session-editor-invalid-end-gate-{width}x{height}.png");
            invalidEditor.GateKeepEditingCommand.Execute(null);
            shell.Calendar.CloseActiveEditor();

            // The whole-task editor listing every session (frame 3a).
            shell.Calendar.OpenTaskEditorForTask(binderTask.Id);
            window.CaptureRenderedFrame();
            Capture(window, directory!, $"whole-task-editor-sessions-{width}x{height}.png");
            shell.Calendar.CloseActiveEditor();

            // Mixed one-off + repeating schedule (frame 4f), then the delete
            // confirmation over it (frame 4k).
            shell.Calendar.OpenTaskEditorForTask(mixedTask.Id);
            window.CaptureRenderedFrame();
            Capture(window, directory!, $"whole-task-editor-mixed-{width}x{height}.png");
            var mixedEditor = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
            mixedEditor.RequestDeleteCommand.Execute(null);
            window.CaptureRenderedFrame();
            Capture(window, directory!, $"whole-task-editor-delete-confirm-{width}x{height}.png");
            mixedEditor.KeepPromptCommand.Execute(null);
            shell.Calendar.CloseActiveEditor();

            // Add session: the pushed session editor in New mode (frame 4h).
            shell.Calendar.OpenTaskEditorForTask(binderTask.Id);
            window.CaptureRenderedFrame();
            ((WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!)
                .AddSessionCommand.Execute(null);
            window.CaptureRenderedFrame();
            Capture(window, directory!, $"session-editor-new-{width}x{height}.png");
            shell.Calendar.CloseActiveEditor();

            // Checked off: subdued block, strike-through, lime check.
            shell.Calendar.SetOccurrenceDone(stats.Id, TestShell.DesignDate, done: true);
            scroller.Offset = new Vector(0, 800);
            Capture(window, directory!, $"calendar-stats-done-{width}x{height}.png");

            // Schoolwork project page: overdue ("needs review") and completed states.
            shell.NavigateCommand.Execute(AppSection.Projects);
            shell.Projects.OpenProject(schoolwork.Id);
            Capture(window, directory!, $"project-scheduled-states-{width}x{height}.png");
            shell.NavigateCommand.Execute(AppSection.Calendar);

            // Week view with editable task sessions (and everything else).
            Capture(window, directory!, $"calendar-week-editable-sessions-{width}x{height}.png");

            // External synced event locked state (no completion circle): scroll into view.
            scroller = surface.FindControl<ScrollViewer>("Scroller")!;
            scroller.Offset = new Vector(0, 650);
            Capture(window, directory!, $"calendar-external-locked-{width}x{height}.png");

            // Create mode with the first-session group prefilled from an empty
            // Week-slot click, Thursday 13:00–14:00 (frame 4b).
            shell.Calendar.OpenNewTaskEditorAt(
                TestShell.DesignDate.AddDays(2), new TimeOnly(13, 0), new TimeOnly(14, 0));
            window.CaptureRenderedFrame();
            Capture(window, directory!, $"whole-task-editor-new-session-{width}x{height}.png");
            shell.Calendar.CloseActiveEditor();
            window.Close();

            // Centered Projects empty state needs a shell without projects.
            var emptyShell = TestShell.Create();
            var emptyWindow = new MainWindow { DataContext = emptyShell, Width = width, Height = height };
            emptyWindow.Show();
            emptyShell.NavigateCommand.Execute(AppSection.Projects);
            Capture(emptyWindow, directory!, $"projects-empty-centered-{width}x{height}.png");
            emptyWindow.Close();
        }
    }

    /// <summary>The 1100×720 minimum window: Daily list and Week timeline, no clipping.</summary>
    [AvaloniaFact]
    public void CaptureMinimumWindowScreens()
    {
        var directory = Environment.GetEnvironmentVariable("BEBOOSTED_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(directory), "BEBOOSTED_SCREENSHOT_DIR is not set");
        Directory.CreateDirectory(directory!);

        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        TestShell.SeedDesignCalendar(tasks, blocks, clock);
        foreach (var task in TestShell.SeededTasks(clock).GetAll())
        {
            tasks.Add(task);
        }

        // An eight-session task: the whole-task body must scroll, not clip (4p).
        var binderTask = BeBoosted.Domain.Tasks.TaskItem.Create(
            "Prepare regional qualifier written event binder and role-play scenario cards",
            clock.Now);
        tasks.Add(binderTask);
        var binderFirst = CalendarBlock.CreateTaskSession(
            binderTask.Id, TestShell.DesignDate, new TimeOnly(19, 0), new TimeOnly(20, 0),
            clock.Now);
        blocks.Add(binderFirst);
        for (var i = 1; i < 8; i++)
        {
            blocks.Add(CalendarBlock.CreateTaskSession(
                binderTask.Id, TestShell.DesignDate.AddDays(i),
                new TimeOnly(19, 0), new TimeOnly(20, 0), clock.Now));
        }

        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = 1100, Height = 720 };
        window.Show();
        Capture(window, directory!, "daily-list-1100x720.png");

        shell.Calendar.ViewKind = CalendarViewKind.Week;
        Capture(window, directory!, "calendar-week-1100x720.png");

        shell.Calendar.OpenTaskEditorForTask(binderTask.Id);
        window.CaptureRenderedFrame();
        Capture(window, directory!, "whole-task-editor-1100x720.png");
        shell.Calendar.CloseActiveEditor();

        shell.Calendar.OpenTaskEditorForBlock(binderFirst.Id, TestShell.DesignDate);
        window.CaptureRenderedFrame();
        Capture(window, directory!, "session-editor-1100x720.png");
        shell.Calendar.CloseActiveEditor();
        window.Close();
    }

    private static void Capture(MainWindow window, string directory, string fileName)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, fileName), new PngBitmapEncoderOptions());
    }
}

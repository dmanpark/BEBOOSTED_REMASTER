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
/// Targeted review screenshots for the commitment editor, editable/locked commitments,
/// and the centered Projects empty state. Runs only when BEBOOSTED_SCREENSHOT_DIR is set.
/// </summary>
public sealed class FeatureScreenshotCaptureTests
{
    [AvaloniaFact]
    public void CaptureCommitmentAndProjectScreens()
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
                CalendarBlockId.New(), null, null, "Imported standup", TestShell.DesignDate,
                new TimeOnly(13, 30), new TimeOnly(14, 0), BlockKind.FixedCommitment, null,
                "google", "evt-1", 0, BlockOutcome.None, null, clock.Now, clock.Now));
            var projects = new InMemoryProjectRepository();
            var schoolwork = Project.Create("Schoolwork", "#5B8DEF", clock.Now);
            projects.Add(schoolwork);
            projects.Add(Project.Create("Math", "#C2803F", clock.Now));

            var shell = TestShell.Create(tasks: tasks, blocks: blocks, projects: projects);
            var window = new MainWindow { DataContext = shell, Width = width, Height = height };
            window.Show();
            window.CaptureRenderedFrame();

            // The Today view's priority-first Daily list.
            Capture(window, directory!, $"daily-list-{width}x{height}.png");

            // New commitment dialog.
            shell.Calendar.OpenNewCommitmentEditorCommand.Execute(null);
            Capture(window, directory!, $"commitment-editor-new-{width}x{height}.png");
            shell.Calendar.CloseCommitmentEditor();

            // Timeline block states render on the Week surface.
            shell.Calendar.ViewKind = CalendarViewKind.Week;
            window.CaptureRenderedFrame();

            // Linked commitment used across the completion states.
            var stats = CalendarBlock.CreateFixedCommitment(
                "Stats HW", TestShell.DesignDate, new TimeOnly(16, 0), new TimeOnly(17, 0),
                clock.Now, projectId: schoolwork.Id);
            blocks.Add(stats);
            blocks.Add(CalendarBlock.CreateFixedCommitment(
                "Vocab review", TestShell.DesignDate.AddDays(-3), new TimeOnly(16, 0), new TimeOnly(17, 0),
                clock.Now, projectId: schoolwork.Id)); // stays incomplete → overdue
            shell.Calendar.Reload();

            var surface = window.GetVisualDescendants().OfType<TimelineSurfaceView>().First();
            var scroller = surface.FindControl<ScrollViewer>("Scroller")!;

            // Incomplete linked commitment with its completion circle.
            scroller.Offset = new Vector(0, 800);
            Capture(window, directory!, $"calendar-stats-incomplete-{width}x{height}.png");

            // Edit dialog with a project selected and the (unchecked) Completed field.
            shell.Calendar.OpenCommitmentEditorFor(stats.Id);
            Capture(window, directory!, $"commitment-editor-edit-project-{width}x{height}.png");
            shell.Calendar.CloseCommitmentEditor();

            // Checked off: subdued block, strike-through, lime check.
            shell.Calendar.SetCommitmentOccurrenceDone(stats.Id, TestShell.DesignDate, done: true);
            scroller.Offset = new Vector(0, 800);
            Capture(window, directory!, $"calendar-stats-done-{width}x{height}.png");

            // Edit dialog reflecting the completed state.
            shell.Calendar.OpenCommitmentEditorFor(stats.Id);
            Capture(window, directory!, $"commitment-editor-edit-completed-{width}x{height}.png");
            shell.Calendar.CloseCommitmentEditor();

            // Schoolwork project page: overdue ("needs review") and completed states.
            shell.NavigateCommand.Execute(AppSection.Projects);
            shell.Projects.OpenProject(schoolwork.Id);
            Capture(window, directory!, $"project-scheduled-states-{width}x{height}.png");
            shell.NavigateCommand.Execute(AppSection.Calendar);

            // Week view with an editable local commitment (and everything else).
            Capture(window, directory!, $"calendar-week-editable-commitment-{width}x{height}.png");

            // External commitment locked state (no completion circle): scroll into view.
            scroller = surface.FindControl<ScrollViewer>("Scroller")!;
            scroller.Offset = new Vector(0, 650);
            Capture(window, directory!, $"calendar-external-locked-{width}x{height}.png");
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

    private static void Capture(MainWindow window, string directory, string fileName)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, fileName), new PngBitmapEncoderOptions());
    }
}

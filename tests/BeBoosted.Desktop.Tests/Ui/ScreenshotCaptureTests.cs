using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// Captures review screenshots at the two design resolutions when
/// BEBOOSTED_SCREENSHOT_DIR is set; skipped in normal test runs.
/// </summary>
public sealed class ScreenshotCaptureTests
{
    [AvaloniaFact]
    public void CaptureShellScreens()
    {
        var directory = Environment.GetEnvironmentVariable("BEBOOSTED_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(directory), "BEBOOSTED_SCREENSHOT_DIR is not set");
        Directory.CreateDirectory(directory!);

        foreach (var (width, height) in new[] { (1440, 960), (1280, 800) })
        {
            var clock = new FakeClock(TestShell.DesignDate);
            var tasks = TestShell.SeededTasks(clock);
            var blocks = new InMemoryCalendarBlockRepository();
            TestShell.SeedDesignCalendar(tasks, blocks, clock);
            var shell = TestShell.Create(tasks: tasks, blocks: blocks);
            var window = new MainWindow
            {
                DataContext = shell,
                Width = width,
                Height = height,
            };
            window.Show();

            Capture(window, directory!, $"shell-calendar-today-{width}x{height}.png");

            shell.Calendar.ViewKind = CalendarViewKind.Week;
            Capture(window, directory!, $"shell-calendar-week-{width}x{height}.png");

            shell.Calendar.ViewKind = CalendarViewKind.Today;
            shell.ToggleInboxCommand.Execute(null);
            Capture(window, directory!, $"shell-inbox-drawer-{width}x{height}.png");
            shell.CloseInboxCommand.Execute(null);

            shell.NavigateCommand.Execute(AppSection.Settings);
            Capture(window, directory!, $"shell-settings-{width}x{height}.png");

            // Projects: reproduce frame 05/06 content through the real flows.
            shell.NavigateCommand.Execute(AppSection.Projects);
            var projects = shell.Projects;
            projects.NewProjectName = "DECA";
            projects.TryCreateProject();
            projects.CloseDetailCommand.Execute(null);
            projects.NewProjectName = "College Admissions";
            projects.TryCreateProject();
            var detail = projects.Detail!;

            detail.NewFileTitle = "Metric Proof";
            detail.NewFileDescription = "Scores, certificates, and verified numbers";
            detail.TryCreateFile();
            var fileDetail = projects.FileDetail!;
            fileDetail.NewNoteTitle = "Leadership metrics";
            fileDetail.NewNoteContent =
                "Led three DECA role-play teams to state finals; organized 120 volunteer hours "
                + "across the robotics club and the regional food bank.";
            fileDetail.TryAddNote();
            fileDetail.NewLinkUrl = "https://collegeboard.org/scores";
            fileDetail.NewLinkTitle = "SAT Score Report";
            fileDetail.TryAddLink();
            Capture(window, directory!, $"project-file-{width}x{height}.png");
            projects.CloseFileCommand.Execute(null);

            detail = projects.Detail!;
            detail.NewFileTitle = "Essay Research";
            detail.NewFileDescription = "Prompts, notes, and reference essays";
            detail.TryCreateFile();
            projects.CloseFileCommand.Execute(null);

            // Assign two inbox tasks to the project (through the one canonical
            // Task editor) so the detail view shows real rows.
            shell.Inbox.Reload();
            foreach (var row in shell.Inbox.Tasks.Where(r =>
                r.Title is "Draft essay outline" or "Email recommendation request").ToList())
            {
                row.EditCommand.Execute(null);
                var editor = (WholeTaskEditorViewModel)shell.Calendar.ActiveTaskEditor!;
                editor.SelectedProject = editor.ProjectOptions
                    .Single(o => o.Name == "College Admissions");
                editor.SaveCommand.Execute(null);
            }

            projects.Detail!.Refresh();
            Capture(window, directory!, $"project-detail-{width}x{height}.png");
            projects.CloseDetailCommand.Execute(null);
            Capture(window, directory!, $"projects-list-{width}x{height}.png");
            shell.Inbox.Reload();

            // Frame 07: AI task review inside the expanded composer.
            shell.NavigateCommand.Execute(AppSection.Calendar);
            shell.Chat.InputText =
                "Finish my DECA presentation before Friday. It probably needs two focused sessions. "
                + "Also I still owe Ms. Rivera the rec request email.";
            shell.Chat.SubmitCommand.Execute(null);
            Capture(window, directory!, $"chat-task-review-{width}x{height}.png");
            if (shell.Chat.Items[^1] is ChatReviewItemViewModel review)
            {
                review.DismissAllCommand.Execute(null);
            }

            shell.Chat.CollapseCommand.Execute(null);
            shell.Chat.Items.Clear();

            shell.PlanCommand.Execute(null);
            Capture(window, directory!, $"plan-draft-{width}x{height}.png");
            shell.Calendar.ApproveDraftCommand.Execute(null);
            Capture(window, directory!, $"plan-approved-undo-toast-{width}x{height}.png");
            shell.UndoApprovalCommand.Execute(null);
            shell.Calendar.DiscardDraftCommand.Execute(null);

            shell.StartPrioritySortCommand.Execute(null);
            Capture(window, directory!, $"priority-sort-comparison-{width}x{height}.png");
            shell.ActiveSort!.ChooseLeftCommand.Execute(null);
            shell.ActiveSort.ChooseTieCommand.Execute(null);
            shell.ActiveSort.BuildPlanNowCommand.Execute(null);
            Capture(window, directory!, $"priority-sort-results-{width}x{height}.png");
            shell.ActiveSort.CloseCommand.Execute(null);

            window.Close();
        }
    }

    private static void Capture(MainWindow window, string directory, string fileName)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, fileName), new PngBitmapEncoderOptions());
    }
}

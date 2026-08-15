using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The commitment editor is a centered in-view modal: opening focuses Title, Escape
/// cancels, the scrim blocks the calendar behind it, and closing restores focus.
/// </summary>
public sealed class CommitmentEditorModalTests
{
    private static (MainWindow Window, ShellViewModel Shell, InMemoryCalendarBlockRepository Blocks)
        CreateShellWindow()
    {
        var clock = new FakeClock(TestShell.DesignDate);
        var tasks = new InMemoryTaskRepository();
        var blocks = new InMemoryCalendarBlockRepository();
        TestShell.SeedDesignCalendar(tasks, blocks, clock);
        var shell = TestShell.Create(tasks: tasks, blocks: blocks);
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 800 };
        window.Show();
        window.CaptureRenderedFrame();
        return (window, shell, blocks);
    }

    private static void ClickNewCommitment(MainWindow window)
    {
        var button = window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.Name == "NewCommitmentButton");
        var point = button.TranslatePoint(
            new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();
    }

    private static T FindNamed<T>(MainWindow window, string name)
        where T : Control
        => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public void NewCommitmentClick_OpensTheCenteredModal_WithTitleFocused()
    {
        var (window, shell, _) = CreateShellWindow();

        ClickNewCommitment(window);

        Assert.NotNull(shell.Calendar.CommitmentEditor);
        var scrim = FindNamed<Border>(window, "CommitmentModalScrim");
        Assert.True(scrim.IsEffectivelyVisible);

        var card = FindNamed<Border>(window, "CommitmentModalCard");
        var cardCenter = card.TranslatePoint(
            new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), scrim)!.Value;
        Assert.True(
            Math.Abs(cardCenter.X - (scrim.Bounds.Width / 2)) <= 1,
            $"Card center X {cardCenter.X} is off the scrim center {scrim.Bounds.Width / 2}");
        Assert.True(
            Math.Abs(cardCenter.Y - (scrim.Bounds.Height / 2)) <= 1,
            $"Card center Y {cardCenter.Y} is off the scrim center {scrim.Bounds.Height / 2}");

        var title = FindNamed<TextBox>(window, "CommitmentTitleBox");
        Assert.Same(title, window.FocusManager?.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_ClosesTheModalWithoutCreating_AndRestoresFocus()
    {
        var (window, shell, blocks) = CreateShellWindow();
        var before = blocks.GetAll().Count;

        ClickNewCommitment(window);
        Assert.NotNull(shell.Calendar.CommitmentEditor);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.CaptureRenderedFrame();

        Assert.Null(shell.Calendar.CommitmentEditor);
        Assert.Equal(before, blocks.GetAll().Count);
        var button = window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.Name == "NewCommitmentButton");
        Assert.Same(button, window.FocusManager?.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact]
    public void Scrim_BlocksCalendarInteractionBehindTheModal()
    {
        var (window, shell, _) = CreateShellWindow();
        ClickNewCommitment(window);

        // The scrim must cover the whole calendar view.
        var scrim = FindNamed<Border>(window, "CommitmentModalScrim");
        var calendarView = window.GetVisualDescendants().OfType<CalendarView>().First();
        Assert.Equal(calendarView.Bounds.Size, scrim.Bounds.Size);

        // Pressing where a calendar block sits lands on the scrim, not the block.
        var blockView = window.GetVisualDescendants()
            .OfType<CalendarBlockView>()
            .First(view => view.IsEffectivelyVisible);
        var point = blockView.TranslatePoint(new Point(10, 10), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();

        Assert.NotSame(blockView, window.FocusManager?.GetFocusedElement());
        Assert.NotNull(shell.Calendar.CommitmentEditor);
        window.Close();
    }

    [AvaloniaFact]
    public void SaveButtonClick_PersistsAndClosesTheModal()
    {
        var (window, shell, blocks) = CreateShellWindow();
        var before = blocks.GetAll().Count;
        ClickNewCommitment(window);

        var editor = shell.Calendar.CommitmentEditor!;
        editor.Title = "Dentist";
        window.CaptureRenderedFrame();

        var save = FindNamed<Button>(window, "CommitmentSaveButton");
        var point = save.TranslatePoint(
            new Point(save.Bounds.Width / 2, save.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();

        Assert.Null(shell.Calendar.CommitmentEditor);
        Assert.Equal(before + 1, blocks.GetAll().Count);
        Assert.Contains(blocks.GetAll(), b => b.Title == "Dentist");
        window.Close();
    }
}

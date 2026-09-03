using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The Inbox drawer moves focus into its capture box on open; closing has to give that
/// focus back (BB-QA-006). The task editor got exactly this treatment; the drawer's close
/// paths previously restored nothing, leaving keyboard users stranded on a control that
/// no longer exists on screen.
/// </summary>
public sealed class InboxDrawerFocusTests
{
    private static (MainWindow Window, ShellViewModel Shell) Show()
    {
        var shell = TestShell.Create();
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 800 };
        window.Show();
        window.CaptureRenderedFrame();
        return (window, shell);
    }

    private static void Render(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
    }

    [AvaloniaFact]
    public void ClosingTheDrawer_ReturnsFocusToTheInvokingRailToggle()
    {
        var (window, shell) = Show();
        var toggle = window.GetVisualDescendants().OfType<ToggleButton>()
            .First(t => AutomationProperties.GetName(t) == "Inbox");
        toggle.Focus();
        Dispatcher.UIThread.RunJobs();

        shell.IsInboxOpen = true;
        Render(window);
        Render(window);

        // The open path is established behavior: the capture box takes focus.
        var duringOpen = window.FocusManager?.GetFocusedElement() as Control;
        Assert.NotNull(duringOpen);
        Assert.NotSame(toggle, duringOpen);

        shell.IsInboxOpen = false;
        Render(window);
        Render(window);

        Assert.Same(toggle, window.FocusManager?.GetFocusedElement());
    }
}

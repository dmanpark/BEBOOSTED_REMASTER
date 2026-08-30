using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The rendered Priority Sort surface. A re-rank has no partial-save exit, so
/// "Build my plan now" must not be visible in re-rank mode — the mode gate lives
/// in the view, not only on the view model.
/// </summary>
public sealed class PrioritySortUiTests
{
    private static void Capture(ShellViewModel shell, string title)
    {
        shell.Inbox.CaptureText = title;
        shell.Inbox.CaptureCommand.Execute(null);
    }

    private static ShellViewModel SortedShell(params string[] titles)
    {
        var shell = TestShell.Create();
        foreach (var title in titles)
        {
            Capture(shell, title);
        }

        shell.StartPrioritySortCommand.Execute(null);
        while (!shell.ActiveSort!.IsFinished)
        {
            shell.ActiveSort.ChooseRightCommand.Execute(null);
        }

        shell.ActiveSort.CloseCommand.Execute(null);
        return shell;
    }

    private static Button? BuildPlanNowButton(MainWindow window)
        => window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => AutomationProperties.GetName(b) == "Build my plan now (finish early)");

    [AvaloniaFact]
    public void RerankMode_DoesNotRenderBuildPlanNow()
    {
        var shell = SortedShell("Alpha", "Beta", "Gamma");
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();

        var alpha = shell.Inbox.Tasks.Single(r => r.Title == "Alpha").Task.Id;
        shell.StartRerank(alpha);
        window.CaptureRenderedFrame();

        Assert.True(shell.ActiveSort!.IsRerank);
        Assert.False(shell.ActiveSort.IsFinished);
        var button = BuildPlanNowButton(window);
        Assert.True(button is null || !button.IsEffectivelyVisible,
            "Build my plan now must not be rendered during a re-rank.");
    }

    [AvaloniaFact]
    public void NormalSort_StillRendersBuildPlanNow()
    {
        var shell = TestShell.Create();
        Capture(shell, "Alpha");
        Capture(shell, "Beta");
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();

        shell.StartPrioritySortCommand.Execute(null);
        window.CaptureRenderedFrame();

        Assert.False(shell.ActiveSort!.IsRerank);
        var button = BuildPlanNowButton(window);
        Assert.NotNull(button);
        Assert.True(button!.IsEffectivelyVisible);
    }
}

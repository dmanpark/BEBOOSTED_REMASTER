using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The Projects empty state must sit in the middle of the usable content region —
/// below the Projects header, spanning the full width right of the navigation rail —
/// not in a narrow left-docked layout slot.
/// </summary>
public sealed class ProjectsEmptyStateTests
{
    private static (MainWindow Window, ProjectsView View) ShowEmptyProjects(
        double width, double height)
    {
        var shell = TestShell.Create();
        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        shell.NavigateCommand.Execute(AppSection.Projects);
        window.CaptureRenderedFrame();
        var view = window.GetVisualDescendants().OfType<ProjectsView>().First();
        return (window, view);
    }

    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1440, 960)]
    public void EmptyState_IsCenteredInTheContentRegion(double width, double height)
    {
        var (window, view) = ShowEmptyProjects(width, height);

        var title = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(t => t.Text == "No projects yet" && t.IsEffectivelyVisible);
        var emptyState = title.FindAncestorOfType<StackPanel>()!;

        var header = view.GetVisualDescendants()
            .OfType<Border>()
            .First(b => b.IsEffectivelyVisible && b.Height == 52);
        var headerBottom = header.TranslatePoint(
            new Point(0, header.Bounds.Height), view)!.Value.Y;

        var center = emptyState.TranslatePoint(
            new Point(emptyState.Bounds.Width / 2, emptyState.Bounds.Height / 2), view)!.Value;
        var expectedX = view.Bounds.Width / 2;
        var expectedY = headerBottom + ((view.Bounds.Height - headerBottom) / 2);

        Assert.True(
            Math.Abs(center.X - expectedX) <= 1,
            $"Empty state center X {center.X} is off the content region center {expectedX}");
        Assert.True(
            Math.Abs(center.Y - expectedY) <= 1,
            $"Empty state center Y {center.Y} is off the content region center {expectedY}");
        window.Close();
    }

    [AvaloniaFact]
    public void WithProjects_TheListReplacesTheEmptyState()
    {
        var shell = TestShell.Create();
        shell.Projects.NewProjectName = "Schoolwork";
        Assert.True(shell.Projects.TryCreateProject());
        shell.Projects.CloseDetailCommand.Execute(null);

        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 800 };
        window.Show();
        shell.NavigateCommand.Execute(AppSection.Projects);
        window.CaptureRenderedFrame();

        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "No projects yet" && t.IsEffectivelyVisible);
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "Schoolwork" && t.IsEffectivelyVisible);
        window.Close();
    }
}

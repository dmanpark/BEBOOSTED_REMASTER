using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// The File reading pane takes the selected resource as its DataContext, but its
/// buttons and section headers are static markup — so with nothing selected they
/// rendered anyway. A brand-new File showed "Open in browser", "Rename" and "Remove
/// from File" for a resource that does not exist, which reads as though something is
/// already there.
/// </summary>
public sealed class EmptyFileReadingPaneTests
{
    private static MainWindow ShowFileWithNoResources()
    {
        var shell = TestShell.Create();
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 960 };
        window.Show();
        shell.NavigateCommand.Execute(AppSection.Projects);

        var projects = shell.Projects;
        projects.NewProjectName = "Schoolwork";
        Assert.True(projects.TryCreateProject());
        projects.Detail!.NewFileTitle = "Vocab";
        Assert.True(projects.Detail.TryCreateFile());

        window.CaptureRenderedFrame();
        return window;
    }

    [AvaloniaFact]
    public void AFileWithNoResources_ShowsNoResourceActions()
    {
        var window = ShowFileWithNoResources();
        Assert.Null(((ShellViewModel)window.DataContext!).Projects.FileDetail!.Selected);

        var strayActions = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsEffectivelyVisible
                && b.Content is string label
                // "Rename" is deliberately not in this list: the File's own header
                // carries a Rename button that should be visible with an empty File.
                // These two are resource actions and have no other source.
                && (label == "Open in browser" || label == "Remove from File"))
            .Select(b => (string)b.Content!)
            .ToList();

        Assert.True(
            strayActions.Count == 0,
            $"nothing is selected, yet these render: {string.Join(", ", strayActions)}");
    }

    /// <summary>The pane must come back the moment a resource is actually selected.</summary>
    [AvaloniaFact]
    public void AddingAResource_BringsTheReadingPaneBack()
    {
        var window = ShowFileWithNoResources();
        var file = ((ShellViewModel)window.DataContext!).Projects.FileDetail!;

        file.NewNoteTitle = "Leadership metrics";
        file.NewNoteContent = "Led three DECA teams.";
        Assert.True(file.TryAddNote());
        window.CaptureRenderedFrame();

        Assert.NotNull(file.Selected);
        Assert.Contains(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && b.Content is "Remove from File");
    }
}

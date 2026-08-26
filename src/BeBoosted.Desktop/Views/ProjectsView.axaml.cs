using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Desktop.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
    }

    private ProjectsViewModel? Vm => DataContext as ProjectsViewModel;

    private void OnBreadcrumbProjectsClick(object? sender, RoutedEventArgs e)
        => Vm?.CloseDetailCommand.Execute(null);

    private void OnCreateProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.TryCreateProject() == true)
        {
            CloseFlyout(sender);
        }
    }

    private void OnCreateFileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Detail?.TryCreateFile() == true)
        {
            CloseFlyout(sender);
        }
    }

    // Rename flyouts seed their field on the way open, so the box shows the current
    // name rather than whatever was typed the last time it was used.
    private void OnBeginProjectRenameClick(object? sender, RoutedEventArgs e)
        => Vm?.Detail?.BeginRename();

    private void OnRenameProjectClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Detail?.TryCommitRename() == true)
        {
            CloseFlyout(sender);
        }
    }

    private void OnBeginFileRenameClick(object? sender, RoutedEventArgs e)
        => Vm?.FileDetail?.BeginRename();

    private void OnRenameFileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.FileDetail?.TryCommitRename() == true)
        {
            CloseFlyout(sender);
        }
    }

    private void OnBeginResourceRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ResourceRowViewModel row })
        {
            row.BeginRename();
        }
    }

    private void OnRenameResourceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ResourceRowViewModel row } && row.TryCommitRename())
        {
            CloseFlyout(sender);
        }
    }

    private void OnAddLinkClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.FileDetail?.TryAddLink() == true)
        {
            CloseFlyout(sender);
        }
    }

    private void OnAddNoteClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.FileDetail?.TryAddNote() == true)
        {
            CloseFlyout(sender);
        }
    }

    private async void OnAddDocumentClick(object? sender, RoutedEventArgs e)
        => await PickAndImportAsync(
            ResourceKind.Document,
            "Add documents",
            new FilePickerFileType("Documents") { Patterns = ["*.pdf", "*.doc", "*.docx", "*.txt", "*.md"] });

    private async void OnAddImageClick(object? sender, RoutedEventArgs e)
        => await PickAndImportAsync(ResourceKind.Image, "Add images", FilePickerFileTypes.ImageAll);

    private async System.Threading.Tasks.Task PickAndImportAsync(
        ResourceKind kind, string title, FilePickerFileType filter)
    {
        if (Vm?.FileDetail is not { } fileDetail
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        var picked = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [filter],
        });

        var paths = picked
            .Select(file => file.TryGetLocalPath())
            .Where(path => path is not null)
            .Select(path => path!)
            .ToList();
        if (paths.Count > 0)
        {
            fileDetail.Import(kind, paths);
        }
    }

    private static void CloseFlyout(object? sender)
    {
        if (sender is Control control
            && control.FindAncestorOfType<FlyoutPresenter>() is { Parent: Popup popup })
        {
            popup.IsOpen = false;
        }
    }
}

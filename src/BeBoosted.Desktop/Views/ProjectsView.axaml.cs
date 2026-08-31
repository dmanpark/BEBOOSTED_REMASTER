using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Desktop.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WatchForPrompts(Vm);
    }

    private ProjectsViewModel? Vm => DataContext as ProjectsViewModel;

    // ---- confirmation prompts take focus ------------------------------------------

    private ProjectsViewModel? _watchedOwner;
    private INotifyPropertyChanged? _watchedProject;
    private INotifyPropertyChanged? _watchedFile;

    /// <summary>
    /// A confirmation is raised from a button that can be anywhere — a group's Delete deep
    /// in the resource list, a row's Remove over in the reading pane — while the prompt
    /// itself docks at the top of the surface. Without moving focus, answering it from the
    /// keyboard means Shift+Tabbing back past everything in between. Watched here rather
    /// than signalled from the view models: the prompt is view-layer behaviour and
    /// <c>Confirmation</c> already announces itself.
    /// </summary>
    private void WatchForPrompts(ProjectsViewModel? owner)
    {
        if (ReferenceEquals(owner, _watchedOwner))
        {
            RebindSurfacePrompts();
            return;
        }

        if (_watchedOwner is not null)
        {
            _watchedOwner.PropertyChanged -= OnOwnerPropertyChanged;
        }

        _watchedOwner = owner;
        if (_watchedOwner is not null)
        {
            _watchedOwner.PropertyChanged += OnOwnerPropertyChanged;
        }

        RebindSurfacePrompts();
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectsViewModel.Detail) or nameof(ProjectsViewModel.FileDetail))
        {
            RebindSurfacePrompts();
        }
    }

    /// <summary>The open Project and File surfaces are replaced as the user navigates.</summary>
    private void RebindSurfacePrompts()
    {
        Rebind(ref _watchedProject, Vm?.Detail);
        Rebind(ref _watchedFile, Vm?.FileDetail);

        void Rebind(ref INotifyPropertyChanged? watched, INotifyPropertyChanged? current)
        {
            if (ReferenceEquals(watched, current))
            {
                return;
            }

            if (watched is not null)
            {
                watched.PropertyChanged -= OnSurfacePropertyChanged;
            }

            watched = current;
            if (watched is not null)
            {
                watched.PropertyChanged += OnSurfacePropertyChanged;
            }
        }
    }

    private void OnSurfacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileDetailViewModel.Confirmation))
        {
            return;
        }

        var raised = sender switch
        {
            FileDetailViewModel file => file.Confirmation is not null,
            ProjectDetailViewModel project => project.Confirmation is not null,
            _ => false,
        };
        if (raised)
        {
            PostFocus(FocusOpenPrompt);
        }
    }

    private bool FocusOpenPrompt()
        => this.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.IsEffectivelyVisible
                && button.Name is "FilePromptConfirmButton" or "ProjectPromptConfirmButton")
            ?.Focus() == true;

    /// <summary>
    /// Focus a control that the change which triggered this has not laid out yet: try once
    /// off the dispatcher, then once more after the layout that materializes it. The shape
    /// MainWindow already uses to restore focus across a rebuilt editor.
    /// </summary>
    private void PostFocus(Func<bool> attempt) => Dispatcher.UIThread.Post(() =>
    {
        if (attempt())
        {
            return;
        }

        EventHandler? onLayout = null;
        onLayout = (_, _) =>
        {
            LayoutUpdated -= onLayout;
            attempt();
        };
        LayoutUpdated += onLayout;
    });

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

    // Every group handler marks the click handled. The Rename/Save buttons sit inside the
    // Expander's own header toggle, and a move rebuilds the list underneath the row that
    // was clicked — an unhandled press would collapse the group it was asked to rename, or
    // land on whatever ListBoxItem has taken the old one's place and reset the selection.
    private void OnCreateGroupClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Vm?.FileDetail?.TryCreateGroup() == true)
        {
            CloseFlyout(sender);
        }
    }

    private void OnBeginGroupRenameClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: ResourceGroupViewModel group })
        {
            group.BeginRename();
        }
    }

    private void OnRenameGroupClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: ResourceGroupViewModel group } && group.TryCommitRename())
        {
            CloseFlyout(sender);
        }
    }

    private void OnMoveResourceClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: ResourceMoveTargetViewModel target } && target.TryMove())
        {
            CloseFlyout(sender);
            FocusMovedRow();
        }
    }

    /// <summary>
    /// Focus follows the resource to wherever it landed. The refresh behind a move replaces
    /// every row, so the Move button that had focus is gone and Avalonia focuses nothing in
    /// its place — leaving a keyboard user outside the tab order, one full traversal from
    /// where they were. The move reselects what it filed, so the selected row is that new
    /// home; this is DailyTaskListView's focus-the-row-you-just-made move, with
    /// MainWindow's post-then-retry-after-layout because the replacement row has not been
    /// realized when the click handler returns.
    /// </summary>
    private void FocusMovedRow()
    {
        if (Vm?.FileDetail?.Selected is not { } moved)
        {
            return;
        }

        PostFocus(() => TryFocusRow(moved));
    }

    private bool TryFocusRow(ResourceRowViewModel row)
        => this.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .FirstOrDefault(item => item.IsEffectivelyVisible && ReferenceEquals(item.DataContext, row))
            ?.Focus() == true;

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

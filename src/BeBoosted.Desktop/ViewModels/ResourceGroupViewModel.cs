using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>One collapsible group inside an open File: a header, its rows, and its count.</summary>
public sealed partial class ResourceGroupViewModel : ViewModelBase
{
    private readonly FileDetailViewModel _owner;

    public ResourceGroupViewModel(
        FileDetailViewModel owner, ResourceGroup group, IReadOnlyList<ResourceRowViewModel> resources)
    {
        _owner = owner;
        Group = group;
        Resources = resources;
    }

    /// <summary>The persisted row, immutable here — a rename rebuilds the whole surface.</summary>
    public ResourceGroup Group { get; }

    public ResourceGroupId Id => Group.Id;

    public string Title => Group.Title;

    /// <summary>
    /// The very same row instances the File's canonical <c>Resources</c> holds, never
    /// copies: the reading pane binds to one selected row, so two objects for one resource
    /// would leave the pane showing a different one than the list highlights.
    /// </summary>
    public IReadOnlyList<ResourceRowViewModel> Resources { get; }

    public int Count => Resources.Count;

    public string CountText => $"{Count} item{(Count == 1 ? string.Empty : "s")}";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial string RenameTitle { get; set; } = string.Empty;

    /// <summary>
    /// This group's view of the one canonical selection. It reports the selected row only
    /// while it holds it, and refuses a null write: several lists bind to the same
    /// selection, and every list that does not hold it clears its own SelectedItem —
    /// which would otherwise wipe the reading pane the moment another list won the click.
    /// </summary>
    public ResourceRowViewModel? SelectedResource
    {
        get => _owner.Selected is { } row && Resources.Contains(row) ? row : null;
        set
        {
            if (value is not null)
            {
                _owner.SelectResource(value.Resource.Id);
            }
        }
    }

    internal void NotifySelectionChanged() => OnPropertyChanged(nameof(SelectedResource));

    /// <summary>Seeds the flyout so the field opens on the current title.</summary>
    public void BeginRename() => RenameTitle = Title;

    /// <summary>Returns true when the rename committed (the view closes its flyout).</summary>
    public bool TryCommitRename() => _owner.TryRenameGroup(Id, RenameTitle);

    [RelayCommand]
    private void Ungroup() => _owner.TryUngroup(Id);

    [RelayCommand]
    private void RequestDelete() => _owner.RequestDeleteGroup(this);
}

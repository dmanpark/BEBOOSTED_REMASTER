using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BeBoosted.Application.Projects;
using BeBoosted.Desktop.Platform;
using BeBoosted.Domain.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>Frame 06: an open File — flat resource list with a preview/reading pane.</summary>
public sealed partial class FileDetailViewModel : ViewModelBase
{
    private readonly ProjectsViewModel _owner;
    private readonly ProjectService _service;
    private readonly IFileRevealService _opener;
    private readonly Application.Ai.AiService _ai;

    public FileDetailViewModel(
        ProjectsViewModel owner,
        Project project,
        ProjectFile file,
        ProjectService service,
        IFileRevealService opener,
        Application.Ai.AiService ai)
    {
        _owner = owner;
        _service = service;
        _opener = opener;
        _ai = ai;
        Project = project;
        File = file;
        Refresh();
    }

    /// <summary>
    /// True while <see cref="Refresh"/> is swapping the three collections. Replacing a
    /// collection makes the ListBox bound to it clear and rewrite its own SelectedItem, so a
    /// burst of selection callbacks arrives mid-rebuild describing lists that are
    /// momentarily half-built. The selection restored at the end of the refresh is the only
    /// one that means anything, so <see cref="SelectResource"/> — the one path every list
    /// writes through — ignores them until then.
    /// </summary>
    private bool _refreshingGroups;

    /// <summary>
    /// Selects a resource by id — the search/citation path, which may land on a row inside
    /// a collapsed group. Opening that group is part of the job: a reading pane showing a
    /// row the user cannot see in any list is worse than not navigating at all.
    /// </summary>
    public void SelectResource(Domain.ResourceId resourceId)
    {
        if (_refreshingGroups)
        {
            return;
        }

        if (Resources.FirstOrDefault(r => r.Resource.Id == resourceId) is not { } row)
        {
            return;
        }

        SelectAndReveal(row);
    }

    /// <summary>
    /// Selects a row and opens whatever group holds it, so the reading pane never shows a
    /// row no list is displaying. Every selection the user did not make by clicking a
    /// visible row goes through here — search navigation, a move, and the fallback a
    /// refresh lands on when the previous selection is gone.
    ///
    /// Deliberately not used when a refresh restores a selection that survived it:
    /// collapsing a group whose member is selected is a legitimate thing to do, and
    /// re-opening it on the next refresh would undo the user's own collapse.
    /// </summary>
    private void SelectAndReveal(ResourceRowViewModel row)
    {
        foreach (var group in Groups)
        {
            if (group.Resources.Contains(row))
            {
                group.IsExpanded = true;
            }
        }

        Selected = row;
    }

    public Project Project { get; }

    public ProjectFile File { get; private set; }

    public string Title => File.Title;

    public string? Description => File.Description;

    public string TabLabel => $"FILE · {Project.Name.ToUpperInvariant()}";

    public IBrush AccentBrush => ProjectsViewModel.BrushFor(Project.AccentColor);

    /// <summary>
    /// Every resource in this File, grouped or not — the canonical index. The count, the
    /// deletion prompt and selection by id all read it, and <see cref="Groups"/> and
    /// <see cref="LooseResources"/> project the very same row instances out of it.
    /// </summary>
    public ObservableCollection<ResourceRowViewModel> Resources { get; } = [];

    public ObservableCollection<ResourceGroupViewModel> Groups { get; } = [];

    /// <summary>The rows no group holds. Shares its instances with <see cref="Resources"/>.</summary>
    public ObservableCollection<ResourceRowViewModel> LooseResources { get; } = [];

    public bool HasResources => Resources.Count > 0;

    public bool HasGroups => Groups.Count != 0;

    public bool HasLooseResources => LooseResources.Count != 0;

    public bool ShowEmptyState => !HasGroups && !HasResources;

    public bool ShowLooseHeader => HasGroups && HasLooseResources;

    // New-group flyout
    [ObservableProperty]
    public partial string NewGroupTitle { get; set; } = string.Empty;

    /// <summary>
    /// What went wrong with the last group action; null when it went cleanly. Cleared by
    /// <see cref="Refresh"/>, so it describes the list currently on screen rather than
    /// hanging over one that an unrelated add, import or rename has since rebuilt.
    /// </summary>
    [ObservableProperty]
    public partial string? GroupNotice { get; private set; }

    /// <summary>
    /// The presentation boundary for every group mutation. A throw from the mutation is a
    /// failure and is reported as one: the notice says why, the collections are left
    /// exactly as they were, and nothing is rebuilt around a change that did not land.
    ///
    /// A throw from the refresh *behind* a mutation is a different thing and is not allowed
    /// to look like the same thing. The write has already committed, so reporting failure
    /// would be a lie and letting it escape would crash the [RelayCommand] that got here —
    /// the user seeing an error dialog for an operation that succeeded. Refresh is
    /// all-or-nothing, so what is left is the previous list, correct as of a moment ago,
    /// with a notice saying it is stale.
    /// </summary>
    private bool TryGroupMutation(Action mutation)
    {
        GroupNotice = null;
        try
        {
            mutation();
        }
        catch (Exception error)
        {
            GroupNotice = error.Message;
            return false;
        }

        try
        {
            Refresh();
        }
        catch (Exception error)
        {
            GroupNotice = $"That worked, but this list couldn't be reloaded: {error.Message}";
        }

        return true;
    }

    /// <summary>Returns true when the group was created (the view closes its flyout).</summary>
    public bool TryCreateGroup()
    {
        if (!TryGroupMutation(() => _service.CreateGroup(File.Id, NewGroupTitle)))
        {
            // The typed text stays: a refused title is one the user still has to fix.
            return false;
        }

        NewGroupTitle = string.Empty;
        return true;
    }

    internal bool TryRenameGroup(Domain.ResourceGroupId id, string title)
        => TryGroupMutation(() => _service.RenameGroup(id, title));

    internal bool TryUngroup(Domain.ResourceGroupId id)
        => TryGroupMutation(() => _service.UngroupGroup(id));

    /// <summary>
    /// Files a resource into a group, or back out to loose. The moved row is reselected by
    /// id afterwards, because the refresh has just replaced the instance the caller held
    /// and the reading pane should still be showing the resource the user just filed.
    /// </summary>
    internal bool TryMoveResource(Domain.ResourceId id, Domain.ResourceGroupId? groupId)
    {
        if (!TryGroupMutation(() => _service.MoveResourceToGroup(id, groupId)))
        {
            return false;
        }

        SelectResource(id);
        return true;
    }

    /// <summary>
    /// Where this row may be filed: every group that is not the one already holding it,
    /// plus the File itself when it is in a group at all. Offering the current container
    /// would be a no-op dressed up as a choice.
    ///
    /// Computed once per refresh into <see cref="ResourceRowViewModel.MoveTargets"/>, the
    /// way that row's stored path and derivations already are. The list the flyout binds to
    /// is a row datum, and the surrounding ScrollViewer realizes every row, so deriving it
    /// lazily through the owner would rebuild it for every row on every refresh.
    /// </summary>
    private IReadOnlyList<ResourceMoveTargetViewModel> MoveTargetsFor(
        IReadOnlyList<ResourceGroupViewModel> groups, ResourceRowViewModel row)
    {
        var targets = groups
            .Where(group => group.Id != row.Resource.GroupId)
            .Select(group => new ResourceMoveTargetViewModel(this, row.Resource.Id, group.Id, group.Title))
            .ToList();
        if (row.Resource.GroupId is not null)
        {
            targets.Add(new ResourceMoveTargetViewModel(
                this, row.Resource.Id, null, "loose in this File"));
        }

        return targets;
    }

    /// <summary>
    /// Deleting a group destroys the documents in it, bytes included, so it asks first and
    /// counts what goes — the same two-step prompt a File deletion uses, through the same
    /// pending-action slot. Ungroup, which destroys nothing, deliberately asks nothing.
    /// </summary>
    internal void RequestDeleteGroup(ResourceGroupViewModel group)
    {
        var count = group.Count;
        Confirmation = new ConfirmationPrompt(
            $"Delete '{group.Title}'? Its {count} resource{(count == 1 ? string.Empty : "s")} "
                + "and any stored files are deleted too.",
            "Delete group",
            IsTaskDeletion: false);
        _pendingConfirmedAction = () => TryGroupMutation(() => _service.DeleteGroup(group.Id));
    }

    /// <summary>
    /// The loose list's view of the one canonical selection, and the mirror of
    /// <see cref="ResourceGroupViewModel.SelectedResource"/>. It reports the selection only
    /// while it holds it, and refuses a null write: several ListBoxes bind to the same
    /// selection, and each one that does not hold it clears its own SelectedItem — which
    /// would otherwise wipe the reading pane the instant another list won the click.
    /// </summary>
    public ResourceRowViewModel? LooseSelectedResource
    {
        get => Selected is { } row && LooseResources.Contains(row) ? row : null;
        set
        {
            if (value is not null)
            {
                // Through SelectResource like every other list, so there is one path into
                // the canonical selection and one place its guards live.
                SelectResource(value.Resource.Id);
            }
        }
    }

    partial void OnSelectedChanged(ResourceRowViewModel? value) => NotifySelectionAcrossLists();

    /// <summary>Every list re-reads whether it is the one holding the selection.</summary>
    private void NotifySelectionAcrossLists()
    {
        OnPropertyChanged(nameof(LooseSelectedResource));
        foreach (var group in Groups)
        {
            group.NotifySelectionChanged();
        }
    }

    public string CountText
        => $"{Resources.Count} resource{(Resources.Count == 1 ? string.Empty : "s")}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ResourceRowViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    // New-resource flyout fields
    [ObservableProperty]
    public partial string NewLinkTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewLinkUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewNoteTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewNoteContent { get; set; } = string.Empty;

    // Rename-this-File flyout
    [ObservableProperty]
    public partial string RenameTitle { get; set; } = string.Empty;

    /// <summary>Seeds the flyout so the field opens on the current title.</summary>
    public void BeginRename() => RenameTitle = File.Title;

    /// <summary>Returns true when the rename committed (the view closes its flyout).</summary>
    public bool TryCommitRename()
    {
        if (string.IsNullOrWhiteSpace(RenameTitle))
        {
            return false;
        }

        File = _service.RenameFile(File.Id, RenameTitle);
        OnPropertyChanged(nameof(Title));
        _owner.Detail?.Refresh(); // the card behind this surface carries the title too
        return true;
    }

    /// <summary>The open two-step delete confirmation, or null when nothing is pending.</summary>
    [ObservableProperty]
    public partial ConfirmationPrompt? Confirmation { get; private set; }

    private Action? _pendingConfirmedAction;

    /// <summary>
    /// Deleting a File takes its resources and their bytes with it, so the prompt
    /// counts what goes rather than asking a bare "are you sure".
    /// </summary>
    [RelayCommand]
    private void RequestDelete()
    {
        var count = Resources.Count;
        Confirmation = new ConfirmationPrompt(
            count == 0
                ? $"Delete '{Title}'? It's empty, so nothing else goes with it."
                : $"Delete '{Title}'? Its {count} resource{(count == 1 ? string.Empty : "s")} "
                    + "and any stored documents are deleted too.",
            "Delete File",
            IsTaskDeletion: false);
        _pendingConfirmedAction = () =>
        {
            _service.DeleteFile(File.Id);
            _owner.CloseFile();
        };
    }

    [RelayCommand]
    private void ConfirmPrompt()
    {
        var pending = _pendingConfirmedAction;
        Confirmation = null;
        _pendingConfirmedAction = null;
        pending?.Invoke();
    }

    [RelayCommand]
    private void KeepPrompt()
    {
        Confirmation = null;
        _pendingConfirmedAction = null;
    }

    /// <summary>One retitled resource; the list rebuilds so the row shows it.</summary>
    internal void RenameResource(ResourceRowViewModel row, string title)
    {
        _service.RenameResource(row.Resource.Id, title);
        Refresh();
    }

    public void Refresh()
    {
        var previous = Selected?.Resource.Id;

        // By group id, never by position: these view models are thrown away and rebuilt
        // here, so an index would follow whatever moved into the slot instead of the group
        // whose header the user collapsed.
        var expansion = Groups.ToDictionary(group => group.Id, group => group.IsExpanded);

        // Everything that can throw happens here, into locals, before a single observable
        // collection is touched. A refresh reads twice — resources, then groups — and a
        // failure between the two used to leave the three collections permanently
        // disagreeing: rows in the canonical list that neither a group nor the loose list
        // admitted to holding. It is also run behind mutations that have already committed,
        // where a half-applied rebuild is the worst of the available outcomes.
        var rows = _service.GetResources(File.Id)
            .Select(resource => new ResourceRowViewModel(this, resource, _opener)
            {
                StoredAbsolutePath = SafeResolve(resource),
                Derivations = _ai.GetDerivations(resource.Id),
            })
            .ToList();

        // One pass to bucket the rows, so filling the groups is linear in the rows rather
        // than a scan of every row per group.
        var members = new Dictionary<Domain.ResourceGroupId, List<ResourceRowViewModel>>();
        var loose = new List<ResourceRowViewModel>();
        foreach (var row in rows)
        {
            if (row.Resource.GroupId is { } groupId)
            {
                if (!members.TryGetValue(groupId, out var bucket))
                {
                    bucket = [];
                    members[groupId] = bucket;
                }

                bucket.Add(row);
            }
            else
            {
                loose.Add(row);
            }
        }

        var groups = _service.GetGroups(File.Id)
            .Select(group => new ResourceGroupViewModel(
                this, group, members.TryGetValue(group.Id, out var bucket) ? bucket : [])
            {
                // A group that did not exist before this refresh arrives expanded.
                IsExpanded = !expansion.TryGetValue(group.Id, out var wasExpanded) || wasExpanded,
            })
            .ToList();

        foreach (var row in rows)
        {
            row.MoveTargets = MoveTargetsFor(groups, row);
        }

        _refreshingGroups = true;
        try
        {
            // The notice describes the list being replaced, so it goes with it — and only
            // once the replacement is certain. Clearing it before the reads above would
            // discard a message still describing the list left on screen when one throws.
            GroupNotice = null;

            Resources.Clear();
            Groups.Clear();
            LooseResources.Clear();

            foreach (var row in rows)
            {
                Resources.Add(row);
            }

            foreach (var group in groups)
            {
                Groups.Add(group);
            }

            foreach (var row in loose)
            {
                LooseResources.Add(row);
            }

            if (rows.FirstOrDefault(row => row.Resource.Id == previous) is { } restored)
            {
                // Restored exactly as it was, collapse included — see SelectAndReveal.
                Selected = restored;
            }
            else if (rows.FirstOrDefault() is { } fallback)
            {
                // A selection the user never asked for, so it is revealed like any other:
                // otherwise the reading pane opens on a row inside a collapsed group and no
                // list reports holding it.
                SelectAndReveal(fallback);
            }
            else
            {
                Selected = null;
            }
        }
        finally
        {
            _refreshingGroups = false;
        }

        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasLooseResources));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowLooseHeader));

        // Explicitly, not only through OnSelectedChanged: when the selected value stayed
        // null — an empty File, or one whose last resource just went — nothing changed and
        // no change notification is raised, yet every list underneath it was rebuilt and
        // still has to re-read the selection it reports.
        NotifySelectionAcrossLists();
    }

    /// <summary>A tampered stored path resolves to nothing rather than breaking the view.</summary>
    private string? SafeResolve(Resource resource)
    {
        try
        {
            return _service.ResolveStoredPath(resource);
        }
        catch (Domain.DomainException)
        {
            return null;
        }
    }

    public bool TryAddLink()
    {
        if (string.IsNullOrWhiteSpace(NewLinkUrl))
        {
            return false;
        }

        var title = string.IsNullOrWhiteSpace(NewLinkTitle) ? NewLinkUrl.Trim() : NewLinkTitle;
        _service.AddLink(File.Id, title, NewLinkUrl);
        NewLinkTitle = string.Empty;
        NewLinkUrl = string.Empty;
        Refresh();
        return true;
    }

    public bool TryAddNote()
    {
        if (string.IsNullOrWhiteSpace(NewNoteTitle))
        {
            return false;
        }

        _service.AddNote(File.Id, NewNoteTitle, NewNoteContent);
        NewNoteTitle = string.Empty;
        NewNoteContent = string.Empty;
        Refresh();
        return true;
    }

    /// <summary>Why part of the last import batch was skipped; null when all succeeded.</summary>
    [ObservableProperty]
    public partial string? ImportNotice { get; private set; }

    /// <summary>
    /// Imports picked files (documents or images) into app-controlled storage. One
    /// failing file (locked, disk full, vanished) must not abort its siblings — or
    /// throw through the async picker click handler.
    /// </summary>
    public void Import(ResourceKind kind, IEnumerable<string> paths)
    {
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                _service.ImportFile(File.Id, kind, path);
            }
            catch (Exception exception) when (exception
                is IOException or UnauthorizedAccessException or Domain.DomainException)
            {
                failures.Add(Path.GetFileName(path));
            }
        }

        ImportNotice = failures.Count == 0
            ? null
            : $"Couldn't import {string.Join(", ", failures)}.";
        Refresh();
    }

    /// <summary>
    /// Removing a resource deletes its stored document too, so it asks first — the
    /// same two-step prompt a File or Project deletion uses.
    /// </summary>
    internal void RequestDeleteResource(ResourceRowViewModel row)
    {
        var message = row.Resource.StoredPath is null
            ? $"Remove '{row.Title}' from this File?"
            : $"Remove '{row.Title}' from this File? Its stored document is deleted too.";
        Confirmation = new ConfirmationPrompt(message, "Remove", IsTaskDeletion: false);
        _pendingConfirmedAction = () => DeleteResource(row);
    }

    public void DeleteResource(ResourceRowViewModel row)
    {
        _service.DeleteResource(row.Resource.Id);
        Refresh();
    }

    [RelayCommand]
    private void Back() => _owner.CloseFileCommand.Execute(null);
}

public sealed partial class ResourceRowViewModel : ViewModelBase
{
    private readonly FileDetailViewModel _owner;
    private readonly IFileRevealService _opener;

    public ResourceRowViewModel(FileDetailViewModel owner, Resource resource, IFileRevealService opener)
    {
        _owner = owner;
        _opener = opener;
        Resource = resource;
    }

    public Resource Resource { get; }

    public string Title => Resource.Title;

    /// <summary>Type chip: PDF / DOC / URL / NOTE / IMG.</summary>
    public string KindChip => Resource.Kind switch
    {
        ResourceKind.Link => "URL",
        ResourceKind.Note => "NOTE",
        ResourceKind.Image => "IMG",
        _ => Path.GetExtension(Resource.OriginalFileName ?? string.Empty).TrimStart('.').ToUpperInvariant()
            is { Length: > 0 and <= 4 } extension ? extension : "DOC",
    };

    public string MetaText
    {
        get
        {
            var added = Resource.AddedAt.LocalDateTime.ToString("MMM d", CultureInfo.CurrentCulture);
            var state = Resource.IndexState switch
            {
                ResourceIndexState.Indexed => "Indexed",
                ResourceIndexState.Failed => "Index failed",
                _ => "Indexing…",
            };
            var source = Resource.Kind switch
            {
                ResourceKind.Link => TryHost(Resource.Url),
                ResourceKind.Note => "Note",
                ResourceKind.Image => "Image",
                _ => "Uploaded",
            };
            return $"{source} · {added} · {state}";
        }
    }

    public string PreviewMeta
    {
        get
        {
            var added = Resource.AddedAt.LocalDateTime.ToString("MMM d", CultureInfo.CurrentCulture);
            return Resource.Kind switch
            {
                ResourceKind.Note => $"note · added {added}",
                ResourceKind.Link => $"link · added {added}",
                ResourceKind.Image => $"image · added {added}",
                _ => $"{Resource.OriginalFileName} · added {added}",
            };
        }
    }

    public string IndexChip => Resource.IndexState switch
    {
        ResourceIndexState.Indexed => "Indexed",
        ResourceIndexState.Failed => "Index failed",
        _ => "Indexing…",
    };

    public bool IsNote => Resource.Kind == ResourceKind.Note;

    public bool IsLink => Resource.Kind == ResourceKind.Link;

    public bool IsImage => Resource.Kind == ResourceKind.Image;

    public bool IsDocument => Resource.Kind == ResourceKind.Document;

    public bool HasStoredFile => Resource.StoredPath is not null;

    public string? NoteContent => Resource.Content;

    public string? LinkUrl => Resource.Url;

    /// <summary>Image preview bitmap; null when the bytes are unavailable.</summary>
    public Bitmap? ImagePreview
    {
        get
        {
            if (!IsImage || _owner is null)
            {
                return null;
            }

            try
            {
                var path = StoredAbsolutePath;
                return path is not null && File.Exists(path) ? new Bitmap(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Where this row may be filed: every group it is not already in, plus the File.
    /// Filled by the refresh that built this row, like <see cref="StoredAbsolutePath"/> and
    /// <see cref="Derivations"/> — a row datum, not a lazy call back into the owner.
    /// </summary>
    public IReadOnlyList<ResourceMoveTargetViewModel> MoveTargets { get; internal set; } = [];

    public bool HasMoveTargets => MoveTargets.Count != 0;

    public string? StoredAbsolutePath { get; internal set; }

    /// <summary>Tasks and answers derived from this resource ("Used by…", "Cited in…").</summary>
    public IReadOnlyList<Application.Ai.ResourceDerivation> Derivations { get; internal set; } = [];

    public bool HasDerivations => Derivations.Count > 0;

    /// <summary>Why the last open/reveal attempt did nothing: a missing stored file
    /// (an interrupted move, or bytes removed externally) or a launch failure.</summary>
    [ObservableProperty]
    public partial string? OpenNotice { get; private set; }

    [RelayCommand]
    private void Select() => _owner.Selected = this;

    [RelayCommand]
    private void OpenExternally()
    {
        if (IsLink && Resource.Url is { } url)
        {
            _opener.OpenUrl(url);
        }
        else if (StoredAbsolutePath is { } path)
        {
            LaunchStored(path, () => _opener.OpenFile(path));
        }
    }

    [RelayCommand]
    private void RevealInFolder()
    {
        if (StoredAbsolutePath is { } path)
        {
            LaunchStored(path, () => _opener.RevealInFolder(path));
        }
    }

    /// <summary>Never hands a dead path to the shell — that throws out of the command.</summary>
    private void LaunchStored(string path, Action launch)
    {
        if (!File.Exists(path))
        {
            OpenNotice = $"The stored file for '{Title}' is missing from disk.";
            return;
        }

        try
        {
            launch();
            OpenNotice = null;
        }
        catch (Exception exception) when (exception
            is System.ComponentModel.Win32Exception
            or IOException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            OpenNotice = $"Couldn't open '{Title}': {exception.Message}";
        }
    }

    [RelayCommand]
    private void Delete() => _owner.RequestDeleteResource(this);

    // Rename-this-resource flyout
    [ObservableProperty]
    public partial string RenameTitle { get; set; } = string.Empty;

    public void BeginRename() => RenameTitle = Title;

    /// <summary>Returns true when the rename committed (the view closes its flyout).</summary>
    public bool TryCommitRename()
    {
        if (string.IsNullOrWhiteSpace(RenameTitle))
        {
            return false;
        }

        _owner.RenameResource(this, RenameTitle);
        return true;
    }

    private static string TryHost(string? url)
    {
        if (url is null)
        {
            return "Link";
        }

        var candidate = url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri.Host : "Link";
    }
}

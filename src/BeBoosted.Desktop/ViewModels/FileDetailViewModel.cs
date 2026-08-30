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

    public void SelectResource(Domain.ResourceId resourceId)
        => Selected = Resources.FirstOrDefault(r => r.Resource.Id == resourceId) ?? Selected;

    public Project Project { get; }

    public ProjectFile File { get; private set; }

    public string Title => File.Title;

    public string? Description => File.Description;

    public string TabLabel => $"FILE · {Project.Name.ToUpperInvariant()}";

    public IBrush AccentBrush => ProjectsViewModel.BrushFor(Project.AccentColor);

    public ObservableCollection<ResourceRowViewModel> Resources { get; } = [];

    public bool HasResources => Resources.Count > 0;

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
        Resources.Clear();
        foreach (var resource in _service.GetResources(File.Id))
        {
            Resources.Add(new ResourceRowViewModel(this, resource, _opener)
            {
                StoredAbsolutePath = SafeResolve(resource),
                Derivations = _ai.GetDerivations(resource.Id),
            });
        }

        Selected = Resources.FirstOrDefault(r => r.Resource.Id == previous) ?? Resources.FirstOrDefault();
        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(CountText));
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

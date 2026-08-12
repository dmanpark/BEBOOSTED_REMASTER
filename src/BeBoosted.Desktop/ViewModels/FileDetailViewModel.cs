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

    public ProjectFile File { get; }

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

    public void Refresh()
    {
        var previous = Selected?.Resource.Id;
        Resources.Clear();
        foreach (var resource in _service.GetResources(File.Id))
        {
            Resources.Add(new ResourceRowViewModel(this, resource, _opener)
            {
                StoredAbsolutePath = _service.ResolveStoredPath(resource),
                Derivations = _ai.GetDerivations(resource.Id),
            });
        }

        Selected = Resources.FirstOrDefault(r => r.Resource.Id == previous) ?? Resources.FirstOrDefault();
        OnPropertyChanged(nameof(HasResources));
        OnPropertyChanged(nameof(CountText));
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

    /// <summary>Imports picked files (documents or images) into app-controlled storage.</summary>
    public void Import(ResourceKind kind, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            _service.ImportFile(File.Id, kind, path);
        }

        Refresh();
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
            _opener.OpenFile(path);
        }
    }

    [RelayCommand]
    private void RevealInFolder()
    {
        if (StoredAbsolutePath is { } path)
        {
            _opener.RevealInFolder(path);
        }
    }

    [RelayCommand]
    private void Delete() => _owner.DeleteResource(this);

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

using Avalonia.Media;
using BeBoosted.Domain;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>One project choice in the Task editor: "No project" or a real project.</summary>
public sealed class ProjectOptionViewModel(ProjectId? id, string name, string? accentColor)
{
    public ProjectId? Id { get; } = id;

    public string Name { get; } = name;

    public bool HasAccent => accentColor is not null;

    /// <summary>Lazy: brushes are composition resources and must be created on the UI thread.</summary>
    public IBrush? AccentBrush => accentColor is null ? null : ProjectsViewModel.BrushFor(accentColor);
}

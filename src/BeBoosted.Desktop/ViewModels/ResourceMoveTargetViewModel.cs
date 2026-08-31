using BeBoosted.Domain;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// One entry in a resource row's Move-to flyout: a destination group, or the File itself
/// for "loose in this File". Immutable — the flyout is rebuilt from the current groups
/// every time it opens, so a target never has to survive a change it did not cause.
/// </summary>
public sealed class ResourceMoveTargetViewModel(
    FileDetailViewModel owner, ResourceId resourceId, ResourceGroupId? groupId, string title)
{
    /// <summary>The destination group, or null for loose in the File.</summary>
    public ResourceGroupId? GroupId { get; } = groupId;

    public string Title { get; } = title;

    /// <summary>Returns true when the move landed (the view closes its flyout).</summary>
    public bool TryMove() => owner.TryMoveResource(resourceId, GroupId);
}

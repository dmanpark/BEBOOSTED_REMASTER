using BeBoosted.Application.Ai;
using BeBoosted.Domain;

namespace BeBoosted.Tests.Support;

/// <summary>
/// Records every provenance invalidation, so a test can assert none happened. Filing a
/// resource into a group or renaming that group changes nothing an AI answer cited — the
/// bytes, the text and the resource identity all survive — so flagging derived items
/// "Needs review" would be noise the user has to clear by hand.
/// </summary>
public sealed class RecordingGroupInvalidator : IProvenanceInvalidator
{
    public List<ResourceId> Calls { get; } = [];

    /// <summary>The one resource whose invalidation throws, for isolation tests.</summary>
    public ResourceId? ThrowFor { get; set; }

    public void InvalidateForResource(ResourceId id)
    {
        Calls.Add(id);
        if (id == ThrowFor)
        {
            throw new InvalidOperationException("invalidation refused");
        }
    }
}

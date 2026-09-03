namespace BeBoosted.Application.Ai;

/// <summary>
/// One capture, as a backend sees it. Deliberately not <see cref="AiContext"/>: that
/// carries a ProjectId, and a backend that never sees a domain identifier cannot leak
/// one into a prompt or invent one in a response. The router maps names to ids.
/// </summary>
public sealed record CaptureRequest(
    string Message, IReadOnlyList<string> ProjectNames, DateOnly Today);

/// <summary>A draft as the model returns it: a project is named, never identified.</summary>
public sealed record CaptureDraft(
    string Title, int? EstimatedMinutes, DateOnly? Deadline, string? ProjectName);

/// <summary>
/// The narrow capture port. Only the two operations a model actually serves in this
/// slice; project Q&amp;A is not here, so no backend is made to implement it.
/// A backend that cannot answer throws, and the router decides what that means.
/// </summary>
public interface ICaptureModel
{
    Task<IReadOnlyList<CaptureDraft>> ExtractAsync(
        CaptureRequest request, CancellationToken cancellationToken = default);

    /// <summary>Duration/deadline for one bare title; null when the model offers nothing.</summary>
    Task<CaptureDraft?> SuggestMetadataAsync(
        CaptureRequest request, CancellationToken cancellationToken = default);
}

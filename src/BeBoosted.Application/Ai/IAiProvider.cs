using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Application.Ai;

/// <summary>Ambient context for AI operations. Retrieval never leaves the active project.</summary>
public sealed record AiContext(ProjectId? ActiveProjectId, DateOnly Today);

/// <summary>A task the provider proposes from a conversation; reviewed before joining the Inbox.</summary>
public sealed record ExtractedTaskDraft(
    string Title,
    TimeSpan? EstimatedDuration,
    DateOnly? Deadline,
    ProjectId? ProjectId,
    string SourceDescription);

public sealed record TaskMetadataSuggestion(TimeSpan? EstimatedDuration, DateOnly? Deadline);

/// <summary>A project-scoped answer with the exact resources it cites.</summary>
public sealed record ProjectAnswerResult(string AnswerText, IReadOnlyList<Resource> Citations);

/// <summary>
/// The AI provider port. UI code never depends on a vendor; version one ships a
/// deterministic local provider, and a network provider can be registered later
/// without touching any workflow.
/// </summary>
public interface IAiProvider
{
    /// <summary>Proposes tasks from a natural-language message.</summary>
    Task<IReadOnlyList<ExtractedTaskDraft>> ExtractTasksAsync(
        string message, AiContext context, CancellationToken cancellationToken = default);

    /// <summary>Suggests duration/deadline metadata for a bare task title.</summary>
    Task<TaskMetadataSuggestion> SuggestMetadataAsync(
        string title, AiContext context, CancellationToken cancellationToken = default);

    /// <summary>Answers a question using only the active project's Files, citing sources.</summary>
    Task<ProjectAnswerResult> AnswerQuestionAsync(
        ProjectId projectId, string question, CancellationToken cancellationToken = default);
}

using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Projects;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Ai;
using BeBoosted.Domain.Projects;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Application.Ai;

/// <summary>Marks AI-derived items Needs review when their sources change or disappear.</summary>
public interface IProvenanceInvalidator
{
    void InvalidateForResource(ResourceId resourceId);
}

/// <summary>How the AI's proposed tasks were handled, per the task-capture permission.</summary>
public sealed record TaskExtractionOutcome(
    IReadOnlyList<ExtractedTaskDraft> Drafts,
    bool AddedAutomatically,
    IReadOnlyList<TaskItem> AddedTasks);

/// <summary>A persisted project answer plus its exact citations.</summary>
public sealed record ProjectAnswerOutcome(
    AiAnswer Answer,
    IReadOnlyList<Resource> Citations,
    bool NeedsReview);

/// <summary>Ties a resource back to the AI items derived from it (the provenance panel).</summary>
public sealed record ResourceDerivation(string Kind, string Title, bool NeedsReview);

/// <summary>
/// AI use cases on top of the provider port: reviewed (or auto-labeled) task capture,
/// project-scoped Q&amp;A with citations, and provenance bookkeeping/invalidation.
/// </summary>
public sealed class AiService(
    IAiProvider provider,
    IAiProvenanceRepository provenance,
    ITaskRepository tasks,
    AiPermissionSettings permissions,
    IClock clock) : IProvenanceInvalidator
{
    public async Task<TaskExtractionOutcome> ExtractTasksAsync(
        string message, AiContext context, CancellationToken cancellationToken = default)
    {
        var drafts = await provider.ExtractTasksAsync(message, context, cancellationToken);
        if (drafts.Count == 0 || permissions.TaskCapture == TaskCapturePermission.ReviewBeforeAdding)
        {
            return new TaskExtractionOutcome(drafts, AddedAutomatically: false, []);
        }

        // Auto-add is allowed, but every task keeps its AI origin and provenance.
        var added = AcceptDrafts(drafts);
        return new TaskExtractionOutcome(drafts, AddedAutomatically: true, added);
    }

    /// <summary>Creates reviewed (or auto-added) tasks with AI origin and shared provenance.</summary>
    public IReadOnlyList<TaskItem> AcceptDrafts(IReadOnlyList<ExtractedTaskDraft> drafts)
    {
        if (drafts.Count == 0)
        {
            return [];
        }

        var record = AiProvenance.Create(AiOperationKind.TaskExtraction, [], clock.Now);
        provenance.Add(record);

        var added = new List<TaskItem>(drafts.Count);
        foreach (var draft in drafts)
        {
            var task = TaskItem.Create(
                draft.Title,
                clock.Now,
                TaskOrigin.Ai,
                estimatedDuration: draft.EstimatedDuration,
                deadline: draft.Deadline,
                projectId: draft.ProjectId,
                provenanceId: record.Id);
            tasks.Add(task);
            added.Add(task);
        }

        return added;
    }

    public async Task<ProjectAnswerOutcome> AskProjectAsync(
        ProjectId projectId, string question, CancellationToken cancellationToken = default)
    {
        var result = await provider.AnswerQuestionAsync(projectId, question, cancellationToken);
        var record = AiProvenance.Create(
            AiOperationKind.ProjectAnswer, result.Citations.Select(c => c.Id), clock.Now);
        provenance.Add(record);
        var answer = new AiAnswer(
            Guid.NewGuid(), record.Id, projectId, question.Trim(), result.AnswerText, clock.Now);
        provenance.AddAnswer(answer);
        return new ProjectAnswerOutcome(answer, result.Citations, NeedsReview: false);
    }

    /// <summary>A source changed or was removed: everything derived from it needs review.</summary>
    public void InvalidateForResource(ResourceId resourceId)
    {
        foreach (var record in provenance.GetBySourceResource(resourceId))
        {
            if (!record.NeedsReview)
            {
                record.MarkNeedsReview();
                provenance.Update(record);
            }
        }
    }

    /// <summary>Whether a task's provenance is flagged Needs review.</summary>
    public bool TaskNeedsReview(TaskItem task)
        => task.ProvenanceId is { } id && provenance.GetById(id)?.NeedsReview == true;

    /// <summary>Tasks and answers derived from the given resource, for the File provenance panel.</summary>
    public IReadOnlyList<ResourceDerivation> GetDerivations(ResourceId resourceId)
    {
        var derivations = new List<ResourceDerivation>();
        foreach (var record in provenance.GetBySourceResource(resourceId))
        {
            if (provenance.GetAnswerByProvenance(record.Id) is { } answer)
            {
                derivations.Add(new ResourceDerivation(
                    "CHAT", $"Cited in “{answer.Question}”", record.NeedsReview));
            }

            foreach (var task in tasks.GetAll().Where(t => t.ProvenanceId == record.Id))
            {
                derivations.Add(new ResourceDerivation(
                    "TASK", $"Used by “{task.Title}”", record.NeedsReview));
            }
        }

        return derivations;
    }
}

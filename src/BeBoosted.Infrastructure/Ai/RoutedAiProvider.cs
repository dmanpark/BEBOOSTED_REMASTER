using BeBoosted.Application.Ai;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Infrastructure.Ai;

/// <summary>
/// The registered <see cref="IAiProvider"/>. It reads the configured source per call, so
/// changing it in Settings takes effect on the next capture with no restart, and it is
/// the single place fallback lives: every failure a backend can have — no key, offline,
/// timeout, refused, unparseable — is the same event to the user, so all of them land
/// here and produce one plain sentence. A user-cancelled request is not a backend
/// failure, so <see cref="OperationCanceledException"/> is deliberately excluded from
/// every catch below and left to propagate.
///
/// Project Q&amp;A is delegated to the heuristic unconditionally: answering well needs
/// resource content that is not indexed yet (only titles and filenames are), and a
/// confident wrong answer over a title-only index would be worse than the keyword
/// answer it replaces. That is a scope boundary, not an oversight.
/// </summary>
public sealed class RoutedAiProvider(
    IAiProvider heuristic,
    Func<CaptureModelSource, ICaptureModel?> backends,
    CaptureModelSettings settings,
    IProjectRepository projects) : IAiProvider
{
    public async Task<CaptureExtractionResult> ExtractTasksAsync(
        string message, AiContext context, CancellationToken cancellationToken = default)
    {
        var source = settings.Source;
        if (source == CaptureModelSource.Heuristic || backends(source) is not { } model)
        {
            return source == CaptureModelSource.Heuristic
                ? await heuristic.ExtractTasksAsync(message, context, cancellationToken)
                : await DegradeAsync(source, message, context, cancellationToken);
        }

        var known = projects.GetAll();
        try
        {
            var drafts = await model.ExtractAsync(
                new CaptureRequest(message, [.. known.Select(p => p.Name)], context.Today),
                cancellationToken);
            return new CaptureExtractionResult([.. drafts.Select(draft => ToDraft(draft, known, context))]);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return await DegradeAsync(source, message, context, cancellationToken);
        }
    }

    public async Task<TaskMetadataSuggestion> SuggestMetadataAsync(
        string title, AiContext context, CancellationToken cancellationToken = default)
    {
        var source = settings.Source;
        if (source != CaptureModelSource.Heuristic && backends(source) is { } model)
        {
            try
            {
                var draft = await model.SuggestMetadataAsync(
                    new CaptureRequest(title, [], context.Today), cancellationToken);
                if (draft is not null)
                {
                    return new TaskMetadataSuggestion(
                        draft.EstimatedMinutes is { } minutes
                            ? TimeSpan.FromMinutes(minutes)
                            : null,
                        draft.Deadline);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Silent by design: this fills an optional hint field, and a notice
                // about a missing duration estimate would be noise.
            }
        }

        return await heuristic.SuggestMetadataAsync(title, context, cancellationToken);
    }

    public Task<ProjectAnswerResult> AnswerQuestionAsync(
        ProjectId projectId, string question, CancellationToken cancellationToken = default)
        => heuristic.AnswerQuestionAsync(projectId, question, cancellationToken);

    private async Task<CaptureExtractionResult> DegradeAsync(
        CaptureModelSource source, string message, AiContext context,
        CancellationToken cancellationToken)
    {
        var fallback = await heuristic.ExtractTasksAsync(message, context, cancellationToken);
        return fallback with { DegradedNotice = NoticeFor(source) };
    }

    /// <summary>Chat copy, never a log line: it names the source and nothing else.</summary>
    private static string NoticeFor(CaptureModelSource source) => source switch
    {
        CaptureModelSource.Claude => "Parsed locally — Claude couldn't be reached.",
        CaptureModelSource.Ollama => "Parsed locally — Ollama couldn't be reached.",
        _ => "Parsed locally.",
    };

    private static ExtractedTaskDraft ToDraft(
        CaptureDraft draft, IReadOnlyList<Project> known, AiContext context)
    {
        // A name the model invented resolves to no project rather than a guess.
        var project = draft.ProjectName is { } name
            ? known.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            : null;

        return new ExtractedTaskDraft(
            draft.Title,
            draft.EstimatedMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
            draft.Deadline,
            project?.Id ?? context.ActiveProjectId,
            "from your message");
    }
}

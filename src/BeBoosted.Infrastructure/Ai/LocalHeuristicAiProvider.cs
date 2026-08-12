using System.Globalization;
using System.Text.RegularExpressions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;

namespace BeBoosted.Infrastructure.Ai;

/// <summary>
/// The deterministic local AI provider: rule-based sentence splitting, duration/deadline
/// parsing, project-name matching, and keyword retrieval over the project-scoped index.
/// Exercises every AI workflow without any network or API key; a real provider can be
/// registered behind the same <see cref="IAiProvider"/> port later.
/// </summary>
public sealed partial class LocalHeuristicAiProvider(
    IResourceRepository resources,
    IProjectRepository projects) : IAiProvider
{
    private static readonly string[] Stopwords =
    [
        "the", "and", "for", "that", "this", "with", "from", "what", "when", "where",
        "which", "should", "would", "could", "about", "have", "does", "will", "them",
        "your", "mine", "into", "than", "then", "week", "today",
    ];

    public Task<IReadOnlyList<ExtractedTaskDraft>> ExtractTasksAsync(
        string message, AiContext context, CancellationToken cancellationToken = default)
    {
        var drafts = new List<ExtractedTaskDraft>();
        var knownProjects = projects.GetAll();
        foreach (var sentence in SplitSentences(message))
        {
            var deadline = ParseDeadline(sentence, context.Today);
            var duration = ParseDuration(sentence);
            var title = CleanTitle(sentence);
            if (title.Length < 3)
            {
                continue;
            }

            ProjectId? projectId = context.ActiveProjectId;
            foreach (var project in knownProjects)
            {
                if (sentence.Contains(project.Name, StringComparison.OrdinalIgnoreCase))
                {
                    projectId = project.Id;
                    break;
                }
            }

            duration ??= DefaultDurationFor(title);
            drafts.Add(new ExtractedTaskDraft(title, duration, deadline, projectId, "from your message"));
        }

        return Task.FromResult<IReadOnlyList<ExtractedTaskDraft>>(drafts);
    }

    public Task<TaskMetadataSuggestion> SuggestMetadataAsync(
        string title, AiContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(new TaskMetadataSuggestion(
            ParseDuration(title) ?? DefaultDurationFor(title),
            ParseDeadline(title, context.Today)));

    public Task<ProjectAnswerResult> AnswerQuestionAsync(
        ProjectId projectId, string question, CancellationToken cancellationToken = default)
    {
        var terms = Regex.Matches(question.ToLowerInvariant(), "[a-z]{4,}")
            .Select(match => match.Value)
            .Where(word => !Stopwords.Contains(word))
            .Distinct()
            .ToList();

        var scores = new Dictionary<ResourceId, (Resource Resource, int Score)>();
        foreach (var term in terms)
        {
            foreach (var resource in resources.SearchInProject(projectId, term))
            {
                scores[resource.Id] = scores.TryGetValue(resource.Id, out var existing)
                    ? (existing.Resource, existing.Score + 1)
                    : (resource, 1);
            }
        }

        var cited = scores.Values
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Resource.AddedAt)
            .Take(3)
            .Select(entry => entry.Resource)
            .ToList();

        if (cited.Count == 0)
        {
            return Task.FromResult(new ProjectAnswerResult(
                "I couldn't find anything about that in this project's Files. "
                + "Add a document, link, or note and ask again.",
                []));
        }

        var lines = cited.Select(resource => $"• {Describe(resource)}");
        var answer = "Here's what this project's Files say:\n" + string.Join("\n", lines);
        return Task.FromResult(new ProjectAnswerResult(answer, cited));
    }

    private static string Describe(Resource resource) => resource.Kind switch
    {
        ResourceKind.Note => $"{resource.Title} — {Snippet(resource.Content)}",
        ResourceKind.Link => $"{resource.Title} ({resource.Url})",
        _ => $"{resource.Title} ({resource.OriginalFileName})",
    };

    private static string Snippet(string? content)
    {
        var text = (content ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return text.Length <= 140 ? text : text[..140] + "…";
    }

    internal static IEnumerable<string> SplitSentences(string message)
        => Regex.Split(
                message,
                @"(?<!\b(?:mr|ms|mrs|dr|st|vs)[.!?])(?<=[.!?])\s+|;\s*|\balso,?\s+",
                RegexOptions.IgnoreCase)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0);

    internal static string CleanTitle(string sentence)
    {
        var title = sentence.Trim().TrimEnd('.', '!', '?');
        title = Regex.Replace(
            title,
            @"^(i\s+(need|have|want|still\s+(need|have|owe))\s+to\s+|i\s+still\s+owe\s+|please\s+|remember\s+to\s+|don't\s+forget\s+to\s+)",
            string.Empty,
            RegexOptions.IgnoreCase);

        // Trailing qualifiers the metadata already captures.
        title = Regex.Replace(
            title,
            @"\s+(before|by|until)\s+(today|tomorrow|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s+it probably needs.*$", string.Empty, RegexOptions.IgnoreCase);
        title = title.Trim();
        return title.Length == 0
            ? title
            : char.ToUpperInvariant(title[0]) + title[1..];
    }

    internal static TimeSpan? ParseDuration(string text)
    {
        var minutes = Regex.Match(text, @"(\d+)\s*(?:min|minute)", RegexOptions.IgnoreCase);
        if (minutes.Success)
        {
            return TimeSpan.FromMinutes(int.Parse(minutes.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        var hours = Regex.Match(text, @"(\d+)\s*(?:h\b|hour)", RegexOptions.IgnoreCase);
        if (hours.Success)
        {
            return TimeSpan.FromHours(int.Parse(hours.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        var sessions = Regex.Match(
            text, @"\b(two|three|2|3)\s+(?:focused\s+)?sessions?\b", RegexOptions.IgnoreCase);
        if (sessions.Success)
        {
            var count = sessions.Groups[1].Value.ToLowerInvariant() switch
            {
                "two" or "2" => 2,
                _ => 3,
            };
            return TimeSpan.FromMinutes(90 * count);
        }

        return null;
    }

    internal static DateOnly? ParseDeadline(string text, DateOnly today)
    {
        if (Regex.IsMatch(text, @"\btoday\b", RegexOptions.IgnoreCase))
        {
            return today;
        }

        if (Regex.IsMatch(text, @"\btomorrow\b", RegexOptions.IgnoreCase))
        {
            return today.AddDays(1);
        }

        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            if (Regex.IsMatch(text, $@"\b{day}\b", RegexOptions.IgnoreCase))
            {
                var offset = ((int)day - (int)today.DayOfWeek + 7) % 7;
                return today.AddDays(offset == 0 ? 7 : offset);
            }
        }

        return null;
    }

    internal static TimeSpan DefaultDurationFor(string title)
    {
        var lowered = title.ToLowerInvariant();
        if (lowered.Contains("email") || lowered.Contains("call") || lowered.Contains("text "))
        {
            return TimeSpan.FromMinutes(10);
        }

        if (lowered.Contains("review") || lowered.Contains("read"))
        {
            return TimeSpan.FromMinutes(45);
        }

        if (lowered.Contains("draft") || lowered.Contains("write") || lowered.Contains("update"))
        {
            return TimeSpan.FromMinutes(60);
        }

        if (lowered.Contains("practice") || lowered.Contains("finish") || lowered.Contains("prepare"))
        {
            return TimeSpan.FromMinutes(90);
        }

        return TimeSpan.FromMinutes(30);
    }
}

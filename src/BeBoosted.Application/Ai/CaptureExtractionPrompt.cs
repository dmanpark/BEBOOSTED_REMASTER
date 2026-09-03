using System.Globalization;
using System.Text;

namespace BeBoosted.Application.Ai;

/// <summary>
/// The one extraction prompt, shared by every backend so their outputs are comparable
/// and one parser can read them both.
/// </summary>
public static class CaptureExtractionPrompt
{
    public const string System = """
        You turn a person's message into task drafts for a calendar planner.

        Reply with JSON only, in this exact shape:
        {"tasks": [{"title": "...", "estimated_minutes": 30, "deadline": "2026-09-04", "project": "..."}]}

        Rules:
        - A sentence that elaborates on the task before it is NOT a new task. "It probably
          needs two focused sessions" describes the previous task; fold that information
          into it rather than creating a task from the fragment.
        - Titles are short and imperative ("Finish DECA presentation"), never a copied
          sentence.
        - Do not invent deadlines, durations, or projects. Omit a field the message does
          not support.
        - "project" must be exactly one of the supplied project names, or omitted.
        - estimated_minutes is a whole number of minutes.
        - deadline is YYYY-MM-DD, resolved against the supplied today's date.
        - A message with no task in it returns {"tasks": []}.
        """;

    public const string MetadataSystem = """
        You estimate scheduling metadata for one task title.

        Reply with JSON only: {"tasks": [{"title": "...", "estimated_minutes": 30, "deadline": "2026-09-04"}]}

        Rules:
        - Return exactly one entry, echoing the title you were given.
        - Do not invent a deadline the title does not imply; omit it instead.
        - estimated_minutes is a whole number of minutes.
        - deadline is YYYY-MM-DD, resolved against the supplied today's date.
        """;

    public static string BuildUser(CaptureRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("Today is ")
            .Append(request.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .AppendLine(".");
        builder.Append("Existing projects: ")
            .AppendLine(request.ProjectNames.Count == 0
                ? "none"
                : string.Join(", ", request.ProjectNames));
        builder.AppendLine().AppendLine("Message:").Append(request.Message);
        return builder.ToString();
    }
}

using System.Globalization;
using System.Text.Json;

namespace BeBoosted.Application.Ai;

/// <summary>
/// Reads a model's JSON into <see cref="CaptureDraft"/> values, bounding every field.
/// This is the only thing between a model's output and the app's data, so nothing here
/// trusts the backend: a malformed entry is skipped, an out-of-range value is dropped,
/// and a payload that is not the agreed shape throws so the router can fall back.
/// </summary>
public static class CaptureDraftParser
{
    /// <summary>
    /// A task's title is a calendar row label, not prose. The parser is the only validation
    /// boundary between model and app, so this cap prevents unchecked model output from claiming
    /// unbounded space in the UI.
    /// </summary>
    private const int MaxTitleLength = 200;

    /// <summary>
    /// A duration of zero or less is meaningless — a model claiming a negative session would be
    /// a parse error, not a plan detail. The parser is the only validation boundary, so the floor
    /// is set here rather than trusted to downstream consumers.
    /// </summary>
    private const int MinMinutes = 1;

    /// <summary>
    /// A session longer than 24 hours is a model hallucination, not a planning detail.
    /// A 1440-minute (24-hour) cap lets the parser refuse to store durations beyond a single day
    /// at the point where the model's JSON is first trusted.
    /// </summary>
    private const int MaxMinutes = 24 * 60;

    public static IReadOnlyList<CaptureDraft> Parse(string json)
    {
        using var document = ParseDocument(Unfence(json));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("tasks", out var tasks)
            || tasks.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The model's reply had no \"tasks\" array.");
        }

        var drafts = new List<CaptureDraft>();
        foreach (var entry in tasks.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || ReadTitle(entry) is not { } title)
            {
                continue; // one bad entry never fails the batch
            }

            drafts.Add(new CaptureDraft(
                title, ReadMinutes(entry), ReadDeadline(entry), ReadProject(entry)));
        }

        return drafts;
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new FormatException("The model's reply was not JSON.", error);
        }
    }

    /// <summary>
    /// Small local models routinely wrap JSON in prose or a code fence even when asked
    /// not to. Taking the outermost braces costs nothing and saves those replies.
    /// </summary>
    private static string Unfence(string json)
    {
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        return start >= 0 && end > start ? json[start..(end + 1)] : json;
    }

    private static string? ReadTitle(JsonElement entry)
    {
        if (!entry.TryGetProperty("title", out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString()?.Trim() is not { Length: > 0 } title)
        {
            return null;
        }

        return title.Length > MaxTitleLength ? title[..MaxTitleLength].TrimEnd() : title;
    }

    private static int? ReadMinutes(JsonElement entry)
    {
        if (!entry.TryGetProperty("estimated_minutes", out var value))
        {
            return null;
        }

        var minutes = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => (int?)null,
        };

        return minutes is >= MinMinutes and <= MaxMinutes ? minutes : null;
    }

    private static DateOnly? ReadDeadline(JsonElement entry)
        => entry.TryGetProperty("deadline", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateOnly.TryParse(
                value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var deadline)
            ? deadline
            : null;

    private static string? ReadProject(JsonElement entry)
        => entry.TryGetProperty("project", out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString()?.Trim() is { Length: > 0 } name
            ? name
            : null;
}

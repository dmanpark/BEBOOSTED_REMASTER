using System.Globalization;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>
/// One schedule row of a task, ready for both editors: the whole-task list and
/// the session editor's position label derive from the same data.
/// </summary>
public sealed record SessionRowData(
    CalendarBlockId Id,
    bool IsRepeating,
    string PrimaryText,
    string SecondaryText,
    string? PositionText,
    string? StatusChip,
    string AccessibleName,
    string EditControlName,
    string RemoveControlName);

/// <summary>
/// The pure schedule-list builder both editors share. Rows order by
/// (date, start, created, id); "SESSION X OF N" counts one-off blocks only,
/// resolved history included — a repeating schedule is its own kind of row and
/// never takes a number.
/// </summary>
public static class SessionListBuilder
{
    public static IReadOnlyList<SessionRowData> Build(IReadOnlyList<CalendarBlock> sessions)
    {
        var ordered = Order(sessions);
        var oneOffCount = ordered.Count(s => s.Recurrence is null);
        var rows = new List<SessionRowData>(ordered.Count);
        var position = 0;
        foreach (var session in ordered)
        {
            if (session.Recurrence is null)
            {
                position++;
                rows.Add(OneOffRow(session, position, oneOffCount));
            }
            else
            {
                rows.Add(RepeatingRow(session));
            }
        }

        return rows;
    }

    /// <summary>Position of one block among the one-offs; (0, count) for a repeating block.</summary>
    public static (int Position, int OneOffCount) PositionOf(
        IReadOnlyList<CalendarBlock> sessions, CalendarBlockId id)
    {
        var ordered = Order(sessions);
        var oneOffCount = ordered.Count(s => s.Recurrence is null);
        var position = 0;
        foreach (var session in ordered)
        {
            if (session.Recurrence is not null)
            {
                if (session.Id == id)
                {
                    return (0, oneOffCount);
                }

                continue;
            }

            position++;
            if (session.Id == id)
            {
                return (position, oneOffCount);
            }
        }

        return (0, oneOffCount);
    }

    /// <summary>The SCHEDULE section's meta line: counts, total duration, or "all done".</summary>
    public static string SummaryFor(IReadOnlyList<CalendarBlock> sessions)
    {
        var oneOffs = sessions.Where(s => s.Recurrence is null).ToList();
        var repeatingCount = sessions.Count - oneOffs.Count;
        if (oneOffs.Count == 0 && repeatingCount == 0)
        {
            return "0 sessions";
        }

        if (repeatingCount == 0)
        {
            var noun = oneOffs.Count == 1 ? "session" : "sessions";
            if (oneOffs.All(s => s.Outcome == BlockOutcome.Done))
            {
                return $"{oneOffs.Count} {noun} · all done";
            }

            var total = TimeSpan.FromTicks(oneOffs.Sum(s => s.Duration.Ticks));
            return $"{oneOffs.Count} {noun} · {TaskRowViewModel.FormatDuration(total)}";
        }

        if (oneOffs.Count == 0)
        {
            return repeatingCount == 1
                ? $"repeating · {TaskRowViewModel.FormatDuration(sessions[0].Duration)}"
                : $"{repeatingCount} repeating";
        }

        var repeatingText = repeatingCount == 1 ? "repeating" : $"{repeatingCount} repeating";
        return $"{oneOffs.Count} one-off · {repeatingText}";
    }

    private static List<CalendarBlock> Order(IReadOnlyList<CalendarBlock> sessions)
        => [.. sessions
            .OrderBy(s => s.Date)
            .ThenBy(s => s.StartTime)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.Id.ToString(), StringComparer.Ordinal)];

    private static SessionRowData OneOffRow(CalendarBlock session, int position, int count)
    {
        var positionText = string.Create(
            CultureInfo.InvariantCulture, $"SESSION {position} OF {count}");
        var accessible = string.Create(
            CultureInfo.InvariantCulture,
            $"Session {position} of {count} — {session.Date:dddd, MMMM d}, "
            + $"{Time(session.StartTime)} to {Time(session.EndTime)}{OutcomeSuffix(session.Outcome)}");
        return new SessionRowData(
            session.Id,
            IsRepeating: false,
            session.Date.ToString("ddd, MMM d", CultureInfo.InvariantCulture),
            SecondaryTextFor(session),
            positionText,
            ChipFor(session.Outcome),
            accessible,
            string.Create(CultureInfo.InvariantCulture, $"Edit session {position} of {count}"),
            string.Create(CultureInfo.InvariantCulture, $"Remove session {position} of {count}"));
    }

    private static SessionRowData RepeatingRow(CalendarBlock session)
    {
        var days = MondayFirst(session.Recurrence!.DaysOfWeek);
        var accessible = string.Create(
            CultureInfo.InvariantCulture,
            $"Repeating schedule — {string.Join(", ", days.Select(d => d.ToString()))}, "
            + $"{Time(session.StartTime)} to {Time(session.EndTime)}");
        return new SessionRowData(
            session.Id,
            IsRepeating: true,
            string.Join(" · ", days.Select(Abbreviate)),
            SecondaryTextFor(session),
            PositionText: null,
            StatusChip: null,
            accessible,
            "Edit repeating schedule",
            "Remove repeating schedule");
    }

    private static string SecondaryTextFor(CalendarBlock session)
        => $"{TimeRange(session.StartTime, session.EndTime)} · "
            + TaskRowViewModel.FormatDuration(session.Duration);

    /// <summary>"9:00 – 10:00 AM"; the start keeps its own meridiem only when it differs.</summary>
    internal static string TimeRange(TimeOnly start, TimeOnly end)
    {
        var sameMeridiem = start.Hour < 12 == end.Hour < 12;
        var startText = start.ToString(sameMeridiem ? "h:mm" : "h:mm tt", CultureInfo.InvariantCulture);
        return $"{startText} – {Time(end)}";
    }

    private static string Time(TimeOnly time) => time.ToString("h:mm tt", CultureInfo.InvariantCulture);

    private static string? ChipFor(BlockOutcome outcome) => outcome switch
    {
        BlockOutcome.Done => "DONE",
        BlockOutcome.NeedsMoreTime => "NEEDS MORE TIME",
        BlockOutcome.DidntHappen => "DIDN'T HAPPEN",
        _ => null,
    };

    private static string OutcomeSuffix(BlockOutcome outcome) => outcome switch
    {
        BlockOutcome.Done => ", done",
        BlockOutcome.NeedsMoreTime => ", needs more time",
        BlockOutcome.DidntHappen => ", didn't happen",
        _ => string.Empty,
    };

    private static List<DayOfWeek> MondayFirst(IEnumerable<DayOfWeek> days)
        => [.. days.OrderBy(d => ((int)d + 6) % 7)];

    private static string Abbreviate(DayOfWeek day)
        => day.ToString()[..3];
}

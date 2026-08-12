using System.Globalization;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Tasks;

namespace BeBoosted.Domain.Planning;

/// <summary>A task the scheduler could not place, with a user-readable reason.</summary>
public sealed record UnplacedTask(TaskId TaskId, string Title, string Reason);

public sealed record SchedulerResult(IReadOnlyList<ProposedBlock> Blocks, IReadOnlyList<UnplacedTask> Unplaced);

/// <summary>
/// Deterministic first-fit planner. Subjective importance comes from Priority Sort
/// ranks; deadlines, durations, fixed commitments, and open time decide feasibility.
/// Long tasks split into sessions of at most 90 minutes. The planner never overlaps
/// fixed events or other proposals, and produces user-relevant Why evidence per block.
/// </summary>
public static class DeterministicScheduler
{
    public static readonly TimeOnly WindowStart = new(8, 0);
    public static readonly TimeOnly WindowEnd = new(21, 0);
    public static readonly TimeSpan MaxSession = TimeSpan.FromMinutes(90);
    public static readonly TimeSpan MinSession = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(30);
    private const int SnapMinutes = 15;

    public static SchedulerResult Plan(
        IReadOnlyList<TaskItem> candidates,
        IReadOnlyList<BlockOccurrence> existing,
        IReadOnlyDictionary<TaskId, PriorityRank> ranks,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly today,
        TimeOnly now)
    {
        // Rank first (subjective priority), then deadline, then capture order — deterministic.
        var ordered = candidates
            .OrderBy(t => ranks.TryGetValue(t.Id, out var rank) ? rank.Rank : int.MaxValue)
            .ThenBy(t => t.Deadline ?? DateOnly.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        var busy = new Dictionary<DateOnly, List<(TimeOnly Start, TimeOnly End)>>();
        foreach (var occurrence in existing)
        {
            Busy(busy, occurrence.Date).Add((occurrence.StartTime, occurrence.EndTime));
        }

        // Tied tasks share a rank, so group before keying.
        var titleByRank = ordered
            .Where(t => ranks.ContainsKey(t.Id))
            .GroupBy(t => ranks[t.Id].Rank)
            .ToDictionary(g => g.Key, g => g.First().Title);

        var blocks = new List<ProposedBlock>();
        var unplaced = new List<UnplacedTask>();

        foreach (var task in ordered)
        {
            var sessions = SplitIntoSessions(task.EstimatedDuration ?? DefaultDuration);
            var placed = new List<ProposedBlock>();
            var fits = true;

            foreach (var session in sessions)
            {
                var slot = FindFirstSlot(task, session, busy, periodStart, periodEnd, today, now);
                if (slot is not { } found)
                {
                    fits = false;
                    break;
                }

                Busy(busy, found.Date).Add((found.Start, found.Start.Add(session)));
                placed.Add(new ProposedBlock(
                    CalendarBlockId.New(),
                    task.Id,
                    found.Date,
                    found.Start,
                    found.Start.Add(session),
                    BuildWhy(task, sessions.Count, ranks, titleByRank),
                    sessions.Count > 1
                        ? string.Create(CultureInfo.InvariantCulture, $"Session {placed.Count + 1} of {sessions.Count}")
                        : null));
            }

            if (fits)
            {
                blocks.AddRange(placed);
            }
            else
            {
                // Roll back partial sessions so shorter, lower-ranked work can use the space.
                foreach (var partial in placed)
                {
                    Busy(busy, partial.Date).Remove((partial.StartTime, partial.EndTime));
                }

                unplaced.Add(new UnplacedTask(task.Id, task.Title, UnplacedReason(task)));
            }
        }

        return new SchedulerResult(blocks, unplaced);
    }

    private static List<(TimeOnly Start, TimeOnly End)> Busy(
        Dictionary<DateOnly, List<(TimeOnly, TimeOnly)>> busy, DateOnly date)
    {
        if (!busy.TryGetValue(date, out var list))
        {
            busy[date] = list = [];
        }

        return list;
    }

    internal static List<TimeSpan> SplitIntoSessions(TimeSpan total)
    {
        var sessions = new List<TimeSpan>();
        var remaining = total;
        while (remaining > MaxSession)
        {
            sessions.Add(MaxSession);
            remaining -= MaxSession;
        }

        if (remaining >= MinSession)
        {
            sessions.Add(remaining);
        }
        else if (sessions.Count > 0)
        {
            sessions[^1] += remaining;
        }
        else
        {
            sessions.Add(MinSession);
        }

        return sessions;
    }

    private static (DateOnly Date, TimeOnly Start)? FindFirstSlot(
        TaskItem task,
        TimeSpan duration,
        Dictionary<DateOnly, List<(TimeOnly Start, TimeOnly End)>> busy,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly today,
        TimeOnly now)
    {
        var firstDay = periodStart < today ? today : periodStart;
        if (task.Constraints?.NotBefore is { } notBefore && notBefore > firstDay)
        {
            firstDay = notBefore;
        }

        var lastDay = task.Deadline is { } deadline && deadline < periodEnd ? deadline : periodEnd;

        for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
        {
            var windowStart = task.Constraints?.EarliestTime is { } earliest && earliest > WindowStart
                ? earliest
                : WindowStart;
            var windowEnd = task.Constraints?.LatestTime is { } latest && latest < WindowEnd
                ? latest
                : WindowEnd;
            if (date == today && now > windowStart)
            {
                windowStart = Snap(now);
            }

            if (windowStart >= windowEnd)
            {
                continue;
            }

            var occupied = busy.TryGetValue(date, out var list)
                ? list.OrderBy(b => b.Start).ToList()
                : [];

            var cursor = windowStart;
            foreach (var (busyStart, busyEnd) in occupied)
            {
                if (busyEnd <= cursor)
                {
                    continue;
                }

                if (busyStart >= windowEnd)
                {
                    break;
                }

                if (busyStart > cursor && busyStart - cursor >= duration)
                {
                    return (date, cursor);
                }

                if (busyEnd > cursor)
                {
                    cursor = Snap(busyEnd);
                }
            }

            if (cursor < windowEnd && windowEnd - cursor >= duration)
            {
                return (date, cursor);
            }
        }

        return null;
    }

    private static TimeOnly Snap(TimeOnly time)
    {
        var minutes = (int)Math.Ceiling(time.ToTimeSpan().TotalMinutes / SnapMinutes) * SnapMinutes;
        return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Min(minutes, (24 * 60) - 1)));
    }

    private static WhyEvidence BuildWhy(
        TaskItem task,
        int sessionCount,
        IReadOnlyDictionary<TaskId, PriorityRank> ranks,
        IReadOnlyDictionary<int, string> titleByRank)
    {
        var duration = task.EstimatedDuration ?? DefaultDuration;
        var durationText = sessionCount > 1
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{sessionCount} sessions · {(int)duration.TotalMinutes} min total")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes} min");

        string? priorityText = null;
        if (ranks.TryGetValue(task.Id, out var rank))
        {
            var below = titleByRank.TryGetValue(rank.Rank + 1, out var nextTitle)
                ? $" · above {nextTitle}"
                : string.Empty;
            priorityText = string.Create(CultureInfo.InvariantCulture, $"Rank #{rank.Rank}{below}");
        }

        return new WhyEvidence(
            task.Deadline?.ToString("dddd, MMM d", CultureInfo.CurrentCulture),
            durationText,
            priorityText,
            "First uninterrupted open block",
            Source: null);
    }

    private static string UnplacedReason(TaskItem task)
    {
        var minutes = (int)(task.EstimatedDuration ?? DefaultDuration).TotalMinutes;
        return task.Deadline is { } deadline
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"no open slot before {deadline:ddd} fits {minutes} min")
            : string.Create(CultureInfo.CurrentCulture, $"no open slot fits {minutes} min");
    }
}

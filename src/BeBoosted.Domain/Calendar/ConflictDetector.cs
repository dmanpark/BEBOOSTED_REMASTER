namespace BeBoosted.Domain.Calendar;

/// <summary>
/// An occurrence of a block on a concrete date (recurrence expanded).
/// <paramref name="IsCompleted"/> reflects a repeating session's per-occurrence
/// completion; task blocks track completion through their outcome instead.
/// </summary>
public sealed record BlockOccurrence(CalendarBlock Block, DateOnly Date, bool IsCompleted = false)
{
    public TimeOnly StartTime => Block.StartTime;

    public TimeOnly EndTime => Block.EndTime;
}

/// <summary>A dated interval participating in conflict detection (block or proposal).</summary>
public readonly record struct TimedItem(CalendarBlockId Id, DateOnly Date, TimeOnly StartTime, TimeOnly EndTime);

/// <summary>
/// Finds overlapping approved occurrences. Conflicts are surfaced, never silently
/// resolved — the user moves or changes one of the blocks.
/// </summary>
public static class ConflictDetector
{
    /// <summary>Ids of blocks that overlap another block on the same date.</summary>
    public static IReadOnlySet<CalendarBlockId> FindConflicts(IReadOnlyList<BlockOccurrence> occurrences)
        => FindConflicts(occurrences
            .Select(o => new TimedItem(o.Block.Id, o.Date, o.StartTime, o.EndTime))
            .ToList());

    /// <summary>Ids of items that overlap another item on the same date (blocks and proposals).</summary>
    public static IReadOnlySet<CalendarBlockId> FindConflicts(IReadOnlyList<TimedItem> items)
    {
        var conflicted = new HashSet<CalendarBlockId>();
        foreach (var group in items.GroupBy(o => o.Date))
        {
            var ordered = group.OrderBy(o => o.StartTime).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                for (var j = i + 1; j < ordered.Count; j++)
                {
                    if (ordered[j].StartTime >= ordered[i].EndTime)
                    {
                        break;
                    }

                    conflicted.Add(ordered[i].Id);
                    conflicted.Add(ordered[j].Id);
                }
            }
        }

        return conflicted;
    }
}

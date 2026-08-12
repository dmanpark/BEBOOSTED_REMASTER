namespace BeBoosted.Domain.Calendar;

/// <summary>An occurrence of a block on a concrete date (recurrence expanded).</summary>
public sealed record BlockOccurrence(CalendarBlock Block, DateOnly Date)
{
    public TimeOnly StartTime => Block.StartTime;

    public TimeOnly EndTime => Block.EndTime;
}

/// <summary>
/// Finds overlapping approved/fixed occurrences. Conflicts are surfaced, never silently
/// resolved — the user moves or changes one of the blocks.
/// </summary>
public static class ConflictDetector
{
    /// <summary>Ids of blocks that overlap another block on the same date.</summary>
    public static IReadOnlySet<CalendarBlockId> FindConflicts(IReadOnlyList<BlockOccurrence> occurrences)
    {
        var conflicted = new HashSet<CalendarBlockId>();
        foreach (var group in occurrences.GroupBy(o => o.Date))
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

                    conflicted.Add(ordered[i].Block.Id);
                    conflicted.Add(ordered[j].Block.Id);
                }
            }
        }

        return conflicted;
    }
}

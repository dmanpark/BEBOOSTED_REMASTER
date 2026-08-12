namespace BeBoosted.Desktop.CalendarEngine;

/// <summary>Input interval for overlap layout.</summary>
public readonly record struct LayoutInterval(int Key, TimeOnly Start, TimeOnly End);

/// <summary>Horizontal slot assigned to an interval: column index within a cluster of columns.</summary>
public readonly record struct LayoutSlot(int Column, int ColumnCount);

/// <summary>
/// Assigns side-by-side columns to overlapping intervals. Intervals that touch
/// (end == next start) do not overlap. Deterministic: ties order by start time then key.
/// </summary>
public static class OverlapLayout
{
    public static IReadOnlyDictionary<int, LayoutSlot> Arrange(IReadOnlyList<LayoutInterval> intervals)
    {
        var result = new Dictionary<int, LayoutSlot>(intervals.Count);
        var ordered = intervals.OrderBy(i => i.Start).ThenBy(i => i.End).ThenBy(i => i.Key).ToList();

        // Group into clusters of transitively-overlapping intervals.
        var cluster = new List<LayoutInterval>();
        var clusterEnd = TimeOnly.MinValue;
        foreach (var interval in ordered)
        {
            if (cluster.Count > 0 && interval.Start >= clusterEnd)
            {
                ArrangeCluster(cluster, result);
                cluster.Clear();
            }

            cluster.Add(interval);
            if (interval.End > clusterEnd)
            {
                clusterEnd = interval.End;
            }
        }

        if (cluster.Count > 0)
        {
            ArrangeCluster(cluster, result);
        }

        return result;
    }

    private static void ArrangeCluster(List<LayoutInterval> cluster, Dictionary<int, LayoutSlot> result)
    {
        var columnEnds = new List<TimeOnly>();
        var assigned = new Dictionary<int, int>(cluster.Count);
        foreach (var interval in cluster)
        {
            var column = -1;
            for (var i = 0; i < columnEnds.Count; i++)
            {
                if (interval.Start >= columnEnds[i])
                {
                    column = i;
                    break;
                }
            }

            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(interval.End);
            }
            else
            {
                columnEnds[column] = interval.End;
            }

            assigned[interval.Key] = column;
        }

        var columnCount = columnEnds.Count;
        foreach (var (key, column) in assigned)
        {
            result[key] = new LayoutSlot(column, columnCount);
        }
    }
}

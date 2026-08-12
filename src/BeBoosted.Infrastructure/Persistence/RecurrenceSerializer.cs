using System.Globalization;
using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Infrastructure.Persistence;

/// <summary>
/// Compact single-column recurrence encoding: "D:interval" or "W:interval:MO,TU,…".
/// </summary>
public static class RecurrenceSerializer
{
    private static readonly Dictionary<DayOfWeek, string> DayCodes = new()
    {
        [DayOfWeek.Monday] = "MO",
        [DayOfWeek.Tuesday] = "TU",
        [DayOfWeek.Wednesday] = "WE",
        [DayOfWeek.Thursday] = "TH",
        [DayOfWeek.Friday] = "FR",
        [DayOfWeek.Saturday] = "SA",
        [DayOfWeek.Sunday] = "SU",
    };

    public static string Serialize(RecurrenceRule rule)
    {
        if (rule.Frequency == RecurrenceFrequency.Daily)
        {
            return string.Create(CultureInfo.InvariantCulture, $"D:{rule.Interval}");
        }

        var days = string.Join(',', rule.DaysOfWeek.OrderBy(d => d).Select(d => DayCodes[d]));
        return string.Create(CultureInfo.InvariantCulture, $"W:{rule.Interval}:{days}");
    }

    public static RecurrenceRule Deserialize(string value)
    {
        var parts = value.Split(':');
        var interval = int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (parts[0] == "D")
        {
            return RecurrenceRule.Daily(interval);
        }

        if (parts[0] == "W" && parts.Length == 3)
        {
            var days = parts[2]
                .Split(',')
                .Select(code => DayCodes.First(pair => pair.Value == code).Key)
                .ToArray();
            return RecurrenceRule.Weekly(interval, days);
        }

        throw new FormatException($"Unrecognized recurrence encoding '{value}'.");
    }
}

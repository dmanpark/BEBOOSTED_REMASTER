using System.Globalization;

namespace BeBoosted.Application.Settings;

/// <summary>Persisted main-window geometry, serialized as a single culture-invariant setting value.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool IsMaximized)
{
    public string Serialize()
        => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Width},{Height},{(IsMaximized ? 1 : 0)}");

    public static WindowPlacement? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',');
        if (parts.Length != 5)
        {
            return null;
        }

        var numbers = new int[5];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        if (numbers[2] <= 0 || numbers[3] <= 0)
        {
            return null;
        }

        return new WindowPlacement(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4] == 1);
    }
}

using System.Globalization;
using Avalonia.Input;
using BeBoosted.Domain;

namespace BeBoosted.Desktop.Controls;

/// <summary>Drag-and-drop payload for scheduling a task from the Inbox onto the calendar.</summary>
public static class TaskDragData
{
    public static readonly DataFormat<string> Format =
        DataFormat.CreateStringApplicationFormat("beboosted-task");

    public static string Serialize(TaskId taskId, int durationMinutes)
        => string.Create(CultureInfo.InvariantCulture, $"{taskId}|{durationMinutes}");

    public static bool TryParse(string? text, out TaskId taskId, out int durationMinutes)
    {
        taskId = default;
        durationMinutes = 0;
        if (text is null)
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var guid)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out durationMinutes))
        {
            return false;
        }

        taskId = new TaskId(guid);
        return true;
    }
}

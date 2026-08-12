using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Settings;

namespace BeBoosted.Application.Ai;

public enum TaskCapturePermission
{
    ReviewBeforeAdding = 0,
    AddAutomatically = 1,
}

public enum CalendarPlanningPermission
{
    ReviewEveryPlan = 0,
    ApplyAutomatically = 1,
}

/// <summary>
/// AI permissions are separate by action: letting BeBoosted add tasks never lets it
/// change the calendar. Both default to review-first.
/// </summary>
public sealed class AiPermissionSettings(ISettingsStore store)
{
    public TaskCapturePermission TaskCapture
    {
        get => store.Get(SettingKeys.AiTaskCapture) == "auto"
            ? TaskCapturePermission.AddAutomatically
            : TaskCapturePermission.ReviewBeforeAdding;
        set => store.Set(
            SettingKeys.AiTaskCapture,
            value == TaskCapturePermission.AddAutomatically ? "auto" : "review");
    }

    public CalendarPlanningPermission CalendarPlanning
    {
        get => store.Get(SettingKeys.AiCalendarPlanning) == "auto"
            ? CalendarPlanningPermission.ApplyAutomatically
            : CalendarPlanningPermission.ReviewEveryPlan;
        set => store.Set(
            SettingKeys.AiCalendarPlanning,
            value == CalendarPlanningPermission.ApplyAutomatically ? "auto" : "review");
    }
}

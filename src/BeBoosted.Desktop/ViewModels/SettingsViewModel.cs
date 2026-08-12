using System.Reflection;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;

namespace BeBoosted.Desktop.ViewModels;

public sealed class SettingsViewModel(IAppDataPaths paths, AiPermissionSettings aiPermissions) : ViewModelBase
{
    public string DataDirectory => paths.DataDirectory;

    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // ---- AI permissions (each is separate; capture never grants calendar access) ----

    public bool IsTaskCaptureReview
    {
        get => aiPermissions.TaskCapture == TaskCapturePermission.ReviewBeforeAdding;
        set
        {
            if (value)
            {
                aiPermissions.TaskCapture = TaskCapturePermission.ReviewBeforeAdding;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTaskCaptureAuto));
            }
        }
    }

    public bool IsTaskCaptureAuto
    {
        get => aiPermissions.TaskCapture == TaskCapturePermission.AddAutomatically;
        set
        {
            if (value)
            {
                aiPermissions.TaskCapture = TaskCapturePermission.AddAutomatically;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTaskCaptureReview));
            }
        }
    }

    public bool IsPlanningReview
    {
        get => aiPermissions.CalendarPlanning == CalendarPlanningPermission.ReviewEveryPlan;
        set
        {
            if (value)
            {
                aiPermissions.CalendarPlanning = CalendarPlanningPermission.ReviewEveryPlan;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPlanningAuto));
            }
        }
    }

    public bool IsPlanningAuto
    {
        get => aiPermissions.CalendarPlanning == CalendarPlanningPermission.ApplyAutomatically;
        set
        {
            if (value)
            {
                aiPermissions.CalendarPlanning = CalendarPlanningPermission.ApplyAutomatically;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPlanningReview));
            }
        }
    }
}

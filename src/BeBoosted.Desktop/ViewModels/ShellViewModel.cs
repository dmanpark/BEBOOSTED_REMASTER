using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

public sealed partial class ShellViewModel : ViewModelBase
{
    public ShellViewModel(
        CalendarViewModel calendar,
        InboxViewModel inbox,
        ProjectsViewModel projects,
        SettingsViewModel settings)
    {
        Calendar = calendar;
        Inbox = inbox;
        Projects = projects;
        Settings = settings;
        CurrentSection = calendar;

        // Scheduling and outcomes change what belongs in the Inbox queue.
        Calendar.DataChanged += Inbox.Reload;
    }

    public CalendarViewModel Calendar { get; }

    public InboxViewModel Inbox { get; }

    public ProjectsViewModel Projects { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial ViewModelBase CurrentSection { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalendarActive))]
    [NotifyPropertyChangedFor(nameof(IsProjectsActive))]
    [NotifyPropertyChangedFor(nameof(IsSettingsActive))]
    public partial AppSection ActiveSection { get; private set; } = AppSection.Calendar;

    /// <summary>The Inbox is a drawer over the current calendar surface, never a full page.</summary>
    [ObservableProperty]
    public partial bool IsInboxOpen { get; set; }

    public bool IsCalendarActive => ActiveSection == AppSection.Calendar;

    public bool IsProjectsActive => ActiveSection == AppSection.Projects;

    public bool IsSettingsActive => ActiveSection == AppSection.Settings;

    [RelayCommand]
    private void Navigate(AppSection section)
    {
        ActiveSection = section;
        CurrentSection = section switch
        {
            AppSection.Projects => Projects,
            AppSection.Settings => Settings,
            _ => Calendar,
        };
    }

    [RelayCommand]
    private void ToggleInbox() => IsInboxOpen = !IsInboxOpen;

    [RelayCommand]
    private void CloseInbox() => IsInboxOpen = false;
}

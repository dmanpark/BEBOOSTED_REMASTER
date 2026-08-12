using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Prioritization;
using BeBoosted.Application.Settings;
using BeBoosted.Domain.Prioritization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly PrioritySortService _prioritySort;
    private readonly IClock _clock;

    public ShellViewModel(
        CalendarViewModel calendar,
        InboxViewModel inbox,
        ProjectsViewModel projects,
        SettingsViewModel settings,
        PrioritySortService prioritySort,
        IClock clock)
    {
        Calendar = calendar;
        Inbox = inbox;
        Projects = projects;
        Settings = settings;
        _prioritySort = prioritySort;
        _clock = clock;
        CurrentSection = calendar;

        // Scheduling and outcomes change what belongs in the Inbox queue.
        Calendar.DataChanged += Inbox.Reload;

        Inbox.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InboxViewModel.OpenCount))
            {
                StartPrioritySortCommand.NotifyCanExecuteChanged();
            }
        };
        Calendar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CalendarViewModel.ViewKind))
            {
                RefreshInboxRanks();
            }
        };
        RefreshInboxRanks();
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

    // ---- Priority Sort ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortActive))]
    public partial PrioritySortViewModel? ActiveSort { get; private set; }

    public bool IsSortActive => ActiveSort is not null;

    /// <summary>The period ranks apply to — follows the visible calendar view.</summary>
    public PlanningPeriod CurrentPlanningPeriod => Calendar.ViewKind == CalendarViewKind.Week
        ? PlanningPeriod.ForWeek(_clock.Today)
        : PlanningPeriod.ForToday(_clock.Today);

    public bool CanStartPrioritySort => Inbox.OpenCount >= 2;

    [RelayCommand(CanExecute = nameof(CanStartPrioritySort))]
    private void StartPrioritySort()
    {
        var candidates = Inbox.Tasks.Select(row => row.Task).ToList();
        ActiveSort = new PrioritySortViewModel(
            CurrentPlanningPeriod,
            candidates,
            _prioritySort,
            _clock,
            onClosed: () => ActiveSort = null,
            onSaved: _ => RefreshInboxRanks());
    }

    /// <summary>Escape closes the topmost temporary surface: sort overlay, then drawer.</summary>
    [RelayCommand]
    private void EscapePressed()
    {
        if (ActiveSort is { } sort)
        {
            sort.CloseCommand.Execute(null);
            return;
        }

        IsInboxOpen = false;
    }

    private void RefreshInboxRanks()
        => Inbox.SetRanks(_prioritySort.GetRankLookup(CurrentPlanningPeriod));
}

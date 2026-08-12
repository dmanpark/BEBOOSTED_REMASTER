using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Prioritization;
using BeBoosted.Application.Settings;
using BeBoosted.Domain;
using BeBoosted.Domain.Prioritization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly PrioritySortService _prioritySort;
    private readonly AiPermissionSettings _aiPermissions;
    private readonly IClock _clock;

    public ShellViewModel(
        CalendarViewModel calendar,
        InboxViewModel inbox,
        ProjectsViewModel projects,
        SettingsViewModel settings,
        ChatViewModel chat,
        PrioritySortService prioritySort,
        AiPermissionSettings aiPermissions,
        Platform.IKeymapService keymap,
        IClock clock)
    {
        ComposerShortcutText = keymap.DisplayString(keymap.ComposerGesture);
        Calendar = calendar;
        Inbox = inbox;
        Projects = projects;
        Settings = settings;
        Chat = chat;
        _prioritySort = prioritySort;
        _aiPermissions = aiPermissions;
        _clock = clock;
        CurrentSection = calendar;

        Chat.Attach(
            contextProvider: () => new AiContext(ActiveChatProjectId, _clock.Today),
            projectNameLookup: Projects.GetProjectName,
            requestPlan: PlanFromChat,
            onTasksChanged: () =>
            {
                Inbox.Reload();
                Projects.RefreshActive();
            },
            openResource: OpenResourceInFiles);
        Projects.AskRequested = OpenProjectChat;
        Projects.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProjectsViewModel.Detail) or nameof(ProjectsViewModel.FileDetail))
            {
                RefreshChatScope();
            }
        };

        // Scheduling and outcomes change what belongs in the Inbox queue and project views.
        Calendar.DataChanged += Inbox.Reload;
        Calendar.DataChanged += Projects.RefreshActive;

        Inbox.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InboxViewModel.OpenCount))
            {
                StartPrioritySortCommand.NotifyCanExecuteChanged();
                PlanCommand.NotifyCanExecuteChanged();
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

    public ChatViewModel Chat { get; }

    /// <summary>"Ctrl+J" on Windows, "⌘J" on macOS — shown on the composer chip.</summary>
    public string ComposerShortcutText { get; }

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
        RefreshChatScope();
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

    /// <summary>Escape closes the topmost temporary surface: sort, expanded chat, then drawer.</summary>
    [RelayCommand]
    private void EscapePressed()
    {
        if (ActiveSort is { } sort)
        {
            sort.CloseCommand.Execute(null);
            return;
        }

        if (Chat.IsExpanded)
        {
            Chat.IsExpanded = false;
            return;
        }

        IsInboxOpen = false;
    }

    // ---- Chatbot composer ----

    /// <summary>Raised when the composer input should take keyboard focus (Ctrl+J / ⌘J).</summary>
    public event Action? ComposerFocusRequested;

    [RelayCommand]
    private void FocusComposer() => ComposerFocusRequested?.Invoke();

    /// <summary>"Ask BeBoosted about this project": expands the composer scoped to it.</summary>
    private void OpenProjectChat()
    {
        Chat.IsExpanded = true;
        ComposerFocusRequested?.Invoke();
    }

    private ProjectId? ActiveChatProjectId
        => ActiveSection == AppSection.Projects
            ? Projects.FileDetail?.Project.Id ?? Projects.Detail?.Project.Id
            : null;

    private void RefreshChatScope()
        => Chat.SetScope(Projects.GetProjectName(ActiveChatProjectId));

    private string PlanFromChat(PlanningPeriod period)
    {
        Navigate(AppSection.Calendar);
        Calendar.CreateDraft(period);
        if (!Calendar.HasDraft)
        {
            return "Nothing in your Inbox fits that period right now, so there's nothing to plan.";
        }

        if (_aiPermissions.CalendarPlanning == CalendarPlanningPermission.ApplyAutomatically)
        {
            Calendar.ApproveDraftCommand.Execute(null);
            return "Planned and applied to your calendar — every block stays labeled and undoable (Ctrl+Z).";
        }

        return $"I've drafted a plan on your calendar ({Calendar.DraftSummaryText}). "
            + "Review the lime blocks and approve what fits.";
    }

    private void OpenResourceInFiles(ResourceId resourceId)
    {
        Chat.IsExpanded = false;
        Navigate(AppSection.Projects);
        Projects.ShowResource(resourceId);
    }

    private void RefreshInboxRanks()
        => Inbox.SetRanks(_prioritySort.GetRankLookup(CurrentPlanningPeriod));

    // ---- Plan drafts ----

    public bool CanPlan => Inbox.OpenCount >= 1;

    /// <summary>Builds a plan draft for the visible period and reviews it on the calendar.</summary>
    [RelayCommand(CanExecute = nameof(CanPlan))]
    private void Plan()
    {
        Calendar.CreateDraft(CurrentPlanningPeriod);
        IsInboxOpen = false;
    }

    /// <summary>Ctrl+Z / ⌘Z: undoes the most recent plan approval.</summary>
    [RelayCommand]
    private void UndoApproval() => Calendar.UndoLastApproval();
}

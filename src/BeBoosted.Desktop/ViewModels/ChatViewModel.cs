using System.Collections.ObjectModel;
using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Domain;
using BeBoosted.Domain.Prioritization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>One proposed task row in the review list (title editable in place).</summary>
public sealed partial class DraftRowViewModel : ViewModelBase
{
    private readonly ChatReviewItemViewModel _owner;

    public DraftRowViewModel(ChatReviewItemViewModel owner, ExtractedTaskDraft draft, string? projectName)
    {
        _owner = owner;
        Draft = draft;
        EditTitle = draft.Title;
        var parts = new List<string>(4);
        if (projectName is not null)
        {
            parts.Add(projectName);
        }

        if (draft.Deadline is { } deadline)
        {
            parts.Add($"due {deadline.ToString("ddd", CultureInfo.CurrentCulture)}");
        }

        if (draft.EstimatedDuration is { } duration)
        {
            parts.Add(TaskRowViewModel.FormatDuration(duration));
        }

        parts.Add(draft.SourceDescription);
        MetaText = string.Join(" · ", parts);
    }

    public ExtractedTaskDraft Draft { get; }

    public string MetaText { get; }

    [ObservableProperty]
    public partial string EditTitle { get; set; }

    public ExtractedTaskDraft Edited => Draft with { Title = EditTitle.Trim() };

    [RelayCommand]
    private void Dismiss() => _owner.Dismiss(this);
}

public abstract class ChatItemViewModel : ViewModelBase;

public sealed class ChatUserMessageViewModel(string text) : ChatItemViewModel
{
    public string Text { get; } = text;
}

public sealed class ChatAssistantMessageViewModel(
    string text, IReadOnlyList<CitationChipViewModel>? citations = null) : ChatItemViewModel
{
    public string Text { get; } = text;

    public IReadOnlyList<CitationChipViewModel> Citations { get; } = citations ?? [];

    public bool HasCitations => Citations.Count > 0;
}

public sealed partial class CitationChipViewModel(string label, ResourceId resourceId, Action<ResourceId> open)
    : ViewModelBase
{
    public string Label { get; } = label;

    [RelayCommand]
    private void Open() => open(resourceId);
}

/// <summary>Frame 07: the reviewable task list an extraction produces.</summary>
public sealed partial class ChatReviewItemViewModel : ChatItemViewModel
{
    private readonly ChatViewModel _owner;

    public ChatReviewItemViewModel(
        ChatViewModel owner,
        IReadOnlyList<ExtractedTaskDraft> drafts,
        Func<ProjectId?, string?> projectNameLookup)
    {
        _owner = owner;
        foreach (var draft in drafts)
        {
            Drafts.Add(new DraftRowViewModel(this, draft, projectNameLookup(draft.ProjectId)));
        }
    }

    public ObservableCollection<DraftRowViewModel> Drafts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(AddAllLabel))]
    public partial string? ResolutionText { get; private set; }

    public bool IsPending => ResolutionText is null;

    public string HeaderText => Drafts.Count == 1
        ? "I found 1 task. Review it before it joins your Inbox:"
        : $"I found {Drafts.Count} tasks. Review them before they join your Inbox:";

    public string AddAllLabel => Drafts.Count == 1 ? "Add task" : $"Add all {Drafts.Count}";

    internal void Dismiss(DraftRowViewModel row)
    {
        Drafts.Remove(row);
        OnPropertyChanged(nameof(AddAllLabel));
        OnPropertyChanged(nameof(HeaderText));
        if (Drafts.Count == 0)
        {
            ResolutionText = "Dismissed — nothing was added.";
        }
    }

    [RelayCommand]
    private void AddAll()
    {
        var added = _owner.AcceptDrafts(Drafts.Select(d => d.Edited).ToList());
        ResolutionText = added == 1 ? "Added 1 task to your Inbox." : $"Added {added} tasks to your Inbox.";
        Drafts.Clear();
    }

    [RelayCommand]
    private void DismissAll()
    {
        Drafts.Clear();
        ResolutionText = "Dismissed — nothing was added.";
    }
}

/// <summary>
/// The chatbot composer: a collapsed strip that expands temporarily over the current
/// surface. Deterministic local provider in v1 — capture review, project-scoped answers
/// with citations, and plan requests, all provenance-tracked.
/// </summary>
public sealed partial class ChatViewModel : ViewModelBase
{
    private readonly AiService _ai;
    private readonly AiPermissionSettings _permissions;
    private readonly IClock _clock;

    private Func<AiContext>? _contextProvider;
    private Func<ProjectId?, string?>? _projectNameLookup;
    private Func<PlanningPeriod, string>? _requestPlan;
    private Action? _onTasksChanged;
    private Action<ResourceId>? _openResource;

    public ChatViewModel(AiService ai, AiPermissionSettings permissions, IClock clock)
    {
        _ai = ai;
        _permissions = permissions;
        _clock = clock;
    }

    /// <summary>Wired by the shell after construction (avoids a ViewModel dependency cycle).</summary>
    public void Attach(
        Func<AiContext> contextProvider,
        Func<ProjectId?, string?> projectNameLookup,
        Func<PlanningPeriod, string> requestPlan,
        Action onTasksChanged,
        Action<ResourceId> openResource)
    {
        _contextProvider = contextProvider;
        _projectNameLookup = projectNameLookup;
        _requestPlan = requestPlan;
        _onTasksChanged = onTasksChanged;
        _openResource = openResource;
    }

    public ObservableCollection<ChatItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Placeholder { get; set; } = "Tell BeBoosted what you need…";

    public bool HasItems => Items.Count > 0;

    /// <summary>Scopes the composer to a project ("Ask about College Admissions…").</summary>
    public void SetScope(string? projectName)
        => Placeholder = projectName is null
            ? "Tell BeBoosted what you need…"
            : $"Ask about {projectName}…";

    [RelayCommand]
    private void Collapse() => IsExpanded = false;

    [RelayCommand]
    private async Task SubmitAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0 || _contextProvider is null)
        {
            return;
        }

        InputText = string.Empty;
        IsExpanded = true;
        Items.Add(new ChatUserMessageViewModel(text));
        OnPropertyChanged(nameof(HasItems));

        var context = _contextProvider();
        if (IsPlanRequest(text))
        {
            Items.Add(new ChatAssistantMessageViewModel(HandlePlanRequest(text, context.Today)));
            return;
        }

        if (context.ActiveProjectId is { } projectId && IsQuestion(text))
        {
            var outcome = await _ai.AskProjectAsync(projectId, text);
            var citations = outcome.Citations
                .Select(citation => new CitationChipViewModel(
                    citation.Title, citation.Id, id => _openResource?.Invoke(id)))
                .ToList();
            Items.Add(new ChatAssistantMessageViewModel(outcome.Answer.AnswerText, citations));
            return;
        }

        if (IsQuestion(text))
        {
            Items.Add(new ChatAssistantMessageViewModel(
                "Open a project (or ask from inside one) and I'll answer from its Files, with sources."));
            return;
        }

        var extraction = await _ai.ExtractTasksAsync(text, context);
        if (extraction.Drafts.Count == 0)
        {
            Items.Add(new ChatAssistantMessageViewModel(
                "I couldn't find a task in that. Try describing what needs to get done."));
            return;
        }

        if (extraction.AddedAutomatically)
        {
            _onTasksChanged?.Invoke();
            var titles = string.Join("\n", extraction.AddedTasks.Select(t => $"• {t.Title}"));
            Items.Add(new ChatAssistantMessageViewModel(
                $"Added {extraction.AddedTasks.Count} task{(extraction.AddedTasks.Count == 1 ? string.Empty : "s")} "
                + $"to your Inbox (labeled AI added):\n{titles}"));
            return;
        }

        Items.Add(new ChatReviewItemViewModel(this, extraction.Drafts, _projectNameLookup ?? (_ => null)));
    }

    internal int AcceptDrafts(IReadOnlyList<ExtractedTaskDraft> drafts)
    {
        var added = _ai.AcceptDrafts(drafts);
        _onTasksChanged?.Invoke();
        return added.Count;
    }

    private string HandlePlanRequest(string text, DateOnly today)
    {
        var period = text.Contains("week", StringComparison.OrdinalIgnoreCase)
            ? PlanningPeriod.ForWeek(today)
            : PlanningPeriod.ForToday(today);
        return _requestPlan?.Invoke(period) ?? "Planning isn't available right now.";
    }

    internal static bool IsPlanRequest(string text)
        => System.Text.RegularExpressions.Regex.IsMatch(
            text, @"^\s*plan\b|\bplan\s+(my\s+)?(today|day|week)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static bool IsQuestion(string text)
        => text.TrimEnd().EndsWith('?')
            || System.Text.RegularExpressions.Regex.IsMatch(
                text, @"^\s*(what|how|when|where|which|who|should|does|do|is|are|can)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

using System.Collections.ObjectModel;
using System.Globalization;
using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Prioritization;
using BeBoosted.Domain;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Domain.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>One task card in the comparison pair.</summary>
public sealed record ComparisonCardViewModel(
    string Title,
    string ProjectLabel,
    string DueText,
    string DurationText)
{
    public bool HasProject => ProjectLabel.Length > 0;

    public bool HasDue => DueText.Length > 0;

    public bool HasDuration => DurationText.Length > 0;
}

public sealed record RankEntryViewModel(string RankText, string Title);

public sealed record TierGroupViewModel(string TierLabel, PlanningTier Tier, IReadOnlyList<RankEntryViewModel> Entries);

/// <summary>
/// The focused full-screen Priority Sort flow: adaptive comparisons, real ties, undo,
/// early exit via Build my plan now, and a results stage with tier groups.
/// </summary>
public sealed partial class PrioritySortViewModel : ViewModelBase
{
    private readonly ComparisonSession _session;
    private readonly PrioritySortService _service;
    private readonly IClock _clock;
    private readonly Dictionary<TaskId, TaskItem> _tasks;
    private readonly Action _onClosed;
    private readonly Action<IReadOnlyList<PriorityRank>> _onSaved;

    public PrioritySortViewModel(
        PlanningPeriod period,
        IReadOnlyList<TaskItem> candidates,
        PrioritySortService service,
        IClock clock,
        Action onClosed,
        Action<IReadOnlyList<PriorityRank>> onSaved)
        : this(
            period, candidates, service.StartSession(period, candidates.Select(t => t.Id)),
            service, clock, onClosed, onSaved)
    {
    }

    /// <summary>
    /// Runs a prepared session. <paramref name="knownTasks"/> must cover every task the
    /// session can name, seed anchors included, because they appear as comparison cards.
    /// </summary>
    public PrioritySortViewModel(
        PlanningPeriod period,
        IReadOnlyList<TaskItem> knownTasks,
        ComparisonSession session,
        PrioritySortService service,
        IClock clock,
        Action onClosed,
        Action<IReadOnlyList<PriorityRank>> onSaved,
        bool isRerank = false)
    {
        _service = service;
        _clock = clock;
        _onClosed = onClosed;
        _onSaved = onSaved;
        _tasks = knownTasks.ToDictionary(t => t.Id);
        Period = period;
        _session = session;
        IsRerank = isRerank;
        if (_session.IsComplete)
        {
            Finish();
        }
        else
        {
            RefreshQuestion();
        }
    }

    public PlanningPeriod Period { get; }

    /// <summary>Re-placing one already-ranked task rather than sorting the queue.</summary>
    public bool IsRerank { get; }

    /// <summary>
    /// A re-rank has no partial-save exit: an unfinished run must leave the saved
    /// ranking exactly as it was, and closing already saves nothing.
    /// </summary>
    public bool ShowBuildPlanNow => !IsRerank;

    public string Prompt => IsRerank
        ? "Where does this belong now?"
        : Period.Kind == PlanningPeriodKind.Today
            ? "If only one gets protected today,\nwhich should it be?"
            : "If only one moves forward this week,\nwhich should it be?";

    public string PeriodLabel => Period.Kind == PlanningPeriodKind.Today ? "Today" : "This week";

    /// <summary>
    /// Results-stage note for a re-rank that needed no questions: without it, the
    /// instant jump to "done" reads like a glitch.
    /// </summary>
    public string ResultsNote => IsRerank && IsFinished && _session.AnsweredCount == 0
        ? "No comparisons were needed — no other task holds a rank for this period yet, so this task is #1."
        : string.Empty;

    public bool HasResultsNote => ResultsNote.Length > 0;

    public ObservableCollection<TierGroupViewModel> ResultGroups { get; } = [];

    [ObservableProperty]
    public partial ComparisonCardViewModel? LeftCard { get; private set; }

    [ObservableProperty]
    public partial ComparisonCardViewModel? RightCard { get; private set; }

    [ObservableProperty]
    public partial string StatusLabel { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial bool IsFinished { get; private set; }

    [ObservableProperty]
    public partial bool CanUndo { get; private set; }

    [RelayCommand]
    private void ChooseLeft() => Answer(ComparisonResult.LeftWins);

    [RelayCommand]
    private void ChooseRight() => Answer(ComparisonResult.RightWins);

    /// <summary>"Too tough to decide": records a real tie and continues immediately.</summary>
    [RelayCommand]
    private void ChooseTie() => Answer(ComparisonResult.Tie);

    [RelayCommand]
    private void Undo()
    {
        if (IsFinished || !_session.Undo())
        {
            return;
        }

        RefreshQuestion();
    }

    /// <summary>
    /// Early exit: ranks what is known; unplaced tasks share the trailing ordinal.
    /// A re-rank has no partial-save exit, so the command refuses even if a view
    /// regression were to render it again.
    /// </summary>
    [RelayCommand]
    private void BuildPlanNow()
    {
        if (!ShowBuildPlanNow)
        {
            return;
        }

        Finish();
    }

    [RelayCommand]
    private void Close() => _onClosed();

    private void Answer(ComparisonResult result)
    {
        if (IsFinished || _session.IsComplete)
        {
            return;
        }

        _session.Record(result);
        if (_session.IsComplete)
        {
            Finish();
        }
        else
        {
            RefreshQuestion();
        }
    }

    private void RefreshQuestion()
    {
        if (_session.CurrentComparison is not { } comparison)
        {
            return;
        }

        LeftCard = BuildCard(comparison.Left);
        RightCard = BuildCard(comparison.Right);
        StatusLabel = IsRerank
            ? $"Re-rank · {PeriodLabel} · Comparison {_session.ComparisonNumber}"
            : $"Priority Sort · {PeriodLabel} · Comparison {_session.ComparisonNumber}";
        Progress = _session.Progress;
        CanUndo = _session.CanUndo;
    }

    private void Finish()
    {
        var ranking = _service.Complete(_session);
        BuildResultGroups(ranking);
        Progress = 1;
        StatusLabel = IsRerank
            ? $"Re-rank · {PeriodLabel} · {_session.AnsweredCount} comparisons"
            : $"Priority Sort · {PeriodLabel} · {_session.AnsweredCount} comparisons";
        IsFinished = true;
        CanUndo = false;
        OnPropertyChanged(nameof(ResultsNote));
        OnPropertyChanged(nameof(HasResultsNote));
        _onSaved(ranking);
    }

    private void BuildResultGroups(IReadOnlyList<PriorityRank> ranking)
    {
        ResultGroups.Clear();
        foreach (var tierGroup in ranking.GroupBy(r => r.Tier).OrderBy(g => g.Key))
        {
            var entries = tierGroup
                .OrderBy(r => r.Rank)
                .Select(r => new RankEntryViewModel(
                    string.Create(CultureInfo.InvariantCulture, $"#{r.Rank}"),
                    _tasks.TryGetValue(r.TaskId, out var task) ? task.Title : "(deleted task)"))
                .ToList();
            ResultGroups.Add(new TierGroupViewModel(TierLabel(tierGroup.Key), tierGroup.Key, entries));
        }
    }

    private static string TierLabel(PlanningTier tier) => tier switch
    {
        PlanningTier.ProtectNow => "Protect now",
        PlanningTier.AdvanceNext => "Advance next",
        _ => "Can wait",
    };

    private ComparisonCardViewModel BuildCard(TaskId taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
        {
            return new ComparisonCardViewModel("(deleted task)", string.Empty, string.Empty, string.Empty);
        }

        var due = task.Deadline is { } deadline ? DescribeDeadline(deadline) : string.Empty;
        var duration = task.EstimatedDuration is { } estimated
            ? $"{TaskRowViewModel.FormatDuration(estimated)} estimated"
            : string.Empty;
        return new ComparisonCardViewModel(task.Title, string.Empty, due, duration);
    }

    private string DescribeDeadline(DateOnly deadline)
    {
        var today = _clock.Today;
        if (deadline == today)
        {
            return "Today";
        }

        if (deadline == today.AddDays(1))
        {
            return "Tomorrow";
        }

        if (deadline > today && deadline <= today.AddDays(6))
        {
            return deadline.ToString("dddd", CultureInfo.CurrentCulture);
        }

        return deadline.ToString("MMM d", CultureInfo.CurrentCulture);
    }
}

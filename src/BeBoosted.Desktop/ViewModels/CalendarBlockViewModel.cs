using System.Globalization;
using BeBoosted.Domain.Calendar;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>One rendered block occurrence on the timeline.</summary>
public sealed partial class CalendarBlockViewModel : ViewModelBase
{
    private readonly CalendarViewModel _owner;

    public CalendarBlockViewModel(
        CalendarViewModel owner,
        BlockOccurrence occurrence,
        string title,
        bool isConflicted,
        bool isDone,
        bool needsOutcome)
    {
        _owner = owner;
        Occurrence = occurrence;
        Title = title;
        IsConflicted = isConflicted;
        IsDone = isDone;
        NeedsOutcome = needsOutcome;
    }

    public BlockOccurrence Occurrence { get; }

    public CalendarBlock Block => Occurrence.Block;

    public string Title { get; }

    public bool IsConflicted { get; }

    /// <summary>Task completed via this block (quiet, struck-through rendering).</summary>
    public bool IsDone { get; }

    /// <summary>Elapsed task block without an outcome yet.</summary>
    public bool NeedsOutcome { get; }

    public bool IsFixed => Block.Kind == BlockKind.FixedCommitment;

    public bool IsTaskBlock => Block.Kind == BlockKind.TaskBlock;

    /// <summary>Task blocks are draggable/resizable; fixed commitments are locked on the surface.</summary>
    public bool IsInteractive => IsTaskBlock;

    public bool ShowCompletionControl => IsTaskBlock && !IsDone;

    public DateOnly Date => Occurrence.Date;

    public double StartMinutes => Block.StartTime.ToTimeSpan().TotalMinutes;

    public double DurationMinutes => Block.Duration.TotalMinutes;

    public string TimeText
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            var start = Block.StartTime.ToString("h:mm", culture);
            var end = Block.EndTime.ToString("h:mm", culture);
            return IsFixed
                ? $"{start} – {end} · Fixed"
                : $"{start} – {end} · {TaskRowViewModel.FormatDuration(Block.Duration)}";
        }
    }

    public string AccessibleName
    {
        get
        {
            var state = IsConflicted ? ", conflict"
                : IsDone ? ", done"
                : NeedsOutcome ? ", needs an outcome"
                : IsFixed ? ", fixed commitment"
                : string.Empty;
            return $"{Title}, {Date:MMMM d}, {Block.StartTime:h\\:mm} to {Block.EndTime:h\\:mm}{state}";
        }
    }

    [ObservableProperty]
    public partial decimal RemainingMinutes { get; set; } = 30;

    [RelayCommand]
    private void RecordDone() => _owner.RecordOutcome(Block.Id, BlockOutcome.Done, null);

    [RelayCommand]
    private void RecordNeedsMoreTime()
        => _owner.RecordOutcome(
            Block.Id,
            BlockOutcome.NeedsMoreTime,
            TimeSpan.FromMinutes((double)Math.Max(5, RemainingMinutes)));

    [RelayCommand]
    private void RecordDidntHappen() => _owner.RecordOutcome(Block.Id, BlockOutcome.DidntHappen, null);

    [RelayCommand]
    private void Unschedule() => _owner.UnscheduleBlock(Block.Id);

    /// <summary>Keyboard movement: minutes within the day (snapped by the caller).</summary>
    public void Nudge(int minutes) => _owner.NudgeBlock(this, minutes);

    public void NudgeDays(int days) => _owner.NudgeBlockDays(this, days);

    public void ResizeBy(int minutes) => _owner.ResizeBlockBy(this, minutes);

    public void MoveTo(DateOnly date, TimeOnly start) => _owner.MoveBlock(Block.Id, date, start);

    public void ResizeTo(TimeOnly end) => _owner.ResizeBlockTo(Block.Id, end);
}

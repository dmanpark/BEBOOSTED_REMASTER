using System.Collections.ObjectModel;
using BeBoosted.Application.Calendar;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeBoosted.Desktop.ViewModels;

/// <summary>A value snapshot of the schedule fields, for truthful dirty detection.</summary>
public sealed record ScheduleSnapshot(
    DateTimeOffset? Date,
    TimeSpan? Start,
    TimeSpan? End,
    bool RepeatsWeekly,
    IReadOnlyList<DayOfWeek> SelectedDays);

/// <summary>
/// The shared Date/Start/End/Repeats/weekday field group used by the session
/// editor and the whole-task editor's create-mode first session.
/// </summary>
public sealed partial class ScheduleFieldsViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial DateTimeOffset? Date { get; set; }

    [ObservableProperty]
    public partial TimeSpan? Start { get; set; }

    [ObservableProperty]
    public partial TimeSpan? End { get; set; }

    [ObservableProperty]
    public partial bool RepeatsWeekly { get; set; }

    /// <summary>Field-pinned validation (frame 4n): the END control owns this error.</summary>
    public bool HasEndBeforeStart => Start is { } start && End is { } end && end <= start;

    public string? EndFieldError => HasEndBeforeStart ? "A block must end after it starts." : null;

    partial void OnStartChanged(TimeSpan? value) => NotifyEndValidation();

    partial void OnEndChanged(TimeSpan? value) => NotifyEndValidation();

    private void NotifyEndValidation()
    {
        OnPropertyChanged(nameof(HasEndBeforeStart));
        OnPropertyChanged(nameof(EndFieldError));
    }

    /// <summary>Sunday-first, matching the approved frames (S M T W T F S).</summary>
    public ObservableCollection<DayToggleViewModel> Days { get; } =
    [
        new(DayOfWeek.Sunday), new(DayOfWeek.Monday), new(DayOfWeek.Tuesday),
        new(DayOfWeek.Wednesday), new(DayOfWeek.Thursday), new(DayOfWeek.Friday),
        new(DayOfWeek.Saturday),
    ];

    public void LoadDefaults(DateOnly date, TimeOnly start, TimeOnly end)
    {
        Date = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue));
        Start = start.ToTimeSpan();
        End = end.ToTimeSpan();
    }

    /// <summary>Loads one block's date, times, and recurrence into the fields.</summary>
    public void Load(CalendarBlock session)
    {
        LoadDefaults(session.Date, session.StartTime, session.EndTime);
        RepeatsWeekly = session.Recurrence is not null;
        foreach (var day in Days)
        {
            day.IsSelected = session.Recurrence?.DaysOfWeek.Contains(day.Day) == true;
        }
    }

    /// <summary>
    /// Null with the spec error when incomplete; otherwise the request. Zero
    /// ticked weekdays default to the date's weekday.
    /// </summary>
    public TaskScheduleRequest? TryBuildSchedule(out string? error)
    {
        if (Date is not { } date || Start is not { } start || End is not { } end)
        {
            error = "Pick a date, start, and end.";
            return null;
        }

        RecurrenceRule? recurrence = null;
        if (RepeatsWeekly)
        {
            var days = Days.Where(d => d.IsSelected).Select(d => d.Day).ToArray();
            if (days.Length == 0)
            {
                days = [DateOnly.FromDateTime(date.Date).DayOfWeek];
            }

            recurrence = RecurrenceRule.Weekly(1, days);
        }

        error = null;
        return new TaskScheduleRequest(
            DateOnly.FromDateTime(date.Date),
            TimeOnly.FromTimeSpan(start), TimeOnly.FromTimeSpan(end), recurrence);
    }

    public ScheduleSnapshot Capture()
        => new(Date, Start, End, RepeatsWeekly,
            [.. Days.Where(d => d.IsSelected).Select(d => d.Day)]);

    public bool IsDirtyAgainst(ScheduleSnapshot snapshot)
        => Date != snapshot.Date
            || Start != snapshot.Start
            || End != snapshot.End
            || RepeatsWeekly != snapshot.RepeatsWeekly
            || !Days.Where(d => d.IsSelected).Select(d => d.Day).SequenceEqual(snapshot.SelectedDays);
}

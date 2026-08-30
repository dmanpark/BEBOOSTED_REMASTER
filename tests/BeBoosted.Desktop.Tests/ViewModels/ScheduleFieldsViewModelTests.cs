using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Domain.Scheduling;

namespace BeBoosted.Desktop.Tests.ViewModels;

/// <summary>
/// The shared Date/Start/End/Repeats field group: the spec's missing-field
/// error, the weekday-default rule, round-tripping a repeating session, and
/// snapshot-based dirty detection.
/// </summary>
public sealed class ScheduleFieldsViewModelTests
{
    private static readonly FakeClock Clock = new(TestShell.DesignDate);

    private static ScheduleFieldsViewModel Loaded()
    {
        var fields = new ScheduleFieldsViewModel();
        fields.LoadDefaults(TestShell.DesignDate, new TimeOnly(9, 0), new TimeOnly(10, 0));
        return fields;
    }

    [Fact]
    public void TryBuildSchedule_MissingAnyField_ReturnsTheSpecError()
    {
        foreach (var strip in new Action<ScheduleFieldsViewModel>[]
        {
            fields => fields.Date = null,
            fields => fields.Start = null,
            fields => fields.End = null,
        })
        {
            var fields = Loaded();
            strip(fields);

            var request = fields.TryBuildSchedule(out var error);

            Assert.Null(request);
            Assert.Equal("Pick a date, start, and end.", error);
        }
    }

    [Fact]
    public void TryBuildSchedule_RepeatsWithNoTickedDay_DefaultsToTheDatesWeekday()
    {
        var fields = Loaded();          // DesignDate is a Tuesday.
        fields.RepeatsWeekly = true;

        var request = fields.TryBuildSchedule(out var error);

        Assert.Null(error);
        Assert.Equal([DayOfWeek.Tuesday], request!.Recurrence!.DaysOfWeek);
    }

    [Fact]
    public void Load_RoundTripsARepeatingSession()
    {
        var session = CalendarBlock.CreateTaskSession(
            TaskId.New(), new DateOnly(2026, 8, 26), new TimeOnly(16, 0), new TimeOnly(17, 0),
            Clock.Now, RecurrenceRule.Weekly(1, DayOfWeek.Wednesday, DayOfWeek.Saturday));
        var fields = new ScheduleFieldsViewModel();

        fields.Load(session);

        Assert.True(fields.RepeatsWeekly);
        Assert.Equal(
            [DayOfWeek.Wednesday, DayOfWeek.Saturday],
            fields.Days.Where(d => d.IsSelected).Select(d => d.Day).ToArray());
        var request = fields.TryBuildSchedule(out var error);
        Assert.Null(error);
        Assert.Equal(new DateOnly(2026, 8, 26), request!.Date);
        Assert.Equal(new TimeOnly(16, 0), request.StartTime);
        Assert.Equal(new TimeOnly(17, 0), request.EndTime);
        Assert.Equal([DayOfWeek.Wednesday, DayOfWeek.Saturday], request.Recurrence!.DaysOfWeek);
    }

    [Fact]
    public void IsDirtyAgainst_DetectsEveryFieldAndDayChange()
    {
        var fields = Loaded();
        var snapshot = fields.Capture();
        Assert.False(fields.IsDirtyAgainst(snapshot));

        var originalDate = fields.Date;
        fields.Date = originalDate!.Value.AddDays(1);
        Assert.True(fields.IsDirtyAgainst(snapshot));
        fields.Date = originalDate;
        Assert.False(fields.IsDirtyAgainst(snapshot));

        fields.Start = new TimeSpan(11, 0, 0);
        Assert.True(fields.IsDirtyAgainst(snapshot));
        fields.Start = new TimeSpan(9, 0, 0);

        fields.End = new TimeSpan(12, 0, 0);
        Assert.True(fields.IsDirtyAgainst(snapshot));
        fields.End = new TimeSpan(10, 0, 0);

        fields.RepeatsWeekly = true;
        Assert.True(fields.IsDirtyAgainst(snapshot));
        fields.RepeatsWeekly = false;
        Assert.False(fields.IsDirtyAgainst(snapshot));

        fields.Days[0].IsSelected = true;
        Assert.True(fields.IsDirtyAgainst(snapshot));
    }

    [Fact]
    public void Days_AreSundayFirst()
    {
        Assert.Equal(
            [
                DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
            ],
            new ScheduleFieldsViewModel().Days.Select(d => d.Day).ToArray());
    }
}

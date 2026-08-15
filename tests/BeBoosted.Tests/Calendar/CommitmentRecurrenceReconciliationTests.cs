using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Calendar;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Calendar;

/// <summary>
/// Editing a series must reconcile completion rows in the same transaction: no
/// completion may survive for a date that is no longer a valid occurrence, and
/// restoring a weekday later must never resurrect an obsolete completion.
/// </summary>
public sealed class CommitmentRecurrenceReconciliationTests : IDisposable
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 8, 11, 14, 10, 0, TimeSpan.FromHours(-7));

        public DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
    }

    /// <summary>Tuesday, 2026-08-11.</summary>
    private static readonly DateOnly Tuesday = new(2026, 8, 11);

    private readonly TempDatabase _database = new();
    private readonly FixedClock _clock = new();
    private readonly CalendarService _service;

    public CommitmentRecurrenceReconciliationTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _service = CreateService();
    }

    private CalendarService CreateService()
        => new(
            new SqliteCalendarBlockRepository(_database.Factory),
            new SqliteCommitmentCompletionRepository(_database.Factory),
            new SqliteCalendarMutations(_database.Factory),
            new SqliteTaskRepository(_database.Factory),
            _clock);

    [Fact]
    public void RemovingACompletedWeekday_PurgesItsRow_AndReAddingStaysIncomplete()
    {
        var series = _service.CreateFixedCommitment(
            "AP Economics", Tuesday.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        Assert.True(_service.CompleteCommitmentOccurrence(series.Id, Tuesday));

        // Drop Tuesdays from the series: the completed Tuesday no longer occurs.
        _service.UpdateFixedCommitment(
            series.Id, "AP Economics", Tuesday.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday), null);

        var completions = new SqliteCommitmentCompletionRepository(_database.Factory);
        Assert.Null(completions.Get(series.Id, Tuesday));

        // Restart the service graph, restore Tuesdays: no resurrection.
        var restarted = CreateService();
        restarted.UpdateFixedCommitment(
            series.Id, "AP Economics", Tuesday.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday), null);
        Assert.False(restarted.IsCommitmentOccurrenceCompleted(series.Id, Tuesday));
    }

    [Fact]
    public void RecurringToOneOff_KeepsOnlyTheCompletionThatStillOccurs()
    {
        var series = _service.CreateFixedCommitment(
            "AP Economics", Tuesday.AddDays(-7), new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday, DayOfWeek.Wednesday));
        _service.CompleteCommitmentOccurrence(series.Id, Tuesday);
        _service.CompleteCommitmentOccurrence(series.Id, Tuesday.AddDays(1));

        // Convert the series into a one-off on the completed Tuesday.
        _service.UpdateFixedCommitment(
            series.Id, "AP Economics", Tuesday, new TimeOnly(8, 30), new TimeOnly(9, 45),
            recurrence: null, projectId: null);

        Assert.True(_service.IsCommitmentOccurrenceCompleted(series.Id, Tuesday));
        Assert.False(_service.IsCommitmentOccurrenceCompleted(series.Id, Tuesday.AddDays(1)));
    }

    [Fact]
    public void RecurringToOneOffOnANewDate_NeverInheritsTheAnchorCompletion()
    {
        // Series anchored on Tuesday (date A); complete the anchor occurrence.
        var series = _service.CreateFixedCommitment(
            "AP Economics", Tuesday, new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        Assert.True(_service.CompleteCommitmentOccurrence(series.Id, Tuesday));

        // Convert into a one-off on Thursday (date B) with no completion request.
        var thursday = Tuesday.AddDays(2);
        _service.UpdateFixedCommitment(
            series.Id, "AP Economics", thursday, new TimeOnly(8, 30), new TimeOnly(9, 45),
            recurrence: null, projectId: null);

        // The anchor completion is gone and B was never completed — a series-anchor
        // completion must not masquerade as a one-off date move.
        Assert.False(_service.IsCommitmentOccurrenceCompleted(series.Id, Tuesday));
        Assert.False(_service.IsCommitmentOccurrenceCompleted(series.Id, thursday));

        var restarted = CreateService();
        Assert.False(restarted.IsCommitmentOccurrenceCompleted(series.Id, thursday));
        Assert.Empty(
            new SqliteCommitmentCompletionRepository(_database.Factory).GetForBlock(series.Id));
    }

    [Fact]
    public void RecurringToOneOff_ARequestedCompletionIsFresh_NeverInherited()
    {
        var series = _service.CreateFixedCommitment(
            "AP Economics", Tuesday, new TimeOnly(8, 30), new TimeOnly(9, 45),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday));
        _service.CompleteCommitmentOccurrence(series.Id, Tuesday);
        var anchorCompletedAt = _clock.Now;

        // Later, convert to a one-off on Thursday with Completed explicitly requested.
        _clock.Now = anchorCompletedAt.AddDays(1);
        var thursday = Tuesday.AddDays(2);
        _service.UpdateFixedCommitment(
            series.Id, "AP Economics", thursday, new TimeOnly(8, 30), new TimeOnly(9, 45),
            recurrence: null, projectId: null,
            new CommitmentCompletionRequest(Tuesday, Completed: true));

        // B is completed by the request itself — freshly stamped, not carried over.
        var completions = new SqliteCommitmentCompletionRepository(_database.Factory);
        var row = Assert.Single(completions.GetForBlock(series.Id));
        Assert.Equal(thursday, row.OccurrenceDate);
        Assert.Equal(_clock.Now, row.CompletedAt);
    }

    [Fact]
    public void CompletedOneOffToRecurring_SurvivesOnlyWhenItsDateStillOccurs()
    {
        // Kept: the anchor Tuesday remains an occurrence of a Tuesday series.
        var kept = _service.CreateFixedCommitment(
            "Stats HW", Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0));
        _service.CompleteCommitmentOccurrence(kept.Id, Tuesday);
        _service.UpdateFixedCommitment(
            kept.Id, "Stats HW", Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Tuesday), null);
        Assert.True(_service.IsCommitmentOccurrenceCompleted(kept.Id, Tuesday));

        // Dropped: a Wednesday-only series never occurs on the completed Tuesday.
        var dropped = _service.CreateFixedCommitment(
            "Vocab review", Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0));
        _service.CompleteCommitmentOccurrence(dropped.Id, Tuesday);
        _service.UpdateFixedCommitment(
            dropped.Id, "Vocab review", Tuesday, new TimeOnly(16, 0), new TimeOnly(17, 0),
            RecurrenceRule.Weekly(1, DayOfWeek.Wednesday), null);
        Assert.False(_service.IsCommitmentOccurrenceCompleted(dropped.Id, Tuesday));
    }

    public void Dispose() => _database.Dispose();
}

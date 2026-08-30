using BeBoosted.Domain;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Tasks;
using BeBoosted.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Persistence;

/// <summary>
/// Upgrade coverage for 0010_unify_tasks: a real pre-migration database whose local
/// fixed commitments (kind 0, provider 'local') become Tasks with task-backed schedule
/// sessions — titles, projects, times, recurrence, and per-date completion preserved,
/// external events untouched, and no duplicates on a re-run.
/// </summary>
public sealed class UnifiedTaskMigrationTests : IDisposable
{
    private const string ProjectId = "6f1de1c2-8a30-4a3b-9a58-c04d5f6a7b01";
    private const string OneOffBlockId = "0d6b2f74-3f57-4de2-9f5c-2a8f4f3f9b11";
    private const string RecurringBlockId = "0d6b2f74-3f57-4de2-9f5c-2a8f4f3f9b22";
    private const string TaskBackedBlockId = "0d6b2f74-3f57-4de2-9f5c-2a8f4f3f9b33";
    private const string ExternalBlockId = "0d6b2f74-3f57-4de2-9f5c-2a8f4f3f9b44";
    private const string ExistingTaskId = "aa1de1c2-8a30-4a3b-9a58-c04d5f6a7b99";

    private const string OneOffCompletedAt = "2026-08-11T13:00:00.0000000-07:00";

    private readonly TempDatabase _database = new();
    private readonly MigrationRunner _runner;

    public UnifiedTaskMigrationTests()
        => _runner = new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance);

    /// <summary>
    /// The design-date calendar as version 0009 stored it: a one-off local commitment
    /// (completed, project-linked), a repeating weekday commitment with two checked-off
    /// occurrences, a normal task-backed block, and an external synced event.
    /// </summary>
    private void BuildPre0010Database()
    {
        _runner.Apply(EmbeddedMigrations.Load().Where(m => m.Version <= 9).ToList());

        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO projects (id, name, accent_color, created_at, modified_at)
            VALUES ('{ProjectId}', 'CAPPs', '#5A6B7F',
                    '2026-08-01T09:00:00.0000000-07:00', '2026-08-01T09:00:00.0000000-07:00');

            INSERT INTO tasks (id, title, estimated_minutes, is_completed, created_at, modified_at)
            VALUES ('{ExistingTaskId}', 'Draft personal statement', 60, 0,
                    '2026-08-10T09:00:00.0000000-07:00', '2026-08-10T09:00:00.0000000-07:00');

            INSERT INTO calendar_blocks
                (id, task_id, project_id, title, date, start_time, end_time, kind, recurrence,
                 provider, external_id, sync_state, outcome, outcome_recorded_at,
                 created_at, modified_at)
            VALUES
                ('{OneOffBlockId}', NULL, '{ProjectId}', 'Lunch', '2026-08-11',
                 '12:00:00.0000000', '12:45:00.0000000', 0, NULL, 'local', NULL, 0, 0, NULL,
                 '2026-08-02T10:00:00.0000000-07:00', '2026-08-03T11:00:00.0000000-07:00'),
                ('{RecurringBlockId}', NULL, '{ProjectId}', 'AP Economics', '2026-08-03',
                 '08:30:00.0000000', '09:45:00.0000000', 0, 'W:1:MO,TU,WE,TH,FR',
                 'local', NULL, 0, 0, NULL,
                 '2026-08-01T09:30:00.0000000-07:00', '2026-08-01T09:30:00.0000000-07:00'),
                ('{TaskBackedBlockId}', '{ExistingTaskId}', NULL, NULL, '2026-08-11',
                 '19:00:00.0000000', '20:00:00.0000000', 1, NULL, 'local', NULL, 0, 0, NULL,
                 '2026-08-10T09:05:00.0000000-07:00', '2026-08-10T09:05:00.0000000-07:00'),
                ('{ExternalBlockId}', NULL, NULL, 'Imported standup', '2026-08-11',
                 '13:30:00.0000000', '14:00:00.0000000', 0, NULL, 'google', 'evt-1', 0, 0, NULL,
                 '2026-08-05T08:00:00.0000000-07:00', '2026-08-05T08:00:00.0000000-07:00');

            INSERT INTO commitment_completions (block_id, occurrence_date, completed_at)
            VALUES
                ('{OneOffBlockId}', '2026-08-11', '{OneOffCompletedAt}'),
                ('{RecurringBlockId}', '2026-08-10', '2026-08-10T10:00:00.0000000-07:00'),
                ('{RecurringBlockId}', '2026-08-11', '2026-08-11T10:00:00.0000000-07:00');
            """;
        command.ExecuteNonQuery();
    }

    private long ScalarLong(string sql)
    {
        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private string? ScalarText(string sql)
    {
        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    /// <summary>TDD phase 1: before the migration, local commitments are NOT task-backed.</summary>
    [Fact]
    public void PreMigration_LocalCommitments_AreNotAllTaskBacked()
    {
        BuildPre0010Database();

        var orphanedLocal = ScalarLong(
            "SELECT COUNT(*) FROM calendar_blocks WHERE provider = 'local' AND task_id IS NULL;");
        Assert.Equal(2, orphanedLocal);
    }

    /// <summary>TDD phase 2: the migration converts local commitments into Tasks.</summary>
    [Fact]
    public void Migration_MakesEveryLocalBlockTaskBacked_WithTaskOwnedTitles()
    {
        BuildPre0010Database();

        _runner.Apply(EmbeddedMigrations.Load());

        Assert.Equal(0, ScalarLong(
            "SELECT COUNT(*) FROM calendar_blocks WHERE provider = 'local' AND task_id IS NULL;"));
        // Every local block is a task session (kind 1); the task owns the title.
        Assert.Equal(0, ScalarLong(
            "SELECT COUNT(*) FROM calendar_blocks WHERE provider = 'local' AND kind <> 1;"));
        Assert.Equal(0, ScalarLong(
            "SELECT COUNT(*) FROM calendar_blocks WHERE provider = 'local' AND title IS NOT NULL;"));
        Assert.Equal(1, ScalarLong("SELECT COUNT(*) FROM tasks WHERE title = 'Lunch';"));
        Assert.Equal(1, ScalarLong("SELECT COUNT(*) FROM tasks WHERE title = 'AP Economics';"));

        // The one-off commitment was checked off — its Task arrives completed, keeping
        // the original completion timestamp.
        Assert.Equal(1, ScalarLong(
            "SELECT is_completed FROM tasks WHERE title = 'Lunch';"));
        Assert.Equal(OneOffCompletedAt, ScalarText(
            "SELECT completed_at FROM tasks WHERE title = 'Lunch';"));
    }

    /// <summary>TDD phase 3: recurrence and per-date completion survive unchanged.</summary>
    [Fact]
    public void Migration_PreservesRecurrenceAndPerDateCompletion()
    {
        BuildPre0010Database();

        _runner.Apply(EmbeddedMigrations.Load());

        Assert.Equal("W:1:MO,TU,WE,TH,FR", ScalarText(
            $"SELECT recurrence FROM calendar_blocks WHERE id = '{RecurringBlockId}';"));
        Assert.Equal("2026-08-03", ScalarText(
            $"SELECT date FROM calendar_blocks WHERE id = '{RecurringBlockId}';"));
        Assert.Equal("08:30:00.0000000", ScalarText(
            $"SELECT start_time FROM calendar_blocks WHERE id = '{RecurringBlockId}';"));

        // Per-occurrence completion lives in the renamed occurrence_completions table,
        // still keyed to exactly the original dates — and only those dates.
        Assert.Equal(2, ScalarLong(
            $"SELECT COUNT(*) FROM occurrence_completions WHERE block_id = '{RecurringBlockId}';"));
        Assert.Equal(1, ScalarLong(
            $"""
            SELECT COUNT(*) FROM occurrence_completions
            WHERE block_id = '{RecurringBlockId}' AND occurrence_date = '2026-08-10';
            """));
        Assert.Equal(1, ScalarLong(
            $"""
            SELECT COUNT(*) FROM occurrence_completions
            WHERE block_id = '{RecurringBlockId}' AND occurrence_date = '2026-08-11';
            """));
    }

    /// <summary>TDD phase 4: a commitment's project link moves onto its new Task.</summary>
    [Fact]
    public void Migration_PreservesProjectAssignment_OnTheTask()
    {
        BuildPre0010Database();

        _runner.Apply(EmbeddedMigrations.Load());

        Assert.Equal(ProjectId, ScalarText(
            "SELECT project_id FROM tasks WHERE title = 'AP Economics';"));
        Assert.Equal(ProjectId, ScalarText(
            "SELECT project_id FROM tasks WHERE title = 'Lunch';"));
        // Ownership is not duplicated: the schedule row no longer carries the link.
        Assert.Null(ScalarText(
            $"SELECT project_id FROM calendar_blocks WHERE id = '{RecurringBlockId}';"));
    }

    /// <summary>TDD phase 5: external synced events stay external and untouched.</summary>
    [Fact]
    public void Migration_LeavesExternalEventsExternal()
    {
        BuildPre0010Database();

        _runner.Apply(EmbeddedMigrations.Load());

        Assert.Null(ScalarText(
            $"SELECT task_id FROM calendar_blocks WHERE id = '{ExternalBlockId}';"));
        Assert.Equal("google", ScalarText(
            $"SELECT provider FROM calendar_blocks WHERE id = '{ExternalBlockId}';"));
        Assert.Equal("evt-1", ScalarText(
            $"SELECT external_id FROM calendar_blocks WHERE id = '{ExternalBlockId}';"));
        Assert.Equal("Imported standup", ScalarText(
            $"SELECT title FROM calendar_blocks WHERE id = '{ExternalBlockId}';"));
        Assert.Equal(0, ScalarLong(
            $"SELECT kind FROM calendar_blocks WHERE id = '{ExternalBlockId}';"));
        Assert.Equal(0, ScalarLong(
            "SELECT COUNT(*) FROM tasks WHERE title = 'Imported standup';"));
    }

    [Fact]
    public void Migration_KeepsExistingTaskBackedBlocksLinked()
    {
        BuildPre0010Database();

        _runner.Apply(EmbeddedMigrations.Load());

        Assert.Equal(ExistingTaskId, ScalarText(
            $"SELECT task_id FROM calendar_blocks WHERE id = '{TaskBackedBlockId}';"));
        Assert.Equal(1, ScalarLong(
            "SELECT COUNT(*) FROM tasks WHERE title = 'Draft personal statement';"));
    }

    [Fact]
    public void Migration_ReappliedOrReopened_CreatesNoDuplicateTasks()
    {
        BuildPre0010Database();

        _runner.Apply(EmbeddedMigrations.Load());
        var taskCount = ScalarLong("SELECT COUNT(*) FROM tasks;");

        // Retried in the same process, and again after "restarting" (a fresh runner).
        _runner.Apply(EmbeddedMigrations.Load());
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());

        Assert.Equal(taskCount, ScalarLong("SELECT COUNT(*) FROM tasks;"));
        Assert.Equal(1, ScalarLong("SELECT COUNT(*) FROM tasks WHERE title = 'Lunch';"));
        Assert.Equal(1, ScalarLong("SELECT COUNT(*) FROM tasks WHERE title = 'AP Economics';"));
    }

    [Fact]
    public void Migration_DatabaseReopens_AndRepositoriesRoundTrip()
    {
        BuildPre0010Database();

        _runner.Apply(EmbeddedMigrations.Load());

        // Reads go through the real repositories after a fresh open.
        var blocks = new SqliteCalendarBlockRepository(_database.Factory);
        var all = blocks.GetAll();
        Assert.Equal(4, all.Count);

        var oneOff = blocks.GetById(CalendarBlockId.Parse(OneOffBlockId))!;
        Assert.Equal(new DateOnly(2026, 8, 11), oneOff.Date);
        Assert.Equal(new TimeOnly(12, 0), oneOff.StartTime);
        Assert.Equal(new TimeOnly(12, 45), oneOff.EndTime);
        Assert.Equal(1, (int)oneOff.Kind);
        Assert.NotNull(oneOff.TaskId);

        var tasks = new SqliteTaskRepository(_database.Factory);
        var lunchTask = tasks.GetById(oneOff.TaskId!.Value);
        Assert.NotNull(lunchTask);
        Assert.Equal("Lunch", lunchTask.Title);
        Assert.True(lunchTask.IsCompleted);

        var external = blocks.GetById(CalendarBlockId.Parse(ExternalBlockId))!;
        Assert.True(external.IsExternal);
        Assert.Equal("Imported standup", external.Title);
    }

    public void Dispose() => _database.Dispose();
}

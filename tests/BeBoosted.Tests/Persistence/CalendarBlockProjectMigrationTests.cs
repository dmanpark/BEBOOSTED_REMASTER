using BeBoosted.Domain;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Persistence;

/// <summary>
/// Upgrade coverage for 0008_calendar_block_projects: a database created at the previous
/// schema version must gain the nullable project link without losing any block data.
/// </summary>
public sealed class CalendarBlockProjectMigrationTests : IDisposable
{
    private const string LegacyBlockId = "0d6b2f74-3f57-4de2-9f5c-2a8f4f3f9b01";

    private readonly TempDatabase _database = new();
    private readonly MigrationRunner _runner;

    public CalendarBlockProjectMigrationTests()
        => _runner = new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance);

    private void BuildPre0008DatabaseWithOneBlock()
    {
        _runner.Apply(EmbeddedMigrations.Load().Where(m => m.Version <= 7).ToList());

        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO calendar_blocks
                (id, task_id, title, date, start_time, end_time, kind, recurrence,
                 provider, external_id, sync_state, outcome, outcome_recorded_at,
                 created_at, modified_at)
            VALUES
                ($id, NULL, 'AP Economics', '2026-08-11', '08:30:00.0000000',
                 '09:45:00.0000000', 0, NULL, 'local', NULL, 0, 0, NULL,
                 '2026-08-11T14:00:00.0000000-07:00', '2026-08-11T14:00:00.0000000-07:00');
            """;
        command.Parameters.AddWithValue("$id", LegacyBlockId);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Upgrade_AddsNullableProjectColumn_WithoutLosingBlocks()
    {
        BuildPre0008DatabaseWithOneBlock();

        // Through the full ladder the legacy commitment survives with its times and,
        // after 0010, is task-backed with its title owned by the migrated Task.
        _runner.Apply(EmbeddedMigrations.Load());

        var repository = new SqliteCalendarBlockRepository(_database.Factory);
        var block = repository.GetById(CalendarBlockId.Parse(LegacyBlockId));
        Assert.NotNull(block);
        Assert.Equal(new DateOnly(2026, 8, 11), block.Date);
        Assert.Equal(new TimeOnly(8, 30), block.StartTime);
        Assert.Equal(new TimeOnly(9, 45), block.EndTime);
        Assert.NotNull(block.TaskId);
    }

    [Fact]
    public void Upgrade_CreatesTheProjectLookupIndex()
    {
        BuildPre0008DatabaseWithOneBlock();

        _runner.Apply(EmbeddedMigrations.Load());

        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_blocks_project';";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    public void Dispose() => _database.Dispose();
}

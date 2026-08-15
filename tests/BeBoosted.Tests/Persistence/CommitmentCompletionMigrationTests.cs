using BeBoosted.Domain;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Persistence;

/// <summary>
/// Upgrade coverage for 0009_commitment_completions: a version-8 database gains the
/// per-occurrence completion table without losing blocks or their project links.
/// </summary>
public sealed class CommitmentCompletionMigrationTests : IDisposable
{
    private const string ProjectId = "11111111-2222-3333-4444-555555555555";
    private const string BlockId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private readonly TempDatabase _database = new();
    private readonly MigrationRunner _runner;

    public CommitmentCompletionMigrationTests()
        => _runner = new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance);

    private void BuildVersion8DatabaseWithLinkedCommitment()
    {
        _runner.Apply(EmbeddedMigrations.Load().Where(m => m.Version <= 8).ToList());

        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO projects (id, name, accent_color, created_at, modified_at)
            VALUES ($projectId, 'Schoolwork', '#5B8DEF',
                    '2026-08-11T14:00:00.0000000-07:00', '2026-08-11T14:00:00.0000000-07:00');
            INSERT INTO calendar_blocks
                (id, task_id, project_id, title, date, start_time, end_time, kind, recurrence,
                 provider, external_id, sync_state, outcome, outcome_recorded_at,
                 created_at, modified_at)
            VALUES
                ($blockId, NULL, $projectId, 'Stats HW', '2026-08-11', '16:00:00.0000000',
                 '17:00:00.0000000', 0, NULL, 'local', NULL, 0, 0, NULL,
                 '2026-08-11T14:00:00.0000000-07:00', '2026-08-11T14:00:00.0000000-07:00');
            """;
        command.Parameters.AddWithValue("$projectId", ProjectId);
        command.Parameters.AddWithValue("$blockId", BlockId);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Upgrade_AddsTheCompletionTable_WithoutLosingBlocksOrProjectLinks()
    {
        BuildVersion8DatabaseWithLinkedCommitment();

        _runner.Apply(EmbeddedMigrations.Load());

        var blocks = new SqliteCalendarBlockRepository(_database.Factory);
        var block = blocks.GetById(CalendarBlockId.Parse(BlockId));
        Assert.NotNull(block);
        Assert.Equal("Stats HW", block.Title);
        Assert.Equal(BeBoosted.Domain.ProjectId.Parse(ProjectId), block.ProjectId);

        // The new table is usable immediately.
        var completions = new SqliteCommitmentCompletionRepository(_database.Factory);
        completions.Add(new BeBoosted.Domain.Calendar.CommitmentCompletion(
            block.Id, block.Date, new DateTimeOffset(2026, 8, 11, 17, 5, 0, TimeSpan.FromHours(-7))));
        Assert.NotNull(completions.Get(block.Id, block.Date));
    }

    public void Dispose() => _database.Dispose();
}

using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Persistence;

/// <summary>
/// Upgrade coverage for the proposal-integrity backstop: proposed_blocks gains a
/// cascading foreign key to tasks, legacy orphan rows are cleaned up instead of
/// failing the migration, valid rows keep every column, and drafts left with
/// nothing pending are discarded rather than shown as empty active drafts.
/// </summary>
public sealed class ProposedBlockIntegrityMigrationTests : IDisposable
{
    private const string TaskId = "11111111-1111-1111-1111-111111111111";
    private const string GhostTaskId = "99999999-9999-9999-9999-999999999999";
    private const string ProposalId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string OrphanProposalId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string ValidBlockId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string ApprovedBlockId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    private const string OrphanBlockId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";

    private readonly TempDatabase _database = new();
    private readonly MigrationRunner _runner;

    public ProposedBlockIntegrityMigrationTests()
        => _runner = new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance);

    private void Execute(string sql)
    {
        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long ScalarLong(string sql)
    {
        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private object? Scalar(string sql)
    {
        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>A version-10 database with a valid draft, an approved row, and orphans.</summary>
    private void BuildVersion10DatabaseWithOrphans()
    {
        _runner.Apply(EmbeddedMigrations.Load().Where(m => m.Version <= 10).ToList());
        Execute(
            $"""
            INSERT INTO tasks (id, title, origin, is_completed, created_at, modified_at)
            VALUES ('{TaskId}', 'Essay outline', 0, 0,
                    '2026-08-11T14:00:00.0000000-07:00', '2026-08-11T14:00:00.0000000-07:00');
            INSERT INTO planning_proposals (id, period_key, state, created_at, modified_at)
            VALUES ('{ProposalId}', 'today:2026-08-11', 0,
                    '2026-08-11T14:00:00.0000000-07:00', '2026-08-11T14:00:00.0000000-07:00'),
                   ('{OrphanProposalId}', 'today:2026-08-10', 0,
                    '2026-08-10T14:00:00.0000000-07:00', '2026-08-10T14:00:00.0000000-07:00');
            INSERT INTO proposed_blocks
                (id, proposal_id, task_id, date, start_time, end_time, status, session_label,
                 why_deadline, why_duration, why_priority, why_availability, why_source)
            VALUES
                ('{ValidBlockId}', '{ProposalId}', '{TaskId}', '2026-08-11',
                 '15:00:00.0000000', '16:00:00.0000000', 0, 'Session 1 of 2',
                 'due Fri', '1 h', '#1', 'free 15:00-17:00', NULL),
                ('{ApprovedBlockId}', '{ProposalId}', '{TaskId}', '2026-08-11',
                 '17:00:00.0000000', '18:00:00.0000000', 1, NULL,
                 NULL, '1 h', NULL, 'free 17:00-18:00', NULL),
                ('{OrphanBlockId}', '{OrphanProposalId}', '{GhostTaskId}', '2026-08-10',
                 '15:00:00.0000000', '16:00:00.0000000', 0, NULL,
                 NULL, '1 h', NULL, 'free 15:00-16:00', NULL);
            """);
    }

    [Fact]
    public void CleanDatabase_DeletingATask_CascadesToItsProposedBlocks()
    {
        _runner.Apply(EmbeddedMigrations.Load());
        Execute(
            $"""
            INSERT INTO tasks (id, title, origin, is_completed, created_at, modified_at)
            VALUES ('{TaskId}', 'Essay outline', 0, 0,
                    '2026-08-11T14:00:00.0000000-07:00', '2026-08-11T14:00:00.0000000-07:00');
            INSERT INTO planning_proposals (id, period_key, state, created_at, modified_at)
            VALUES ('{ProposalId}', 'today:2026-08-11', 0,
                    '2026-08-11T14:00:00.0000000-07:00', '2026-08-11T14:00:00.0000000-07:00');
            INSERT INTO proposed_blocks
                (id, proposal_id, task_id, date, start_time, end_time, status, session_label,
                 why_deadline, why_duration, why_priority, why_availability, why_source)
            VALUES ('{ValidBlockId}', '{ProposalId}', '{TaskId}', '2026-08-11',
                    '15:00:00.0000000', '16:00:00.0000000', 0, NULL,
                    NULL, '1 h', NULL, 'free', NULL);
            """);

        Execute($"DELETE FROM tasks WHERE id = '{TaskId}';");

        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM proposed_blocks;"));
    }

    [Fact]
    public void CleanDatabase_RejectsAProposedBlock_ForAMissingTask()
    {
        _runner.Apply(EmbeddedMigrations.Load());
        Execute(
            $"""
            INSERT INTO planning_proposals (id, period_key, state, created_at, modified_at)
            VALUES ('{ProposalId}', 'today:2026-08-11', 0,
                    '2026-08-11T14:00:00.0000000-07:00', '2026-08-11T14:00:00.0000000-07:00');
            """);

        Assert.Throws<SqliteException>(() => Execute(
            $"""
            INSERT INTO proposed_blocks
                (id, proposal_id, task_id, date, start_time, end_time, status, session_label,
                 why_deadline, why_duration, why_priority, why_availability, why_source)
            VALUES ('{OrphanBlockId}', '{ProposalId}', '{GhostTaskId}', '2026-08-11',
                    '15:00:00.0000000', '16:00:00.0000000', 0, NULL,
                    NULL, '1 h', NULL, 'free', NULL);
            """));
    }

    [Fact]
    public void Upgrade_From0010_DropsOrphans_KeepsValidRows_AndDiscardsEmptiedDrafts()
    {
        BuildVersion10DatabaseWithOrphans();

        _runner.Apply(EmbeddedMigrations.Load());

        // The legacy orphan is cleaned up instead of failing the migration.
        Assert.Equal(0, ScalarLong(
            $"SELECT COUNT(*) FROM proposed_blocks WHERE id = '{OrphanBlockId}';"));

        // Valid rows survive with every column and status intact.
        Assert.Equal(2, ScalarLong(
            $"SELECT COUNT(*) FROM proposed_blocks WHERE proposal_id = '{ProposalId}';"));
        Assert.Equal("Session 1 of 2", (string)Scalar(
            $"SELECT session_label FROM proposed_blocks WHERE id = '{ValidBlockId}';")!);
        Assert.Equal("due Fri", (string)Scalar(
            $"SELECT why_deadline FROM proposed_blocks WHERE id = '{ValidBlockId}';")!);
        Assert.Equal(1L, ScalarLong(
            $"SELECT status FROM proposed_blocks WHERE id = '{ApprovedBlockId}';"));

        // The draft that held only the orphan is discarded, not left active and empty.
        Assert.Equal(2L, ScalarLong(
            $"SELECT state FROM planning_proposals WHERE id = '{OrphanProposalId}';"));
        Assert.Equal(0L, ScalarLong(
            $"SELECT state FROM planning_proposals WHERE id = '{ProposalId}';"));

        // The proposal index survives the table rebuild.
        Assert.Equal(1, ScalarLong(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_proposed_blocks_proposal';"));

        // And the foreign key is enforced from now on.
        Execute($"DELETE FROM tasks WHERE id = '{TaskId}';");
        Assert.Equal(0, ScalarLong("SELECT COUNT(*) FROM proposed_blocks;"));
    }

    public void Dispose() => _database.Dispose();
}

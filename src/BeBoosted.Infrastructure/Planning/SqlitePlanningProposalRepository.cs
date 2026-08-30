using System.Globalization;
using BeBoosted.Application.Planning;
using BeBoosted.Domain;
using BeBoosted.Domain.Planning;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Infrastructure.Planning;

public sealed class SqlitePlanningProposalRepository : IPlanningProposalRepository
{
    private readonly SqliteConnectionFactory? _connectionFactory;
    private readonly SqliteConnection? _sharedConnection;
    private readonly SqliteTransaction? _transaction;

    public SqlitePlanningProposalRepository(SqliteConnectionFactory connectionFactory)
        => _connectionFactory = connectionFactory;

    /// <summary>Binds every operation to one shared connection and transaction.</summary>
    internal SqlitePlanningProposalRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _sharedConnection = connection;
        _transaction = transaction;
    }

    private SqliteSession OpenSession() => _sharedConnection is not null
        ? new SqliteSession(_sharedConnection, _transaction, ownsConnection: false)
        : new SqliteSession(_connectionFactory!.Open(), null, ownsConnection: true);

    public void Save(PlanningProposal proposal)
    {
        if (_sharedConnection is not null)
        {
            // Already inside an atomic mutation scope: enlist in its transaction.
            using var session = OpenSession();
            SaveCore(session, proposal);
            return;
        }

        using var connection = _connectionFactory!.Open();
        using var transaction = connection.BeginTransaction();
        using (var session = new SqliteSession(connection, transaction, ownsConnection: false))
        {
            SaveCore(session, proposal);
        }

        transaction.Commit();
    }

    private static void SaveCore(SqliteSession session, PlanningProposal proposal)
    {
        using (var upsert = session.CreateCommand())
        {
            upsert.CommandText =
                """
                INSERT INTO planning_proposals (id, period_key, state, created_at, modified_at)
                VALUES ($id, $periodKey, $state, $createdAt, $modifiedAt)
                ON CONFLICT (id) DO UPDATE SET state = excluded.state, modified_at = excluded.modified_at;
                """;
            upsert.Parameters.AddWithValue("$id", proposal.Id.ToString());
            upsert.Parameters.AddWithValue("$periodKey", proposal.Period.Key);
            upsert.Parameters.AddWithValue("$state", (long)proposal.State);
            upsert.Parameters.AddWithValue("$createdAt", proposal.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            upsert.Parameters.AddWithValue("$modifiedAt", proposal.ModifiedAt.ToString("O", CultureInfo.InvariantCulture));
            upsert.ExecuteNonQuery();
        }

        using (var clear = session.CreateCommand())
        {
            clear.CommandText = "DELETE FROM proposed_blocks WHERE proposal_id = $proposalId;";
            clear.Parameters.AddWithValue("$proposalId", proposal.Id.ToString());
            clear.ExecuteNonQuery();
        }

        foreach (var block in proposal.Blocks)
        {
            using var insert = session.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO proposed_blocks
                    (id, proposal_id, task_id, date, start_time, end_time, status, session_label,
                     why_deadline, why_duration, why_priority, why_availability, why_source)
                VALUES ($id, $proposalId, $taskId, $date, $startTime, $endTime, $status, $sessionLabel,
                        $whyDeadline, $whyDuration, $whyPriority, $whyAvailability, $whySource);
                """;
            insert.Parameters.AddWithValue("$id", block.Id.ToString());
            insert.Parameters.AddWithValue("$proposalId", proposal.Id.ToString());
            insert.Parameters.AddWithValue("$taskId", block.TaskId.ToString());
            insert.Parameters.AddWithValue("$date", block.Date.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$startTime", block.StartTime.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$endTime", block.EndTime.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$status", (long)block.Status);
            insert.Parameters.AddWithValue("$sessionLabel", (object?)block.SessionLabel ?? DBNull.Value);
            insert.Parameters.AddWithValue("$whyDeadline", (object?)block.Why.Deadline ?? DBNull.Value);
            insert.Parameters.AddWithValue("$whyDuration", block.Why.Duration);
            insert.Parameters.AddWithValue("$whyPriority", (object?)block.Why.Priority ?? DBNull.Value);
            insert.Parameters.AddWithValue("$whyAvailability", block.Why.Availability);
            insert.Parameters.AddWithValue("$whySource", (object?)block.Why.Source ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
    }

    public PlanningProposal? GetById(PlanningProposalId id)
        => Query(
            "SELECT id, period_key, state, created_at, modified_at FROM planning_proposals WHERE id = $key;",
            id.ToString()).FirstOrDefault();

    public PlanningProposal? GetActiveDraft()
        => Query(
            "SELECT id, period_key, state, created_at, modified_at FROM planning_proposals WHERE state = 0 ORDER BY created_at DESC LIMIT 1;",
            key: null).FirstOrDefault();

    public IReadOnlyList<PlanningProposal> GetAll()
        => Query(
            "SELECT id, period_key, state, created_at, modified_at FROM planning_proposals ORDER BY created_at;",
            key: null);

    public void Delete(PlanningProposalId id)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = "DELETE FROM planning_proposals WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    private List<PlanningProposal> Query(string sql, string? key)
    {
        using var session = OpenSession();
        var headers = new List<(PlanningProposalId Id, PlanningPeriod Period, ProposalState State,
            DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt)>();
        using (var command = session.CreateCommand())
        {
            command.CommandText = sql;
            if (key is not null)
            {
                command.Parameters.AddWithValue("$key", key);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                headers.Add((
                    PlanningProposalId.Parse(reader.GetString(0)),
                    ParsePeriod(reader.GetString(1)),
                    (ProposalState)reader.GetInt64(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
            }
        }

        return headers
            .Select(h => new PlanningProposal(
                h.Id, h.Period, LoadBlocks(session, h.Id), h.State, h.CreatedAt, h.ModifiedAt))
            .ToList();
    }

    private static List<ProposedBlock> LoadBlocks(SqliteSession session, PlanningProposalId proposalId)
    {
        using var command = session.CreateCommand();
        command.CommandText =
            """
            SELECT id, task_id, date, start_time, end_time, status, session_label,
                   why_deadline, why_duration, why_priority, why_availability, why_source
            FROM proposed_blocks WHERE proposal_id = $proposalId ORDER BY date, start_time;
            """;
        command.Parameters.AddWithValue("$proposalId", proposalId.ToString());
        using var reader = command.ExecuteReader();
        var blocks = new List<ProposedBlock>();
        while (reader.Read())
        {
            blocks.Add(new ProposedBlock(
                CalendarBlockId.Parse(reader.GetString(0)),
                TaskId.Parse(reader.GetString(1)),
                DateOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                TimeOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                TimeOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                new WhyEvidence(
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                (ProposedBlockStatus)reader.GetInt64(5)));
        }

        return blocks;
    }

    private static PlanningPeriod ParsePeriod(string key)
    {
        var parts = key.Split(':');
        return new PlanningPeriod(
            parts[0] == "today" ? PlanningPeriodKind.Today : PlanningPeriodKind.Week,
            DateOnly.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}

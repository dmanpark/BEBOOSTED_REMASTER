using System.Globalization;
using BeBoosted.Application.Calendar;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Infrastructure.Calendar;

public sealed class SqliteCalendarBlockRepository : ICalendarBlockRepository
{
    private readonly SqliteConnectionFactory? _connectionFactory;
    private readonly SqliteConnection? _sharedConnection;
    private readonly SqliteTransaction? _transaction;

    public SqliteCalendarBlockRepository(SqliteConnectionFactory connectionFactory)
        => _connectionFactory = connectionFactory;

    /// <summary>Binds every operation to one shared connection and transaction.</summary>
    internal SqliteCalendarBlockRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _sharedConnection = connection;
        _transaction = transaction;
    }

    private SqliteSession OpenSession() => _sharedConnection is not null
        ? new SqliteSession(_sharedConnection, _transaction, ownsConnection: false)
        : new SqliteSession(_connectionFactory!.Open(), null, ownsConnection: true);

    private const string Columns =
        "id, task_id, project_id, title, date, start_time, end_time, kind, recurrence, provider, " +
        "external_id, sync_state, outcome, outcome_recorded_at, created_at, modified_at";

    public void Add(CalendarBlock block)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO calendar_blocks ({Columns})
            VALUES ($id, $taskId, $projectId, $title, $date, $startTime, $endTime, $kind,
                    $recurrence, $provider, $externalId, $syncState, $outcome,
                    $outcomeRecordedAt, $createdAt, $modifiedAt);
            """;
        Bind(command, block);
        command.ExecuteNonQuery();
    }

    public void Update(CalendarBlock block)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            UPDATE calendar_blocks SET
                task_id = $taskId, project_id = $projectId, title = $title, date = $date,
                start_time = $startTime, end_time = $endTime, kind = $kind,
                recurrence = $recurrence, provider = $provider, external_id = $externalId,
                sync_state = $syncState, outcome = $outcome,
                outcome_recorded_at = $outcomeRecordedAt,
                created_at = $createdAt, modified_at = $modifiedAt
            WHERE id = $id;
            """;
        Bind(command, block);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new DomainException($"Calendar block {block.Id} no longer exists.");
        }
    }

    public void Delete(CalendarBlockId id)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = "DELETE FROM calendar_blocks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public CalendarBlock? GetById(CalendarBlockId id)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM calendar_blocks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<CalendarBlock> GetAll()
        => Query($"SELECT {Columns} FROM calendar_blocks ORDER BY date, start_time;", _ => { });

    public IReadOnlyList<CalendarBlock> GetCandidatesBetween(DateOnly from, DateOnly to)
        => Query(
            $"""
            SELECT {Columns} FROM calendar_blocks
            WHERE (date BETWEEN $from AND $to)
               OR (recurrence IS NOT NULL AND date <= $to)
            ORDER BY date, start_time;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$to", to.ToString("O", CultureInfo.InvariantCulture));
            });

    public IReadOnlyList<CalendarBlock> GetForTask(TaskId taskId)
        => Query(
            $"SELECT {Columns} FROM calendar_blocks WHERE task_id = $taskId ORDER BY date, start_time;",
            command => command.Parameters.AddWithValue("$taskId", taskId.ToString()));

    public IReadOnlyList<CalendarBlock> GetForProject(ProjectId projectId)
        => Query(
            $"SELECT {Columns} FROM calendar_blocks WHERE project_id = $projectId ORDER BY date, start_time;",
            command => command.Parameters.AddWithValue("$projectId", projectId.ToString()));

    public IReadOnlyList<CalendarBlock> GetElapsedWithoutOutcome(DateOnly today, TimeOnly now)
        => Query(
            $"""
            SELECT {Columns} FROM calendar_blocks
            WHERE kind = 1 AND outcome = 0
              AND (date < $today OR (date = $today AND end_time <= $now))
            ORDER BY date, start_time;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$today", today.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            });

    public IReadOnlySet<TaskId> GetTaskIdsWithPendingBlocks()
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT task_id FROM calendar_blocks WHERE task_id IS NOT NULL AND outcome = 0;";
        using var reader = command.ExecuteReader();
        var ids = new HashSet<TaskId>();
        while (reader.Read())
        {
            ids.Add(TaskId.Parse(reader.GetString(0)));
        }

        return ids;
    }

    private List<CalendarBlock> Query(string sql, Action<SqliteCommand> configure)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = sql;
        configure(command);
        using var reader = command.ExecuteReader();
        var blocks = new List<CalendarBlock>();
        while (reader.Read())
        {
            blocks.Add(Map(reader));
        }

        return blocks;
    }

    private static void Bind(SqliteCommand command, CalendarBlock block)
    {
        command.Parameters.AddWithValue("$id", block.Id.ToString());
        command.Parameters.AddWithValue(
            "$taskId", block.TaskId is { } taskId ? taskId.ToString() : DBNull.Value);
        command.Parameters.AddWithValue(
            "$projectId", block.ProjectId is { } projectId ? projectId.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$title", block.Title is { } title ? title : DBNull.Value);
        command.Parameters.AddWithValue("$date", block.Date.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$startTime", block.StartTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endTime", block.EndTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$kind", (long)block.Kind);
        command.Parameters.AddWithValue(
            "$recurrence",
            block.Recurrence is { } recurrence ? RecurrenceSerializer.Serialize(recurrence) : DBNull.Value);
        command.Parameters.AddWithValue("$provider", block.Provider);
        command.Parameters.AddWithValue(
            "$externalId", block.ExternalId is { } externalId ? externalId : DBNull.Value);
        command.Parameters.AddWithValue("$syncState", (long)block.SyncState);
        command.Parameters.AddWithValue("$outcome", (long)block.Outcome);
        command.Parameters.AddWithValue(
            "$outcomeRecordedAt",
            block.OutcomeRecordedAt is { } recorded
                ? recorded.ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", block.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$modifiedAt", block.ModifiedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static CalendarBlock Map(SqliteDataReader reader)
        => CalendarBlock.Rehydrate(
            CalendarBlockId.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : TaskId.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : ProjectId.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateOnly.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            TimeOnly.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            TimeOnly.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            (BlockKind)reader.GetInt64(7),
            reader.IsDBNull(8) ? null : RecurrenceSerializer.Deserialize(reader.GetString(8)),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            (int)reader.GetInt64(11),
            (BlockOutcome)reader.GetInt64(12),
            reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture));
}

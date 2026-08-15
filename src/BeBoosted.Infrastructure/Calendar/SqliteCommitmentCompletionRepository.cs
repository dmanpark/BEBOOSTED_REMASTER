using System.Globalization;
using BeBoosted.Application.Calendar;
using BeBoosted.Domain;
using BeBoosted.Domain.Calendar;
using BeBoosted.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Infrastructure.Calendar;

public sealed class SqliteCommitmentCompletionRepository : ICommitmentCompletionRepository
{
    private readonly SqliteConnectionFactory? _connectionFactory;
    private readonly SqliteConnection? _sharedConnection;
    private readonly SqliteTransaction? _transaction;

    public SqliteCommitmentCompletionRepository(SqliteConnectionFactory connectionFactory)
        => _connectionFactory = connectionFactory;

    /// <summary>Binds every operation to one shared connection and transaction.</summary>
    internal SqliteCommitmentCompletionRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _sharedConnection = connection;
        _transaction = transaction;
    }

    private SqliteSession OpenSession() => _sharedConnection is not null
        ? new SqliteSession(_sharedConnection, _transaction, ownsConnection: false)
        : new SqliteSession(_connectionFactory!.Open(), null, ownsConnection: true);

    public void Add(CommitmentCompletion completion)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            INSERT INTO commitment_completions (block_id, occurrence_date, completed_at)
            VALUES ($blockId, $occurrenceDate, $completedAt)
            ON CONFLICT (block_id, occurrence_date)
            DO UPDATE SET completed_at = excluded.completed_at;
            """;
        command.Parameters.AddWithValue("$blockId", completion.BlockId.ToString());
        command.Parameters.AddWithValue(
            "$occurrenceDate", completion.OccurrenceDate.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$completedAt", completion.CompletedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void Remove(CalendarBlockId blockId, DateOnly occurrenceDate)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            "DELETE FROM commitment_completions WHERE block_id = $blockId AND occurrence_date = $occurrenceDate;";
        command.Parameters.AddWithValue("$blockId", blockId.ToString());
        command.Parameters.AddWithValue(
            "$occurrenceDate", occurrenceDate.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public CommitmentCompletion? Get(CalendarBlockId blockId, DateOnly occurrenceDate)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            SELECT block_id, occurrence_date, completed_at FROM commitment_completions
            WHERE block_id = $blockId AND occurrence_date = $occurrenceDate;
            """;
        command.Parameters.AddWithValue("$blockId", blockId.ToString());
        command.Parameters.AddWithValue(
            "$occurrenceDate", occurrenceDate.ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<CommitmentCompletion> GetForBlock(CalendarBlockId blockId)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            SELECT block_id, occurrence_date, completed_at FROM commitment_completions
            WHERE block_id = $blockId
            ORDER BY occurrence_date;
            """;
        command.Parameters.AddWithValue("$blockId", blockId.ToString());
        using var reader = command.ExecuteReader();
        var completions = new List<CommitmentCompletion>();
        while (reader.Read())
        {
            completions.Add(Map(reader));
        }

        return completions;
    }

    public IReadOnlyList<CommitmentCompletion> GetBetween(DateOnly from, DateOnly to)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            SELECT block_id, occurrence_date, completed_at FROM commitment_completions
            WHERE occurrence_date BETWEEN $from AND $to
            ORDER BY occurrence_date;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        var completions = new List<CommitmentCompletion>();
        while (reader.Read())
        {
            completions.Add(Map(reader));
        }

        return completions;
    }

    private static CommitmentCompletion Map(SqliteDataReader reader)
        => new(
            CalendarBlockId.Parse(reader.GetString(0)),
            DateOnly.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
}

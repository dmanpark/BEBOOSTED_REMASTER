using System.Globalization;
using BeBoosted.Application.Tasks;
using BeBoosted.Domain;
using BeBoosted.Domain.Scheduling;
using BeBoosted.Domain.Tasks;
using BeBoosted.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Infrastructure.Tasks;

public sealed class SqliteTaskRepository(SqliteConnectionFactory connectionFactory) : ITaskRepository
{
    private const string Columns =
        "id, title, estimated_minutes, deadline, project_id, not_before, earliest_time, latest_time, " +
        "recurrence, origin, provenance_id, is_completed, completed_at, created_at, modified_at";

    public void Add(TaskItem task)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO tasks ({Columns})
            VALUES ($id, $title, $estimatedMinutes, $deadline, $projectId, $notBefore, $earliestTime,
                    $latestTime, $recurrence, $origin, $provenanceId, $isCompleted, $completedAt,
                    $createdAt, $modifiedAt);
            """;
        BindParameters(command, task);
        command.ExecuteNonQuery();
    }

    public void Update(TaskItem task)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE tasks SET
                title = $title, estimated_minutes = $estimatedMinutes, deadline = $deadline,
                project_id = $projectId, not_before = $notBefore, earliest_time = $earliestTime,
                latest_time = $latestTime, recurrence = $recurrence, origin = $origin,
                provenance_id = $provenanceId, is_completed = $isCompleted,
                completed_at = $completedAt, created_at = $createdAt, modified_at = $modifiedAt
            WHERE id = $id;
            """;
        BindParameters(command, task);
        var rows = command.ExecuteNonQuery();
        if (rows == 0)
        {
            throw new DomainException($"Task {task.Id} no longer exists.");
        }
    }

    public void Delete(TaskId id)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public TaskItem? GetById(TaskId id)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<TaskItem> GetAll() => Query($"SELECT {Columns} FROM tasks ORDER BY created_at;");

    public IReadOnlyList<TaskItem> GetInbox()
        => Query($"SELECT {Columns} FROM tasks WHERE is_completed = 0 ORDER BY created_at;");

    private List<TaskItem> Query(string sql)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var tasks = new List<TaskItem>();
        while (reader.Read())
        {
            tasks.Add(Map(reader));
        }

        return tasks;
    }

    private static void BindParameters(SqliteCommand command, TaskItem task)
    {
        command.Parameters.AddWithValue("$id", task.Id.ToString());
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue(
            "$estimatedMinutes",
            task.EstimatedDuration is { } duration ? (object)(long)duration.TotalMinutes : DBNull.Value);
        command.Parameters.AddWithValue(
            "$deadline",
            task.Deadline is { } deadline ? deadline.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$projectId",
            task.ProjectId is { } projectId ? projectId.ToString() : DBNull.Value);
        command.Parameters.AddWithValue(
            "$notBefore",
            task.Constraints?.NotBefore is { } notBefore
                ? notBefore.ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$earliestTime",
            task.Constraints?.EarliestTime is { } earliest
                ? earliest.ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$latestTime",
            task.Constraints?.LatestTime is { } latest
                ? latest.ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$recurrence",
            task.Recurrence is { } recurrence ? RecurrenceSerializer.Serialize(recurrence) : DBNull.Value);
        command.Parameters.AddWithValue("$origin", (long)task.Origin);
        command.Parameters.AddWithValue(
            "$provenanceId",
            task.ProvenanceId is { } provenanceId ? provenanceId.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$isCompleted", task.IsCompleted ? 1L : 0L);
        command.Parameters.AddWithValue(
            "$completedAt",
            task.CompletedAt is { } completedAt
                ? completedAt.ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", task.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$modifiedAt", task.ModifiedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static TaskItem Map(SqliteDataReader reader)
    {
        var earliestTime = reader.IsDBNull(6)
            ? (TimeOnly?)null
            : TimeOnly.Parse(reader.GetString(6), CultureInfo.InvariantCulture);
        var latestTime = reader.IsDBNull(7)
            ? (TimeOnly?)null
            : TimeOnly.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
        var notBefore = reader.IsDBNull(5)
            ? (DateOnly?)null
            : DateOnly.Parse(reader.GetString(5), CultureInfo.InvariantCulture);
        var constraints = notBefore is null && earliestTime is null && latestTime is null
            ? null
            : new SchedulingConstraints(notBefore, earliestTime, latestTime);

        return TaskItem.Rehydrate(
            TaskId.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : TimeSpan.FromMinutes(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : DateOnly.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : ProjectId.Parse(reader.GetString(4)),
            constraints,
            reader.IsDBNull(8) ? null : RecurrenceSerializer.Deserialize(reader.GetString(8)),
            (TaskOrigin)reader.GetInt64(9),
            reader.IsDBNull(10) ? null : AiProvenanceId.Parse(reader.GetString(10)),
            reader.GetInt64(11) == 1,
            reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture));
    }
}

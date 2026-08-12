using System.Globalization;
using BeBoosted.Application.Prioritization;
using BeBoosted.Domain;
using BeBoosted.Domain.Prioritization;
using BeBoosted.Infrastructure.Persistence;

namespace BeBoosted.Infrastructure.Prioritization;

public sealed class SqlitePrioritizationRepository(SqliteConnectionFactory connectionFactory)
    : IPrioritizationRepository
{
    public void SaveDecisions(IReadOnlyList<ComparisonDecision> decisions)
    {
        if (decisions.Count == 0)
        {
            return;
        }

        using var connection = connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var decision in decisions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO comparisons (id, period_key, left_task_id, right_task_id, result, decided_at)
                VALUES ($id, $periodKey, $left, $right, $result, $decidedAt);
                """;
            command.Parameters.AddWithValue("$id", decision.Id.ToString());
            command.Parameters.AddWithValue("$periodKey", decision.Period.Key);
            command.Parameters.AddWithValue("$left", decision.LeftTaskId.ToString());
            command.Parameters.AddWithValue("$right", decision.RightTaskId.ToString());
            command.Parameters.AddWithValue("$result", (long)decision.Result);
            command.Parameters.AddWithValue(
                "$decidedAt", decision.DecidedAt.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<ComparisonDecision> GetDecisions(string periodKey)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, period_key, left_task_id, right_task_id, result, decided_at
            FROM comparisons WHERE period_key = $periodKey ORDER BY decided_at, id;
            """;
        command.Parameters.AddWithValue("$periodKey", periodKey);
        using var reader = command.ExecuteReader();
        var decisions = new List<ComparisonDecision>();
        while (reader.Read())
        {
            decisions.Add(new ComparisonDecision(
                ComparisonId.Parse(reader.GetString(0)),
                ParsePeriod(reader.GetString(1)),
                TaskId.Parse(reader.GetString(2)),
                TaskId.Parse(reader.GetString(3)),
                (ComparisonResult)reader.GetInt64(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        }

        return decisions;
    }

    public void ReplaceRanks(string periodKey, IReadOnlyList<PriorityRank> ranks)
    {
        using var connection = connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM priority_ranks WHERE period_key = $periodKey;";
            delete.Parameters.AddWithValue("$periodKey", periodKey);
            delete.ExecuteNonQuery();
        }

        foreach (var rank in ranks)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO priority_ranks (period_key, task_id, rank, tier)
                VALUES ($periodKey, $taskId, $rank, $tier);
                """;
            insert.Parameters.AddWithValue("$periodKey", periodKey);
            insert.Parameters.AddWithValue("$taskId", rank.TaskId.ToString());
            insert.Parameters.AddWithValue("$rank", (long)rank.Rank);
            insert.Parameters.AddWithValue("$tier", (long)rank.Tier);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<PriorityRank> GetRanks(string periodKey)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT task_id, rank, tier FROM priority_ranks WHERE period_key = $periodKey ORDER BY rank, task_id;";
        command.Parameters.AddWithValue("$periodKey", periodKey);
        using var reader = command.ExecuteReader();
        var ranks = new List<PriorityRank>();
        while (reader.Read())
        {
            ranks.Add(new PriorityRank(
                TaskId.Parse(reader.GetString(0)),
                (int)reader.GetInt64(1),
                (PlanningTier)reader.GetInt64(2)));
        }

        return ranks;
    }

    private static PlanningPeriod ParsePeriod(string key)
    {
        var parts = key.Split(':');
        var anchor = DateOnly.Parse(parts[1], CultureInfo.InvariantCulture);
        return new PlanningPeriod(
            parts[0] == "today" ? PlanningPeriodKind.Today : PlanningPeriodKind.Week,
            anchor);
    }
}

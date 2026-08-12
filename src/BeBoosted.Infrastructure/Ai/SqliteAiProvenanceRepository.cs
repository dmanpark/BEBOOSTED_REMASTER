using System.Globalization;
using BeBoosted.Application.Ai;
using BeBoosted.Domain;
using BeBoosted.Domain.Ai;
using BeBoosted.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Infrastructure.Ai;

public sealed class SqliteAiProvenanceRepository(SqliteConnectionFactory connectionFactory)
    : IAiProvenanceRepository
{
    public void Add(AiProvenance provenance)
    {
        using var connection = connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO ai_provenance (id, operation, needs_review, created_at)
                VALUES ($id, $operation, $needsReview, $createdAt);
                """;
            insert.Parameters.AddWithValue("$id", provenance.Id.ToString());
            insert.Parameters.AddWithValue("$operation", (long)provenance.Operation);
            insert.Parameters.AddWithValue("$needsReview", provenance.NeedsReview ? 1L : 0L);
            insert.Parameters.AddWithValue(
                "$createdAt", provenance.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        foreach (var resourceId in provenance.SourceResourceIds)
        {
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText =
                "INSERT INTO ai_provenance_sources (provenance_id, resource_id) VALUES ($provenanceId, $resourceId);";
            link.Parameters.AddWithValue("$provenanceId", provenance.Id.ToString());
            link.Parameters.AddWithValue("$resourceId", resourceId.ToString());
            link.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Update(AiProvenance provenance)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ai_provenance SET needs_review = $needsReview WHERE id = $id;";
        command.Parameters.AddWithValue("$id", provenance.Id.ToString());
        command.Parameters.AddWithValue("$needsReview", provenance.NeedsReview ? 1L : 0L);
        command.ExecuteNonQuery();
    }

    public AiProvenance? GetById(AiProvenanceId id)
    {
        using var connection = connectionFactory.Open();
        return Load(connection, "WHERE p.id = $key", id.ToString()).FirstOrDefault();
    }

    public IReadOnlyList<AiProvenance> GetBySourceResource(ResourceId resourceId)
    {
        using var connection = connectionFactory.Open();
        return Load(
            connection,
            "WHERE p.id IN (SELECT provenance_id FROM ai_provenance_sources WHERE resource_id = $key)",
            resourceId.ToString());
    }

    public void AddAnswer(AiAnswer answer)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ai_answers (id, provenance_id, project_id, question, answer, created_at)
            VALUES ($id, $provenanceId, $projectId, $question, $answer, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", answer.Id.ToString());
        command.Parameters.AddWithValue("$provenanceId", answer.ProvenanceId.ToString());
        command.Parameters.AddWithValue("$projectId", answer.ProjectId.ToString());
        command.Parameters.AddWithValue("$question", answer.Question);
        command.Parameters.AddWithValue("$answer", answer.AnswerText);
        command.Parameters.AddWithValue("$createdAt", answer.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AiAnswer> GetAnswersForProject(ProjectId projectId)
        => LoadAnswers("WHERE project_id = $key", projectId.ToString());

    public AiAnswer? GetAnswerByProvenance(AiProvenanceId provenanceId)
        => LoadAnswers("WHERE provenance_id = $key", provenanceId.ToString()).FirstOrDefault();

    private List<AiProvenance> Load(SqliteConnection connection, string whereClause, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT p.id, p.operation, p.needs_review, p.created_at FROM ai_provenance p {whereClause};";
        command.Parameters.AddWithValue("$key", key);
        var records = new List<(AiProvenanceId Id, AiOperationKind Op, bool NeedsReview, DateTimeOffset Created)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                records.Add((
                    AiProvenanceId.Parse(reader.GetString(0)),
                    (AiOperationKind)reader.GetInt64(1),
                    reader.GetInt64(2) == 1,
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
            }
        }

        var results = new List<AiProvenance>(records.Count);
        foreach (var record in records)
        {
            using var sources = connection.CreateCommand();
            sources.CommandText =
                "SELECT resource_id FROM ai_provenance_sources WHERE provenance_id = $provenanceId;";
            sources.Parameters.AddWithValue("$provenanceId", record.Id.ToString());
            var resourceIds = new List<ResourceId>();
            using (var reader = sources.ExecuteReader())
            {
                while (reader.Read())
                {
                    resourceIds.Add(ResourceId.Parse(reader.GetString(0)));
                }
            }

            results.Add(AiProvenance.Rehydrate(
                record.Id, record.Op, resourceIds, record.NeedsReview, record.Created));
        }

        return results;
    }

    private List<AiAnswer> LoadAnswers(string whereClause, string key)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT id, provenance_id, project_id, question, answer, created_at FROM ai_answers {whereClause} ORDER BY created_at;";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        var answers = new List<AiAnswer>();
        while (reader.Read())
        {
            answers.Add(new AiAnswer(
                Guid.Parse(reader.GetString(0)),
                AiProvenanceId.Parse(reader.GetString(1)),
                ProjectId.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
        }

        return answers;
    }
}

using Microsoft.Extensions.Logging;

namespace BeBoosted.Infrastructure.Persistence;

/// <summary>
/// Applies forward-only SQL migrations in version order. Each migration runs in its own
/// transaction and is recorded in schema_migrations, so re-running is a no-op.
/// </summary>
public sealed class MigrationRunner(SqliteConnectionFactory connectionFactory, ILogger<MigrationRunner> logger)
{
    /// <summary>Applies pending migrations and returns the ones that ran.</summary>
    public IReadOnlyList<Migration> Apply(IReadOnlyList<Migration> migrations)
    {
        var duplicate = migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate migration version {duplicate.Key}.");
        }

        using var connection = connectionFactory.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    applied_at_utc TEXT NOT NULL
                ) STRICT;
                """;
            create.ExecuteNonQuery();
        }

        var appliedVersions = new HashSet<long>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT version FROM schema_migrations;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                appliedVersions.Add(reader.GetInt64(0));
            }
        }

        var newlyApplied = new List<Migration>();
        foreach (var migration in migrations.OrderBy(m => m.Version))
        {
            if (appliedVersions.Contains(migration.Version))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var apply = connection.CreateCommand())
            {
                apply.Transaction = transaction;
                apply.CommandText = migration.Sql;
                apply.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_migrations (version, name, applied_at_utc) VALUES ($version, $name, $appliedAt);";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$appliedAt", DateTime.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
            newlyApplied.Add(migration);
            logger.LogInformation("Applied migration {Version} {Name}", migration.Version, migration.Name);
        }

        return newlyApplied;
    }
}

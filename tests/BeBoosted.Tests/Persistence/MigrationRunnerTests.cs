using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Persistence;

public sealed class MigrationRunnerTests : IDisposable
{
    private readonly TempDatabase _database = new();

    private MigrationRunner CreateRunner() => new(_database.Factory, NullLogger<MigrationRunner>.Instance);

    [Fact]
    public void Apply_RunsMigrationsInVersionOrder_EvenWhenListIsUnordered()
    {
        var migrations = new[]
        {
            new Migration(2, "second", "CREATE TABLE two (id INTEGER PRIMARY KEY) STRICT;"),
            new Migration(1, "first", "CREATE TABLE one (id INTEGER PRIMARY KEY) STRICT;"),
        };

        var applied = CreateRunner().Apply(migrations);

        Assert.Equal([1, 2], applied.Select(m => m.Version));
        Assert.Equal(["one", "two"], ListTables().Where(t => t is "one" or "two"));
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var migrations = new[] { new Migration(1, "first", "CREATE TABLE one (id INTEGER PRIMARY KEY) STRICT;") };
        var runner = CreateRunner();

        var first = runner.Apply(migrations);
        var second = runner.Apply(migrations);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void Apply_OnlyRunsPendingMigrationsOnUpgrade()
    {
        var runner = CreateRunner();
        runner.Apply([new Migration(1, "first", "CREATE TABLE one (id INTEGER PRIMARY KEY) STRICT;")]);

        var applied = runner.Apply(
        [
            new Migration(1, "first", "CREATE TABLE one (id INTEGER PRIMARY KEY) STRICT;"),
            new Migration(2, "second", "CREATE TABLE two (id INTEGER PRIMARY KEY) STRICT;"),
        ]);

        Assert.Equal([2], applied.Select(m => m.Version));
    }

    [Fact]
    public void Apply_ThrowsOnDuplicateVersions()
    {
        var migrations = new[]
        {
            new Migration(1, "a", "SELECT 1;"),
            new Migration(1, "b", "SELECT 1;"),
        };

        Assert.Throws<InvalidOperationException>(() => CreateRunner().Apply(migrations));
    }

    [Fact]
    public void Apply_RollsBackFailedMigrationWithoutRecordingIt()
    {
        var runner = CreateRunner();
        var migrations = new[]
        {
            new Migration(1, "good", "CREATE TABLE one (id INTEGER PRIMARY KEY) STRICT;"),
            new Migration(2, "bad", "CREATE TABLE broken (id INTEGER PRIMARY KEY) STRICT; INSERT INTO nonexistent VALUES (1);"),
        };

        Assert.Throws<SqliteException>(() => runner.Apply(migrations));

        Assert.Contains("one", ListTables());
        Assert.DoesNotContain("broken", ListTables());
        Assert.Equal([1L], ListAppliedVersions());
    }

    [Fact]
    public void EmbeddedMigrations_LoadAndProduceSettingsTable()
    {
        var migrations = EmbeddedMigrations.Load();

        Assert.NotEmpty(migrations);
        Assert.Equal(1, migrations[0].Version);
        Assert.Equal(migrations.Select(m => m.Version).OrderBy(v => v), migrations.Select(m => m.Version));
        Assert.Equal(migrations.Select(m => m.Version).Distinct().Count(), migrations.Count);

        CreateRunner().Apply(migrations);
        Assert.Contains("settings", ListTables());
    }

    public void Dispose() => _database.Dispose();

    private List<string> ListTables()
    {
        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private List<long> ListAppliedVersions()
    {
        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();
        var versions = new List<long>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt64(0));
        }

        return versions;
    }
}

using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Persistence;

/// <summary>
/// Upgrade coverage for 0013_resource_groups: an existing database gains an empty
/// resource_groups table and a nullable resources.group_id, and every resource row it
/// already held survives byte-for-byte as a loose resource. Seeded with raw SQL rather
/// than the repositories so the assertions describe the on-disk schema, not the mapper
/// that is changing in the same task.
/// </summary>
public sealed class ResourceGroupsMigrationTests : IDisposable
{
    private const string ProjectIdText = "31111111-2222-3333-4444-555555555555";
    private const string FileIdText = "3aaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ResourceIdText = "39999999-8888-7777-6666-555555555555";
    private const string StoredPathText = "CAPPs\\Transcripts\\Transcript.pdf";
    private const string IndexTextText = "quetzalcoatlus";

    private readonly TempDatabase _database = new();
    private readonly MigrationRunner _runner;

    public ResourceGroupsMigrationTests()
        => _runner = new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance);

    private void BuildPre0013DatabaseWithAStoredResource()
    {
        _runner.Apply(EmbeddedMigrations.Load().Where(m => m.Version <= 12).ToList());

        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO projects (id, name, accent_color, folder_segment, created_at, modified_at)
            VALUES ('{ProjectIdText}', 'CAPPs', '#5A6B7F', 'CAPPs',
                    '2026-08-01T09:00:00.0000000-07:00', '2026-08-01T09:00:00.0000000-07:00');

            INSERT INTO project_files (id, project_id, title, description, folder_segment, created_at, modified_at)
            VALUES ('{FileIdText}', '{ProjectIdText}', 'Transcripts', NULL, 'Transcripts',
                    '2026-08-01T09:05:00.0000000-07:00', '2026-08-01T09:05:00.0000000-07:00');

            INSERT INTO resources (id, file_id, kind, title, url, content, original_file_name,
                                   stored_path, added_at, index_state, index_text, modified_at)
            VALUES ('{ResourceIdText}', '{FileIdText}', 0, 'Transcript', NULL, NULL, 'Transcript.pdf',
                    '{StoredPathText}', '2026-08-01T09:10:00.0000000-07:00', 1, '{IndexTextText}',
                    '2026-08-01T09:10:00.0000000-07:00');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Applied twice: the runner records versions, so the second pass is a no-op.</summary>
    private void ApplyEverythingTwice()
    {
        _runner.Apply(EmbeddedMigrations.Load());
        _runner.Apply(EmbeddedMigrations.Load());
    }

    private object? Scalar(string sql)
    {
        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    [Fact]
    public void Upgrade_LeavesTheExistingResourceRowIntact()
    {
        BuildPre0013DatabaseWithAStoredResource();

        ApplyEverythingTwice();

        Assert.Equal(ResourceIdText, Scalar($"SELECT id FROM resources WHERE id = '{ResourceIdText}';"));
        Assert.Equal(StoredPathText, Scalar($"SELECT stored_path FROM resources WHERE id = '{ResourceIdText}';"));
        Assert.Equal(IndexTextText, Scalar($"SELECT index_text FROM resources WHERE id = '{ResourceIdText}';"));
    }

    [Fact]
    public void Upgrade_LeavesEveryExistingResourceLoose()
    {
        BuildPre0013DatabaseWithAStoredResource();

        ApplyEverythingTwice();

        Assert.Null(Scalar($"SELECT group_id FROM resources WHERE id = '{ResourceIdText}';"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM resources WHERE group_id IS NOT NULL;"));
    }

    [Fact]
    public void Upgrade_CreatesAnEmptyResourceGroupsTable()
    {
        BuildPre0013DatabaseWithAStoredResource();

        ApplyEverythingTwice();

        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM resource_groups;"));
    }

    /// <summary>
    /// A fresh connection, opened after the upgrade, sees the new shape — the ALTER is
    /// really committed to the file and not merely visible to the migrating connection.
    /// </summary>
    [Fact]
    public void Upgrade_AFreshConnectionReadsTheNewColumnAndTable()
    {
        BuildPre0013DatabaseWithAStoredResource();

        ApplyEverythingTwice();

        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM resources r
            LEFT JOIN resource_groups g ON g.id = r.group_id
            WHERE r.group_id IS NULL;
            """;
        Assert.Equal(1L, command.ExecuteScalar());
    }

    public void Dispose() => _database.Dispose();
}

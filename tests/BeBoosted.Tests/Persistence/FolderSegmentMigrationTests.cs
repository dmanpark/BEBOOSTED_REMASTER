using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Persistence;

/// <summary>
/// Upgrade coverage for 0012_folder_segments: projects and project_files each gain a
/// non-null folder_segment column, defaulting existing rows to the empty "not yet
/// claimed" sentinel that Task 7's backfill looks for.
/// </summary>
public sealed class FolderSegmentMigrationTests : IDisposable
{
    private const string ProjectIdText = "11111111-2222-3333-4444-555555555555";
    private const string FileIdText = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private readonly TempDatabase _database = new();
    private readonly MigrationRunner _runner;

    public FolderSegmentMigrationTests()
        => _runner = new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance);

    private void BuildPre0012DatabaseWithProjectAndFile()
    {
        _runner.Apply(EmbeddedMigrations.Load().Where(m => m.Version <= 11).ToList());

        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO projects (id, name, accent_color, created_at, modified_at)
            VALUES ('{ProjectIdText}', 'CAPPs', '#5A6B7F',
                    '2026-08-01T09:00:00.0000000-07:00', '2026-08-01T09:00:00.0000000-07:00');

            INSERT INTO project_files (id, project_id, title, description, created_at, modified_at)
            VALUES ('{FileIdText}', '{ProjectIdText}', 'Transcripts', NULL,
                    '2026-08-01T09:05:00.0000000-07:00', '2026-08-01T09:05:00.0000000-07:00');
            """;
        command.ExecuteNonQuery();
    }

    private string? ScalarText(string sql)
    {
        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private long ScalarLong(string sql)
    {
        using var connection = _database.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Upgrade_AddsFolderSegmentColumns_DefaultingExistingRowsToEmptyString()
    {
        BuildPre0012DatabaseWithProjectAndFile();

        _runner.Apply(EmbeddedMigrations.Load());

        Assert.Equal(string.Empty, ScalarText(
            $"SELECT folder_segment FROM projects WHERE id = '{ProjectIdText}';"));
        Assert.Equal(string.Empty, ScalarText(
            $"SELECT folder_segment FROM project_files WHERE id = '{FileIdText}';"));
    }

    [Fact]
    public void Upgrade_KeepsTheColumnsNonNull()
    {
        BuildPre0012DatabaseWithProjectAndFile();

        _runner.Apply(EmbeddedMigrations.Load());

        Assert.Equal(0, ScalarLong(
            "SELECT COUNT(*) FROM projects WHERE folder_segment IS NULL;"));
        Assert.Equal(0, ScalarLong(
            "SELECT COUNT(*) FROM project_files WHERE folder_segment IS NULL;"));
    }

    [Fact]
    public void Upgrade_DatabaseReopens_AndRepositoriesRoundTripTheDefaultSegment()
    {
        BuildPre0012DatabaseWithProjectAndFile();

        _runner.Apply(EmbeddedMigrations.Load());

        var projects = new SqliteProjectRepository(_database.Factory);
        var project = projects.GetById(ProjectId.Parse(ProjectIdText));
        Assert.NotNull(project);
        Assert.Equal(string.Empty, project.FolderSegment);

        var files = new SqliteProjectFileRepository(_database.Factory);
        var file = files.GetById(ProjectFileId.Parse(FileIdText));
        Assert.NotNull(file);
        Assert.Equal(string.Empty, file.FolderSegment);
    }

    public void Dispose() => _database.Dispose();
}

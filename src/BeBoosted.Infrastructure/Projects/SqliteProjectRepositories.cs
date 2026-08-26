using System.Globalization;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Infrastructure.Projects;

public sealed class SqliteProjectRepository(SqliteConnectionFactory connectionFactory) : IProjectRepository
{
    public void Add(Project project)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO projects (id, name, accent_color, created_at, modified_at)
            VALUES ($id, $name, $accent, $createdAt, $modifiedAt);
            """;
        Bind(command, project);
        command.ExecuteNonQuery();
    }

    public void Update(Project project)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE projects SET name = $name, accent_color = $accent, modified_at = $modifiedAt
            WHERE id = $id;
            """;
        Bind(command, project);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new DomainException("That project no longer exists.");
        }
    }

    public void Delete(ProjectId id)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public Project? GetById(ProjectId id)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, accent_color, created_at, modified_at FROM projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<Project> GetAll()
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, accent_color, created_at, modified_at FROM projects ORDER BY created_at;";
        using var reader = command.ExecuteReader();
        var projects = new List<Project>();
        while (reader.Read())
        {
            projects.Add(Map(reader));
        }

        return projects;
    }

    private static void Bind(SqliteCommand command, Project project)
    {
        command.Parameters.AddWithValue("$id", project.Id.ToString());
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$accent", project.AccentColor);
        command.Parameters.AddWithValue("$createdAt", project.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$modifiedAt", project.ModifiedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static Project Map(SqliteDataReader reader)
        => Project.Rehydrate(
            ProjectId.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
}

public sealed class SqliteProjectFileRepository(SqliteConnectionFactory connectionFactory) : IProjectFileRepository
{
    private const string Columns = "id, project_id, title, description, created_at, modified_at";

    public void Add(ProjectFile file)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO project_files ({Columns})
            VALUES ($id, $projectId, $title, $description, $createdAt, $modifiedAt);
            """;
        Bind(command, file);
        command.ExecuteNonQuery();
    }

    public void Update(ProjectFile file)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE project_files SET title = $title, description = $description, modified_at = $modifiedAt
            WHERE id = $id;
            """;
        Bind(command, file);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new DomainException("That File no longer exists.");
        }
    }

    public void Delete(ProjectFileId id)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM project_files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public ProjectFile? GetById(ProjectFileId id)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM project_files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<ProjectFile> GetForProject(ProjectId projectId)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM project_files WHERE project_id = $projectId ORDER BY created_at;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        using var reader = command.ExecuteReader();
        var files = new List<ProjectFile>();
        while (reader.Read())
        {
            files.Add(Map(reader));
        }

        return files;
    }

    private static void Bind(SqliteCommand command, ProjectFile file)
    {
        command.Parameters.AddWithValue("$id", file.Id.ToString());
        command.Parameters.AddWithValue("$projectId", file.ProjectId.ToString());
        command.Parameters.AddWithValue("$title", file.Title);
        command.Parameters.AddWithValue("$description", (object?)file.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", file.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$modifiedAt", file.ModifiedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static ProjectFile Map(SqliteDataReader reader)
        => ProjectFile.Rehydrate(
            ProjectFileId.Parse(reader.GetString(0)),
            ProjectId.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture));
}

public sealed class SqliteResourceRepository(SqliteConnectionFactory connectionFactory) : IResourceRepository
{
    private const string Columns =
        "id, file_id, kind, title, url, content, original_file_name, stored_path, added_at, index_state, modified_at";

    public void Add(Resource resource)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO resources ({Columns})
            VALUES ($id, $fileId, $kind, $title, $url, $content, $originalFileName, $storedPath,
                    $addedAt, $indexState, $modifiedAt);
            """;
        Bind(command, resource);
        command.ExecuteNonQuery();
    }

    public void Update(Resource resource)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE resources SET
                title = $title, url = $url, content = $content, stored_path = $storedPath,
                index_state = $indexState, modified_at = $modifiedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", resource.Id.ToString());
        command.Parameters.AddWithValue("$title", resource.Title);
        command.Parameters.AddWithValue("$url", (object?)resource.Url ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", (object?)resource.Content ?? DBNull.Value);
        // The layout reconciler relocates stored bytes, so the path is no longer fixed.
        command.Parameters.AddWithValue("$storedPath", (object?)resource.StoredPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$indexState", (long)resource.IndexState);
        command.Parameters.AddWithValue("$modifiedAt", resource.ModifiedAt.ToString("O", CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() == 0)
        {
            throw new DomainException("That resource no longer exists.");
        }
    }

    public void Delete(ResourceId id)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM resources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public Resource? GetById(ResourceId id)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM resources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<Resource> GetForFile(ProjectFileId fileId)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM resources WHERE file_id = $fileId ORDER BY added_at;";
        command.Parameters.AddWithValue("$fileId", fileId.ToString());
        using var reader = command.ExecuteReader();
        var resources = new List<Resource>();
        while (reader.Read())
        {
            resources.Add(Map(reader));
        }

        return resources;
    }

    public int CountForFile(ProjectFileId fileId)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM resources WHERE file_id = $fileId;";
        command.Parameters.AddWithValue("$fileId", fileId.ToString());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void SetIndexText(ResourceId id, string text)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE resources SET index_text = $text WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Resource> SearchInProject(ProjectId projectId, string query)
    {
        using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.id, r.file_id, r.kind, r.title, r.url, r.content, r.original_file_name,
                   r.stored_path, r.added_at, r.index_state, r.modified_at
            FROM resources r
            JOIN project_files f ON f.id = r.file_id
            WHERE f.project_id = $projectId
              AND (r.index_text LIKE $query COLLATE NOCASE OR r.title LIKE $query COLLATE NOCASE)
            ORDER BY r.added_at;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$query", $"%{query}%");
        using var reader = command.ExecuteReader();
        var resources = new List<Resource>();
        while (reader.Read())
        {
            resources.Add(Map(reader));
        }

        return resources;
    }

    private static void Bind(SqliteCommand command, Resource resource)
    {
        command.Parameters.AddWithValue("$id", resource.Id.ToString());
        command.Parameters.AddWithValue("$fileId", resource.FileId.ToString());
        command.Parameters.AddWithValue("$kind", (long)resource.Kind);
        command.Parameters.AddWithValue("$title", resource.Title);
        command.Parameters.AddWithValue("$url", (object?)resource.Url ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", (object?)resource.Content ?? DBNull.Value);
        command.Parameters.AddWithValue("$originalFileName", (object?)resource.OriginalFileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$storedPath", (object?)resource.StoredPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$addedAt", resource.AddedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$indexState", (long)resource.IndexState);
        command.Parameters.AddWithValue("$modifiedAt", resource.ModifiedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static Resource Map(SqliteDataReader reader)
        => Resource.Rehydrate(
            ResourceId.Parse(reader.GetString(0)),
            ProjectFileId.Parse(reader.GetString(1)),
            (ResourceKind)reader.GetInt64(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
            (ResourceIndexState)reader.GetInt64(9),
            DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture));
}

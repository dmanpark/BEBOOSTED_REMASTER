using System.Globalization;
using BeBoosted.Application.Projects;
using BeBoosted.Domain;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Infrastructure.Projects;

public sealed class SqliteProjectRepository : IProjectRepository
{
    private readonly SqliteConnectionFactory? _connectionFactory;
    private readonly SqliteConnection? _sharedConnection;
    private readonly SqliteTransaction? _transaction;

    public SqliteProjectRepository(SqliteConnectionFactory connectionFactory)
        => _connectionFactory = connectionFactory;

    /// <summary>Binds every operation to one shared connection and transaction.</summary>
    internal SqliteProjectRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _sharedConnection = connection;
        _transaction = transaction;
    }

    private SqliteSession OpenSession() => _sharedConnection is not null
        ? new SqliteSession(_sharedConnection, _transaction, ownsConnection: false)
        : new SqliteSession(_connectionFactory!.Open(), null, ownsConnection: true);

    public void Add(Project project)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            INSERT INTO projects (id, name, accent_color, folder_segment, created_at, modified_at)
            VALUES ($id, $name, $accent, $folderSegment, $createdAt, $modifiedAt);
            """;
        Bind(command, project);
        command.ExecuteNonQuery();
    }

    public void Update(Project project)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            UPDATE projects SET name = $name, accent_color = $accent, folder_segment = $folderSegment,
                modified_at = $modifiedAt
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
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = "DELETE FROM projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public Project? GetById(ProjectId id)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            "SELECT id, name, accent_color, folder_segment, created_at, modified_at FROM projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<Project> GetAll()
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            "SELECT id, name, accent_color, folder_segment, created_at, modified_at FROM projects ORDER BY created_at;";
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
        command.Parameters.AddWithValue("$folderSegment", project.FolderSegment);
        command.Parameters.AddWithValue("$createdAt", project.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$modifiedAt", project.ModifiedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static Project Map(SqliteDataReader reader)
        => Project.Rehydrate(
            ProjectId.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.GetString(3));
}

public sealed class SqliteProjectFileRepository : IProjectFileRepository
{
    private readonly SqliteConnectionFactory? _connectionFactory;
    private readonly SqliteConnection? _sharedConnection;
    private readonly SqliteTransaction? _transaction;

    private const string Columns = "id, project_id, title, description, folder_segment, created_at, modified_at";

    public SqliteProjectFileRepository(SqliteConnectionFactory connectionFactory)
        => _connectionFactory = connectionFactory;

    /// <summary>Binds every operation to one shared connection and transaction.</summary>
    internal SqliteProjectFileRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _sharedConnection = connection;
        _transaction = transaction;
    }

    private SqliteSession OpenSession() => _sharedConnection is not null
        ? new SqliteSession(_sharedConnection, _transaction, ownsConnection: false)
        : new SqliteSession(_connectionFactory!.Open(), null, ownsConnection: true);

    public void Add(ProjectFile file)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO project_files ({Columns})
            VALUES ($id, $projectId, $title, $description, $folderSegment, $createdAt, $modifiedAt);
            """;
        Bind(command, file);
        command.ExecuteNonQuery();
    }

    public void Update(ProjectFile file)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            UPDATE project_files SET title = $title, description = $description,
                folder_segment = $folderSegment, modified_at = $modifiedAt
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
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = "DELETE FROM project_files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public ProjectFile? GetById(ProjectFileId id)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM project_files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<ProjectFile> GetForProject(ProjectId projectId)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
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
        command.Parameters.AddWithValue("$folderSegment", file.FolderSegment);
        command.Parameters.AddWithValue("$createdAt", file.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$modifiedAt", file.ModifiedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static ProjectFile Map(SqliteDataReader reader)
        => ProjectFile.Rehydrate(
            ProjectFileId.Parse(reader.GetString(0)),
            ProjectId.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.GetString(4));
}

public sealed class SqliteResourceRepository : IResourceRepository
{
    private readonly SqliteConnectionFactory? _connectionFactory;
    private readonly SqliteConnection? _sharedConnection;
    private readonly SqliteTransaction? _transaction;

    // Written out again, by hand, in Update's parameter block and in SearchInProject's
    // explicit SELECT list. All three have to agree: a column added to one and missed in
    // another is a silent read/write mismatch, not a compile error.
    private const string Columns =
        "id, file_id, kind, title, url, content, original_file_name, stored_path, added_at, index_state, " +
        "modified_at, group_id";

    public SqliteResourceRepository(SqliteConnectionFactory connectionFactory)
        => _connectionFactory = connectionFactory;

    /// <summary>Binds every operation to one shared connection and transaction.</summary>
    internal SqliteResourceRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _sharedConnection = connection;
        _transaction = transaction;
    }

    private SqliteSession OpenSession() => _sharedConnection is not null
        ? new SqliteSession(_sharedConnection, _transaction, ownsConnection: false)
        : new SqliteSession(_connectionFactory!.Open(), null, ownsConnection: true);

    public void Add(Resource resource)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO resources ({Columns})
            VALUES ($id, $fileId, $kind, $title, $url, $content, $originalFileName, $storedPath,
                    $addedAt, $indexState, $modifiedAt, $groupId);
            """;
        Bind(command, resource);
        command.ExecuteNonQuery();
    }

    public void Update(Resource resource)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            UPDATE resources SET
                title = $title, url = $url, content = $content, stored_path = $storedPath,
                index_state = $indexState, modified_at = $modifiedAt, group_id = $groupId
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
        command.Parameters.AddWithValue("$groupId", (object?)resource.GroupId?.ToString() ?? DBNull.Value);
        // index_text stays out of this statement on purpose. Moving a resource between
        // groups is not a content change, so the text the indexer already extracted has
        // to survive the write — only SetIndexText may touch it.
        if (command.ExecuteNonQuery() == 0)
        {
            throw new DomainException("That resource no longer exists.");
        }
    }

    public void Delete(ResourceId id)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = "DELETE FROM resources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public Resource? GetById(ResourceId id)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM resources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<Resource> GetForFile(ProjectFileId fileId)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
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
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM resources WHERE file_id = $fileId;";
        command.Parameters.AddWithValue("$fileId", fileId.ToString());
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void SetIndexText(ResourceId id, string text)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = "UPDATE resources SET index_text = $text WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Resource> SearchInProject(ProjectId projectId, string query)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            SELECT r.id, r.file_id, r.kind, r.title, r.url, r.content, r.original_file_name,
                   r.stored_path, r.added_at, r.index_state, r.modified_at, r.group_id
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
        command.Parameters.AddWithValue("$groupId", (object?)resource.GroupId?.ToString() ?? DBNull.Value);
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
            DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            reader.IsDBNull(11) ? null : ResourceGroupId.Parse(reader.GetString(11)));
}

/// <summary>
/// The one level of containers inside a File. Same factory/shared-connection shape as
/// every other project repository here, so a group write can join a project mutation's
/// transaction and roll back with it.
/// </summary>
public sealed class SqliteResourceGroupRepository : IResourceGroupRepository
{
    private readonly SqliteConnectionFactory? _connectionFactory;
    private readonly SqliteConnection? _sharedConnection;
    private readonly SqliteTransaction? _transaction;

    private const string Columns = "id, file_id, title, folder_segment, sort_order, created_at, modified_at";

    public SqliteResourceGroupRepository(SqliteConnectionFactory connectionFactory)
        => _connectionFactory = connectionFactory;

    /// <summary>Binds every operation to one shared connection and transaction.</summary>
    internal SqliteResourceGroupRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _sharedConnection = connection;
        _transaction = transaction;
    }

    private SqliteSession OpenSession() => _sharedConnection is not null
        ? new SqliteSession(_sharedConnection, _transaction, ownsConnection: false)
        : new SqliteSession(_connectionFactory!.Open(), null, ownsConnection: true);

    public void Add(ResourceGroup group)
    {
        RequireReservedSegment(group);
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO resource_groups ({Columns})
            VALUES ($id, $fileId, $title, $folderSegment, $sortOrder, $createdAt, $modifiedAt);
            """;
        Bind(command, group);
        command.ExecuteNonQuery();
    }

    public void Update(ResourceGroup group)
    {
        RequireReservedSegment(group);
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            """
            UPDATE resource_groups SET title = $title, folder_segment = $folderSegment,
                sort_order = $sortOrder, modified_at = $modifiedAt
            WHERE id = $id;
            """;
        Bind(command, group);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new DomainException("That group no longer exists.");
        }
    }

    /// <summary>
    /// Removes the group row only. resources.group_id is ON DELETE SET NULL, so the
    /// members survive and become loose in the File — deliberately not CASCADE, because
    /// that is what makes Ungroup a non-destructive action.
    /// </summary>
    public void Delete(ResourceGroupId id)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = "DELETE FROM resource_groups WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public ResourceGroup? GetById(ResourceGroupId id)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM resource_groups WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>
    /// Ordered by sort_order, then creation, then id. The id tiebreak is what keeps the
    /// sequence stable when several groups are created inside the same clock tick.
    /// </summary>
    public IReadOnlyList<ResourceGroup> GetForFile(ProjectFileId fileId)
    {
        using var session = OpenSession();
        using var command = session.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM resource_groups WHERE file_id = $fileId ORDER BY sort_order, created_at, id;";
        command.Parameters.AddWithValue("$fileId", fileId.ToString());
        using var reader = command.ExecuteReader();
        var groups = new List<ResourceGroup>();
        while (reader.Read())
        {
            groups.Add(Map(reader));
        }

        return groups;
    }

    /// <summary>
    /// The empty segment is the in-memory "not yet reserved" state a freshly created
    /// group holds. Persisting it would leave a row naming a directory nothing ever
    /// claimed, which every later reconcile would then fight over.
    /// </summary>
    private static void RequireReservedSegment(ResourceGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.FolderSegment))
        {
            throw new DomainException("A group needs a claimed folder segment.");
        }
    }

    private static void Bind(SqliteCommand command, ResourceGroup group)
    {
        command.Parameters.AddWithValue("$id", group.Id.ToString());
        command.Parameters.AddWithValue("$fileId", group.FileId.ToString());
        command.Parameters.AddWithValue("$title", group.Title);
        command.Parameters.AddWithValue("$folderSegment", group.FolderSegment);
        command.Parameters.AddWithValue("$sortOrder", (long)group.SortOrder);
        command.Parameters.AddWithValue("$createdAt", group.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$modifiedAt", group.ModifiedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static ResourceGroup Map(SqliteDataReader reader)
        => ResourceGroup.Rehydrate(
            ResourceGroupId.Parse(reader.GetString(0)),
            ProjectFileId.Parse(reader.GetString(1)),
            reader.GetString(2),
            (int)reader.GetInt64(4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.GetString(3));
}

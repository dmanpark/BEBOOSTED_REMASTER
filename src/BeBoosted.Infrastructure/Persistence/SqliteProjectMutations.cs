using BeBoosted.Application.Projects;
using BeBoosted.Application.Tasks;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Tasks;

namespace BeBoosted.Infrastructure.Persistence;

/// <summary>
/// Runs a project mutation as one real SQLite transaction on one connection: the
/// repositories handed to the mutation are bound to that transaction, so a thrown
/// exception rolls back every project, File, resource, and task write together.
/// </summary>
public sealed class SqliteProjectMutations(SqliteConnectionFactory connectionFactory)
    : IProjectMutations
{
    public void Execute(
        Action<IProjectRepository, IProjectFileRepository, IResourceRepository, ITaskRepository> mutation)
    {
        using var connection = connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        mutation(
            new SqliteProjectRepository(connection, transaction),
            new SqliteProjectFileRepository(connection, transaction),
            new SqliteResourceRepository(connection, transaction),
            new SqliteTaskRepository(connection, transaction));
        transaction.Commit();
    }
}

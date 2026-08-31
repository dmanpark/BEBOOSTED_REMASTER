using BeBoosted.Application.Projects;
using BeBoosted.Application.Tasks;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Tasks;

namespace BeBoosted.Tests.Support;

/// <summary>
/// Runs the real mutation, on a real transaction, against real transaction-bound
/// repositories — and then throws before the commit. So the callback's writes are
/// genuinely rolled back rather than merely never attempted.
///
/// That distinction is the whole point. A substituted repository that throws on its
/// first call proves only that the service stopped; it never exercises the transaction,
/// so it would pass just as happily against an implementation that commits each member
/// separately. This one lets every delete and update land, then discards them all.
///
/// Deliberately a near-twin of <c>ProjectServiceTests.FailAfterMutation</c>, which is
/// private to that class and predates this file. Sharing one double across both would
/// have to be named for neither caller or for only one of them; the duplication is two
/// constructor calls wide and says plainly which tests each belongs to.
/// </summary>
public sealed class FailGroupMutation(SqliteConnectionFactory factory) : IProjectMutations
{
    public void Execute(
        Action<IProjectRepository, IProjectFileRepository, IResourceRepository, ITaskRepository,
            IResourceGroupRepository> mutation)
    {
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction();
        mutation(
            new SqliteProjectRepository(connection, transaction),
            new SqliteProjectFileRepository(connection, transaction),
            new SqliteResourceRepository(connection, transaction),
            new SqliteTaskRepository(connection, transaction),
            new SqliteResourceGroupRepository(connection, transaction));
        throw new InvalidOperationException("after mutation, before commit");
    }
}

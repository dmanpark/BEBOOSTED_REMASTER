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
/// That distinction is the whole point. A substituted repository that throws on its first
/// call proves only that the service stopped; it never exercises the transaction, so it
/// would pass just as happily against an implementation that commits each row separately.
/// This one lets every write land, then discards them all.
///
/// Nothing here is specific to any one use case — it is "run the real callback on a real
/// transaction, then throw" — so it is shared by every rollback test rather than copied per
/// caller. No test asserts on the message.
/// </summary>
public sealed class FailAfterMutation(SqliteConnectionFactory factory) : IProjectMutations
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
        throw new InvalidOperationException("after the mutation, before the commit");
    }
}

using BeBoosted.Application.Calendar;
using BeBoosted.Infrastructure.Calendar;

namespace BeBoosted.Infrastructure.Persistence;

/// <summary>
/// Runs a calendar mutation as one real SQLite transaction on one connection: the
/// repositories handed to the mutation are bound to that transaction, so a thrown
/// exception rolls back every block and completion write together and rethrows.
/// </summary>
public sealed class SqliteCalendarMutations(SqliteConnectionFactory connectionFactory)
    : ICalendarMutations
{
    public void Execute(Action<ICalendarBlockRepository, ICommitmentCompletionRepository> mutation)
    {
        using var connection = connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        mutation(
            new SqliteCalendarBlockRepository(connection, transaction),
            new SqliteCommitmentCompletionRepository(connection, transaction));
        transaction.Commit();
    }
}

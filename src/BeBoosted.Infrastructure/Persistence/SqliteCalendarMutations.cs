using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Tasks;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Planning;
using BeBoosted.Infrastructure.Tasks;

namespace BeBoosted.Infrastructure.Persistence;

/// <summary>
/// Runs a task-and-schedule mutation as one real SQLite transaction on one connection:
/// the repositories handed to the mutation are bound to that transaction, so a thrown
/// exception rolls back every task, block, completion, and planning-proposal write
/// together and rethrows.
/// </summary>
public sealed class SqliteCalendarMutations(SqliteConnectionFactory connectionFactory)
    : ICalendarMutations
{
    public void Execute(
        Action<ICalendarBlockRepository, IOccurrenceCompletionRepository, ITaskRepository,
            IPlanningProposalRepository> mutation)
    {
        using var connection = connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        mutation(
            new SqliteCalendarBlockRepository(connection, transaction),
            new SqliteOccurrenceCompletionRepository(connection, transaction),
            new SqliteTaskRepository(connection, transaction),
            new SqlitePlanningProposalRepository(connection, transaction));
        transaction.Commit();
    }
}

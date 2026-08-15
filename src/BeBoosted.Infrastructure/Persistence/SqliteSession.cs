using Microsoft.Data.Sqlite;

namespace BeBoosted.Infrastructure.Persistence;

/// <summary>
/// One repository call's connection scope: either a connection the repository opens
/// and owns for the call, or a shared connection + transaction supplied by an atomic
/// mutation scope (see <see cref="SqliteCalendarMutations"/>), which the session
/// never disposes. Commands are always enlisted in the active transaction.
/// </summary>
internal sealed class SqliteSession(
    SqliteConnection connection,
    SqliteTransaction? transaction,
    bool ownsConnection) : IDisposable
{
    public SqliteCommand CreateCommand()
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        return command;
    }

    public void Dispose()
    {
        if (ownsConnection)
        {
            connection.Dispose();
        }
    }
}

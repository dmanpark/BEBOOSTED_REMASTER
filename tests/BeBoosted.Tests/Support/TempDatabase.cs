using BeBoosted.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace BeBoosted.Tests.Support;

/// <summary>A disposable SQLite database file in the system temp directory.</summary>
public sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), $"beboosted-test-{Guid.NewGuid():N}.db");
        Factory = new SqliteConnectionFactory(DatabasePath);
    }

    public string DatabasePath { get; }

    public SqliteConnectionFactory Factory { get; }

    public void Dispose()
    {
        // Clear only this database's pool — ClearAllPools would race with tests
        // running in parallel on their own database files.
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            try
            {
                File.Delete(DatabasePath + suffix);
            }
            catch (IOException)
            {
                // Best-effort cleanup of temp files; never fail a test over it.
            }
        }
    }
}

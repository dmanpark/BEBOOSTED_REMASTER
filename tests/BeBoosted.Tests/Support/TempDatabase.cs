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
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var file = DatabasePath + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}

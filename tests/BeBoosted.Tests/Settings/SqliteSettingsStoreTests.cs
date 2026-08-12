using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Settings;
using BeBoosted.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeBoosted.Tests.Settings;

public sealed class SqliteSettingsStoreTests : IDisposable
{
    private readonly TempDatabase _database = new();
    private readonly SqliteSettingsStore _store;

    public SqliteSettingsStoreTests()
    {
        new MigrationRunner(_database.Factory, NullLogger<MigrationRunner>.Instance)
            .Apply(EmbeddedMigrations.Load());
        _store = new SqliteSettingsStore(_database.Factory);
    }

    [Fact]
    public void Get_ReturnsNullForMissingKey() => Assert.Null(_store.Get("missing"));

    [Fact]
    public void SetAndGet_RoundTrips()
    {
        _store.Set("calendar.lastView", "week");
        Assert.Equal("week", _store.Get("calendar.lastView"));
    }

    [Fact]
    public void Set_OverwritesExistingValue()
    {
        _store.Set("key", "first");
        _store.Set("key", "second");
        Assert.Equal("second", _store.Get("key"));
    }

    [Fact]
    public void Remove_DeletesKey()
    {
        _store.Set("key", "value");
        _store.Remove("key");
        Assert.Null(_store.Get("key"));
    }

    [Fact]
    public void Values_SurviveReopeningTheDatabaseFile()
    {
        _store.Set("key", "persisted");

        var reopened = new SqliteSettingsStore(new SqliteConnectionFactory(_database.DatabasePath));
        Assert.Equal("persisted", reopened.Get("key"));
    }

    public void Dispose() => _database.Dispose();
}

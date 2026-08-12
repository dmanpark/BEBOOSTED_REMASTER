namespace BeBoosted.Application.Abstractions;

/// <summary>
/// Key/value persistence for application settings. Synchronous by design: the backing
/// store is a local SQLite file and settings are read during startup, before any UI
/// event loop exists to await on.
/// </summary>
public interface ISettingsStore
{
    string? Get(string key);

    void Set(string key, string value);

    void Remove(string key);
}

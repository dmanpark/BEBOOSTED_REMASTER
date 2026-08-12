namespace BeBoosted.Application.Abstractions;

/// <summary>Platform-specific locations for application-owned local data.</summary>
public interface IAppDataPaths
{
    /// <summary>Root directory for all locally persisted application state.</summary>
    string DataDirectory { get; }

    /// <summary>Directory for structured log files.</summary>
    string LogsDirectory { get; }

    /// <summary>Directory holding imported project resource bytes, keyed by stable identifiers.</summary>
    string ResourcesDirectory { get; }
}

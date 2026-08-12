using BeBoosted.Application.Abstractions;

namespace BeBoosted.Infrastructure.Storage;

/// <summary>
/// Default application-data locations. On Windows this resolves under %LOCALAPPDATA%\BeBoosted;
/// on macOS under ~/Library/Application Support/BeBoosted. The BEBOOSTED_DATA_DIR environment
/// variable overrides the root, which packaging smoke tests use to run against a clean profile.
/// </summary>
public sealed class DefaultAppDataPaths : IAppDataPaths
{
    public DefaultAppDataPaths(string rootDirectory)
    {
        DataDirectory = rootDirectory;
        LogsDirectory = Path.Combine(rootDirectory, "logs");
        ResourcesDirectory = Path.Combine(rootDirectory, "resources");
    }

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    public string ResourcesDirectory { get; }

    public static DefaultAppDataPaths CreateDefault()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("BEBOOSTED_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new DefaultAppDataPaths(overrideRoot);
        }

        var root = OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "BeBoosted")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BeBoosted");
        return new DefaultAppDataPaths(root);
    }

    /// <summary>Creates the directories if missing. Called once by the composition root.</summary>
    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ResourcesDirectory);
    }
}

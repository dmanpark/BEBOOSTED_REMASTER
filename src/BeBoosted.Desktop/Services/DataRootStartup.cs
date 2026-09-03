using BeBoosted.Infrastructure.Storage;

namespace BeBoosted.Desktop.Services;

/// <summary>Startup-error copy for a data root that could not be created (BB-QA-004).</summary>
public sealed record DataRootFailure(string Title, string Detail);

/// <summary>
/// The very first startup step: creating the data root. It runs before logging and
/// dependency injection exist, so a failure here has no log to land in and exactly one
/// surface — the startup error window the caller shows with the returned copy. Throwing
/// through the framework hook instead would kill the process with no window and no log.
/// </summary>
public static class DataRootStartup
{
    public static DataRootFailure? Prepare(DefaultAppDataPaths paths)
    {
        try
        {
            paths.EnsureDirectoriesExist();
            return null;
        }
        catch (Exception exception)
        {
            return new DataRootFailure(
                "BeBoosted could not create its data folder.",
                $"The folder at {paths.DataDirectory} could not be created or written.\n\n{exception.Message}");
        }
    }
}

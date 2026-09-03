using BeBoosted.Desktop.Services;
using BeBoosted.Infrastructure.Storage;

namespace BeBoosted.Desktop.Tests.Services;

/// <summary>
/// The data root is created before logging and dependency injection exist, so a failure
/// there has exactly one surface: the startup error window. These tests pin that the
/// preparation step reports such a failure as copy for that window instead of throwing
/// through the framework hook, which historically killed the process with no window and
/// no log (BB-QA-004).
/// </summary>
public sealed class DataRootStartupTests
{
    [Fact]
    public void HealthyDataRoot_CreatesEveryDirectoryAndReportsNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"beboosted-dataroot-{Guid.NewGuid():N}");
        var paths = new DefaultAppDataPaths(root);
        try
        {
            var failure = DataRootStartup.Prepare(paths);

            Assert.Null(failure);
            Assert.True(Directory.Exists(paths.DataDirectory));
            Assert.True(Directory.Exists(paths.LogsDirectory));
            Assert.True(Directory.Exists(paths.ResourcesDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void FileOccupyingTheDataRoot_ReportsAFailureNamingThePath()
    {
        var poison = Path.Combine(Path.GetTempPath(), $"beboosted-dataroot-{Guid.NewGuid():N}");
        File.WriteAllText(poison, "a file where the data root must go");
        var paths = new DefaultAppDataPaths(poison);
        try
        {
            var failure = DataRootStartup.Prepare(paths);

            Assert.NotNull(failure);
            Assert.Equal("BeBoosted could not create its data folder.", failure.Title);
            Assert.Contains(paths.DataDirectory, failure.Detail);
        }
        finally
        {
            File.Delete(poison);
        }
    }

    [Fact]
    public void FileOccupyingASubdirectory_ReportsAFailureInsteadOfThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"beboosted-dataroot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "logs"), "a file where the logs directory must go");
        var paths = new DefaultAppDataPaths(root);
        try
        {
            var failure = DataRootStartup.Prepare(paths);

            Assert.NotNull(failure);
            Assert.Contains(paths.DataDirectory, failure.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

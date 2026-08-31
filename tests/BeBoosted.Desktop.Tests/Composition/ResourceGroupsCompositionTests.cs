using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Projects;
using BeBoosted.Domain.Projects;
using BeBoosted.Infrastructure;
using BeBoosted.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeBoosted.Desktop.Tests.Composition;

/// <summary>
/// The resource-group feature exercised through the container the app actually builds.
///
/// Every other test in the suite hands its subject the collaborators it wants. That is the
/// right shape for behaviour, and it is exactly why none of them can fail when a service is
/// left out of <see cref="ServiceCollectionExtensions.AddBeBoostedInfrastructure"/>, bound to
/// the wrong lifetime, or reachable only through an optional constructor parameter the
/// container silently passes as null. This test builds the production registration with
/// <c>ValidateOnBuild</c> and <c>ValidateScopes</c> on, applies the real embedded migrations,
/// and drives the real <see cref="ProjectService"/> — so a composition mistake is red here
/// rather than at the user's first launch.
///
/// The whole run lives in its own temporary profile directory; nothing here can see, move or
/// delete a byte of the user's real library.
/// </summary>
public sealed class ResourceGroupsCompositionTests
{
    /// <summary>
    /// A throwaway profile under the system temp directory, named per run so parallel tests
    /// never share one.
    /// </summary>
    private sealed class TemporaryPaths : IAppDataPaths, IDisposable
    {
        public TemporaryPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), $"beboosted-group-di-{Guid.NewGuid():N}");
            LogsDirectory = Path.Combine(DataDirectory, "logs");
            ResourcesDirectory = Path.Combine(DataDirectory, "resources");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(ResourcesDirectory);
        }

        public string DataDirectory { get; }

        public string LogsDirectory { get; }

        public string ResourcesDirectory { get; }

        /// <summary>The database file the production registration derives from these paths.</summary>
        public string DatabasePath => Path.Combine(DataDirectory, "beboosted.db");

        public void Dispose()
        {
            // Only this run's pool: ClearAllPools would yank connections out from under
            // tests running in parallel against their own database files.
            //
            // Microsoft.Data.Sqlite keys its pools on the whole connection string, so a
            // hand-written "Data Source=<path>" clears a pool nothing ever filled and leaves
            // the file locked. The string has to be the one the app's own
            // SqliteConnectionFactory builds, so ask that factory for it rather than
            // restating its keywords here and drifting the day it gains another.
            using (var pooled = new SqliteConnectionFactory(DatabasePath).Open())
            {
                SqliteConnection.ClearPool(pooled);
            }

            try
            {
                // Only this run's own directory, named above and never a configured root.
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a temp directory; never fail a test over it.
            }
        }
    }

    [Fact]
    public void ProductionComposition_CanCreateMoveAndDeleteAGroup()
    {
        using var paths = new TemporaryPaths();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddBeBoostedInfrastructure(paths)
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        // The real embedded scripts, not a hand-written schema: 0013 is the group table this
        // feature stands on, so a migration that never shipped as an embedded resource fails
        // here instead of at launch.
        var applied = provider.GetRequiredService<MigrationRunner>().Apply(EmbeddedMigrations.Load());
        Assert.Contains(applied, migration => migration.Version == 13);

        // A clean profile has no unclaimed folder segments, so the startup pass must run its
        // sweep rather than hold it back. A deferral here would mean grouped documents never
        // reach their folders on a fresh install.
        var startup = provider.GetRequiredService<ResourceLayoutStartup>().Run();
        Assert.False(startup.ReconcileDeferred);

        var service = provider.GetRequiredService<ProjectService>();
        var project = service.CreateProject("Schoolwork");
        var file = service.CreateFile(project.Id, "Spanish", null);
        var group = service.CreateGroup(file.Id, "Unit 3");

        var source = Path.Combine(paths.DataDirectory, "input.txt");
        File.WriteAllText(source, "composition sentinel");
        var resource = service.ImportFile(file.Id, ResourceKind.Document, source);

        // Phase 1 has no group-targeted import: an import always lands loose in the File and
        // is filed afterwards. This is the only place that contract is checked through the
        // production graph.
        Assert.Null(resource.GroupId);

        service.MoveResourceToGroup(resource.Id, group.Id);
        var grouped = Assert.Single(service.GetResources(file.Id));
        Assert.Equal(group.Id, grouped.GroupId);

        var stored = service.ResolveStoredPath(grouped);
        Assert.NotNull(stored);
        Assert.Equal("composition sentinel", File.ReadAllText(stored));

        service.DeleteGroup(group.Id);
        Assert.Empty(service.GetGroups(file.Id));
        Assert.Empty(service.GetResources(file.Id));

        // Rows alone would be a passing test with the user's bytes still on disk.
        Assert.False(File.Exists(stored));
    }
}

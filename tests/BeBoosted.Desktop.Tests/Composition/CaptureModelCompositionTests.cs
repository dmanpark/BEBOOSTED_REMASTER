using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Infrastructure;
using BeBoosted.Infrastructure.Ai;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace BeBoosted.Desktop.Tests.Composition;

/// <summary>
/// The production container, validated. A registration that only unit tests cover can be
/// missing here and nothing would notice until the app ran.
/// </summary>
public sealed class CaptureModelCompositionTests
{
    private sealed class TemporaryPaths : IAppDataPaths, IDisposable
    {
        public TemporaryPaths()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), $"beboosted-capture-di-{Guid.NewGuid():N}");
            LogsDirectory = Path.Combine(DataDirectory, "logs");
            ResourcesDirectory = Path.Combine(DataDirectory, "resources");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(ResourcesDirectory);
        }

        public string DataDirectory { get; }

        public string LogsDirectory { get; }

        public string ResourcesDirectory { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Builds the production registration and applies the real embedded migrations, the
    /// same way <c>App.axaml.cs</c> does at launch — without them, every table these
    /// tests touch (settings, projects, ...) is simply absent, which would fail the
    /// heuristic-fallback test for a schema reason that has nothing to do with what this
    /// suite is proving.
    /// </summary>
    private static ServiceProvider Build(TemporaryPaths paths)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddBeBoostedInfrastructure(paths)
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        services.GetRequiredService<MigrationRunner>().Apply(EmbeddedMigrations.Load());
        return services;
    }

    [Fact]
    public void TheRegisteredProvider_IsTheRouter()
    {
        using var paths = new TemporaryPaths();
        using var services = Build(paths);

        Assert.IsType<RoutedAiProvider>(services.GetRequiredService<IAiProvider>());
    }

    [Fact]
    public void BothBackendsAndTheSettingsAccessorResolve()
    {
        using var paths = new TemporaryPaths();
        using var services = Build(paths);

        Assert.NotNull(services.GetRequiredService<CaptureModelSettings>());
        Assert.NotNull(services.GetRequiredService<OllamaCaptureModel>());
        Assert.NotNull(services.GetRequiredService<ClaudeCaptureModel>());
        Assert.NotNull(services.GetRequiredService<ISecretProtector>());
    }

    /// <summary>
    /// A fresh profile must behave exactly as it does today: the heuristic parses, and
    /// no capture touches a network.
    /// </summary>
    [Fact]
    public async Task AFreshProfile_ParsesWithTheHeuristic()
    {
        using var paths = new TemporaryPaths();
        using var services = Build(paths);
        var provider = services.GetRequiredService<IAiProvider>();

        var result = await provider.ExtractTasksAsync(
            "Draft the essay outline",
            new AiContext(null, new DateOnly(2026, 9, 3)),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Drafts);
        Assert.Null(result.DegradedNotice);
    }
}

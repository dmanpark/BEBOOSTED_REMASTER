using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Settings;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace BeBoosted.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBeBoostedInfrastructure(this IServiceCollection services, IAppDataPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(_ => new SqliteConnectionFactory(Path.Combine(paths.DataDirectory, "beboosted.db")));
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<AppSettings>();
        return services;
    }
}

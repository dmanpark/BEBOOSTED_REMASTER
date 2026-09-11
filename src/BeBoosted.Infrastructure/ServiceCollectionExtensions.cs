using BeBoosted.Application.Abstractions;
using BeBoosted.Application.Ai;
using BeBoosted.Application.Calendar;
using BeBoosted.Application.Planning;
using BeBoosted.Application.Prioritization;
using BeBoosted.Application.Projects;
using BeBoosted.Application.Settings;
using BeBoosted.Application.Tasks;
using BeBoosted.Infrastructure.Ai;
using BeBoosted.Infrastructure.Calendar;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Planning;
using BeBoosted.Infrastructure.Prioritization;
using BeBoosted.Infrastructure.Projects;
using BeBoosted.Infrastructure.Security;
using BeBoosted.Infrastructure.Settings;
using BeBoosted.Infrastructure.Tasks;
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
        services.AddSingleton<ITaskRepository, SqliteTaskRepository>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<ICalendarBlockRepository, SqliteCalendarBlockRepository>();
        services.AddSingleton<IOccurrenceCompletionRepository, SqliteOccurrenceCompletionRepository>();
        services.AddSingleton<ICalendarMutations, SqliteCalendarMutations>();
        services.AddSingleton<CalendarService>();
        services.AddSingleton<InboxQueryService>();
        services.AddSingleton<IPrioritizationRepository, SqlitePrioritizationRepository>();
        services.AddSingleton<PrioritySortService>();
        services.AddSingleton<IPlanningProposalRepository, SqlitePlanningProposalRepository>();
        services.AddSingleton<PlanningService>();
        services.AddSingleton<IProjectRepository, SqliteProjectRepository>();
        services.AddSingleton<IProjectFileRepository, SqliteProjectFileRepository>();
        services.AddSingleton<IResourceRepository, SqliteResourceRepository>();
        services.AddSingleton<IResourceGroupRepository, SqliteResourceGroupRepository>();
        services.AddSingleton<IResourceStorage, LocalResourceStorage>();
        services.AddSingleton<IProjectMutations, SqliteProjectMutations>();
        services.AddSingleton<FolderIdentityBackfill>();
        services.AddSingleton<ResourceLayoutReconciler>();
        services.AddSingleton<ResourceLayoutStartup>();
        services.AddSingleton<IResourceIndexer, SimpleLocalIndexer>();
        services.AddSingleton<ProjectService>();
        services.AddSingleton<CaptureModelSettings>();
        services.AddSingleton<ISecretProtector>(_ => OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : new UnavailableSecretProtector());
        services.AddSingleton<LocalHeuristicAiProvider>();
        // The capture HttpClient. Its budget belongs to the Ollama backend that
        // consumes it, so the number lives there rather than floating here.
        services.AddSingleton(_ => new HttpClient { Timeout = OllamaCaptureModel.RequestTimeout });
        services.AddSingleton<OllamaCaptureModel>();
        services.AddSingleton<ClaudeCaptureModel>();
        services.AddSingleton<IAiProvider>(sp => new RoutedAiProvider(
            sp.GetRequiredService<LocalHeuristicAiProvider>(),
            source => source switch
            {
                CaptureModelSource.Ollama => sp.GetRequiredService<OllamaCaptureModel>(),
                CaptureModelSource.Claude => sp.GetRequiredService<ClaudeCaptureModel>(),
                _ => null,
            },
            sp.GetRequiredService<CaptureModelSettings>(),
            sp.GetRequiredService<IProjectRepository>()));
        services.AddSingleton<IAiProvenanceRepository, SqliteAiProvenanceRepository>();
        services.AddSingleton<AiPermissionSettings>();
        services.AddSingleton<AiService>();
        services.AddSingleton<IProvenanceInvalidator>(sp => sp.GetRequiredService<AiService>());
        return services;
    }
}

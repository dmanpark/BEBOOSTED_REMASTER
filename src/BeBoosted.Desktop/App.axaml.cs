using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BeBoosted.Application.Projects;
using BeBoosted.Desktop.Platform;
using BeBoosted.Desktop.Services;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;
using BeBoosted.Infrastructure;
using BeBoosted.Infrastructure.Persistence;
using BeBoosted.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BeBoosted.Desktop;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = DefaultAppDataPaths.CreateDefault();
            paths.EnsureDirectoriesExist();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(paths.LogsDirectory, "beboosted-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)
                .WriteTo.Debug()
                .CreateLogger();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddSerilog());
            services.AddBeBoostedInfrastructure(paths);
            services.AddSingleton<IKeymapService, DefaultKeymapService>();
            services.AddSingleton<IFileRevealService, DefaultFileRevealService>();
            services.AddSingleton<WindowStateService>();
            services.AddSingleton<CalendarViewModel>();
            services.AddSingleton<InboxViewModel>();
            services.AddSingleton<ChatViewModel>();
            services.AddSingleton<ProjectsViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<ShellViewModel>();
            _services = services.BuildServiceProvider();

            desktop.Exit += (_, _) =>
            {
                _services.Dispose();
                Log.CloseAndFlush();
            };

            try
            {
                _services.GetRequiredService<MigrationRunner>().Apply(EmbeddedMigrations.Load());
            }
            catch (Exception exception)
            {
                Log.Fatal(exception, "Local database could not be opened or migrated");
                desktop.MainWindow = new StartupErrorWindow(
                    "BeBoosted could not open its local data.",
                    $"The database at {paths.DataDirectory} could not be opened or upgraded.\n\n{exception.Message}");
                base.OnFrameworkInitializationCompleted();
                return;
            }

            try
            {
                // Order is load-bearing, and so is the gate below. Rows persisted before
                // migration 0012 hold an empty folder segment, and ResourceLayout.FolderFor
                // returns segments verbatim — so reconciling against one resolves its
                // folder to the resources root and physically moves its documents there.
                // Backfill claims each row's existing directory first; the sweep runs only
                // if it left nothing behind, because the backfill recovers per entity
                // rather than throwing and can now return having skipped a row.
                //
                // Deferring is the cheap side of the trade: the documents simply stay where
                // they are and a later launch picks them up, whereas sweeping unclaimed
                // rows destroys the layout. One bad row still costs only itself — every
                // other project is backfilled, and the sweep resumes once it succeeds.
                //
                // This governs the startup sweep only. ProjectService reconciles a single
                // Project on rename, outside this block entirely, so the reconciler also
                // refuses the half-backfilled shape itself rather than trusting call order.
                var backfill = _services.GetRequiredService<FolderIdentityBackfill>().Backfill();
                if (backfill.Claimed > 0)
                {
                    Log.Information(
                        "Backfilled folder segments for {Count} projects and Files", backfill.Claimed);
                }

                if (backfill.Skipped > 0)
                {
                    Log.Warning(
                        "{Count} projects or Files could not claim a folder segment; "
                        + "deferring the resource layout reconcile until they can",
                        backfill.Skipped);
                }
                else
                {
                    var moved = _services.GetRequiredService<ResourceLayoutReconciler>().Reconcile();
                    if (moved > 0)
                    {
                        Log.Information("Moved {Count} stored resources into named folders", moved);
                    }
                }
            }
            catch (Exception exception)
            {
                // Layout is cosmetic: every resource still resolves through its recorded
                // path, so a failure here must never stop the app from starting.
                Log.Warning(exception, "Resource folder layout could not be reconciled");
            }

            var window = new MainWindow
            {
                DataContext = _services.GetRequiredService<ShellViewModel>(),
            };
            _services.GetRequiredService<WindowStateService>().Attach(window);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

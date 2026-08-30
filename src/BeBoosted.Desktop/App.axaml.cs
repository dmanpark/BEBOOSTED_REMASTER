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
                // Ordering, the gate between the two steps, and the reason for both live
                // in ResourceLayoutStartup, where a test can reach them. What stays here
                // is reporting: the pass is cosmetic bookkeeping and must never keep the
                // app from starting, so a failure is caught and logged, not surfaced.
                var layout = _services.GetRequiredService<ResourceLayoutStartup>().Run();
                if (layout.Backfill.Claimed > 0)
                {
                    Log.Information(
                        "Backfilled folder segments for {Count} projects and Files",
                        layout.Backfill.Claimed);
                }

                foreach (var failure in layout.Backfill.Failures)
                {
                    Log.Warning(failure.Error, "Could not claim a folder segment for {Entity}", failure.Entity);
                }

                if (layout.ReconcileDeferred)
                {
                    // Deliberately global and, against a deterministic fault, indefinite:
                    // the sweep stays held back on every launch until every row is claimed.
                    // Its work is postponed rather than lost, and a rename still reconciles
                    // the project it touches, so this trades a cosmetic delay for the
                    // irreversible relocation a sweep over unclaimed rows would perform.
                    Log.Warning(
                        "{Count} projects or Files still hold no folder segment; deferring the "
                        + "resource layout reconcile, which would otherwise move their documents "
                        + "into the resources root",
                        layout.Backfill.Skipped);
                }
                else if (layout.Moved > 0)
                {
                    Log.Information("Moved {Count} stored resources into named folders", layout.Moved);
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

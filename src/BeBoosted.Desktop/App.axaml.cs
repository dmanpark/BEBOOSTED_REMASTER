using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            services.AddSingleton<WindowStateService>();
            services.AddSingleton<CalendarViewModel>();
            services.AddSingleton<InboxViewModel>();
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

using Avalonia;
using Avalonia.Media;

namespace BeBoosted.Desktop;

internal static class Program
{
    // Avalonia and third-party APIs must not be used before AppMain runs.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://BeBoosted/Assets/Fonts/InstrumentSans#Instrument Sans",
            })
            .LogToTrace();
}

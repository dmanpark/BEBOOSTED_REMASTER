using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using BeBoosted.Desktop;
using BeBoosted.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace BeBoosted.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
        })
        .With(new FontManagerOptions
        {
            DefaultFamilyName = "avares://BeBoosted/Assets/Fonts/InstrumentSans#Instrument Sans",
        });
}

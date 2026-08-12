using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Tests.Support;
using BeBoosted.Desktop.ViewModels;
using BeBoosted.Desktop.Views;

namespace BeBoosted.Desktop.Tests.Ui;

/// <summary>
/// Captures review screenshots at the two design resolutions when
/// BEBOOSTED_SCREENSHOT_DIR is set; skipped in normal test runs.
/// </summary>
public sealed class ScreenshotCaptureTests
{
    [AvaloniaFact]
    public void CaptureShellScreens()
    {
        var directory = Environment.GetEnvironmentVariable("BEBOOSTED_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(directory), "BEBOOSTED_SCREENSHOT_DIR is not set");
        Directory.CreateDirectory(directory!);

        foreach (var (width, height) in new[] { (1440, 960), (1280, 800) })
        {
            var shell = TestShell.Create();
            var window = new MainWindow
            {
                DataContext = shell,
                Width = width,
                Height = height,
            };
            window.Show();

            Capture(window, directory!, $"shell-calendar-today-{width}x{height}.png");

            shell.Calendar.ViewKind = CalendarViewKind.Week;
            Capture(window, directory!, $"shell-calendar-week-{width}x{height}.png");

            shell.Calendar.ViewKind = CalendarViewKind.Today;
            shell.ToggleInboxCommand.Execute(null);
            Capture(window, directory!, $"shell-inbox-drawer-{width}x{height}.png");
            shell.CloseInboxCommand.Execute(null);

            shell.NavigateCommand.Execute(AppSection.Settings);
            Capture(window, directory!, $"shell-settings-{width}x{height}.png");

            window.Close();
        }
    }

    private static void Capture(MainWindow window, string directory, string fileName)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, fileName), new PngBitmapEncoderOptions());
    }
}

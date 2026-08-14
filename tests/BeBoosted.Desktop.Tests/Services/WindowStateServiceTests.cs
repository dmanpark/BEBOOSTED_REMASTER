using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Services;
using BeBoosted.Desktop.Tests.Support;

namespace BeBoosted.Desktop.Tests.Services;

/// <summary>
/// BB-QA-002 regression coverage at the service level: attaching a window must clamp both
/// default and restored geometry to the current screen's working area while keeping the
/// existing placement persistence and maximized-restore behavior intact.
/// </summary>
public sealed class WindowStateServiceTests
{
    private static (WindowStateService Service, AppSettings Settings) CreateService()
    {
        var settings = new AppSettings(new InMemorySettingsStore());
        return (new WindowStateService(settings), settings);
    }

    /// <summary>A window carrying the shell's XAML geometry so tests mirror MainWindow.</summary>
    private static Window CreateShellSizedWindow() => new()
    {
        Width = 1440,
        Height = 960,
        MinWidth = 1100,
        MinHeight = 720,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
    };

    private static void AssertInsideWorkArea(Window window)
    {
        var screens = window.Screens.All;
        Assert.SkipWhen(screens.Count == 0, "Headless platform reports no screens");

        var chrome = window.FrameSize is { } frame
            ? new Size(
                Math.Max(0, frame.Width - window.ClientSize.Width),
                Math.Max(0, frame.Height - window.ClientSize.Height))
            : WindowStateService.EstimatedChromeSize;
        var scaling = screens[0].Scaling;
        var frameWidth = WindowPlacementMath.ToPixelsCeiling(window.ClientSize.Width, scaling)
            + WindowPlacementMath.ToPixelsCeiling(chrome.Width, scaling);
        var frameHeight = WindowPlacementMath.ToPixelsCeiling(window.ClientSize.Height, scaling)
            + WindowPlacementMath.ToPixelsCeiling(chrome.Height, scaling);
        var workArea = screens[0].WorkingArea;
        var position = window.Position;

        Assert.True(position.X >= workArea.X, $"Frame left {position.X} outside work area {workArea}");
        Assert.True(position.Y >= workArea.Y, $"Frame top {position.Y} outside work area {workArea}");
        Assert.True(
            position.X + frameWidth <= workArea.Right,
            $"Frame right {position.X + frameWidth} outside work area {workArea}");
        Assert.True(
            position.Y + frameHeight <= workArea.Bottom,
            $"Frame bottom {position.Y + frameHeight} outside work area {workArea}");
    }

    [AvaloniaFact]
    public void Attach_WithoutSavedPlacement_ClampsDefaultGeometryToWorkArea()
    {
        var (service, _) = CreateService();
        var window = CreateShellSizedWindow();

        service.Attach(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
            AssertInsideWorkArea(window);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Attach_WithWildSavedPlacement_ClampsIntoWorkArea()
    {
        var (service, settings) = CreateService();
        settings.SetWindowPlacement(new WindowPlacement(-9999, -9999, 20000, 20000, IsMaximized: false));
        var window = CreateShellSizedWindow();

        service.Attach(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            AssertInsideWorkArea(window);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Attach_WithMaximizedPlacement_RestoresMaximized()
    {
        var (service, settings) = CreateService();
        settings.SetWindowPlacement(new WindowPlacement(0, 0, 1200, 800, IsMaximized: true));
        var window = CreateShellSizedWindow();

        service.Attach(window);
        window.Show();
        try
        {
            Assert.Equal(WindowState.Maximized, window.WindowState);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CloseAndReattach_RoundTripsPlacement()
    {
        var (service, settings) = CreateService();
        var first = CreateShellSizedWindow();
        service.Attach(first);
        first.Show();
        Dispatcher.UIThread.RunJobs();
        first.Position = new PixelPoint(96, 64);
        first.Width = 1250;
        first.Height = 780;
        first.Close();

        Assert.Equal(new WindowPlacement(96, 64, 1250, 780, IsMaximized: false), settings.GetWindowPlacement());

        var second = CreateShellSizedWindow();
        service.Attach(second);
        second.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(1250, second.Width);
            Assert.Equal(780, second.Height);
            AssertInsideWorkArea(second);
        }
        finally
        {
            second.Close();
        }
    }

    [AvaloniaFact]
    public void Close_WhileMinimized_KeepsPreviousPlacement()
    {
        var (service, settings) = CreateService();
        var stored = new WindowPlacement(100, 100, 1200, 800, IsMaximized: false);
        settings.SetWindowPlacement(stored);
        var window = CreateShellSizedWindow();

        service.Attach(window);
        window.Show();
        window.WindowState = WindowState.Minimized;
        window.Close();

        Assert.Equal(stored, settings.GetWindowPlacement());
    }

    [AvaloniaFact]
    public void RestoreDown_FromOversizedMaximizedPlacement_ClampsToWorkArea()
    {
        var (service, settings) = CreateService();
        settings.SetWindowPlacement(new WindowPlacement(0, 0, 20000, 20000, IsMaximized: true));
        var window = CreateShellSizedWindow();

        service.Attach(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WindowState.Maximized, window.WindowState);

        // Simulate what a too-small pre-show chrome estimate leaves behind: normal bounds
        // that overflow the work area while the window sits maximized on top of them.
        window.Position = new PixelPoint(-4000, -4000);
        window.Width = 20000;
        window.Height = 20000;
        Dispatcher.UIThread.RunJobs();

        window.WindowState = WindowState.Normal;
        Dispatcher.UIThread.RunJobs();
        try
        {
            AssertInsideWorkArea(window);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RequestClamp_OnDisplayChange_RestoresContainment()
    {
        // Screens.Changed and ScalingChanged both funnel into RequestClamp; simulate a
        // display change by poisoning the live geometry and firing the same entry point.
        var (service, _) = CreateService();
        var window = CreateShellSizedWindow();
        service.Attach(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Position = new PixelPoint(-9999, -9999);
        window.Width = 20000;
        window.Height = 20000;
        Dispatcher.UIThread.RunJobs();

        service.RequestClamp(window);
        Dispatcher.UIThread.RunJobs();
        try
        {
            AssertInsideWorkArea(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Delivers a fractional client size the way a native platform resize would: through
    /// the internal resize notification, which updates ClientSize independently of the
    /// Width/Height properties.
    /// </summary>
    private static void SimulateNativeResize(Window window, Size clientSize)
    {
        var method = typeof(Window).GetMethod(
            "HandleResized",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.SkipWhen(method is null, "Window.HandleResized is not reachable");
        var reasonType = method!.GetParameters()[1].ParameterType;
        method.Invoke(window, [clientSize, Enum.Parse(reasonType, "User")]);
    }

    [AvaloniaFact]
    public void RestoreDown_WithFractionalClientOverflow_AppliesOnePixelCorrection()
    {
        // The audited runtime hazard: a live client with a fractional DIP height
        // (816.5 DIP = 1633 px at 200%) whose frame overflows the work area by less than
        // one whole DIP. Truncating the fraction or tolerating sub-DIP differences leaves
        // a one-pixel overflow. Exercised through the real event wiring: the clamp runs
        // because the Maximized→Normal transition fires the service's PropertyChanged
        // subscription, and the platform delivers the restored fractional client size
        // (via the native resize notification) before the deferred clamp executes.
        // At 200% the half-DIP client is pixel-aligned (1633 px) and survives layout
        // rounding; headless runs at scale 1 where rounding would snap it away, so the
        // test window disables layout rounding to model the real-scale physics.
        var (service, _) = CreateService();
        var window = CreateShellSizedWindow();
        window.UseLayoutRounding = false;
        service.Attach(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var screens = window.Screens.All;
        Assert.SkipWhen(screens.Count == 0, "Headless platform reports no screens");
        var workArea = screens[0].WorkingArea;
        var chrome = window.FrameSize is { } frame
            ? Math.Max(0, frame.Height - window.ClientSize.Height)
            : WindowStateService.EstimatedChromeSize.Height;

        // Settle a contained integral geometry first, then maximize.
        window.Position = new PixelPoint(workArea.X, workArea.Y);
        window.Width = 1200;
        window.Height = (workArea.Height / screens[0].Scaling) - chrome;
        Dispatcher.UIThread.RunJobs();
        window.WindowState = WindowState.Maximized;
        Dispatcher.UIThread.RunJobs();

        // Restore down; before the deferred clamp runs, the platform delivers restored
        // normal bounds whose frame bottom lands half a DIP past the work-area bottom.
        window.WindowState = WindowState.Normal;
        window.Position = new PixelPoint(workArea.X, workArea.Y);
        var fractionalSize = new Size(1200, (workArea.Height / screens[0].Scaling) - chrome + 0.5);
        SimulateNativeResize(window, fractionalSize);
        Assert.SkipWhen(
            window.ClientSize.Height % 1 == 0,
            "Headless platform snapped the fractional client size");

        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(); // let layout apply any correction the clamp wrote

        // The corrective write must have happened, and independently of the production
        // conversion helper the client must have lost exactly one physical pixel:
        // ceiling(fractional) − corrected = 1 at the headless 1.0 scale.
        var correctedHeight = (workArea.Height / screens[0].Scaling) - chrome;
        Assert.Equal(correctedHeight, window.ClientSize.Height);
        Assert.Equal(
            1,
            (int)Math.Ceiling(fractionalSize.Height) - (int)window.ClientSize.Height);
        try
        {
            AssertInsideWorkArea(window);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DisplayChangeWhileMinimized_IsAppliedWhenRestoredToNormal()
    {
        var (service, _) = CreateService();
        var window = CreateShellSizedWindow();
        service.Attach(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();

        // A display change arrives while minimized: the window holds stale, now-invalid
        // normal geometry and the display events fire the shared clamp entry point.
        window.Position = new PixelPoint(-9999, -9999);
        window.Width = 20000;
        window.Height = 20000;
        Dispatcher.UIThread.RunJobs();
        service.RequestClamp(window);
        Dispatcher.UIThread.RunJobs();

        // While minimized the clamp is intentionally deferred: nothing may move yet.
        Assert.Equal(WindowState.Minimized, window.WindowState);
        Assert.Equal(new PixelPoint(-9999, -9999), window.Position);

        window.WindowState = WindowState.Normal;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(); // let layout apply any correction the clamp wrote
        try
        {
            AssertInsideWorkArea(window);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RequestClamp_OnContainedWindow_IsIdempotent()
    {
        var (service, _) = CreateService();
        var window = CreateShellSizedWindow();
        service.Attach(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var position = window.Position;
        var width = window.Width;
        var height = window.Height;

        service.RequestClamp(window);
        service.RequestClamp(window);
        Dispatcher.UIThread.RunJobs();
        service.RequestClamp(window);
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(position, window.Position);
            Assert.Equal(width, window.Width);
            Assert.Equal(height, window.Height);
        }
        finally
        {
            window.Close();
        }
    }
}

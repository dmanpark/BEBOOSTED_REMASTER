using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using BeBoosted.Application.Settings;

namespace BeBoosted.Desktop.Services;

/// <summary>
/// Persists and restores main-window geometry across sessions, keeping the frame inside the
/// selected monitor's working area so bottom-edge controls never sit under the taskbar. The
/// clamp re-runs after opening, after restoring down from maximized, and after scaling or
/// screen-layout changes, using the measured frame size once one is available.
/// </summary>
public sealed class WindowStateService(AppSettings settings)
{
    /// <summary>
    /// DIP allowance for the OS frame (side borders, title bar) when clamping before the
    /// window is shown; corrected with the measured frame size once the window has opened.
    /// </summary>
    internal static readonly Size EstimatedChromeSize = new(0, 32);

    private readonly HashSet<Window> _pendingClamps = [];

    public void Attach(Window window)
    {
        Restore(window);
        window.Opened += (_, _) => RequestClamp(window);
        window.PropertyChanged += (_, e) =>
        {
            // Any return to Normal re-validates the bounds: restore-down re-applies
            // normal bounds that were clamped with estimated chrome only, and display
            // changes that arrive while minimized are skipped by the clamp until the
            // window is normal again.
            if (e.Property == Window.WindowStateProperty
                && e.NewValue is WindowState.Normal
                && e.OldValue is not WindowState.Normal)
            {
                RequestClamp(window);
            }
        };
        window.ScalingChanged += (_, _) => RequestClamp(window);
        EventHandler screensChanged = (_, _) => RequestClamp(window);
        window.Screens.Changed += screensChanged;
        window.Closed += (_, _) => window.Screens.Changed -= screensChanged;
        window.Closing += (_, _) => Save(window);
    }

    /// <summary>
    /// Coalesces clamp requests into one pass deferred at background priority, so Avalonia
    /// has applied the new window metrics before the geometry is validated against them.
    /// </summary>
    internal void RequestClamp(Window window)
    {
        if (!_pendingClamps.Add(window))
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                _pendingClamps.Remove(window);
                ClampToWorkArea(window);
            },
            DispatcherPriority.Background);
    }

    private void Restore(Window window)
    {
        var resolved = WindowPlacementMath.Resolve(
            settings.GetWindowPlacement(),
            new Size(window.Width, window.Height),
            new Size(window.MinWidth, window.MinHeight),
            EstimatedChromeSize,
            ScreensOf(window));
        if (resolved is null)
        {
            return;
        }

        window.Width = resolved.Width;
        window.Height = resolved.Height;
        if (resolved.Position is { } position)
        {
            // CenterScreen would reposition the window again at Show and undo the clamp.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = position;
        }

        if (resolved.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private static void ClampToWorkArea(Window window)
    {
        if (!window.IsVisible || window.WindowState != WindowState.Normal)
        {
            return;
        }

        var chrome = window.FrameSize is { } frame
            ? new Size(
                Math.Max(0, frame.Width - window.ClientSize.Width),
                Math.Max(0, frame.Height - window.ClientSize.Height))
            : EstimatedChromeSize;
        var resolved = WindowPlacementMath.ResolveLive(
            window.Position,
            window.ClientSize, // fractional live client dimensions preserved
            new Size(window.MinWidth, window.MinHeight),
            chrome,
            window.RenderScaling,
            ScreensOf(window));
        if (resolved?.Position is not { } position)
        {
            return;
        }

        // Containment is decided in physical pixels, and every nonzero pixel correction is
        // applied — a sub-DIP difference can still be a whole pixel (816.5→816 DIP at 200%
        // is 1633→1632 px). Writing only on pixel differences also means a clamp can never
        // feed a resize/reposition event loop: a second pass resolves to the same pixels.
        // Pixels are compared at the resolved target screen's scaling, never an unrelated one.
        var scaling = resolved.Scaling;
        if (WindowPlacementMath.ToPixelsCeiling(resolved.Width, scaling)
            != WindowPlacementMath.ToPixelsCeiling(window.ClientSize.Width, scaling))
        {
            if (window.Width == resolved.Width)
            {
                // The desired-size property can diverge from the live client during native
                // resizing; resync it first so the corrective write below is never a no-op.
                window.Width = window.ClientSize.Width;
            }

            window.Width = resolved.Width;
        }

        if (WindowPlacementMath.ToPixelsCeiling(resolved.Height, scaling)
            != WindowPlacementMath.ToPixelsCeiling(window.ClientSize.Height, scaling))
        {
            if (window.Height == resolved.Height)
            {
                window.Height = window.ClientSize.Height;
            }

            window.Height = resolved.Height;
        }

        if (window.Position != position)
        {
            window.Position = position;
        }
    }

    private static IReadOnlyList<ScreenWorkArea> ScreensOf(Window window)
        => [.. window.Screens.All
            .OrderByDescending(screen => screen.IsPrimary)
            .Select(screen => new ScreenWorkArea(screen.WorkingArea, screen.Scaling))];

    private void Save(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            return;
        }

        var placement = new WindowPlacement(
            window.Position.X,
            window.Position.Y,
            (int)window.Width,
            (int)window.Height,
            window.WindowState == WindowState.Maximized);
        settings.SetWindowPlacement(placement);
    }
}

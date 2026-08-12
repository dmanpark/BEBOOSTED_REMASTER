using Avalonia;
using Avalonia.Controls;
using BeBoosted.Application.Settings;

namespace BeBoosted.Desktop.Services;

/// <summary>Persists and restores main-window geometry across sessions.</summary>
public sealed class WindowStateService(AppSettings settings)
{
    public void Attach(Window window)
    {
        Restore(window);
        window.Closing += (_, _) => Save(window);
    }

    private void Restore(Window window)
    {
        var placement = settings.GetWindowPlacement();
        if (placement is null)
        {
            return;
        }

        window.Width = Math.Max(placement.Width, window.MinWidth);
        window.Height = Math.Max(placement.Height, window.MinHeight);

        // Only restore the position when it still lands on a connected screen.
        var probe = new PixelRect(placement.X, placement.Y, placement.Width, placement.Height);
        if (window.Screens.All.Any(screen => screen.Bounds.Intersects(probe)))
        {
            window.Position = new PixelPoint(placement.X, placement.Y);
        }

        if (placement.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

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

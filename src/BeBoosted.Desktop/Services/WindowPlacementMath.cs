using Avalonia;
using BeBoosted.Application.Settings;

namespace BeBoosted.Desktop.Services;

/// <summary>A connected screen's working area in physical pixels plus its DIP scaling.</summary>
public readonly record struct ScreenWorkArea(PixelRect WorkingArea, double Scaling);

/// <summary>
/// Resolved normal-state window geometry: frame position in physical pixels (null when no
/// screen information exists to validate one against), client size in DIPs, and the
/// scaling of the screen the geometry was resolved against (1.0 when screenless) so
/// callers compare pixels at the selected screen's scale, never an unrelated one.
/// </summary>
public sealed record ResolvedWindowBounds(
    PixelPoint? Position, double Width, double Height, bool IsMaximized, double Scaling = 1.0);

/// <summary>
/// Pure placement geometry so work-area clamping is unit-testable without a windowing system.
/// Saved placements store the frame position in physical pixels and the client size in DIPs;
/// screens scale between the two, so every rule here converts per target screen.
/// </summary>
public static class WindowPlacementMath
{
    /// <summary>Guards ceiling conversions against floating-point dust from DIP↔pixel round trips.</summary>
    private const double PixelEpsilon = 0.001;

    /// <summary>Worst-case (ceiling) DIP→pixel conversion, immune to floating-point dust.</summary>
    internal static int ToPixelsCeiling(double dip, double scaling)
        => (int)Math.Ceiling((dip * scaling) - PixelEpsilon);

    /// <summary>
    /// Resolves safe geometry for a live window from its actual — possibly fractional —
    /// client size in DIPs and its current frame position in physical pixels. The fraction
    /// is preserved: containment is decided on the client's worst-case (ceiling) pixel
    /// footprint, so an 816.5-DIP client on a 200% screen counts as its full 1633 pixels.
    /// </summary>
    internal static ResolvedWindowBounds? ResolveLive(
        PixelPoint position,
        Size clientSize,
        Size minClientSize,
        Size chromeSize,
        double liveScaling,
        IReadOnlyList<ScreenWorkArea> screens)
    {
        // A live window has exactly one actual physical frame — its measured geometry at
        // its current scaling. Target-screen selection must compare that one rectangle
        // against every candidate; the selected screen's scaling applies only afterwards,
        // when the safe target DIP dimensions are computed.
        var liveFrame = new PixelRect(
            position.X,
            position.Y,
            ToPixelsCeiling(clientSize.Width, liveScaling) + ToPixelsCeiling(chromeSize.Width, liveScaling),
            ToPixelsCeiling(clientSize.Height, liveScaling) + ToPixelsCeiling(chromeSize.Height, liveScaling));
        return ResolveCore(
            position,
            Math.Max(clientSize.Width, minClientSize.Width),
            Math.Max(clientSize.Height, minClientSize.Height),
            isMaximized: false,
            minClientSize,
            chromeSize,
            screens,
            liveFrame);
    }

    /// <summary>
    /// Resolves the geometry to apply for a launch: the saved placement when one exists,
    /// otherwise the given defaults, with size and position constrained to the working area
    /// of the screen the window belongs on (the first screen — primary — when the saved
    /// position no longer lands on any). Returns null when there is nothing to apply.
    ///
    /// Invariant: the frame is fully contained in the working area whenever that area can
    /// hold the minimum window plus chrome. On smaller work areas the minimum wins (the
    /// window would re-inflate below it anyway); the frame then overflows right/bottom only,
    /// and top/left alignment keeps the title bar reachable.
    /// </summary>
    public static ResolvedWindowBounds? Resolve(
        WindowPlacement? saved,
        Size defaultClientSize,
        Size minClientSize,
        Size chromeSize,
        IReadOnlyList<ScreenWorkArea> screens)
    {
        if (saved is null && screens.Count == 0)
        {
            return null;
        }

        return ResolveCore(
            saved is null ? null : new PixelPoint(saved.X, saved.Y),
            saved is null ? defaultClientSize.Width : Math.Max(saved.Width, minClientSize.Width),
            saved is null ? defaultClientSize.Height : Math.Max(saved.Height, minClientSize.Height),
            saved?.IsMaximized ?? false,
            minClientSize,
            chromeSize,
            screens);
    }

    private static ResolvedWindowBounds ResolveCore(
        PixelPoint? savedPosition,
        double width,
        double height,
        bool isMaximized,
        Size minClientSize,
        Size chromeSize,
        IReadOnlyList<ScreenWorkArea> screens,
        PixelRect? liveFrame = null)
    {
        if (screens.Count == 0)
        {
            // No screen to validate a position against; apply only the size.
            return new ResolvedWindowBounds(null, width, height, isMaximized, Scaling: 1.0);
        }

        var (target, savedIsOnTarget) = PickTargetScreen(
            savedPosition, width, height, chromeSize, screens, liveFrame);
        var workArea = target.WorkingArea;
        var scaling = target.Scaling;

        // All clamping happens in integer physical pixels: DIP work-area bounds pushed
        // through fractional scale factors would otherwise round a frame one pixel past
        // the work-area edge. Desired sizes convert with ceiling semantics so fractional
        // live clients count as their full worst-case pixel footprint.
        var chromePxWidth = ToPixelsCeiling(chromeSize.Width, scaling);
        var chromePxHeight = ToPixelsCeiling(chromeSize.Height, scaling);
        var clientPxWidth = Math.Min(
            ToPixelsCeiling(width, scaling),
            Math.Max(ToPixelsCeiling(minClientSize.Width, scaling), workArea.Width - chromePxWidth));
        var clientPxHeight = Math.Min(
            ToPixelsCeiling(height, scaling),
            Math.Max(ToPixelsCeiling(minClientSize.Height, scaling), workArea.Height - chromePxHeight));

        // Return pixel-aligned DIP sizes so any later DIP→pixel conversion — including a
        // ceiling one — lands back on the same integer and cannot overflow by a pixel.
        width = clientPxWidth / scaling;
        height = clientPxHeight / scaling;

        var frameWidth = clientPxWidth + chromePxWidth;
        var frameHeight = clientPxHeight + chromePxHeight;
        int x, y;
        if (savedPosition is not { } saved || !savedIsOnTarget)
        {
            x = workArea.X + Math.Max(0, (workArea.Width - frameWidth) / 2);
            y = workArea.Y + Math.Max(0, (workArea.Height - frameHeight) / 2);
        }
        else
        {
            x = Math.Clamp(saved.X, workArea.X, Math.Max(workArea.X, workArea.Right - frameWidth));
            y = Math.Clamp(saved.Y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - frameHeight));
        }

        return new ResolvedWindowBounds(new PixelPoint(x, y), width, height, isMaximized, scaling);
    }

    /// <summary>
    /// Picks the screen whose working area overlaps the window frame the most; falls back
    /// to the first screen (with a recenter signal) when the placement is disconnected.
    /// The window is one physical rectangle: a live window supplies its actual measured
    /// frame, and a saved startup placement is reconstructed once — with the scale of the
    /// screen anchoring its saved top-left (first screen when none contains it) — before
    /// that same rectangle is compared against every candidate.
    /// </summary>
    private static (ScreenWorkArea Target, bool SavedIsOnTarget) PickTargetScreen(
        PixelPoint? savedPosition,
        double width,
        double height,
        Size chromeSize,
        IReadOnlyList<ScreenWorkArea> screens,
        PixelRect? liveFrame)
    {
        if (savedPosition is not { } saved)
        {
            return (screens[0], false);
        }

        var frame = liveFrame
            ?? ReconstructedFrame(saved, width, height, chromeSize, AnchorScale(saved, screens));
        var best = screens[0];
        long bestOverlap = 0;
        foreach (var screen in screens)
        {
            if (!screen.WorkingArea.Intersects(frame))
            {
                continue;
            }

            var overlap = screen.WorkingArea.Intersect(frame);
            var area = (long)overlap.Width * overlap.Height;
            if (area > bestOverlap)
            {
                bestOverlap = area;
                best = screen;
            }
        }

        return (best, bestOverlap > 0);
    }

    /// <summary>
    /// The scale a saved placement was most plausibly recorded at: the scale of the screen
    /// containing its top-left corner, or deterministically the first (primary) screen's
    /// when the position no longer lands on any connected screen.
    /// </summary>
    private static double AnchorScale(PixelPoint anchor, IReadOnlyList<ScreenWorkArea> screens)
    {
        foreach (var screen in screens)
        {
            if (screen.WorkingArea.Contains(anchor))
            {
                return screen.Scaling;
            }
        }

        return screens[0].Scaling;
    }

    private static PixelRect ReconstructedFrame(
        PixelPoint position, double width, double height, Size chromeSize, double scaling)
        => new(
            position.X,
            position.Y,
            ToPixelsCeiling(width, scaling) + ToPixelsCeiling(chromeSize.Width, scaling),
            ToPixelsCeiling(height, scaling) + ToPixelsCeiling(chromeSize.Height, scaling));
}

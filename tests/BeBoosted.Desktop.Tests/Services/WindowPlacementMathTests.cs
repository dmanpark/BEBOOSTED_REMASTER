using Avalonia;
using BeBoosted.Application.Settings;
using BeBoosted.Desktop.Services;

namespace BeBoosted.Desktop.Tests.Services;

/// <summary>
/// Regression coverage for BB-QA-002: restored and default window geometry must be fully
/// contained in the selected screen's working area across scaling, taskbar, resolution,
/// and monitor changes. Positions are physical pixels; sizes are DIPs.
/// </summary>
public sealed class WindowPlacementMathTests
{
    /// <summary>Total frame overhead in DIPs: side resize borders plus the title bar.</summary>
    private static readonly Size Chrome = new(14, 32);

    private static readonly Size DefaultSize = new(1440, 960);
    private static readonly Size MinSize = new(1100, 720);

    /// <summary>The audited machine: 2880×1800 at 200% with a 96 px bottom taskbar.</summary>
    private static readonly ScreenWorkArea HighDpiScreen = new(new PixelRect(0, 0, 2880, 1704), 2.0);

    /// <summary>A 1920×1080 monitor at 100% with a 40 px bottom taskbar.</summary>
    private static readonly ScreenWorkArea StandardScreen = new(new PixelRect(0, 0, 1920, 1040), 1.0);

    private static ResolvedWindowBounds Resolve(WindowPlacement? saved, params ScreenWorkArea[] screens)
    {
        var resolved = WindowPlacementMath.Resolve(saved, DefaultSize, MinSize, Chrome, screens);
        Assert.NotNull(resolved);
        return resolved;
    }

    /// <summary>
    /// The frame the OS could produce at worst: client and chrome each converted DIP→pixel
    /// with ceiling semantics, so a resolved size that only survives round-conversion fails.
    /// </summary>
    private static PixelSize FramePx(ResolvedWindowBounds resolved, ScreenWorkArea screen)
        => new(
            WindowPlacementMath.ToPixelsCeiling(resolved.Width, screen.Scaling)
                + WindowPlacementMath.ToPixelsCeiling(Chrome.Width, screen.Scaling),
            WindowPlacementMath.ToPixelsCeiling(resolved.Height, screen.Scaling)
                + WindowPlacementMath.ToPixelsCeiling(Chrome.Height, screen.Scaling));

    private static void AssertContained(ResolvedWindowBounds resolved, ScreenWorkArea screen)
    {
        Assert.NotNull(resolved.Position);
        var position = resolved.Position!.Value;
        var frame = FramePx(resolved, screen);
        var frameWidth = frame.Width;
        var frameHeight = frame.Height;
        var workArea = screen.WorkingArea;

        Assert.True(position.X >= workArea.X, $"Frame left {position.X} outside work area {workArea}");
        Assert.True(position.Y >= workArea.Y, $"Frame top {position.Y} outside work area {workArea}");
        Assert.True(
            position.X + frameWidth <= workArea.Right,
            $"Frame right {position.X + frameWidth} outside work area {workArea}");
        Assert.True(
            position.Y + frameHeight <= workArea.Bottom,
            $"Frame bottom {position.Y + frameHeight} outside work area {workArea}");
    }

    [Fact]
    public void DefaultLaunch_OnHighDpiScreen_FitsInsideWorkArea()
    {
        var resolved = Resolve(saved: null, HighDpiScreen);

        // Work area is 852 DIP tall at 200%; the client must also leave room for the title bar.
        Assert.True(resolved.Height <= 852 - Chrome.Height, $"Height {resolved.Height} overflows the work area");
        Assert.False(resolved.IsMaximized);
        AssertContained(resolved, HighDpiScreen);
    }

    [Fact]
    public void RestoredOversizedPlacement_OnHighDpiScreen_IsClamped()
    {
        // The exact audited defect: a persisted 1440×960 DIP window on the 200% display.
        var resolved = Resolve(new WindowPlacement(0, 0, 1440, 960, IsMaximized: false), HighDpiScreen);

        AssertContained(resolved, HighDpiScreen);
    }

    [Fact]
    public void NegativePlacement_IsMovedInsideWorkArea()
    {
        var resolved = Resolve(new WindowPlacement(-5000, -4000, 1200, 800, IsMaximized: false), StandardScreen);

        Assert.Equal(1200, resolved.Width);
        Assert.Equal(800, resolved.Height);
        AssertContained(resolved, StandardScreen);
    }

    [Fact]
    public void PartiallyIntersectingPlacement_IsPulledFullyInside()
    {
        var resolved = Resolve(new WindowPlacement(1500, 700, 1200, 800, IsMaximized: false), StandardScreen);

        Assert.Equal(1200, resolved.Width);
        Assert.Equal(800, resolved.Height);
        AssertContained(resolved, StandardScreen);
    }

    [Fact]
    public void PlacementOnDisconnectedScreen_RecentersOnRemainingScreen()
    {
        var resolved = Resolve(new WindowPlacement(5000, 300, 1200, 800, IsMaximized: false), StandardScreen);

        AssertContained(resolved, StandardScreen);
        Assert.Equal(353, resolved.Position!.Value.X); // (1920 − (1200 + 14)) / 2
    }

    [Fact]
    public void OversizedSavedSize_ClampsToWorkArea()
    {
        var resolved = Resolve(new WindowPlacement(0, 0, 20000, 20000, IsMaximized: false), StandardScreen);

        Assert.Equal(1920 - Chrome.Width, resolved.Width);
        Assert.Equal(1040 - Chrome.Height, resolved.Height);
        AssertContained(resolved, StandardScreen);
    }

    [Fact]
    public void ResolutionShrink_ReclampsFormerlyValidPlacement()
    {
        // Saved on a larger display; the machine now runs 1366×768 with a 48 px taskbar.
        var small = new ScreenWorkArea(new PixelRect(0, 0, 1366, 720), 1.0);

        var resolved = Resolve(new WindowPlacement(0, 0, 1600, 900, IsMaximized: false), small);

        Assert.Equal(1366 - Chrome.Width, resolved.Width);
        // The minimum client height cannot fit under the chrome here; the window stays at the
        // minimum (the OS min-size constraint would win anyway) but must stay top-aligned so
        // the title bar and all upper content remain reachable.
        Assert.Equal(MinSize.Height, resolved.Height);
        Assert.Equal(0, resolved.Position!.Value.Y);
    }

    [Fact]
    public void TinySavedSize_IsFlooredToMinimum()
    {
        var resolved = Resolve(new WindowPlacement(10, 10, 200, 150, IsMaximized: false), StandardScreen);

        Assert.Equal(MinSize.Width, resolved.Width);
        Assert.Equal(MinSize.Height, resolved.Height);
        AssertContained(resolved, StandardScreen);
    }

    [Fact]
    public void MaximizedPlacement_KeepsFlagWithSafeNormalBounds()
    {
        var resolved = Resolve(new WindowPlacement(0, 0, 1440, 960, IsMaximized: true), HighDpiScreen);

        Assert.True(resolved.IsMaximized);
        AssertContained(resolved, HighDpiScreen);
    }

    [Fact]
    public void OffsetWorkArea_WithTaskbarOnLeftAndTop_IsRespected()
    {
        var offset = new ScreenWorkArea(new PixelRect(64, 48, 1800, 1000), 1.0);

        var resolved = Resolve(new WindowPlacement(0, 0, 1200, 800, IsMaximized: false), offset);

        Assert.True(resolved.Position!.Value.X >= 64);
        Assert.True(resolved.Position!.Value.Y >= 48);
        AssertContained(resolved, offset);
    }

    [Fact]
    public void PlacementOnSecondaryHighDpiScreen_StaysThere()
    {
        var secondary = new ScreenWorkArea(new PixelRect(1920, 0, 2880, 1704), 2.0);

        var resolved = Resolve(
            new WindowPlacement(2200, 100, 1400, 900, IsMaximized: false),
            StandardScreen,
            secondary);

        Assert.True(resolved.Position!.Value.X >= 1920, "Window jumped off its saved monitor");
        AssertContained(resolved, secondary);
    }

    [Fact]
    public void SafePlacement_IsPreservedExactly()
    {
        var resolved = Resolve(new WindowPlacement(100, 100, 1200, 800, IsMaximized: false), StandardScreen);

        Assert.Equal(new PixelPoint(100, 100), resolved.Position);
        Assert.Equal(1200, resolved.Width);
        Assert.Equal(800, resolved.Height);
        Assert.False(resolved.IsMaximized);
    }

    [Fact]
    public void SavedPlacementWithoutScreens_KeepsSizeWithoutPosition()
    {
        var resolved = Resolve(new WindowPlacement(50, 60, 1200, 800, IsMaximized: false));

        Assert.Null(resolved.Position);
        Assert.Equal(1200, resolved.Width);
        Assert.Equal(800, resolved.Height);
    }

    [Fact]
    public void NothingSavedAndNoScreens_ReturnsNull()
        => Assert.Null(WindowPlacementMath.Resolve(null, DefaultSize, MinSize, Chrome, []));

    [Theory]
    [InlineData(1.2)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void FractionalScaling_OversizedPlacement_FillsWorkAreaWithoutOnePixelOverflow(double scaling)
    {
        var screen = new ScreenWorkArea(new PixelRect(0, 0, 2560, 1400), scaling);

        var resolved = Resolve(new WindowPlacement(0, 0, 4000, 3000, IsMaximized: false), screen);

        AssertContained(resolved, screen);
        var frame = FramePx(resolved, screen);
        Assert.Equal(2560, frame.Width);
        Assert.Equal(1400, frame.Height);
    }

    [Fact]
    public void FractionalScaling_1366WideWorkArea_DoesNotOverflowByOnePixel()
    {
        // Review case: 1366 px work area at 1.2 scaling with 14 DIP chrome must resolve to
        // a frame of exactly 1366 px, not 1367.
        var screen = new ScreenWorkArea(new PixelRect(0, 0, 1366, 1000), 1.2);

        var resolved = Resolve(new WindowPlacement(0, 0, 1600, 800, IsMaximized: false), screen);

        AssertContained(resolved, screen);
        Assert.Equal(1366, FramePx(resolved, screen).Width);
    }

    [Fact]
    public void TaskbarGrowth_WhileWindowOpen_ReclampsCurrentGeometry()
    {
        // The service feeds the window's live geometry back through Resolve when the
        // screen layout changes; a taller taskbar must push the window up.
        var shrunk = new ScreenWorkArea(new PixelRect(0, 0, 1920, 1000), 1.0);

        var resolved = Resolve(new WindowPlacement(0, 208, 1200, 800, IsMaximized: false), shrunk);

        Assert.Equal(168, resolved.Position!.Value.Y); // 1000 − (800 + 32)
        AssertContained(resolved, shrunk);
    }

    [Fact]
    public void DpiIncrease_WhileWindowOpen_ReclampsCurrentGeometry()
    {
        // Same monitor rescaled from 100% to 150% while the window is open.
        var rescaled = new ScreenWorkArea(new PixelRect(0, 0, 2560, 1400), 1.5);

        var resolved = Resolve(new WindowPlacement(0, 0, 2400, 1200, IsMaximized: false), rescaled);

        AssertContained(resolved, rescaled);
    }

    [Fact]
    public void FractionalLiveClient_AfterWorkAreaShrinksOnePixel_GivesBackOnePixel()
    {
        // Exact audited runtime case: 200% display whose work area shrank 1704→1703 px
        // while the live client is 816.5 DIP (1633 px) tall under 71 px (35.5 DIP) chrome.
        // The fractional client must be preserved, and the resolver must give back exactly
        // one pixel: a 1632 px (816 DIP) client.
        var screen = new ScreenWorkArea(new PixelRect(0, 0, 2880, 1703), 2.0);

        var resolved = WindowPlacementMath.ResolveLive(
            new PixelPoint(0, 0),
            new Size(1427, 816.5),
            MinSize,
            new Size(13, 35.5),
            2.0,
            [screen]);

        Assert.NotNull(resolved);
        Assert.Equal(816, resolved!.Height);
        Assert.Equal(1632, WindowPlacementMath.ToPixelsCeiling(resolved.Height, screen.Scaling));
        Assert.Equal(
            1703,
            WindowPlacementMath.ToPixelsCeiling(resolved.Height + 35.5, screen.Scaling));
    }

    [Fact]
    public void LiveMixedDpi_TargetSelectionUsesTheActualPhysicalFrame()
    {
        // A live window has exactly one physical frame while its monitor is selected:
        // 1000 DIP client + 14 DIP chrome at the live 1.0 scale = 1014 px, at X=1200.
        // That frame overlaps the left (100%) screen by 720 px and the right (200%)
        // screen by 294 px, so the left screen is the correct target. Reconstructing a
        // hypothetical 2028 px frame with the right screen's scaling would produce
        // 1308 px of right-screen overlap and wrongly move the window there.
        var left = new ScreenWorkArea(new PixelRect(0, 0, 1920, 1040), 1.0);
        var right = new ScreenWorkArea(new PixelRect(1920, 0, 2880, 1704), 2.0);
        var selectionMin = new Size(400, 300); // selection scenario; app minimum covered elsewhere

        var resolved = WindowPlacementMath.ResolveLive(
            new PixelPoint(1200, 0),
            new Size(1000, 700),
            selectionMin,
            new Size(14, 32),
            1.0,
            [left, right]);

        Assert.NotNull(resolved);
        Assert.Equal(1.0, resolved!.Scaling); // left screen retained
        Assert.Equal(1000, resolved.Width);   // fits the left work area unchanged
        Assert.Equal(700, resolved.Height);
        // Independently computed: the 1014 px frame is pulled fully onto the left
        // screen, so X = 1920 − 1014 = 906 and the frame right edge is exactly 1920.
        Assert.Equal(906, resolved.Position!.Value.X);
        Assert.Equal(0, resolved.Position.Value.Y);
    }

    [Fact]
    public void SavedMixedDpi_TargetSelectionUsesOneAnchorScaledFrame()
    {
        // A saved startup placement also describes one window, so exactly one physical
        // frame must be reconstructed — using the scale of the screen anchoring the
        // saved top-left (1000,0 lies on the 100% left screen): 1200 DIP client +
        // 14 DIP chrome = 1214 px, overlapping the left screen by 920 px and the right
        // screen by 294 px. Reconstructing per candidate would produce a hypothetical
        // 2428 px frame on the 200% screen with 1508 px of overlap and wrongly select it.
        var left = new ScreenWorkArea(new PixelRect(0, 0, 1920, 1080), 1.0);
        var right = new ScreenWorkArea(new PixelRect(1920, 0, 2880, 1800), 2.0);

        var resolved = Resolve(new WindowPlacement(1000, 0, 1200, 800, IsMaximized: false), left, right);

        Assert.Equal(1.0, resolved.Scaling); // left screen retained
        Assert.Equal(1200, resolved.Width);  // fits the left work area unchanged
        Assert.Equal(800, resolved.Height);
        // Independently computed: the 1214 px frame is pulled fully onto the left
        // screen, so X = 1920 − 1214 = 706 and the frame right edge is exactly 1920.
        Assert.Equal(706, resolved.Position!.Value.X);
        Assert.Equal(0, resolved.Position.Value.Y);
    }

    [Fact]
    public void WorkAreaSmallerThanMinimum_KeepsMinimumAndAlignsTopLeft()
    {
        // Invariant: the frame is fully contained whenever the work area can hold the
        // minimum window; below that the approved 1100×720 minimum wins, the frame is
        // NOT contained, and top-left alignment keeps the title bar reachable.
        var tiny = new ScreenWorkArea(new PixelRect(0, 0, 800, 600), 1.0);

        var resolved = Resolve(new WindowPlacement(100, 100, 1200, 900, IsMaximized: false), tiny);

        Assert.Equal(MinSize.Width, resolved.Width);
        Assert.Equal(MinSize.Height, resolved.Height);
        Assert.Equal(new PixelPoint(0, 0), resolved.Position);
    }
}

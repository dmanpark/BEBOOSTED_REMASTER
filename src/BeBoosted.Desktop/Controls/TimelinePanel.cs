using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BeBoosted.Desktop.CalendarEngine;

namespace BeBoosted.Desktop.Controls;

/// <summary>
/// Positions calendar block views vertically by time and side-by-side when they overlap.
/// All geometry comes from <see cref="TimelineGeometry"/> — no mockup coordinates.
/// Chrome (hour rules, now indicator, drop preview) is drawn by <see cref="TimelineDecorations"/>.
/// </summary>
public sealed class TimelinePanel : Panel
{
    public static readonly StyledProperty<double> HourHeightProperty =
        AvaloniaProperty.Register<TimelinePanel, double>(nameof(HourHeight), 56);

    public static readonly StyledProperty<int> StartHourProperty =
        AvaloniaProperty.Register<TimelinePanel, int>(nameof(StartHour), 0);

    public static readonly StyledProperty<int> EndHourProperty =
        AvaloniaProperty.Register<TimelinePanel, int>(nameof(EndHour), 24);

    public static readonly AttachedProperty<double> StartMinutesProperty =
        AvaloniaProperty.RegisterAttached<TimelinePanel, Control, double>("StartMinutes");

    public static readonly AttachedProperty<double> DurationMinutesProperty =
        AvaloniaProperty.RegisterAttached<TimelinePanel, Control, double>("DurationMinutes", 30);

    static TimelinePanel()
    {
        AffectsMeasure<TimelinePanel>(HourHeightProperty, StartHourProperty, EndHourProperty);

        // Attached-position changes on a child (live drag/resize) re-lay-out its panel.
        // Measure, not just arrange: moving a block changes which blocks share its hour,
        // and therefore the width every one of them is measured at.
        StartMinutesProperty.Changed.AddClassHandler<Control>(
            (control, _) => (control.GetVisualParent() as TimelinePanel)?.InvalidateMeasure());
        DurationMinutesProperty.Changed.AddClassHandler<Control>(
            (control, _) => (control.GetVisualParent() as TimelinePanel)?.InvalidateMeasure());
    }

    public double HourHeight
    {
        get => GetValue(HourHeightProperty);
        set => SetValue(HourHeightProperty, value);
    }

    public int StartHour
    {
        get => GetValue(StartHourProperty);
        set => SetValue(StartHourProperty, value);
    }

    public int EndHour
    {
        get => GetValue(EndHourProperty);
        set => SetValue(EndHourProperty, value);
    }

    public static double GetStartMinutes(Control control) => control.GetValue(StartMinutesProperty);

    public static void SetStartMinutes(Control control, double value) => control.SetValue(StartMinutesProperty, value);

    public static double GetDurationMinutes(Control control) => control.GetValue(DurationMinutesProperty);

    public static void SetDurationMinutes(Control control, double value)
        => control.SetValue(DurationMinutesProperty, value);

    public TimelineGeometry Geometry
        => new(StartHour * 60, EndHour * 60, HourHeight);

    /// <summary>
    /// Measures every child at the rectangle it will be arranged into. Measuring them at
    /// the day column's full width instead — which is what this used to do — left each
    /// block's inner Grid holding the column widths it worked out for a width it never
    /// got: the star column stayed generous, the trailing Auto column sat where a
    /// full-width block would have put it, and on any overlapping block that position was
    /// outside the block. Since a block's arranged width is a function of how many
    /// sessions share its hour, and not of the window, the escape happened at every
    /// window size. Measure and arrange now derive their rectangles from the same place,
    /// so they agree under any finite width.
    /// </summary>
    /// <remarks>
    /// "Finite" is the one caveat, and it is the last place the two can still diverge: an
    /// infinite available width has no arranged counterpart, so measure substitutes 400
    /// and arrange uses whatever finite width it is given. The substitute is unreachable
    /// in this host — the panel sits inside TimelineSurfaceView's Scroller, whose
    /// HorizontalScrollBarVisibility is Disabled (Avalonia's default, read back from a
    /// rendered Week: seven panels, every one at a finite share of the viewport width),
    /// so it always constrains width. Reaching it would take a new host that measures the
    /// panel unconstrained horizontally (a horizontal ScrollViewer, or an auto-width
    /// container), and such a host would need this number to become that host's real
    /// width rather than a guess.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        var rects = BlockRects(width);
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(rects[i].Size);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : width, Geometry.TotalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rects = BlockRects(finalSize.Width);
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(rects[i]);
        }

        return finalSize;
    }

    /// <summary>
    /// Where each child goes for a panel of this width: vertical position and height from
    /// the block's own time, horizontal position and width from how its hour is shared.
    /// </summary>
    private List<Rect> BlockRects(double width)
    {
        var geometry = Geometry;
        var startFloor = StartHour * 60.0;

        var intervals = new List<LayoutInterval>(Children.Count);
        for (var i = 0; i < Children.Count; i++)
        {
            var start = Math.Max(GetStartMinutes(Children[i]), startFloor);
            var end = Math.Min(start + Math.Max(GetDurationMinutes(Children[i]), 5), EndHour * 60.0);
            intervals.Add(new LayoutInterval(i, start, Math.Max(end, start + 1)));
        }

        var slots = OverlapLayout.Arrange(intervals);
        const double horizontalInset = 4;
        var contentWidth = Math.Max(width - (horizontalInset * 2), 0);

        var rects = new List<Rect>(Children.Count);
        for (var i = 0; i < Children.Count; i++)
        {
            var slot = slots[i];
            var y = geometry.YFromMinutes(intervals[i].Start);
            var height = Math.Max(
                geometry.HeightForDuration(TimeSpan.FromMinutes(intervals[i].End - intervals[i].Start)), 18);
            var columnWidth = contentWidth / slot.ColumnCount;
            var x = horizontalInset + (slot.Column * columnWidth);
            var blockWidth = Math.Max(columnWidth - (slot.ColumnCount > 1 ? 2 : 0), 10);
            rects.Add(new Rect(x, y, blockWidth, height));
        }

        return rects;
    }
}

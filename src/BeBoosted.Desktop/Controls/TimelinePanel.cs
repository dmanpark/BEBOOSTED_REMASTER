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

        // Attached-position changes on a child (live drag/resize) re-arrange its panel.
        StartMinutesProperty.Changed.AddClassHandler<Control>(
            (control, _) => (control.GetVisualParent() as TimelinePanel)?.InvalidateArrange());
        DurationMinutesProperty.Changed.AddClassHandler<Control>(
            (control, _) => (control.GetVisualParent() as TimelinePanel)?.InvalidateArrange());
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

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = Geometry.TotalHeight;
        foreach (var child in Children)
        {
            child.Measure(new Size(
                double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
                Geometry.HeightForDuration(TimeSpan.FromMinutes(GetDurationMinutes(child)))));
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
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
        var contentWidth = Math.Max(finalSize.Width - (horizontalInset * 2), 0);

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var slot = slots[i];
            var y = geometry.YFromMinutes(intervals[i].Start);
            var height = Math.Max(
                geometry.HeightForDuration(TimeSpan.FromMinutes(intervals[i].End - intervals[i].Start)), 18);
            var columnWidth = contentWidth / slot.ColumnCount;
            var x = horizontalInset + (slot.Column * columnWidth);
            var width = Math.Max(columnWidth - (slot.ColumnCount > 1 ? 2 : 0), 10);
            child.Arrange(new Rect(x, y, width, height));
        }

        return finalSize;
    }
}

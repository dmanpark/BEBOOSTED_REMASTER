using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BeBoosted.Desktop.CalendarEngine;

namespace BeBoosted.Desktop.Controls;

/// <summary>
/// Draws the timeline chrome under the blocks: fine graphite hour rules, the
/// current-time indicator, and the lime drag-drop preview slot.
/// </summary>
public sealed class TimelineDecorations : Control
{
    public static readonly StyledProperty<double> HourHeightProperty =
        AvaloniaProperty.Register<TimelineDecorations, double>(nameof(HourHeight), 56);

    public static readonly StyledProperty<int> StartHourProperty =
        AvaloniaProperty.Register<TimelineDecorations, int>(nameof(StartHour), 0);

    public static readonly StyledProperty<int> EndHourProperty =
        AvaloniaProperty.Register<TimelineDecorations, int>(nameof(EndHour), 24);

    /// <summary>Minutes since midnight for the current-time rule; negative hides it.</summary>
    public static readonly StyledProperty<double> NowMinutesProperty =
        AvaloniaProperty.Register<TimelineDecorations, double>(nameof(NowMinutes), -1);

    public static readonly StyledProperty<double> PreviewStartMinutesProperty =
        AvaloniaProperty.Register<TimelineDecorations, double>(nameof(PreviewStartMinutes), -1);

    public static readonly StyledProperty<double> PreviewDurationMinutesProperty =
        AvaloniaProperty.Register<TimelineDecorations, double>(nameof(PreviewDurationMinutes), 0);

    private static readonly Color Graphite = Color.Parse("#20231F");
    private static readonly Color Lime = Color.Parse("#C8F24A");
    private static readonly Color LimeWash = Color.Parse("#EDF8C8");

    private static readonly Typeface MonoTypeface =
        new(new FontFamily("avares://BeBoosted/Assets/Fonts/IBMPlexMono#IBM Plex Mono"));

    static TimelineDecorations()
    {
        AffectsMeasure<TimelineDecorations>(HourHeightProperty, StartHourProperty, EndHourProperty);
        AffectsRender<TimelineDecorations>(
            HourHeightProperty, NowMinutesProperty, PreviewStartMinutesProperty, PreviewDurationMinutesProperty);
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

    public double NowMinutes
    {
        get => GetValue(NowMinutesProperty);
        set => SetValue(NowMinutesProperty, value);
    }

    public double PreviewStartMinutes
    {
        get => GetValue(PreviewStartMinutesProperty);
        set => SetValue(PreviewStartMinutesProperty, value);
    }

    public double PreviewDurationMinutes
    {
        get => GetValue(PreviewDurationMinutesProperty);
        set => SetValue(PreviewDurationMinutesProperty, value);
    }

    public TimelineGeometry Geometry
        => new(StartHour * 60, EndHour * 60, HourHeight);

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, Geometry.TotalHeight);

    public override void Render(DrawingContext context)
    {
        var geometry = Geometry;
        var rulePen = new Pen(new SolidColorBrush(Graphite, 0.10), 1);
        foreach (var mark in geometry.HourMarks())
        {
            var y = Math.Round(geometry.YFromTime(mark)) + 0.5;
            context.DrawLine(rulePen, new Point(0, y), new Point(Bounds.Width, y));
        }

        RenderPreview(context, geometry);
        RenderNowIndicator(context, geometry);
    }

    private void RenderPreview(DrawingContext context, TimelineGeometry geometry)
    {
        if (PreviewStartMinutes < 0 || PreviewDurationMinutes <= 0)
        {
            return;
        }

        var start = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(PreviewStartMinutes));
        var y = geometry.YFromTime(start);
        var height = geometry.HeightForDuration(TimeSpan.FromMinutes(PreviewDurationMinutes));
        var rect = new Rect(4, y, Math.Max(Bounds.Width - 8, 10), Math.Max(height, 14));
        var pen = new Pen(new SolidColorBrush(Graphite, 0.55), 1.5) { DashStyle = new DashStyle([3, 2], 0) };
        context.DrawRectangle(new SolidColorBrush(LimeWash, 0.85), pen, new RoundedRect(rect, 5));

        var end = start.AddMinutes(PreviewDurationMinutes);
        var label = new FormattedText(
            $"{start:h\\:mm} – {end:h\\:mm}",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            MonoTypeface,
            11,
            new SolidColorBrush(Graphite));
        context.DrawText(
            label,
            new Point(rect.X + ((rect.Width - label.Width) / 2), rect.Y + ((rect.Height - label.Height) / 2)));
    }

    private void RenderNowIndicator(DrawingContext context, TimelineGeometry geometry)
    {
        var startMinutes = StartHour * 60.0;
        var endMinutes = EndHour * 60.0;
        if (NowMinutes < startMinutes || NowMinutes > endMinutes)
        {
            return;
        }

        var y = geometry.YFromTime(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(NowMinutes)));
        var graphiteBrush = new SolidColorBrush(Graphite);
        context.DrawLine(new Pen(graphiteBrush, 1.5), new Point(0, y), new Point(Bounds.Width, y));
        context.DrawEllipse(new SolidColorBrush(Lime), new Pen(graphiteBrush, 1.5), new Point(5, y), 4.5, 4.5);
    }
}

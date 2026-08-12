using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BeBoosted.Desktop.CalendarEngine;

namespace BeBoosted.Desktop.Controls;

/// <summary>Right-aligned hour labels beside the timeline (IBM Plex Mono, 11px floor).</summary>
public sealed class TimeGutter : Control
{
    public static readonly StyledProperty<double> HourHeightProperty =
        AvaloniaProperty.Register<TimeGutter, double>(nameof(HourHeight), 56);

    public static readonly StyledProperty<int> StartHourProperty =
        AvaloniaProperty.Register<TimeGutter, int>(nameof(StartHour), 6);

    public static readonly StyledProperty<int> EndHourProperty =
        AvaloniaProperty.Register<TimeGutter, int>(nameof(EndHour), 23);

    private static readonly Typeface MonoTypeface =
        new(new FontFamily("avares://BeBoosted/Assets/Fonts/IBMPlexMono#IBM Plex Mono"));

    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#73776E"));

    static TimeGutter()
    {
        AffectsMeasure<TimeGutter>(HourHeightProperty, StartHourProperty, EndHourProperty);
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

    protected override Size MeasureOverride(Size availableSize)
        => new(56, new TimelineGeometry(new TimeOnly(StartHour, 0), new TimeOnly(EndHour, 0), HourHeight).TotalHeight);

    public override void Render(DrawingContext context)
    {
        var geometry = new TimelineGeometry(new TimeOnly(StartHour, 0), new TimeOnly(EndHour, 0), HourHeight);
        foreach (var mark in geometry.HourMarks())
        {
            var text = new FormattedText(
                mark.ToString("h tt", System.Globalization.CultureInfo.CurrentCulture),
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                11,
                LabelBrush);
            context.DrawText(text, new Point(Bounds.Width - text.Width - 8, geometry.YFromTime(mark) - (text.Height / 2)));
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BeBoosted.Desktop.Controls;

/// <summary>Graphite diagonal hatching for conflict states — never an alarming red panel.</summary>
public sealed class HatchOverlay : Control
{
    private static readonly Pen HatchPen = new(new SolidColorBrush(Color.Parse("#20231F"), 0.07), 5);

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        using var clip = context.PushClip(new Rect(bounds.Size));
        for (var x = -bounds.Height; x < bounds.Width; x += 11)
        {
            context.DrawLine(HatchPen, new Point(x, bounds.Height), new Point(x + bounds.Height, 0));
        }
    }
}

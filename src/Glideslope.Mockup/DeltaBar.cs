using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glideslope.Mockup;

// The white tick is drawn from the actual arranged center; negative values extend left.
internal sealed class DeltaBar(double delta, bool compact, bool dark, PaceBand band) : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var track = new Rect(0, 0, w, h);
        context.DrawRectangle(new SolidColorBrush(Color.Parse(dark ? "#303A4B" : "#DCE5F0")), null, track, h / 2, h / 2);
        var center = w / 2;
        var maxDelta = compact ? 0.20 : 0.25;
        var length = Math.Min(Math.Abs(delta) / maxDelta, 1) * (center - 4);
        var fill = new Rect(delta < 0 ? center - length : center, 0, length, h);
        var color = PacePreviewPolicy.Brush(dark, band);
        context.DrawRectangle(color, null, fill, h / 2, h / 2);
        // Square the end that touches zero while leaving the exposed end round.
        var squareWidth = Math.Min(length, h / 2);
        var square = new Rect(delta < 0 ? center - squareWidth : center, 0, squareWidth, h);
        context.DrawRectangle(color, null, square);
        context.DrawLine(new Pen(dark ? Brushes.White : new SolidColorBrush(Color.Parse("#25364E")), 2),
            new Point(center, -1), new Point(center, h + 1));
    }
}

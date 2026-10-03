using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glideslope.Mockup;

internal sealed class RemainingBar(double remaining, bool dark, PaceBand band) : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        context.DrawRectangle(new SolidColorBrush(Color.Parse(dark ? "#303A4B" : "#DCE5F0")), null, new Rect(0, 0, w, h), h / 2, h / 2);
        var fill = PacePreviewPolicy.Hex(dark, band);
        context.DrawRectangle(new SolidColorBrush(Color.Parse(fill)), null,
            new Rect(0, 0, Math.Clamp(remaining, 0, 1) * w, h), h / 2, h / 2);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glideslope.Mockup;

// Small vector mark: the slate diagonal is the glide path; blue is observed usage.
internal sealed class GlideslopeMark : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 64;
        if (scale <= 0) return;
        Point P(double x, double y) => new(x * scale, y * scale);

        context.DrawRectangle(new SolidColorBrush(Color.Parse("#E8F1FF")), null,
            new Rect(0, 0, 64 * scale, 64 * scale), 14 * scale, 14 * scale);
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#8B9AAF")), 2.5 * scale),
            P(12, 17), P(52, 49));

        var blue = new Pen(new SolidColorBrush(Color.Parse("#2469BD")), 4 * scale, lineCap: PenLineCap.Round);
        var samples = new[] { P(12, 18), P(22, 24), P(31, 22), P(40, 34), P(52, 37) };
        for (var i = 1; i < samples.Length; i++)
            context.DrawLine(blue, samples[i - 1], samples[i]);
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#2469BD")), null, samples[^1], 3 * scale, 3 * scale);
    }
}

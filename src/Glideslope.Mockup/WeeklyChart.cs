using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glideslope.Mockup;

internal sealed class WeeklyChart(bool dark, double currentRemaining) : Control
{
    // Explicit synthetic observations for this visual prototype; X is fraction of the week.
    private static readonly (double Time, double Remaining)[] Samples =
    [
        (0.00, 0.99), (0.06, 0.97), (0.13, 0.87), (0.19, 0.84),
        (0.26, 0.75), (0.32, 0.73), (0.38, 0.61), (0.44, 0.56),
        (0.49, 0.52), (0.53, 0.41), (0.60, 0.32)
    ];

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 100 || h < 100) return;

        const double top = 12;
        const double bottom = 33;
        const double left = 7;
        const double right = 8;
        var plotW = w - left - right;
        var plotH = h - top - bottom;
        Point P(double time, double remaining) => new(left + time * plotW, top + (1 - remaining) * plotH);

        var grid = new Pen(new SolidColorBrush(Color.Parse(dark ? "#354154" : "#CCD7E4")), 1);
        var axis = new Pen(new SolidColorBrush(Color.Parse(dark ? "#607084" : "#A5B2C3")), 1);
        var guide = new Pen(new SolidColorBrush(Color.Parse(dark ? "#AEB8C9" : "#8995A7")), 1.5);
        var actual = new Pen(new SolidColorBrush(Color.Parse(dark ? "#61A9F5" : "#2268BB")), 3.5, lineCap: PenLineCap.Round);

        foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
            context.DrawLine(grid, P(0, fraction), P(1, fraction));

        var days = new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
        for (var day = 0; day < 7; day++)
        {
            var x = P(day / 7.0, 0).X;
            context.DrawLine(grid, new Point(x, top), new Point(x, top + plotH));
            DrawText(context, days[day], x + (day == 0 ? 0 : -12), top + plotH + 10,
                day == 4 ? (dark ? "#CBD7E6" : "#354961") : (dark ? "#77869A" : "#64758A"), 10);
        }
        context.DrawLine(axis, P(1, 0), P(1, 1));
        context.DrawLine(guide, P(0, 1), P(1, 0));

        double Adapt(double remaining) => Math.Clamp(
            1 - (1 - remaining) * (1 - currentRemaining) / (1 - Samples[^1].Remaining), 0, 1);
        for (var i = 1; i < Samples.Length; i++)
            context.DrawLine(actual, P(Samples[i - 1].Time, Adapt(Samples[i - 1].Remaining)),
                P(Samples[i].Time, Adapt(Samples[i].Remaining)));

        var last = P(Samples[^1].Time, currentRemaining);
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse(dark ? "#7185A0" : "#9AAABD")), 1, dashStyle: DashStyle.Dash),
            new Point(last.X, top), new Point(last.X, top + plotH));
        context.DrawEllipse(new SolidColorBrush(Color.Parse(dark ? "#171D29" : "#F6F8FC")),
            new Pen(new SolidColorBrush(Color.Parse(dark ? "#61A9F5" : "#2268BB")), 3), last, 6, 6);
        DrawText(context, "NOW", last.X - 14, 0, dark ? "#AFCFF3" : "#245D9B", 10);
    }

    private static void DrawText(DrawingContext context, string value, double x, double y, string color, double size)
    {
        var text = new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), size,
            new SolidColorBrush(Color.Parse(color)));
        context.DrawText(text, new Point(x, y));
    }
}

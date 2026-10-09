using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Glideslope.Domain;

namespace Glideslope.App;

/// <summary>One remaining bar or centered signed pace bar with a stable zero marker.</summary>
internal sealed class QuotaMeter(bool delta) : Control
{
    private double? _remaining;
    private double? _signedDelta;
    private PaceBand? _band;

    public bool IsDark { get; set; } = true;

    /// <summary>A delta bar reaches full scale at one seventh of its window, matching the pace thresholds.</summary>
    internal const double DeltaFullScaleFraction = 1.0 / 7;

    /// <summary>The signed pace delta, or null when no fill should be drawn.</summary>
    internal double? SignedDelta => _signedDelta;

    public void SetValue(double? remaining, PaceBand? band, double? signedDelta)
    {
        _remaining = remaining;
        _band = band;
        _signedDelta = signedDelta;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;
        var track = new SolidColorBrush(Color.Parse(IsDark ? "#303A4B" : "#DCE5F0"));
        context.DrawRectangle(track, null, new Rect(0, 0, width, height), height / 2, height / 2);
        var fillColor = _band is { } band
            ? Color.Parse(PaceColors.For(IsDark, band))
            : Color.Parse(IsDark ? "#68778D" : "#8998AB");
        if (delta)
        {
            if (_signedDelta is { } signed)
            {
                var center = width / 2;
                var amount = Math.Min(Math.Abs(signed) / DeltaFullScaleFraction, 1) * center;
                var x = signed < 0 ? center - amount : center;
                context.DrawRectangle(new SolidColorBrush(fillColor), null, new Rect(x, 0, amount, height), height / 2, height / 2);
                if (amount > 0)
                {
                    var squareWidth = Math.Min(amount, height / 2);
                    context.DrawRectangle(new SolidColorBrush(fillColor), null,
                        new Rect(signed < 0 ? center - squareWidth : center, 0, squareWidth, height));
                }
            }
            IBrush marker = IsDark ? Brushes.White : new SolidColorBrush(Color.Parse("#25364E"));
            context.DrawLine(new Pen(marker, 2), new Point(width / 2, -1), new Point(width / 2, height + 1));
        }
        else if (_remaining is { } remaining)
        {
            var fraction = Math.Clamp(remaining, 0, 1);
            context.DrawRectangle(new SolidColorBrush(fillColor), null,
                new Rect(0, 0, fraction * width, height), height / 2, height / 2);
        }
    }
}

internal static class PaceColors
{
    public static string For(bool dark, PaceBand band) => (dark, band) switch
    {
        (true, PaceBand.StrongCushion) => "#39FF88",
        (true, PaceBand.OnPace) => "#8FE36B",
        (true, PaceBand.SlightlyOver) => "#E9D66B",
        (true, PaceBand.Over) => "#FFC928",
        (true, PaceBand.SeverelyOver) => "#FF8A65",
        (true, PaceBand.CriticalOver) => "#FF3B30",
        (false, PaceBand.StrongCushion) => "#008F46",
        (false, PaceBand.OnPace) => "#4E833C",
        (false, PaceBand.SlightlyOver) => "#806B00",
        (false, PaceBand.Over) => "#A85A00",
        (false, PaceBand.SeverelyOver) => "#BB4932",
        (false, PaceBand.CriticalOver) => "#C62828",
        _ => throw new ArgumentOutOfRangeException(nameof(band))
    };
}

/// <summary>Plots selected observations for the provider-reported week and an optional average-rate projection.</summary>
internal sealed class WeeklyHistoryChart : Control
{
    private UsageWindowIdentity? _window;
    private ChartSeries _series = new([], []);
    private UsageProjection? _projection;
    private SnapshotFreshness _projectionFreshness = SnapshotFreshness.RestoredHistorical;
    private bool _projectionHeld;
    private Point? _pointer;
    private bool _historyAvailable = true;

    /// <summary>Font size for custom-drawn day labels, kept at the card's text floor.</summary>
    internal const double DayLabelFontSize = 16;
    internal const double EmptyStateFontSize = 16;

    /// <summary>Observed samples form a thin line; markers identify the first sample and unobserved gaps.</summary>
    internal const double TraceThickness = 1.5;
    internal const double MarkerRadius = 3.5;

    public bool IsDark { get; set; } = true;
    public TimeSpan SamplingInterval { get; set; } = TimeSpan.FromMinutes(5);
    internal bool HasProjection => _projection is not null;
    internal bool IsProjectionHeld => _projectionHeld;

    public WeeklyHistoryChart()
    {
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => { _pointer = null; InvalidateVisual(); };
    }

    public void SetSeries(UsageWindowIdentity? window, IEnumerable<UsageObservation> samples, bool historyAvailable = true)
    {
        _window = window;
        _historyAvailable = historyAvailable;
        _series = window is null
            ? new ChartSeries([], [])
            : ChartSeriesSelector.Select(samples, window, SamplingInterval, DateTimeOffset.UtcNow);
        InvalidateVisual();
    }

    public void SetProjection(UsageProjection? projection, SnapshotFreshness freshness)
    {
        _projection = projection;
        _projectionFreshness = freshness;
        InvalidateVisual();
    }

    public void SetProjectionHeld(bool held)
    {
        if (_projectionHeld == held) return;
        _projectionHeld = held;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 100 || height < 100) return;
        const double left = 8;
        const double right = 8;
        const double top = 18;
        // Reserve enough space below the plot for the day labels.
        const double bottom = 34;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        if (plotWidth <= 0 || plotHeight <= 0) return;
        var start = _window?.NominalStartUtc;
        var reset = _window?.ResetAtUtc;
        Point At(double instantFraction, double remaining) => new(left + Math.Clamp(instantFraction, 0, 1) * plotWidth,
            top + (1 - Math.Clamp(remaining, 0, 1)) * plotHeight);

        var gridColor = Color.Parse(IsDark ? "#354154" : "#CCD7E4");
        var axisColor = Color.Parse(IsDark ? "#607084" : "#A5B2C3");
        var guideColor = Color.Parse(IsDark ? "#AEB8C9" : "#8995A7");
        var gridPen = new Pen(new SolidColorBrush(gridColor), 1);
        var guidePen = new Pen(new SolidColorBrush(guideColor), 1.5);
        var dayPen = new Pen(new SolidColorBrush(axisColor), 1);
        foreach (var level in new[] { 0.0, 0.5, 1.0 })
            context.DrawLine(gridPen, At(0, level), At(1, level));
        if (start is { } nominalStart && reset is { } resetAt && resetAt > nominalStart)
        {
            var duration = (resetAt - nominalStart).TotalSeconds;
            for (var day = 0; day < 7; day++)
            {
                var tick = nominalStart.AddDays(day);
                var fraction = (tick - nominalStart).TotalSeconds / duration;
                if (fraction > 1) continue;
                var x = At(fraction, 0).X;
                context.DrawLine(dayPen, new Point(x, top), new Point(x, top + plotHeight));
                var label = LocalizedText.ChartDay(tick.ToLocalTime().LocalDateTime);
                DrawText(context, label, x - 10, top + plotHeight + 8, IsDark ? "#94A1B7" : "#64758A", DayLabelFontSize);
            }
            context.DrawLine(guidePen, At(0, 1), At(1, 0));

            var durationTicks = (resetAt - nominalStart).Ticks;
            // Mark the first sample and both ends of unobserved gaps; other samples are line vertices.
            var traceColor = Color.Parse(IsDark ? "#61A9F5" : "#2268BB");
            var actualPen = new Pen(new SolidColorBrush(traceColor), TraceThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var gapPen = new Pen(new SolidColorBrush(traceColor, 0.6), TraceThickness, dashStyle: DashStyle.Dash, lineCap: PenLineCap.Round);
            var markers = new HashSet<UsageObservation>();
            if (_series.Samples.Length > 0) markers.Add(_series.Samples[0]);
            foreach (var segment in _series.Segments)
            {
                var fromX = (segment.From.ObservedAtUtc - nominalStart).Ticks / (double)durationTicks;
                var toX = (segment.To.ObservedAtUtc - nominalStart).Ticks / (double)durationTicks;
                context.DrawLine(segment.IsUnobservedGap ? gapPen : actualPen,
                    At(fromX, segment.From.RemainingFraction), At(toX, segment.To.RemainingFraction));
                if (segment.IsUnobservedGap)
                {
                    markers.Add(segment.From);
                    markers.Add(segment.To);
                }
            }
            var markerFill = new SolidColorBrush(Color.Parse(IsDark ? "#171D29" : "#F6F8FC"));
            foreach (var sample in markers)
            {
                var fraction = (sample.ObservedAtUtc - nominalStart).Ticks / (double)durationTicks;
                context.DrawEllipse(markerFill, actualPen, At(fraction, sample.RemainingFraction), MarkerRadius, MarkerRadius);
            }

        }

        if (_pointer is { } pointer && pointer.X >= left && pointer.X <= left + plotWidth)
        {
            var x = Math.Clamp(pointer.X, left, left + plotWidth);
            var hoverPen = new Pen(new SolidColorBrush(Color.Parse(IsDark ? "#8898AE" : "#71839A")), 1, dashStyle: DashStyle.Dash);
            context.DrawLine(hoverPen, new Point(x, top), new Point(x, top + plotHeight));
            if (_window is { } hoveredWindow && _series.Samples.Length > 0)
            {
                var instant = hoveredWindow.NominalStartUtc.AddTicks((long)(((x - left) / plotWidth) * (hoveredWindow.ResetAtUtc - hoveredWindow.NominalStartUtc).Ticks));
                var nearest = _series.Samples.MinBy(sample => Math.Abs((sample.ObservedAtUtc - instant).Ticks));
                if (nearest is not null)
                    ToolTip.SetTip(this, LocalizedText.ChartSampleTooltip(nearest.ObservedAtUtc.ToLocalTime().LocalDateTime, nearest.RemainingFraction,
                        DateTimeOffset.UtcNow - nearest.ObservedAtUtc));
            }
        }
        if (_series.Samples.IsEmpty)
            DrawText(context, LocalizedText.ChartEmptyState(_window is not null && !_historyAvailable), left + 4,
                top + plotHeight / 2 - 7, IsDark ? "#94A1B7" : "#596B80", EmptyStateFontSize);

        if (_window is { } projectionWindow && start is { } projectionStart && reset is { } projectionReset &&
            _projectionHeld && _projectionFreshness == SnapshotFreshness.Fresh && _projection is { } projection &&
            projection.RunsOutAtUtc > projectionStart)
        {
            var durationTicks = (projectionReset - projectionStart).Ticks;
            var endAt = projection.RunsOutAtUtc <= projectionReset ? projection.RunsOutAtUtc : projectionReset;
            var endFraction = (endAt - projectionStart).Ticks / (double)durationTicks;
            // The projection uses the same weekly burn rate from reset start through the latest real sample.
            var band = ProjectionBand(projection, projectionWindow);
            var projectionColor = PaceColors.For(IsDark, band);
            var projectionPen = new Pen(new SolidColorBrush(Color.Parse(projectionColor)), TraceThickness,
                dashStyle: DashStyle.Dot, lineCap: PenLineCap.Round);
            var startPoint = At(0, 1);
            var endPoint = At(endFraction, RemainingAt(projectionWindow, projection, endAt));
            context.DrawLine(projectionPen, startPoint, endPoint);

            var label = ProjectionLabel(projection, projectionReset);
            var text = MakeText(label, projectionColor, EmptyStateFontSize);
            var labelMaxWidth = plotWidth / 2;
            text.MaxTextWidth = labelMaxWidth;
            var labelWidth = Math.Min(text.Width, labelMaxWidth);
            var lineMidpointX = (startPoint.X + endPoint.X) / 2;
            var labelX = Math.Clamp(lineMidpointX - labelWidth / 2, left, left + plotWidth - labelWidth);
            double LineY(double x) => startPoint.Y + (endPoint.Y - startPoint.Y) *
                Math.Clamp((x - startPoint.X) / Math.Max(1, endPoint.X - startPoint.X), 0, 1);
            var aboveLine = LineY(labelX) - text.Height - 4;
            var belowLine = LineY(labelX + labelWidth) + 4;
            var labelY = aboveLine >= top
                ? Math.Min(aboveLine, top + plotHeight - text.Height)
                : Math.Clamp(belowLine, top, Math.Max(top, top + plotHeight - text.Height));
            const double labelPadding = 3;
            var plotBackground = new SolidColorBrush(Color.Parse(IsDark ? "#171D29" : "#F6F8FC"));
            context.DrawRectangle(plotBackground, null,
                new Rect(labelX - labelPadding, labelY - labelPadding,
                    labelWidth + labelPadding * 2, text.Height + labelPadding * 2));
            context.DrawText(text, new Point(labelX, labelY));
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    private static void DrawText(DrawingContext context, string value, double x, double y, string color, double size)
    {
        var text = MakeText(value, color, size);
        context.DrawText(text, new Point(x, y));
    }

    private static string ProjectionLabel(UsageProjection projection, DateTimeOffset reset)
    {
        return projection.RunsOutAtUtc >= reset
            ? LocalizedText.ChartProjectionAfterReset(projection.RunsOutAtUtc - reset)
            : LocalizedText.ChartProjectionRunOut(projection.RunsOutAtUtc, projection.RunsOutAtUtc - DateTimeOffset.Now);
    }

    internal static PaceBand ProjectionBand(UsageProjection projection, UsageWindowIdentity window)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(window);
        if (projection.RunsOutAtUtc >= window.ResetAtUtc) return PaceBand.StrongCushion;
        var shortfallFraction = (projection.RunsOutAtUtc - window.ResetAtUtc).Ticks /
                                (double)(window.ResetAtUtc - window.NominalStartUtc).Ticks;
        var band = PaceCalculator.BandFor(shortfallFraction, window.ResetAtUtc - window.NominalStartUtc);
        return band == PaceBand.OnPace ? PaceBand.SlightlyOver : band;
    }

    internal static double RemainingAt(UsageWindowIdentity window, UsageProjection projection, DateTimeOffset instant)
    {
        var runOutTicks = (projection.RunsOutAtUtc - window.NominalStartUtc).Ticks;
        if (runOutTicks <= 0) return 0;
        return Math.Clamp(1 - (instant - window.NominalStartUtc).Ticks / (double)runOutTicks, 0, 1);
    }

    private static FormattedText MakeText(string value, string color, double size)
    {
        // Use the active UI culture and platform font, matching the card's TextBlocks.
        return new FormattedText(value, LocalizedText.UiCulture,
            FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, new SolidColorBrush(Color.Parse(color)));
    }
}

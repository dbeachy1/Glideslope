using Avalonia;
using Avalonia.Controls;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Minimum and default sizes for the full and mini card layouts. The full card uses one 1:1 logical-pixel layout
/// with a 16 px text floor. The panels below adapt the chart heading and summary column to available space.
/// </summary>
internal static class CardLayoutTiers
{
    public const double SummaryColumnWidth = 330;
    public const double ColumnGap = 25;
    public const double ChartMinWidth = 360;
    public const double FrameInsetsX = 74;
    public const double FrameInsetsY = 60;
    /// <summary>Frame insets, summary column, gap, and minimum chart width combined and rounded up.</summary>
    public const double MinWidth = 790;
    public const double MinHeight = 680;
    /// <summary>Width added per attempt when translated summary content needs more space.</summary>
    public const double SummaryWidthStep = 10;

    // These dimensions preserve the mini layout's content, including status, pace, and credit notices.
    public const double MiniMinWidth = 470;
    public const double MiniMinHeight = 270;
    public const double MiniDefaultWidth = MiniMinWidth + 60;
    public const double MiniDefaultHeight = MiniMinHeight;
}

/// <summary>
/// Lays out the weekly heading, legend, and Refresh button on one row when they fit; otherwise the legend moves
/// to a second row.
/// </summary>
internal sealed class ChartHeadingPanel : Panel
{
    internal const double ColumnGap = 12;
    internal const double RowGap = 5;
    private readonly Control _heading;
    private readonly Control _legend;
    private readonly Control _action;
    private bool? _lastStacked;

    public ChartHeadingPanel(Control heading, Control legend, Control action)
    {
        _heading = heading;
        _legend = legend;
        _action = action;
        Children.Add(heading);
        Children.Add(legend);
        Children.Add(action);
    }

    /// <summary>True while the legend sits on its own row under the heading and Refresh.</summary>
    internal bool IsStacked { get; private set; }

    /// <summary>Raised when the arrangement switches between one row and two, with the width that decided it
    /// (the card logs it).</summary>
    internal event Action<bool, double>? ArrangementChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _action.Measure(unbounded);
        _legend.Measure(unbounded);
        _heading.Measure(unbounded);
        var action = _action.DesiredSize;
        var legend = _legend.DesiredSize;
        var natural = _heading.DesiredSize;
        var oneRowWidth = natural.Width + ColumnGap + legend.Width + ColumnGap + action.Width;
        IsStacked = double.IsFinite(width) && oneRowWidth > width;
        if (_lastStacked != IsStacked)
        {
            _lastStacked = IsStacked;
            ArrangementChanged?.Invoke(IsStacked, width);
        }

        if (!IsStacked)
        {
            // One row: the heading keeps at least its natural one-line width, so it never wraps here.
            var headingWidth = double.IsFinite(width)
                ? Math.Max(0, width - legend.Width - action.Width - 2 * ColumnGap)
                : natural.Width;
            _heading.Measure(new Size(headingWidth, double.PositiveInfinity));
            var rowHeight = Math.Max(_heading.DesiredSize.Height, Math.Max(legend.Height, action.Height));
            return new Size(double.IsFinite(width) ? width : oneRowWidth, rowHeight);
        }

        _heading.Measure(new Size(Math.Max(0, width - ColumnGap - action.Width), double.PositiveInfinity));
        _legend.Measure(new Size(width, double.PositiveInfinity));
        var firstRow = Math.Max(_heading.DesiredSize.Height, action.Height);
        return new Size(width, firstRow + RowGap + _legend.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        var action = _action.DesiredSize;
        var legend = _legend.DesiredSize;
        if (!IsStacked)
        {
            var rowHeight = finalSize.Height;
            var actionX = width - action.Width;
            var legendX = actionX - ColumnGap - legend.Width;
            _heading.Arrange(new Rect(0, 0, Math.Max(0, legendX - ColumnGap), rowHeight));
            _legend.Arrange(new Rect(legendX, (rowHeight - legend.Height) / 2, legend.Width, legend.Height));
            _action.Arrange(new Rect(actionX, (rowHeight - action.Height) / 2, action.Width, action.Height));
            return finalSize;
        }

        var firstRow = Math.Max(_heading.DesiredSize.Height, action.Height);
        _heading.Arrange(new Rect(0, 0, Math.Max(0, width - ColumnGap - action.Width), firstRow));
        _action.Arrange(new Rect(width - action.Width, (firstRow - action.Height) / 2, action.Width, action.Height));
        var legendWidth = Math.Min(legend.Width, width);
        _legend.Arrange(new Rect(width - legendWidth, firstRow + RowGap, legendWidth, legend.Height));
        return finalSize;
    }
}

/// <summary>
/// Lays out the summary and chart side by side. The summary column widens in fixed steps when translated text
/// does not fit vertically, while preserving the chart's minimum width.
/// </summary>
internal sealed class CardBodyPanel : Panel
{
    private readonly Control _summary;
    private readonly Control _chartGroup;

    public CardBodyPanel(Control summary, Control chartGroup)
    {
        _summary = summary;
        _chartGroup = chartGroup;
        Children.Add(summary);
        Children.Add(chartGroup);
    }

    /// <summary>The summary column's current width.</summary>
    internal double SummaryWidth { get; private set; } = CardLayoutTiers.SummaryColumnWidth;

    /// <summary>Raised when the summary column's width changes: previous width, new width and the body height
    /// that decided it (the card logs it).</summary>
    internal event Action<double, double, double>? SummaryWidthChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : CardLayoutTiers.SummaryColumnWidth + CardLayoutTiers.ColumnGap + CardLayoutTiers.ChartMinWidth;
        var height = availableSize.Height;
        var widest = Math.Max(CardLayoutTiers.SummaryColumnWidth, width - CardLayoutTiers.ColumnGap - CardLayoutTiers.ChartMinWidth);
        var chosen = CardLayoutTiers.SummaryColumnWidth;
        _summary.Measure(new Size(chosen, double.PositiveInfinity));
        if (double.IsFinite(height) && _summary.DesiredSize.Height > height + 0.5 && widest > chosen)
        {
            chosen = widest;
            for (var candidate = CardLayoutTiers.SummaryColumnWidth + CardLayoutTiers.SummaryWidthStep; candidate < widest;
                 candidate += CardLayoutTiers.SummaryWidthStep)
            {
                _summary.Measure(new Size(candidate, double.PositiveInfinity));
                if (_summary.DesiredSize.Height <= height + 0.5)
                {
                    chosen = candidate;
                    break;
                }
            }
        }

        if (chosen != SummaryWidth)
        {
            var previous = SummaryWidth;
            SummaryWidth = chosen;
            SummaryWidthChanged?.Invoke(previous, chosen, height);
        }

        _summary.Measure(new Size(chosen, height));
        _chartGroup.Measure(new Size(Math.Max(0, width - CardLayoutTiers.ColumnGap - chosen), height));
        return new Size(width, Math.Max(_summary.DesiredSize.Height, _chartGroup.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _summary.Arrange(new Rect(0, 0, SummaryWidth, finalSize.Height));
        var chartLeft = SummaryWidth + CardLayoutTiers.ColumnGap;
        _chartGroup.Arrange(new Rect(chartLeft, 0, Math.Max(0, finalSize.Width - chartLeft), finalSize.Height));
        return finalSize;
    }
}

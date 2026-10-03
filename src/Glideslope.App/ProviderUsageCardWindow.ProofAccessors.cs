using Avalonia;
using Avalonia.Controls;
using Glideslope.Core;

namespace Glideslope.App;

// Internal accessors that exist only for the headless proofs (CardPresentationProof, CardAccessibilityProof
// and CardPeekSpecs in Glideslope.Shell.Specs): the card's controls, their text and bounds, the mini body's
// controls, the Undock button and the resize grips. Production code does not read them.
internal sealed partial class ProviderUsageCardWindow
{
    internal Control OuterFrameControl => _outer;
    internal Control InnerContentControl => _root;
    internal TextBlock StatusLineControl => _statusLine;
    internal Control ChartGroupControl => _chartGroup;
    internal Control SummaryControl => _summary;
    internal Button RefreshControl => _refresh;
    // Title-row controls used by presentation proofs.
    internal Control TitleNameControl => _titleName;
    internal Control TitleDotControl => _titleDot;
    internal Control TitleVersionControl => _titleVersion;
    internal Button PreviousWeekControl => _previousWeek;
    internal Button NextWeekControl => _nextWeek;
    internal Button CurrentWeekControl => _currentWeek;
    internal TextBlock PlanTypeControl => _planType;
    internal TextBlock FableHeadingControl => _fableHeading;
    // Layout, Fable, and card-size controls used by presentation proofs.
    internal bool IsChartHeadingStacked => _chartHeadingRow.IsStacked;
    internal double SummaryColumnWidth => _body.SummaryWidth;
    internal string FableDeltaSummaryText => _fableDeltaSummary.Text ?? string.Empty;
    internal QuotaMeter FableDeltaBarControl => _fableDeltaBar;
    internal Button CardSizeControl => _cardSize;
    internal Flyout CardSizeFlyout => _cardSizeFlyout;
    internal Slider CardSizeSlider => _scaleSlider;
    internal string CardSizeValueText => _scaleValue.Text ?? string.Empty;
    internal int CardScalePercent => _scalePercent;
    internal Button ModeToggleControl => _modeToggle;
    internal Button MiniRefreshControl => _miniRefresh;
    internal bool IsMiniBodyVisible => _miniBody.Control.IsVisible;
    internal bool IsFullBodyVisible => _fullBody.IsVisible;
    internal MiniCardContent MiniBody => _miniBody;
    internal bool IsShortWindowSectionVisible => _shortPanel.IsVisible;
    // These accessors expose the full body's CreditStrip; its reserved height follows the strip's minimum and can
    // grow for longer text.
    internal double CreditStripReservedHeight => _creditStrip.ReservedHeight;
    internal Control CreditStripControl => _creditStrip.Control;
    internal bool NoticeVisible => _creditStrip.NoticeVisible;
    internal Control NoticeContentControl => _creditStrip.NoticeContentControl;
    internal string WeeklySummaryText => _weeklySummary.Text ?? string.Empty;
    internal string WeeklyResetText => _weeklyReset.Text ?? string.Empty;
    internal string NoticeText => _creditStrip.NoticeText;
    internal bool CreditBoxVisible => _creditStrip.CreditBoxVisible;
    internal Control CreditContentControl => _creditStrip.CreditContentControl;
    internal bool IsCreditInventoryVisible => _creditStrip.IsCreditInventoryVisible;
    internal bool IsCreditStripVisuallyPainted => _creditStrip.IsVisuallyPainted;
    internal string CreditCountText => _creditStrip.CreditCountText;
    internal string CreditCountToolTip => ToolTip.GetTip(_creditStrip.CreditCountControl) as string ?? string.Empty;
    internal IReadOnlyCollection<Control> ResizeGripControls => _resizeGrips;
    internal IReadOnlyDictionary<Control, WindowEdge> ResizeGripEdges => _resizeGripEdges;
    internal Button ResetSizeControl => _resetSize;
    internal Button SettingsControl => _settings;
    internal WeeklyHistoryChart ChartControl => _chart;
    internal TextBlock SummaryDragTarget => _weeklyRemaining;
    internal Control ShortWindowSectionControl => _shortPanel;
    internal Control WeeklyPaceTitleControl => _weeklyDeltaTitle;
    internal Control WeeklyDeltaBarControl => _weeklyDeltaBar;
    internal Control FeaturedWeeklySectionControl => _fableRow;
    internal Control FooterControl => _footer;
    internal Rect BoundsWithinCard(Control control)
    {
        var point = control.TranslatePoint(new Point(0, 0), _outer)
            ?? throw new InvalidOperationException("Card control is not connected to the card frame.");
        return new Rect(point, control.Bounds.Size);
    }
    internal MenuFlyout? CreateCreditDetailsFlyoutForCurrentInventory() => _creditStrip.CreateCreditDetailsFlyoutForCurrentInventory();

    internal Button UndockControl => _undock;
    internal CardDockSide? CurrentUndockSide => _undockSide;
}

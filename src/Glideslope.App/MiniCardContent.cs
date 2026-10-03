using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.App;

/// <summary>
/// The small card body — a big weekly-remaining percentage on the left, and on the
/// right a vertical stack of the reset line, the (conditional) credit strip, the pace line in capitals, a
/// narrow centered delta bar and a full-width remaining bar. When the provider is not Ready, both columns
/// hide and one message block takes their place: the status title and details (the same text the full card's
/// status line uses), and, for a signed-out or missing CLI, the mini CreditStrip in its notice state under it.
/// The card (ProviderUsageCardWindow) hosts this as one of its two bodies and owns everything the design places
/// in the shared header (mark size, title size, the toggle, Refresh, the dot/version/week buttons); this class
/// only builds and drives the content below the header's divider.
/// </summary>
internal sealed class MiniCardContent
{
    /// <summary>Design §4.3: "a little bit smaller" than the full card's 16 px floor.</summary>
    internal const double MiniTextFloor = 14;
    /// <summary>Design §4.3: 76 × 14/16, the full card's big-percentage size scaled to the mini floor.</summary>
    internal const double MiniPercentSize = 66;

    private readonly Grid _root;
    private readonly Border _divider;
    private readonly Grid _columns;
    private readonly TextBlock _remainingPercentage;
    private readonly StackPanel _rightStack;
    private readonly TextBlock _resetLine;
    private readonly TextBlock _paceLine;
    private readonly Grid _deltaBarHost;
    private readonly QuotaMeter _deltaBar;
    private readonly QuotaMeter _remainingBar;
    private readonly StackPanel _messageStack;
    private readonly TextBlock _messageText;
    private readonly CreditStrip _creditStrip;

    public MiniCardContent()
    {
        _root = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*") };
        // Design §4.3: "a 1 px divider under the header with a 6 px top margin" — the mini body's own divider,
        // separate from the full body's (10 px top margin), since each body now owns everything below the header.
        _divider = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(_divider, 0);
        _root.Children.Add(_divider);

        _columns = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"), ColumnSpacing = 16, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(_columns, 1);
        _root.Children.Add(_columns);

        _remainingPercentage = new TextBlock
        {
            FontSize = MiniPercentSize,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _columns.Children.Add(_remainingPercentage);

        _rightStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_rightStack, 1);
        _columns.Children.Add(_rightStack);

        _resetLine = Label(bold: true, wrap: true);
        _rightStack.Children.Add(_resetLine);
        _paceLine = Label(bold: true, wrap: true);
        _rightStack.Children.Add(_paceLine);

        // Design §4.3: the delta bar sits in the middle column of a 1*,3*,1* grid (60 % of the column
        // width, centered) directly under the pace line.
        _deltaBarHost = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("1*,3*,1*") };
        _deltaBar = new QuotaMeter(delta: true) { Height = 12 };
        Grid.SetColumn(_deltaBar, 1);
        _deltaBarHost.Children.Add(_deltaBar);
        _rightStack.Children.Add(_deltaBarHost);

        _remainingBar = new QuotaMeter(delta: false) { Height = 12 };
        _rightStack.Children.Add(_remainingBar);

        // Design §4.3: with no snapshot or a non-Ready status, the message block replaces both columns.
        _messageText = new TextBlock { FontSize = MiniTextFloor, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        _messageStack = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
        _messageStack.Children.Add(_messageText);
        Grid.SetRow(_messageStack, 1);
        _root.Children.Add(_messageStack);

        _creditStrip = new CreditStrip(MiniTextFloor);
    }

    /// <summary>Set from the card's theme so text, bars, and the credit strip use matching colors.</summary>
    internal bool IsDark
    {
        get => _isDark;
        set
        {
            _isDark = value;
            _deltaBar.IsDark = value;
            _remainingBar.IsDark = value;
            _deltaBar.InvalidateVisual();
            _remainingBar.InvalidateVisual();
        }
    }

    private bool _isDark = true;

    internal Control Control => _root;
    internal Control DividerControl => _divider;
    internal Control ColumnsControl => _columns;
    internal TextBlock RemainingPercentageControl => _remainingPercentage;
    internal TextBlock ResetLineControl => _resetLine;
    internal TextBlock PaceLineControl => _paceLine;
    internal QuotaMeter DeltaBarControl => _deltaBar;
    internal QuotaMeter RemainingBarControl => _remainingBar;
    internal Control MessageControl => _messageStack;
    internal TextBlock MessageTextControl => _messageText;
    internal CreditStrip CreditStrip => _creditStrip;
    internal bool IsColumnsVisible => _columns.IsVisible;
    internal bool IsMessageVisible => _messageStack.IsVisible;

    /// <summary>Design §4.3: Ready shows the two columns (the weekly remaining percentage, the reset line, the
    /// credit strip when the provider reports credits, the pace line in capitals, the delta bar and the
    /// remaining bar); anything else shows the message block instead, with the mini credit strip in its notice
    /// state under the message for a signed-out or missing CLI.</summary>
    internal void UpdateState(ProviderDisplayState? state)
    {
        _creditStrip.Update(state);
        // Show the data columns only when a ready state includes a snapshot.
        var ready = state is { Status: ProviderStatus.Ready, Snapshot: not null };
        _columns.IsVisible = ready;
        _messageStack.IsVisible = !ready;
        if (ready)
        {
            PlaceCreditStrip(inRightColumn: _creditStrip.IsCreditInventoryVisible, inMessage: false);
            UpdateColumns(state!);
        }
        else
        {
            PlaceCreditStrip(inRightColumn: false, inMessage: _creditStrip.NoticeVisible);
            SetMessage(state);
        }
        _creditStrip.ApplyTheme(IsDark);
    }

    private void UpdateColumns(ProviderDisplayState state)
    {
        var weekly = state.Snapshot?.Buckets.FirstOrDefault(bucket => bucket.Role == QuotaBucketRole.Weekly);
        if (weekly is null)
        {
            _remainingPercentage.Text = "—";
            _remainingPercentage.Foreground = PaceBrush(null);
            _resetLine.Text = LocalizedText.CardResetUnavailable;
            SetPaceLine(LocalizedText.CardWindowUnavailable, PaceBrush(null));
            _deltaBar.SetValue(null, null, null);
            _remainingBar.SetValue(null, null, null);
            SetBarTooltips(null, null);
            return;
        }

        _remainingPercentage.Text = LocalizedText.Percentage(weekly.RemainingFraction);
        var pace = PaceCalculator.Calculate(weekly, DateTimeOffset.UtcNow, state.Freshness ?? SnapshotFreshness.RestoredHistorical);
        // Design §4.3 "colored by pace band exactly like _weeklyRemaining": the percentage is always band-colored
        // (even while the window has not started), the pace line only while it has, matching the full card.
        _remainingPercentage.Foreground = PaceBrush(pace.Band);
        _resetLine.Text = !weekly.WindowStarted && weekly.Duration is { } duration
            ? LocalizedText.CardResetAfterFirstUse(duration)
            : FormatReset(weekly);
        var paceText = weekly.WindowStarted ? LocalizedText.PaceSummary(pace, weekly.Duration) : LocalizedText.CardWindowNotStarted;
        SetPaceLine(paceText, weekly.WindowStarted ? PaceBrush(pace.Band) : PaceBrush(null));
        _deltaBar.SetValue(weekly.RemainingFraction, pace.Band, weekly.WindowStarted ? pace.DeltaFraction : null);
        _remainingBar.SetValue(weekly.RemainingFraction, pace.Band, null);
        SetBarTooltips(weekly.RemainingFraction, paceText);
    }

    private void SetPaceLine(string naturalText, IBrush color)
    {
        // Design §4.3: the pace line reads in capitals, upper-cased with the UI culture at run time (not
        // stored twice); the delta bar's tooltip below uses the same text without the capitalization.
        _paceLine.Text = naturalText.ToUpper(LocalizedText.UiCulture);
        _paceLine.Foreground = color;
    }

    /// <summary>Design §4.3: the remaining bar's tooltip is "{0} remaining"; the delta bar's is the pace line's
    /// own (non-capitalized) text; each bar's automation name matches its tooltip; a dash state (no weekly
    /// bucket) has no tooltip on either bar.</summary>
    private void SetBarTooltips(double? remainingFraction, string? naturalPaceText)
    {
        if (remainingFraction is not { } fraction)
        {
            ToolTip.SetTip(_remainingBar, null);
            ToolTip.SetTip(_deltaBar, null);
            AutomationProperties.SetName(_remainingBar, string.Empty);
            AutomationProperties.SetName(_deltaBar, string.Empty);
            return;
        }

        var remainingTooltip = LocalizedText.CardRemainingTooltip(fraction);
        ToolTip.SetTip(_remainingBar, remainingTooltip);
        AutomationProperties.SetName(_remainingBar, remainingTooltip);
        ToolTip.SetTip(_deltaBar, naturalPaceText);
        AutomationProperties.SetName(_deltaBar, naturalPaceText ?? string.Empty);
    }

    /// <summary>Design §4.3: the same status-line wording the full card's status line uses (Card_StatusLine's
    /// title and details), bold title then normal details, wrapping. Mini never shows the "Past week" line: it
    /// has no week navigation at all.</summary>
    private void SetMessage(ProviderDisplayState? state)
    {
        var parts = LocalizedText.StatusLine(state);
        var inlines = new InlineCollection();
        foreach (var part in parts)
            inlines.Add(new Run(part.Text) { FontWeight = part.IsTitle ? FontWeight.SemiBold : FontWeight.Normal });
        _messageText.Inlines = inlines;
    }

    /// <summary>Moves the one shared CreditStrip control to whichever host wants it this update (the right
    /// column's stack, the message block, or neither), removing it from its previous parent first so it is
    /// never a child of two panels. Neither flag true means the strip takes no space at all, matching design
    /// §4.3's "otherwise hidden, taking no space".</summary>
    private void PlaceCreditStrip(bool inRightColumn, bool inMessage)
    {
        var control = _creditStrip.Control;
        var currentParent = control.Parent as Panel;
        if (inRightColumn)
        {
            if (ReferenceEquals(currentParent, _rightStack)) return;
            currentParent?.Children.Remove(control);
            // Between the reset line and the pace line, as design §4.3 lists it.
            _rightStack.Children.Insert(1, control);
            return;
        }
        if (inMessage)
        {
            if (ReferenceEquals(currentParent, _messageStack)) return;
            currentParent?.Children.Remove(control);
            _messageStack.Children.Add(control);
            return;
        }
        currentParent?.Children.Remove(control);
    }

    private static string FormatReset(QuotaBucket bucket) => bucket.ResetAtUtc is { } reset
        ? LocalizedText.CardResetAt(reset, DateTimeOffset.UtcNow)
        : LocalizedText.CardResetUnavailable;

    private IBrush PaceBrush(PaceBand? band) => band is { } value
        ? new SolidColorBrush(Color.Parse(PaceColors.For(IsDark, value)))
        : new SolidColorBrush(Color.Parse(IsDark ? "#AEB8C9" : "#596B80"));

    private static TextBlock Label(bool bold = false, bool wrap = false) => new()
    {
        FontSize = MiniTextFloor,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };
}

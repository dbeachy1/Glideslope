using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.App;

/// <summary>Renders one provider's scheduler state. At the default scale, its single layout uses 1:1 logical
/// pixels and a minimum size that preserves the 16 px text floor; user scaling applies to the whole card.</summary>
internal sealed partial class ProviderUsageCardWindow : Window, ICardLayoutWindow, ICardModeWindow, IPeekableCardWindow
{
    public const double DefaultCardWidth = 940;
    public const double DefaultCardHeight = 680;   // The default layout needs 680 px to preserve the 16 px text floor.
    internal const double CardTextFloor = 16;
    private const double CornerHitSize = 20;
    private const double EdgeHitSize = 8;
    /// <summary>Z-order floor for every resize grip; see AddResizeGrip.</summary>
    internal const int GripZIndex = 10;
    /// <summary>Assembly version shown in the title row, matching the project version automatically.</summary>
    private static readonly string AppVersion = typeof(ProviderUsageCardWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private readonly string _providerId;
    private readonly TextBlock _titleName;
    private readonly TextBlock _titleDot;
    private readonly TextBlock _titleVersion;
    // The offset is 0 for the current week and increases for older stored weeks; the count is the number
    // available in the current bucket. A count below 2 leaves nothing to browse.
    private readonly Button _previousWeek;
    private readonly Button _nextWeek;
    private readonly Button _currentWeek;
    private int _windowOffset;
    private int _windowCount;
    private UsageWindowIdentity? _viewedHistoryWindow;
    private readonly TextBlock _chartHeading;
    private readonly TextBlock _planType;       // window design §15.5: "Plan type: …" on its own line
    private readonly TextBlock _fableHeading;   // window design §15.6: carries the Fable reset when it differs
    private readonly TextBlock _weeklyRemaining;
    private readonly TextBlock _weeklyReset;
    private readonly TextBlock _weeklySummary;
    private readonly TextBlock _weeklyBarValue;
    private readonly TextBlock _shortSummary;
    private readonly TextBlock _shortReset;
    private readonly TextBlock _shortRemainingValue;
    // CreditStrip encapsulates its border, notice, credit box, count button, flyout, and theme.
    private readonly CreditStrip _creditStrip;
    private readonly Grid _shortPanel;
    private readonly Grid _fableRow;
    private readonly TextBlock _fableRemainingValue;
    private readonly QuotaMeter _fableRemainingBar;
    private readonly QuotaMeter _fableDeltaBar;
    private readonly TextBlock _fableDeltaSummary;
    private readonly QuotaMeter _weeklyRemainingBar;
    private readonly QuotaMeter _weeklyDeltaBar;
    private readonly QuotaMeter _shortRemainingBar;
    private readonly QuotaMeter _shortDeltaBar;
    private readonly WeeklyHistoryChart _chart;
    private readonly Button _refresh;
    private readonly Button _resetSize;
    private readonly Button _settings;
    private readonly Grid _moveDragSurface;
    private readonly Border _outer;
    private readonly Border _inner;
    private readonly Grid _root;
    // The full and mini bodies share the header and occupy the same host, with only one visible at a time.
    private readonly Grid _fullBody;
    private readonly MiniCardContent _miniBody;
    private CardMode _mode = CardMode.Full;
    private readonly Button _modeToggle;
    private readonly Button _miniRefresh;
    private readonly StackPanel _weekButtonsPanel;
    internal const string ModeToggleFullGlyph = "▭";
    internal const string ModeToggleMiniGlyph = "▣";
    internal const double MiniMarkSize = 28;
    internal const double MiniTitleNameSize = 25;
    internal const double TitleVersionOpticalOffsetY = 4;
    private static readonly Thickness FullInnerPadding = new(30, 23, 30, 23);
    private static readonly Thickness MiniInnerPadding = new(16, 12, 16, 12);
    // CardBodyPanel preserves a readable summary width and expands it for longer translated text.
    private readonly CardBodyPanel _body;
    private readonly StackPanel _summary;
    private readonly Grid _chartGroup;
    // The heading, legend, and Refresh share one row when they fit and wrap to two rows otherwise.
    private readonly ChartHeadingPanel _chartHeadingRow;
    private readonly WrapPanel _legend;
    private readonly TextBlock _chartHint;
    // ApplyStatusLine rebuilds the Runs from the localized Card_StatusLine resource so punctuation and order
    // follow the translation rather than being fixed in code.
    private readonly TextBlock _statusLine;
    // Title-row size button, flyout slider, and layout-aware scale host.
    internal const string CardSizeGlyph = "Aa";
    private const double CardSizeSliderLength = 200;
    private readonly Button _cardSize;
    private readonly Flyout _cardSizeFlyout;
    private readonly Slider _scaleSlider;
    private readonly TextBlock _scaleValue;
    private readonly LayoutTransformControl _scaleHost;
    private int _scalePercent = CardScale.DefaultPercent;
    private int _lastRequestedScalePercent = CardScale.DefaultPercent;
    private bool _applyingScale;
    private bool _scaleChangedWhileOpen;
    private bool? _fableStartedShown;
    private readonly IDiagnosticSink _diagnostics;
    private const double UndockHoverDepth = 40;      // how far from a docked edge the hover zone reaches
    private const double UndockHoverSpan = 0.5;      // middle 50% of the edge's length
    private const double UndockButtonInset = 12;     // distance from the frame edge to the button
    private readonly Button _undock;
    private CardDockSide? _undockSide;
    private IReadOnlySet<CardDockSide> _dockedSides = new HashSet<CardDockSide>();
    private static readonly Cursor DragMoveCursor = new(StandardCursorType.DragMove);
    private readonly Border _magnetTop;
    private readonly Border _magnetRight;
    private readonly Border _magnetBottom;
    private readonly Border _magnetLeft;
    private CardDockSide? _magnetSide;
    private readonly GlideslopeMark _mark;
    private readonly Grid _titleStack;
    private readonly TextBlock _weeklyDeltaTitle;
    private readonly TextBlock _footer;
    private readonly HashSet<Control> _resizeGrips = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Control, WindowEdge> _resizeGripEdges = new(ReferenceEqualityComparer.Instance);
    private readonly Action<string> _retryRequested;
    private readonly DispatcherTimer _redrawTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private UsageWindowIdentity? _currentHistoryWindow;
    private ProviderDisplayState? _state;
    private bool _programmaticClose;
    private bool _allowUserClose;

    public event EventHandler? UserCloseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ResetSizeRequested;
    public event EventHandler? MoveDragStarted;
    public event Action<ProviderUsageCardWindow, WindowEdge>? ResizeDragStarted;
    public event EventHandler? LayoutGestureCancelled;
    public event EventHandler? DetachDragStarted;
    public event Action<ProviderUsageCardWindow, CardDockSide>? UndockRequested;
    /// <summary>Window design §15.2: the week buttons ask the coordinator for another stored window. The step
    /// is the change in <see cref="HistoryWindowOffset"/>: +1 is one week older (previous week), −1 one week
    /// newer (next week), and 0 means back to the current week (Now; also raised on a weekly rollover). The
    /// coordinator computes the new offset as clamp(current + step, 0, count − 1) (window design §15.3).</summary>
    public event Action<ProviderUsageCardWindow, int>? HistoryWindowStepRequested;
    /// <summary>Raised on each title-row size slider step (70–150 percent, in steps of 10). The coordinator
    /// applies it to every card and resizes the windows; the card never resizes itself.</summary>
    public event Action<ProviderUsageCardWindow, int>? CardScaleRequested;
    /// <summary>Raised when the size flyout closes, so the coordinator saves the
    /// setting once instead of on every slider step.</summary>
    public event EventHandler? CardScaleChangeEnded;
    /// <summary>Raised when the title-row toggle is clicked. The coordinator owns
    /// the mode switch (the whole group, docking's target-wins rule, the window's size and minimum); the card
    /// only asks and later receives <see cref="ApplyMode"/>.</summary>
    public event EventHandler? ModeToggleRequested;
    /// <summary>2.1 Ctrl peek design §4.1: from _moveDragSurface's PointerEntered and PointerExited.</summary>
    public event Action<ProviderUsageCardWindow, bool>? PeekHoverChanged;
    /// <summary>Reports pointer and Ctrl-key input used to update peek state.</summary>
    public event Action<ProviderUsageCardWindow, KeyModifiers, bool>? PeekInputObserved;
    /// <summary>Raised when the header's minimize button is clicked.</summary>
    public event EventHandler? MinimizeRequested;
    // Track each Ctrl key so releasing one does not clear the other.
    private readonly HashSet<Key> _ctrlKeysDown = [];

    public ProviderUsageCardWindow(string providerId, bool showMark, Action<string> retryRequested, IDiagnosticSink? diagnostics = null)
    {
        _providerId = providerId;
        _retryRequested = retryRequested ?? throw new ArgumentNullException(nameof(retryRequested));
        // Diagnostics are optional for cards constructed outside the application.
        _diagnostics = diagnostics ?? new NullDiagnosticSink();
        Title = LocalizedText.CardTitle(providerId);
        Width = DefaultCardWidth;
        Height = DefaultCardHeight;
        MinWidth = CardLayoutTiers.MinWidth;
        MinHeight = LocalizedText.CardMinimumHeight;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CornerRadius = new CornerRadius(18);

        var outer = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(5), ClipToBounds = true };
        var inner = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = FullInnerPadding };
        outer.Child = inner;
        // Keep the header above the shared full/mini body host.
        var root = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*") };
        inner.Child = root;
        _outer = outer;
        _inner = inner;
        _root = root;

        Icon = AppIcon.Get(_diagnostics);
        var header = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto") };
        _mark = new GlideslopeMark(providerId) { Width = 35, Height = 35, IsVisible = showMark };
        header.Children.Add(_mark);
        // Window design §15.4: the provider name, dot, version and week buttons share one vertical center.
        // Columns Auto,Auto,*,Auto: name, dot, version, then the week buttons (§15.2).
        var title = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*,Auto"),
            RowDefinitions = RowDefinitions.Parse("Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(showMark ? 12 : 0, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        // Keep the title Grid so SetMarkVisible can adjust its margin when the mark is hidden.
        _titleStack = title;
        var titleText = new TextBlock { Text = LocalizedText.ProviderName(providerId), FontSize = 29, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _titleName = titleText;
        title.Children.Add(titleText);
        // The title row contains the provider name, app name and version, and history buttons.
        var titleDot = new TextBlock { Text = "•", FontSize = CardTextFloor, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        _titleDot = titleDot;
        Grid.SetColumn(titleDot, 1);
        title.Children.Add(titleDot);
        _titleVersion = new TextBlock
        {
            Text = LocalizedText.CardAppNameVersion(AppVersion),
            FontSize = CardTextFloor,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform(0, TitleVersionOpticalOffsetY),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_titleVersion, 2);
        title.Children.Add(_titleVersion);
        // Window design §15.2: ‹ › and Now, a little apart (spacing 6, left margin 14), built by the same
        // factory as ↺ and ⚙ with 16 px text. Buttons are excluded from body-drag like every other Button.
        var weekButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(14, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _weekButtonsPanel = weekButtons;
        _previousWeek = WeekButton("‹", 26, LocalizedText.CardPreviousWeekTooltip);
        _previousWeek.Click += (_, _) => RequestHistoryStep(+1);
        weekButtons.Children.Add(_previousWeek);
        _nextWeek = WeekButton("›", 26, LocalizedText.CardNextWeekTooltip);
        _nextWeek.Click += (_, _) => RequestHistoryStep(-1);
        weekButtons.Children.Add(_nextWeek);
        _currentWeek = WeekButton(LocalizedText.CardCurrentWeek, 44, LocalizedText.CardCurrentWeekTooltip);
        // "Now" is text, not a glyph: at least 44 wide (the factory's fixed width is lifted so a longer
        // translation can grow), at the factory's 26 px height.
        _currentWeek.Width = double.NaN;
        _currentWeek.MaxWidth = double.PositiveInfinity;
        _currentWeek.Click += (_, _) => RequestHistoryStep(0);
        weekButtons.Children.Add(_currentWeek);
        Grid.SetColumn(weekButtons, 3);
        title.Children.Add(weekButtons);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        // Refresh is shown here only in mini mode; the full card's Refresh sits in the chart heading.
        // It sits left of the toggle so the header controls keep their positions between modes; enabled by the same rule as
        // the full card's Refresh (see UpdateState).
        _miniRefresh = new Button
        {
            Content = LocalizedText.CardRefresh,
            // Use the mini card's text size.
            FontSize = MiniCardContent.MiniTextFloor,
            Height = 26,
            MinWidth = 72,
            Padding = new Thickness(10, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        AutomationProperties.SetName(_miniRefresh, LocalizedText.CardRefresh);
        _miniRefresh.Click += (_, _) => _retryRequested(_providerId);
        actions.Children.Add(_miniRefresh);
        // The toggle shares the action-button style and starts in its Full-mode appearance.
        _modeToggle = CardHeaderButtons.ActionButton(ModeToggleFullGlyph, 29, CardTextFloor);
        ToolTip.SetTip(_modeToggle, LocalizedText.CardMiniModeTooltip);
        AutomationProperties.SetName(_modeToggle, LocalizedText.CardMiniModeTooltip);
        _modeToggle.Click += (_, _) => ModeToggleRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(_modeToggle);
        _resetSize = CardHeaderButtons.CreateResetSizeButton();
        _resetSize.Click += (_, _) => ResetSizeRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(_resetSize);
        // The size flyout stays outside the scaled card content, so it remains readable at every card size.
        _scaleValue = new TextBlock
        {
            FontSize = CardTextFloor,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        _scaleSlider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = CardScale.MinimumPercent,
            Maximum = CardScale.MaximumPercent,
            TickFrequency = CardScale.StepPercent,
            IsSnapToTickEnabled = true,
            TickPlacement = TickPlacement.Outside,
            SmallChange = CardScale.StepPercent,
            LargeChange = CardScale.StepPercent,
            Height = CardSizeSliderLength,
            HorizontalAlignment = HorizontalAlignment.Center,
            Value = CardScale.DefaultPercent
        };
        AutomationProperties.SetName(_scaleSlider, LocalizedText.CardSize);
        _scaleSlider.ValueChanged += OnScaleSliderValueChanged;
        var cardSizePanel = new StackPanel { Spacing = 8, Margin = new Thickness(4) };
        cardSizePanel.Children.Add(new TextBlock
        {
            Text = LocalizedText.CardSize,
            FontSize = CardTextFloor,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 180
        });
        cardSizePanel.Children.Add(_scaleValue);
        cardSizePanel.Children.Add(_scaleSlider);
        // Esc closes the flyout. The popup closes on Esc by itself too; handling it here makes that deterministic.
        cardSizePanel.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _cardSizeFlyout?.Hide();
            e.Handled = true;
        };
        _cardSizeFlyout = new Flyout { Content = cardSizePanel, Placement = PlacementMode.BottomEdgeAlignedRight };
        _cardSizeFlyout.Opened += OnCardSizeFlyoutOpened;
        _cardSizeFlyout.Closed += OnCardSizeFlyoutClosed;
        _cardSize = CardHeaderButtons.ActionButton(CardSizeGlyph, 29, CardTextFloor);
        ToolTip.SetTip(_cardSize, LocalizedText.CardSize);
        AutomationProperties.SetName(_cardSize, LocalizedText.CardSize);
        _cardSize.Flyout = _cardSizeFlyout;
        actions.Children.Add(_cardSize);
        SetScaleSliderQuietly(CardScale.DefaultPercent);
        _settings = CardHeaderButtons.CreateSettingsButton();
        _settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(_settings);
        var minimize = CardHeaderButtons.CreateMinimizeButton();
        minimize.Click += (_, _) => MinimizeRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(minimize);
        var close = CardHeaderButtons.CreateCloseButton(providerId);
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        Grid.SetRow(header, 0);
        root.Children.Add(header);
        // Keep the chart row flexible so it fills the remaining height.
        _fullBody = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto,Auto,Auto,Auto") };
        Grid.SetRow(_fullBody, 1);
        root.Children.Add(_fullBody);
        var divider = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(divider, 0);
        _fullBody.Children.Add(divider);

        _summary = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Top };
        _weeklyReset = Label(LocalizedText.CardResetUnavailable, CardTextFloor, bold: true, wrap: true);
        _summary.Children.Add(_weeklyReset);
        // Match the credit strip's text to the card's minimum text size.
        _creditStrip = new CreditStrip(CardTextFloor);
        _summary.Children.Add(_creditStrip.Control);
        _weeklyRemaining = Label("—", 76, bold: true);
        _weeklyRemaining.Margin = new Thickness(-4, -1, 0, -16);
        _summary.Children.Add(_weeklyRemaining);
        _summary.Children.Add(Label(LocalizedText.CardWeeklyRemaining, 19, bold: true, wrap: true));
        _shortPanel = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 1,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var shortDeltaTitle = Label(LocalizedText.CardFiveHourDelta, CardTextFloor, bold: true, wrap: true);
        Grid.SetRow(shortDeltaTitle, 0);
        _shortPanel.Children.Add(shortDeltaTitle);
        var shortRemainingHeader = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 5 };
        shortRemainingHeader.Children.Add(Label(LocalizedText.CardFiveHourRemaining, CardTextFloor, bold: true, wrap: true));
        _shortRemainingValue = Label("—", CardTextFloor, bold: true);
        Grid.SetColumn(_shortRemainingValue, 1);
        shortRemainingHeader.Children.Add(_shortRemainingValue);
        Grid.SetRow(shortRemainingHeader, 0);
        Grid.SetColumn(shortRemainingHeader, 1);
        _shortPanel.Children.Add(shortRemainingHeader);
        _shortDeltaBar = new QuotaMeter(delta: true) { Height = 14 };
        Grid.SetRow(_shortDeltaBar, 1);
        _shortPanel.Children.Add(_shortDeltaBar);
        _shortRemainingBar = new QuotaMeter(delta: false) { Height = 12 };
        Grid.SetRow(_shortRemainingBar, 1);
        Grid.SetColumn(_shortRemainingBar, 1);
        _shortPanel.Children.Add(_shortRemainingBar);
        _shortSummary = Label(LocalizedText.CardPaceUnavailable, CardTextFloor, bold: true, wrap: true);
        _shortSummary.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(_shortSummary, 2);
        Grid.SetColumnSpan(_shortSummary, 2);
        _shortPanel.Children.Add(_shortSummary);
        _shortReset = Label(LocalizedText.CardResetUnavailable, CardTextFloor, wrap: true);
        Grid.SetRow(_shortReset, 3);
        Grid.SetColumnSpan(_shortReset, 2);
        _shortPanel.Children.Add(_shortReset);
        _summary.Children.Add(_shortPanel);

        _chartGroup = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto"), RowSpacing = 5 };
        _chartHeading = Label(LocalizedText.CardWeeklyGlideSlope, CardTextFloor, bold: true, wrap: true);
        // Show plan type below the heading when the provider supplies one.
        _planType = Label(string.Empty, CardTextFloor, wrap: true);
        _planType.IsVisible = false;
        var chartHeadingCell = new StackPanel { Spacing = 2 };
        chartHeadingCell.Children.Add(_chartHeading);
        chartHeadingCell.Children.Add(_planType);
        // Let translated legend items wrap within the chart column.
        _legend = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 13, LineSpacing = 2 };
        _legend.Children.Add(LegendItem(Brush("#61A9F5"), LocalizedText.CardObservedLegend));
        _legend.Children.Add(LegendItem(Brush("#AEB8C9"), LocalizedText.CardEvenPaceLegend));
        _refresh = new Button
        {
            Content = LocalizedText.CardRefresh, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 96, FontSize = CardTextFloor,
            HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 5)
        };
        AutomationProperties.SetName(_refresh, LocalizedText.CardRefresh);
        _refresh.Click += (_, _) => _retryRequested(_providerId);
        // Keep the heading on one row when possible and stack it when the available width is too small.
        _chartHeadingRow = new ChartHeadingPanel(chartHeadingCell, _legend, _refresh);
        _chartHeadingRow.ArrangementChanged += (stacked, width) => _diagnostics.Record(new DiagnosticEvent(
            "card_heading_layout", _providerId, $"{(stacked ? "two_rows" : "one_row")},width={width:0}"));
        _chartGroup.Children.Add(_chartHeadingRow);
        _statusLine = new TextBlock
        {
            FontSize = CardTextFloor,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(_statusLine, 1);
        _chartGroup.Children.Add(_statusLine);
        _chart = new WeeklyHistoryChart { MinHeight = 100, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_chart, 2);
        _chartGroup.Children.Add(_chart);
        _chartHint = Label(LocalizedText.CardChartWindowHint, CardTextFloor, wrap: true);
        Grid.SetRow(_chartHint, 3);
        _chartGroup.Children.Add(_chartHint);
        _body = new CardBodyPanel(_summary, _chartGroup) { Margin = new Thickness(0, 4, 0, 4) };
        _body.SummaryWidthChanged += (from, to, bodyHeight) => _diagnostics.Record(new DiagnosticEvent(
            "card_summary_width", _providerId, $"from={from:0},to={to:0},bodyHeight={bodyHeight:0}"));
        Grid.SetRow(_body, 1);
        _fullBody.Children.Add(_body);

        _weeklyDeltaTitle = Label(LocalizedText.CardWeeklyDeltaTitle, CardTextFloor, bold: true, wrap: true);
        Grid.SetRow(_weeklyDeltaTitle, 2);
        _fullBody.Children.Add(_weeklyDeltaTitle);
        var deltaPanel = new StackPanel { Spacing = 3 };
        _weeklyDeltaBar = new QuotaMeter(delta: true) { Height = 20 };
        deltaPanel.Children.Add(_weeklyDeltaBar);
        _weeklySummary = Label(LocalizedText.CardPaceUnavailable, CardTextFloor, bold: true, wrap: true);
        _weeklySummary.HorizontalAlignment = HorizontalAlignment.Center;
        deltaPanel.Children.Add(_weeklySummary);
        Grid.SetRow(deltaPanel, 3);
        _fullBody.Children.Add(deltaPanel);

        var remaining = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 0) };
        var remainingHeading = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        remainingHeading.Children.Add(Label(LocalizedText.CardWeeklyRemainingSection, CardTextFloor, bold: true, wrap: true));
        _weeklyBarValue = Label("—", CardTextFloor, bold: true);
        Grid.SetColumn(_weeklyBarValue, 1);
        remainingHeading.Children.Add(_weeklyBarValue);
        remaining.Children.Add(remainingHeading);
        _weeklyRemainingBar = new QuotaMeter(delta: false) { Height = 18 };
        remaining.Children.Add(_weeklyRemainingBar);
        _fableRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("2*,*"), ColumnSpacing = 18, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
        var fableRemaining = new StackPanel { Spacing = 3 };
        var fableHeading = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        _fableHeading = Label(LocalizedText.CardFableThisWeek, CardTextFloor, bold: true, wrap: true);
        fableHeading.Children.Add(_fableHeading);
        _fableRemainingValue = Label("—", CardTextFloor, bold: true);
        Grid.SetColumn(_fableRemainingValue, 1);
        fableHeading.Children.Add(_fableRemainingValue);
        fableRemaining.Children.Add(fableHeading);
        _fableRemainingBar = new QuotaMeter(delta: false) { Height = 14 };
        fableRemaining.Children.Add(_fableRemainingBar);
        _fableRow.Children.Add(fableRemaining);
        var fableDelta = new StackPanel { Spacing = 3 };
        fableDelta.Children.Add(Label(LocalizedText.CardFableDeltaTitle, CardTextFloor, bold: true, wrap: true));
        _fableDeltaBar = new QuotaMeter(delta: true) { Height = 14 };
        fableDelta.Children.Add(_fableDeltaBar);
        _fableDeltaSummary = Label(LocalizedText.CardPaceUnavailable, CardTextFloor, bold: true, wrap: true);
        _fableDeltaSummary.HorizontalAlignment = HorizontalAlignment.Center;
        fableDelta.Children.Add(_fableDeltaSummary);
        Grid.SetColumn(fableDelta, 1);
        _fableRow.Children.Add(fableDelta);
        remaining.Children.Add(_fableRow);
        Grid.SetRow(remaining, 4);
        _fullBody.Children.Add(remaining);

        _footer = Label(LocalizedText.CardFooterNoSynthetic, CardTextFloor, topMargin: 4, wrap: true);
        Grid.SetRow(_footer, 5);
        _fullBody.Children.Add(_footer);

        // Keep the mini body beside the full body; both receive every state update.
        _miniBody = new MiniCardContent();
        _miniBody.Control.IsVisible = false;
        Grid.SetRow(_miniBody.Control, 1);
        root.Children.Add(_miniBody.Control);
        // The transform scales the card frame while resize grips, magnet bars, and Undock remain at fixed sizes.
        _scaleHost = new LayoutTransformControl { LayoutTransform = new ScaleTransform(1, 1), Child = _outer };
        _moveDragSurface = new Grid();
        _moveDragSurface.Children.Add(_scaleHost);
        AddResizeGrip(_moveDragSurface, WindowEdge.West);
        AddResizeGrip(_moveDragSurface, WindowEdge.East);
        AddResizeGrip(_moveDragSurface, WindowEdge.North);
        AddResizeGrip(_moveDragSurface, WindowEdge.South);
        // Add corners last so the full-length edge targets cannot intercept corner drags.
        AddResizeGrip(_moveDragSurface, WindowEdge.NorthWest);
        AddResizeGrip(_moveDragSurface, WindowEdge.NorthEast);
        AddResizeGrip(_moveDragSurface, WindowEdge.SouthWest);
        AddResizeGrip(_moveDragSurface, WindowEdge.SouthEast);
        // Magnet highlight (window design §6): a 4 px bar along the middle 25% of whichever side faces
        // a snap candidate. Not hit-testable, and
        // above every other card element (GripZIndex + 1) so it is never hidden by the card content
        // or the resize grips.
        _magnetTop = CreateMagnetBar(HorizontalAlignment.Center, VerticalAlignment.Top);
        _magnetBottom = CreateMagnetBar(HorizontalAlignment.Center, VerticalAlignment.Bottom);
        _magnetLeft = CreateMagnetBar(HorizontalAlignment.Left, VerticalAlignment.Center);
        _magnetRight = CreateMagnetBar(HorizontalAlignment.Right, VerticalAlignment.Center);
        _moveDragSurface.Children.Add(_magnetTop);
        _moveDragSurface.Children.Add(_magnetBottom);
        _moveDragSurface.Children.Add(_magnetLeft);
        _moveDragSurface.Children.Add(_magnetRight);
        // Undock button (window design §5.6): hidden until the pointer nears the middle of a docked edge,
        // and above the grips and magnet bars so it is always clickable when shown.
        _undock = new Button
        {
            Content = LocalizedText.CardUndock,
            FontSize = CardTextFloor,
            MinWidth = 96,
            IsVisible = false,
            ZIndex = GripZIndex + 2,
        };
        AutomationProperties.SetName(_undock, LocalizedText.CardUndock);
        _undock.Click += (_, _) => { if (_undockSide is { } side) UndockRequested?.Invoke(this, side); };
        _moveDragSurface.Children.Add(_undock);
        _moveDragSurface.PointerExited += (_, _) => HideUndock();
        // 2.1 Ctrl peek design §4.1: PeekHoverChanged from the same PointerEntered/PointerExited pair the
        // Undock button already uses.
        _moveDragSurface.PointerEntered += (_, _) => PeekHoverChanged?.Invoke(this, true);
        _moveDragSurface.PointerExited += (_, _) => PeekHoverChanged?.Invoke(this, false);
        _moveDragSurface.SizeChanged += (_, _) => UpdateMagnetBarLengths();
        _moveDragSurface.PointerPressed += OnCardPointerPressed;
        _moveDragSurface.PointerMoved += OnCardPointerMoved;
        // End a peek before any control handles a press anywhere in the card.
        AddHandler(PointerPressedEvent, OnWindowTunnelPointerPressed, RoutingStrategies.Tunnel);
        Content = _moveDragSurface;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                LayoutGestureCancelled?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            // Key events provide modifier state for the next pointer event; track left and right Ctrl separately.
            if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            {
                _ctrlKeysDown.Add(e.Key);
                PeekInputObserved?.Invoke(this, e.KeyModifiers | KeyModifiers.Control, false);
            }
        };
        KeyUp += (_, e) =>
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            {
                _ctrlKeysDown.Remove(e.Key);
                var modifiers = _ctrlKeysDown.Count > 0 ? e.KeyModifiers | KeyModifiers.Control : e.KeyModifiers & ~KeyModifiers.Control;
                PeekInputObserved?.Invoke(this, modifiers, false);
            }
        };
        // Clear key state when focus leaves because this window may not receive the corresponding KeyUp.
        Deactivated += (_, _) => _ctrlKeysDown.Clear();
        Closing += OnClosing;
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        Opened += (_, _) => ApplyTheme();
        _redrawTimer.Tick += (_, _) => { if (_state is not null) UpdateState(_state); };
        _redrawTimer.Start();
        ApplyTheme();
        UpdateState(null);
        RefreshWeekButtons();
    }

    // The card mode and its shared-header toggle.
    internal CardMode Mode => _mode;
    // CardPeekController sets this while the full card is shown during a peek; Undock stays hidden until it ends.
    // Public because this implicitly implements IPeekableCardWindow.IsPeeking.
    public bool IsPeeking { get; set; }

    /// <summary>Swaps the bodies and mode-specific header items, updates padding and theme, and leaves window
    /// sizing to the coordinator. Entering mini while browsing history requests step 0 so returning to full
    /// mode starts at the current week. A call with the current mode does nothing.</summary>
    // Public because this implicitly implements ICardModeWindow.ApplyMode.
    public void ApplyMode(CardMode mode)
    {
        if (mode == _mode) return;
        var from = _mode;
        _mode = mode;
        var mini = mode == CardMode.Mini;
        _fullBody.IsVisible = !mini;
        _miniBody.Control.IsVisible = mini;
        _inner.Padding = mini ? MiniInnerPadding : FullInnerPadding;
        _mark.Width = mini ? MiniMarkSize : 35;
        _mark.Height = mini ? MiniMarkSize : 35;
        _titleName.FontSize = mini ? MiniTitleNameSize : 29;
        _titleDot.IsVisible = !mini;
        _titleVersion.IsVisible = !mini;
        _weekButtonsPanel.IsVisible = !mini;
        _miniRefresh.IsVisible = mini;
        _modeToggle.Content = mini ? ModeToggleMiniGlyph : ModeToggleFullGlyph;
        var toggleTooltip = mini ? LocalizedText.CardFullModeTooltip : LocalizedText.CardMiniModeTooltip;
        ToolTip.SetTip(_modeToggle, toggleTooltip);
        AutomationProperties.SetName(_modeToggle, toggleTooltip);
        ApplyTheme();
        // Reset history browsing on entry so returning to full mode starts at the current week.
        if (mini && _viewedHistoryWindow is not null)
            HistoryWindowStepRequested?.Invoke(this, 0);
        _diagnostics.Record(new DiagnosticEvent("card_mode_applied", _providerId, $"mode={mode},from={from}"));
    }

    public string ProviderId => _providerId;
    object ICardLayoutWindow.NativeHandle => this;
    internal void SetMarkVisible(bool visible)
    {
        _mark.IsVisible = visible;
        _titleStack.Margin = new Thickness(visible ? 12 : 0, 0, 0, 0);
    }

    public void CloseProgrammatically() { _redrawTimer.Stop(); _programmaticClose = true; Close(); }
    public void CompleteUserClose() { _redrawTimer.Stop(); _allowUserClose = true; Close(); }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_programmaticClose || _allowUserClose) return;
        // Let operating-system shutdown close the window without treating it as a user-requested card close.
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            _redrawTimer.Stop();
            _diagnostics.Record(new DiagnosticEvent("card_closed_for_shutdown", _providerId, $"reason={e.CloseReason}"));
            return;
        }
        e.Cancel = true;
        UserCloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTheme()
    {
        var dark = ActualThemeVariant != ThemeVariant.Light;
        var surface = Brush(dark ? "#171D29" : "#F6F8FC");
        Background = Brushes.Transparent;
        _outer.Background = surface;
        _outer.BorderBrush = Brush(dark ? "#69758A" : "#8A9BB0");
        _inner.Background = surface;
        _inner.BorderBrush = Brush(dark ? "#354154" : "#D0DBE7");
        foreach (var border in _root.GetVisualDescendants().OfType<Border>())
        {
            border.Background ??= Brush(dark ? "#1D2635" : "#FFFFFF");
            border.BorderBrush ??= Brush(dark ? "#354154" : "#D0DBE7");
        }
        var titleColor = _providerId switch
        {
            ProviderIds.Claude => "#D97757",
            ProviderIds.Gemini => "#8AB4F8",
            _ => dark ? "#F5F7FB" : "#172438",
        };
        foreach (var visual in this.GetVisualDescendants())
        {
            if (visual is TextBlock block)
            {
                if (ReferenceEquals(block, _titleDot)) block.Foreground = Brush(dark ? "#FFFFFF" : "#596B80");
                else if (ReferenceEquals(block, _titleVersion)) block.Foreground = Brush(dark ? "#94A1B7" : "#596B80");
                // Match the title control directly because its font size changes in mini mode.
                else if (ReferenceEquals(block, _titleName)) block.Foreground = Brush(titleColor);
                else if (ReferenceEquals(block, _weeklyRemaining)) block.Foreground = Brush(dark ? "#E9D66B" : "#806B00");
                else if (block.Foreground is null || block.Foreground == Brushes.Black) block.Foreground = Brush(dark ? "#F5F7FB" : "#172438");
            }
        }
        // Tint the mode toggle with the provider color while keeping its glyph legible in both themes.
        _modeToggle.Background = Brush(_providerId switch
        {
            ProviderIds.Claude => dark ? "#F6D5C7" : "#F2C2AD",
            ProviderIds.Gemini => dark ? "#D3E3FC" : "#BDD4F8",
            _ => dark ? "#E4E8EF" : "#D5DBE4",
        });
        _modeToggle.Foreground = Brush("#1D2635");
        _modeToggle.CornerRadius = new CornerRadius(6);
        _chart.IsDark = dark;
        foreach (var meter in new[] { _weeklyRemainingBar, _weeklyDeltaBar, _shortRemainingBar, _shortDeltaBar, _fableRemainingBar, _fableDeltaBar }) meter.IsDark = dark;
        // Recompute pace colors after changing the theme.
        _miniBody.IsDark = dark;
        _chart.InvalidateVisual();
        if (_state is not null) UpdateState(_state);
    }

    private static TextBlock Label(string text, double size, bool bold = false, bool wrap = false, double topMargin = 0) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Margin = new Thickness(0, topMargin, 0, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Control LegendItem(IBrush color, string text)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        item.Children.Add(new Border { Width = 14, Height = 3, Background = color, VerticalAlignment = VerticalAlignment.Center });
        item.Children.Add(Label(text, CardTextFloor));
        return item;
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
    private IBrush PaceBrush(PaceBand? band) => band is { } value
        ? Brush(PaceColors.For(ActualThemeVariant != ThemeVariant.Light, value))
        : Brush(ActualThemeVariant != ThemeVariant.Light ? "#AEB8C9" : "#596B80");
}

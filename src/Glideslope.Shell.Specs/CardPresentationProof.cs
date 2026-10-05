using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.Shell.Specs;

/// <summary>Exercises the production card controls with synthetic provider snapshots in Avalonia's headless platform.</summary>
internal static partial class CardPresentationProof
{
    public static void Run()
    {
        AppBuilder.Configure<PresentationTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        // The proof uses the app's theme and platform-default font (Segoe UI on Windows).
        var defaultFont = FontManager.Current.DefaultFontFamily.Name;
        Console.WriteLine($"presentation proof: FluentTheme loaded, default font '{defaultFont}'");
        Assert(!OperatingSystem.IsWindows() || defaultFont == "Segoe UI",
            $"on Windows the proofs lay text out in Segoe UI, the app's default font (got '{defaultFont}')");
        // Pin English wording and formats so the assertions are independent of the machine's culture.
        using var english = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-US"));
        AssertWidestEnglishResetLineFits();

        var now = DateTimeOffset.UtcNow;
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: false, _ => { });
        try
        {
            window.UpdateState(State(Snapshot(now, [])));
            Assert(!window.IsShortWindowSectionVisible, "weekly-only snapshot hides the complete five-hour section");
            Assert(window.CreditStripReservedHeight == CreditStripHeight && !window.IsCreditInventoryVisible,
                "missing reset credits leave the reserved strip blank without collapsing it");
            Assert(!window.IsCreditStripVisuallyPainted,
                "missing reset credits leave the reserved strip visually blank");

            // Exercise Claude's longest realistic content at every supported size and text scale. The
            // separate Codex proof covers credit inventory, which Claude does not provide.
            var completeClaude = Snapshot(now,
            [
                Bucket("claude.short", QuotaBucketRole.Short, now),
                Bucket("claude.fable.weekly", QuotaBucketRole.FeaturedWeekly, now)
            ]);
            window.UpdateState(State(completeClaude));
            Assert(window.IsShortWindowSectionVisible, "an actual short bucket shows the five-hour section");
            Assert(!window.IsCreditInventoryVisible,
                "the realistic Claude worst case has no credits (design doc §4.4); the Codex pass below proves the credit strip fits");
            window.Show();
            AssertLayoutAtEverySize(window, ProviderIds.Claude);
            window.UpdateState(State(completeClaude) with { Status = ProviderStatus.SchemaChanged });
            AssertLayoutAtEverySize(window, $"{ProviderIds.Claude} schema-changed");
            // Window design Appendix H.8: a Ready card whose history scope is unavailable shows
            // Card_HistoryScopeUnavailable, the longest real status text, so it gets its own pass too.
            window.UpdateState(State(completeClaude) with { SafeErrorCode = "claude_history_scope_unavailable" });
            AssertLayoutAtEverySize(window, $"{ProviderIds.Claude} history-scope-unavailable");
            // Check the longest status at the minimum and maximum card scales.
            AssertLayoutAtEveryScale(window, $"{ProviderIds.Claude} history-scope-unavailable");

            Assert(window.CanBeginMoveDragFrom(window.SummaryDragTarget), "ordinary summary content routes a body drag to the card mover");
            Assert(!window.CanBeginMoveDragFrom(window.RefreshControl), "refresh button remains interactive instead of moving the card");
            Assert(!window.CanBeginMoveDragFrom(window.ChartControl), "chart press and hover region remains interactive instead of moving the card");
            Assert(window.ResizeGripControls.Count == 8 && window.ResizeGripControls.All(grip => !window.CanBeginMoveDragFrom(grip)),
                "all edge and corner resize grips keep their native resize route");

            // Each grip's cursor must match the edge used by its resize hit test; none uses the default arrow.
            Assert(window.ResizeGripEdges.Count == 8, "every resize grip is mapped to the edge its hit test starts a resize for");
            foreach (var (grip, edge) in window.ResizeGripEdges)
            {
                var expected = ProviderUsageCardWindow.CursorForEdge(edge);
                Assert(ReferenceEquals(grip.Cursor, expected),
                    $"the {edge} resize grip shows the same standard cursor its own hit test maps to");
            }
            // Diagonal corners: NW pairs with SE (one shape) and NE pairs with SW (the other shape),
            // and neither corner shape is reused for a straight edge or the other diagonal.
            var topLeft = ProviderUsageCardWindow.CursorForEdge(WindowEdge.NorthWest);
            var bottomRight = ProviderUsageCardWindow.CursorForEdge(WindowEdge.SouthEast);
            var topRight = ProviderUsageCardWindow.CursorForEdge(WindowEdge.NorthEast);
            var bottomLeft = ProviderUsageCardWindow.CursorForEdge(WindowEdge.SouthWest);
            var north = ProviderUsageCardWindow.CursorForEdge(WindowEdge.North);
            var south = ProviderUsageCardWindow.CursorForEdge(WindowEdge.South);
            var west = ProviderUsageCardWindow.CursorForEdge(WindowEdge.West);
            var east = ProviderUsageCardWindow.CursorForEdge(WindowEdge.East);
            Assert(!ReferenceEquals(topLeft, topRight) && !ReferenceEquals(topLeft, north) && !ReferenceEquals(topLeft, west),
                "the NW corner has its own diagonal cursor, distinct from the other diagonal and the straight edges");
            Assert(ReferenceEquals(north, south) && ReferenceEquals(west, east) && !ReferenceEquals(north, west),
                "top/bottom share one vertical cursor, left/right share one horizontal cursor, and the two differ");
            Assert(!ReferenceEquals(bottomRight, bottomLeft) && !ReferenceEquals(topLeft, bottomLeft) && !ReferenceEquals(topRight, bottomRight),
                "each of the four corners keeps a cursor distinct from the two adjacent corners on the other diagonal");
            Assert(window.GetVisualDescendants().OfType<Button>().All(button => !window.CanBeginMoveDragFrom(button)),
                "gear, close, refresh, and credit buttons remain interactive");

            // The magnet highlight marks the docking side without intercepting pointer input.
            var rightBar = window.MagnetBarFor(CardDockSide.Right);
            var leftBar = window.MagnetBarFor(CardDockSide.Left);
            window.ShowMagnet(CardDockSide.Right);
            Assert(window.CurrentMagnetSide == CardDockSide.Right, "showing the magnet on a side records it as the current side");
            Assert(rightBar.IsVisible && !leftBar.IsVisible, "only the requested side's bar becomes visible");
            Assert(!rightBar.IsHitTestVisible, "the magnet bar is excluded from hit testing so pointer input still reaches the card");
            Assert(Math.Abs(rightBar.Height - window.Height * 0.25) < 5,
                $"the magnet bar covers the middle 25% of its side's length (bar height {rightBar.Height:0.0}, card height {window.Height:0.0})");
            window.ShowMagnet(CardDockSide.Left);
            Assert(!rightBar.IsVisible && leftBar.IsVisible, "showing a different side hides the previous one");
            window.ClearMagnet();
            Assert(window.CurrentMagnetSide is null && !leftBar.IsVisible, "clearing the magnet hides every side's bar");

            AssertUndockButton(window);
            AssertTitleRowCentered(window);
            AssertRepresentativeTitleRowScales();

            var oneCredit = Credits(1, now);
            window.UpdateState(State(Snapshot(now, [], oneCredit)));
            Assert(window.IsCreditInventoryVisible && window.CreditCountText == "1" && window.IsCreditStripVisuallyPainted,
                "positive normalized credit inventory is rendered in a painted green strip");
            Assert(window.CreateCreditDetailsFlyoutForCurrentInventory() is null, "one credit has no empty details popover");
            Assert(!window.CreditCountToolTip.Contains("Click", StringComparison.Ordinal),
                "one credit tooltip does not advertise the unavailable flyout");

            var severalCredits = Credits(3, now);
            window.UpdateState(State(Snapshot(now, [], severalCredits)));
            var flyout = window.CreateCreditDetailsFlyoutForCurrentInventory();
            Assert(window.CreditCountText == "3" && flyout is not null && flyout.Items.Count > 0,
                "multiple real credits expose known detail rows and any undisclosed aggregate");
            Assert(window.CreditCountToolTip.Contains("Click to see", StringComparison.Ordinal),
                "multiple credits advertise the available details flyout");
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"credit_flyout_host_{theme}_render_failed")) { }
                var themedFlyout = window.CreateCreditDetailsFlyoutForCurrentInventory()
                    ?? throw new InvalidOperationException($"credit_flyout_{theme}_missing");
                var rows = themedFlyout.Items.OfType<MenuItem>().ToArray();
                Assert(rows.Length > 0 && rows.All(row => !row.IsEnabled && row.Opacity == 1 &&
                       row.Background is SolidColorBrush { Color: var rowBackground } && rowBackground == Color.Parse("#1D2635") &&
                       row.Foreground is SolidColorBrush { Color: var rowForeground } && rowForeground == Colors.White &&
                       row.Header is TextBlock { Foreground: SolidColorBrush { Color: var headerForeground } } && headerForeground == Colors.White),
                    $"credit detail rows in {theme} remain disabled with fully opaque white text on a dark background");
                Assert(themedFlyout.FlyoutPresenterTheme is ControlTheme { BasedOn: not null, TargetType: var targetType } presenterTheme &&
                       targetType == typeof(MenuFlyoutPresenter) &&
                       presenterTheme.Setters.OfType<Setter>().Any(setter => setter.Property == TemplatedControl.BackgroundProperty &&
                           setter.Value is SolidColorBrush { Color: var presenterBackground } && presenterBackground == Color.Parse("#1D2635")),
                    $"credit detail presenter uses its dark background theme in {theme}");
                // A real menu presenter exercises disabled-row templates even when headless popups cannot attach.
                var menuPresenter = new MenuFlyoutPresenter
                {
                    ItemsSource = themedFlyout.Items,
                    Theme = themedFlyout.FlyoutPresenterTheme
                };
                var popupHost = new Window { Width = 500, Height = 120, RequestedThemeVariant = theme, Content = menuPresenter };
                try
                {
                    popupHost.Show();
                    using (popupHost.CaptureRenderedFrame() ?? throw new InvalidOperationException($"credit_presenter_{theme}_render_failed")) { }
                    Assert(menuPresenter.Background is SolidColorBrush { Color: var renderedBackground } &&
                           renderedBackground == Color.Parse("#1D2635") &&
                           menuPresenter.GetVisualDescendants().OfType<MenuItem>().Count() == rows.Length,
                        $"credit detail presenter in {theme} renders every row on the dark background");
                    Assert(rows.All(row => row.Header is TextBlock header && header.Bounds.Width > 0 && header.Bounds.Height > 0 &&
                           header.Foreground is SolidColorBrush { Color: var renderedForeground } && renderedForeground == Colors.White &&
                           header.GetVisualAncestors().Prepend(header).All(visual => visual.Opacity == 1)),
                        $"credit detail text in {theme} stays opaque white through the disabled menu template");
                }
                finally { popupHost.Close(); }
            }
            window.RequestedThemeVariant = ThemeVariant.Default;

            using (LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("ru"), CultureInfo.GetCultureInfo("ru"),
                       new LocalizationResourceProof.RussianCreditTooltipResources()))
            {
                var twentyOneCredits = Credits(21, now);
                window.UpdateState(State(Snapshot(now, [], twentyOneCredits)));
                Assert(window.CreditCountToolTip.StartsWith("click one", StringComparison.Ordinal) &&
                       window.CreateCreditDetailsFlyoutForCurrentInventory() is not null,
                    "Russian 21 selects the One noun category while the real credit strip still exposes its click flyout");
                window.UpdateState(State(Snapshot(now, [], oneCredit)));
                Assert(window.CreditCountToolTip.StartsWith("no click one", StringComparison.Ordinal) &&
                       window.CreateCreditDetailsFlyoutForCurrentInventory() is null,
                    "Russian one uses the no-click sentence and has no flyout");
            }

            var knownZero = Credits(0, now);
            window.UpdateState(State(Snapshot(now, [], knownZero)));
            Assert(window.CreditStripReservedHeight == CreditStripHeight && !window.IsCreditInventoryVisible && !window.IsCreditStripVisuallyPainted,
                "known zero credits leave the reserved strip blank and unpainted");

            // The reset-size button matches the gear's style and sits immediately to its left,
            // with its own tooltip and accessible name.
            Assert(window.ResetSizeControl.Bounds.Width == window.SettingsControl.Bounds.Width &&
                   window.ResetSizeControl.Bounds.Height == window.SettingsControl.Bounds.Height,
                "the reset-size button matches the gear's size");
            var resetSizeBounds = window.BoundsWithinCard(window.ResetSizeControl);
            var settingsBounds = window.BoundsWithinCard(window.SettingsControl);
            Assert(resetSizeBounds.Right <= settingsBounds.Left + 0.5,
                "the reset-size button sits immediately left of the settings gear");
            Assert(Avalonia.Controls.ToolTip.GetTip(window.ResetSizeControl) as string == LocalizedText.CardResetSizeTooltip &&
                   Avalonia.Automation.AutomationProperties.GetName(window.ResetSizeControl) == LocalizedText.CardResetSizeTooltip,
                "the reset-size button has an English tooltip and a matching accessible name");
        }
        finally
        {
            window.CloseProgrammatically();
        }
        AssertCodexAndGeminiAtEverySize();
        AssertGripsAreTopmostForEveryProviderType();
        AssertWeekNavigation();
        AssertBrowsingWithoutCurrentWindow();
        AssertSignInNotice();
        AssertPlanTypeLine();
        AssertFableReset();
        AssertFableNotStarted();
        AssertEveryCardStateFits();
        AssertCardSizeControl();
        AssertPseudoLocaleFits();
        // 2.0 mini mode design §8.2: the mini body's own presentation proofs.
        AssertMiniCardMatrix();
        AssertMiniBarTooltips();
        AssertMiniHeaderOrder();
        AssertApplyModeNeverResizes();
        AssertApplyModeSameModeDoesNothing();
        AssertModeEntersMiniRequestsStepZeroWhilePastWeekShown();
        AssertMiniRoundTrip();
        AssertMiniPseudoLocaleFits();
        AssertCardScaleMiniCases();
        AssertShutdownCloseIsNotUserClose();   // 2.2.2
        SettingsBehaviorProof.Run();
    }

    private static AccountSnapshot PlanSnapshot(DateTimeOffset now, string? plan) =>
        new(ProviderIds.Claude, "synthetic-shell-spec-scope", plan, now, now, "synthetic.shell.spec",
            [Bucket("weekly", QuotaBucketRole.Weekly, now)]);

    private static AccountSnapshot FableSnapshot(DateTimeOffset now, DateTimeOffset weeklyReset, DateTimeOffset fableReset) =>
        new(ProviderIds.Claude, "synthetic-shell-spec-scope", "Synthetic", now, now, "synthetic.shell.spec",
        [
            new QuotaBucket("weekly", QuotaBucketRole.Weekly, 0.63, TimeSpan.FromDays(7), weeklyReset, "synthetic.shell.spec"),
            new QuotaBucket("claude.short", QuotaBucketRole.Short, 0.63, TimeSpan.FromHours(5), now.AddHours(3), "synthetic.shell.spec"),
            new QuotaBucket("claude.fable.weekly", QuotaBucketRole.FeaturedWeekly, 0.63, TimeSpan.FromDays(7), fableReset, "synthetic.shell.spec"),
        ]);

    /// <summary>The credit strip's reserved height, sufficient for
    /// 16pt text (including a wrapped expiration line) without clipping.</summary>
    private const double CreditStripHeight = 70;

    private static ProviderDisplayState State(AccountSnapshot snapshot) => new(
        snapshot.ProviderId, 1, true, false, ProviderStatus.Ready, snapshot, SnapshotFreshness.Fresh,
        null, [], null, TimeSpan.Zero, snapshot.ReceivedAtUtc);

    private static AccountSnapshot Snapshot(DateTimeOffset now, IEnumerable<QuotaBucket> extra,
        ResetCreditInventory? credits = null, string providerId = ProviderIds.Claude)
    {
        var weekly = Bucket("weekly", QuotaBucketRole.Weekly, now);
        return new AccountSnapshot(providerId, "synthetic-shell-spec-scope", "Synthetic", now, now,
            "synthetic.shell.spec", [weekly, .. extra], credits);
    }

    private static QuotaBucket Bucket(string id, QuotaBucketRole role, DateTimeOffset now) =>
        new(id, role, 0.63,
            role == QuotaBucketRole.Short ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7),
            role == QuotaBucketRole.Short ? now.AddHours(3) : now.AddDays(7), "synthetic.shell.spec");

    private static ResetCreditInventory Credits(int count, DateTimeOffset now)
    {
        var candidates = Enumerable.Range(0, count)
            .Select(index => new ResetCreditCandidate($"synthetic-{index}", "weekly_reset", now.AddDays(index + 1)));
        return ResetCreditNormalizer.Normalize(count, candidates, "weekly_reset", now).Inventory
            ?? throw new InvalidOperationException("synthetic_credit_normalization_failed");
    }

    /// <summary>Design §4.5: a window whose elapsed fraction is under 1 % keeps the hours-under/over-budget
    /// phrase but suppresses the pace percent, giving the longest realistic pace line ("N.N hours over
    /// budget · pace percent unavailable: window just started"). Measures and proves that the mini
    /// minimum and default hold this line without overlap or clipping.</summary>
    private static QuotaBucket JustStartedWeekly(DateTimeOffset now) =>
        new("weekly", QuotaBucketRole.Weekly, 0.95, TimeSpan.FromDays(7), now.AddDays(7).AddHours(-1), "synthetic.shell.spec");

    private static void AssertNear(Rect actual, Rect expected, string name, WindowEdge edge) =>
        Assert(Math.Abs(actual.X - expected.X) < 0.5 && Math.Abs(actual.Y - expected.Y) < 0.5 &&
               Math.Abs(actual.Width - expected.Width) < 0.5 && Math.Abs(actual.Height - expected.Height) < 0.5,
            $"{name}: {edge} grip at {actual} should be {expected}");

    private static string Describe(Control control) => control switch
    {
        TextBlock block => $"TextBlock '{(block.Text ?? block.Inlines?.Text ?? string.Empty)}'",
        Button button => $"Button '{button.Content}'",
        _ => control.GetType().Name
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    /// <summary>
    /// no two visible blocks on the card overlap. A block is every visible, non-empty
    /// TextBlock, Button, QuotaMeter, chart and Border inside the card content that is not part of a Button's own
    /// template; a control and its own ancestor never count as overlapping. A control with negative margins (the big
    /// percentage: -4 left, -1 top, -16 bottom, to align its glyphs) is first shrunk by them, since that overlap is
    /// deliberate. Intersections up to 0.5 px are layout rounding.
    /// </summary>
    private static void AssertNoOverlaps(ProviderUsageCardWindow window, string name)
    {
        var root = window.InnerContentControl;
        var blocks = root.GetVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Where(control => control is TextBlock or Button or QuotaMeter or WeeklyHistoryChart or Border)
            .Where(control => control is not TextBlock block || !string.IsNullOrEmpty(block.Text ?? block.Inlines?.Text))
            .Where(control => !control.GetVisualAncestors().TakeWhile(ancestor => !ReferenceEquals(ancestor, root)).OfType<Button>().Any())
            .Select(control => (Control: control, Rect: EffectiveBounds(window, control), Ancestors: control.GetVisualAncestors().ToHashSet()))
            .ToList();
        var overlaps = new List<string>();
        for (var first = 0; first < blocks.Count; first++)
        {
            for (var second = first + 1; second < blocks.Count; second++)
            {
                var (a, rectA, ancestorsA) = blocks[first];
                var (b, rectB, ancestorsB) = blocks[second];
                if (ancestorsA.Contains(b) || ancestorsB.Contains(a)) continue;
                var width = Math.Min(rectA.Right, rectB.Right) - Math.Max(rectA.Left, rectB.Left);
                var height = Math.Min(rectA.Bottom, rectB.Bottom) - Math.Max(rectA.Top, rectB.Top);
                if (width > 0.5 && height > 0.5)
                    overlaps.Add($"{Describe(a)} {rectA} overlaps {Describe(b)} {rectB} by {width:0.0}x{height:0.0}");
            }
        }
        Assert(overlaps.Count == 0, $"{name}: no two visible card blocks overlap ({overlaps.Count}: {string.Join("; ", overlaps.Take(6))}; " +
            $"summary column {window.SummaryColumnWidth:0} needing {window.SummaryControl.DesiredSize.Height:0}, chart group {window.BoundsWithinCard(window.ChartGroupControl)} needing {window.ChartGroupControl.DesiredSize.Height:0})");
    }

    private static Rect EffectiveBounds(ProviderUsageCardWindow window, Control control)
    {
        var bounds = window.BoundsWithinCard(control);
        var margin = control.Margin;
        var left = Math.Max(0, -margin.Left);
        var top = Math.Max(0, -margin.Top);
        var right = Math.Max(0, -margin.Right);
        var bottom = Math.Max(0, -margin.Bottom);
        return new Rect(bounds.X + left, bounds.Y + top,
            Math.Max(0, bounds.Width - left - right), Math.Max(0, bounds.Height - top - bottom));
    }

    /// <summary>The proofs load the app's FluentTheme as
    /// GlideslopeApplication.Initialize does, so Buttons, CheckBoxes, ComboBoxes, NumericUpDowns and Sliders have
    /// their templates and real sizes so presentation measurements match the app.</summary>
    private sealed class PresentationTestApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            RequestedThemeVariant = ThemeVariant.Default;
        }
    }
}

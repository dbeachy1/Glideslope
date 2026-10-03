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

// The mini card proofs: the mini size, scale and pseudo-locale matrices, the bar tooltips, the header order,
// ApplyMode's rules (never resizes, same mode does nothing, entering mini while browsing asks for step 0), the
// round trip back to full, and CardScale's mini cases. Governed by 2.0 mini mode design §4 and §8.
internal static partial class CardPresentationProof
{
    /// <summary>Design §8.2: the two measured mini sizes (minimum, default) for every card state the full
    /// matrix covers, plus the two extra worst cases §4.5 names (Codex credits and the longest realistic pace
    /// line; the longest status message and the longest error-with-notice combination).</summary>
    private static readonly (double Width, double Height)[] MiniProvenSizes =
    [
        (CardLayoutTiers.MiniMinWidth, CardLayoutTiers.MiniMinHeight),
        (CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight),
    ];

    private static void AssertMiniCardMatrix()
    {
        var now = DateTimeOffset.UtcNow;
        ProviderDisplayState Failed(string providerId, ProviderStatus status, string code, DateTimeOffset? retry = null) =>
            new(providerId, 1, true, false, status, null, SnapshotFreshness.Fresh, retry, [], code, TimeSpan.Zero, now);
        var idle = new AccountSnapshot(ProviderIds.Codex, "synthetic-shell-spec-scope", "Synthetic", now, now, "synthetic.shell.spec",
        [
            new QuotaBucket("weekly", QuotaBucketRole.Weekly, 1.0, TimeSpan.FromDays(7), now.AddDays(7), "synthetic.shell.spec", windowStarted: false),
        ]);
        var states = new (string Name, string ProviderId, ProviderDisplayState? State)[]
        {
            ("codex credits+longest pace", ProviderIds.Codex, State(new AccountSnapshot(ProviderIds.Codex, "synthetic-shell-spec-scope",
                "Synthetic", now, now, "synthetic.shell.spec", [JustStartedWeekly(now)], Credits(3, now)))),
            ("claude longest pace", ProviderIds.Claude, State(new AccountSnapshot(ProviderIds.Claude, "synthetic-shell-spec-scope",
                "Synthetic", now, now, "synthetic.shell.spec", [JustStartedWeekly(now)]))),
            ("gemini ready", ProviderIds.Gemini, State(Snapshot(now, [], providerId: ProviderIds.Gemini))),
            ("connecting", ProviderIds.Claude, null),
            ("signed out", ProviderIds.Claude, Failed(ProviderIds.Claude, ProviderStatus.NeedsSignIn, "claude_signed_out")),
            ("cli missing", ProviderIds.Codex, Failed(ProviderIds.Codex, ProviderStatus.MissingApplication, "codex_cli_missing")),
            ("rate limited", ProviderIds.Gemini, Failed(ProviderIds.Gemini, ProviderStatus.RateLimited, "gemini_rate_limited", now.AddMinutes(20))),
            ("offline", ProviderIds.Claude, Failed(ProviderIds.Claude, ProviderStatus.Offline, "claude_cli_timeout")),
            // Design §4.5 worst cases: the longest status message any provider produces, and the longest
            // error-with-notice combination.
            ("longest status (schema changed)", ProviderIds.Claude, Failed(ProviderIds.Claude, ProviderStatus.SchemaChanged, "claude_schema_changed")),
            ("signed out with notice", ProviderIds.Gemini, Failed(ProviderIds.Gemini, ProviderStatus.NeedsSignIn, "gemini_signed_out")),
            ("not started", ProviderIds.Codex, State(idle)),
        };
        foreach (var (stateName, providerId, state) in states)
            foreach (var (width, height) in MiniProvenSizes)
                AssertMiniLayoutAtSize(providerId, state, width, height, $"{providerId} mini {stateName} {width:0}x{height:0}");

        AssertMiniLayoutAtEveryScale();
    }

    /// <summary>Design §4.5: the two mini scale steps, 70 % and 150 %, at both mini sizes (the same shape as
    /// the full card's AssertLayoutAtEveryScale: the card's own content stays laid out at 1/scale through its
    /// LayoutTransformControl, exactly as ApplyCardScale already does for the full body, and the window is
    /// sized at scale times the unscaled mini size).</summary>
    private static void AssertMiniLayoutAtEveryScale()
    {
        var now = DateTimeOffset.UtcNow;
        var state = State(new AccountSnapshot(ProviderIds.Claude, "synthetic-shell-spec-scope", "Synthetic", now, now,
            "synthetic.shell.spec", [JustStartedWeekly(now)]));
        foreach (var percent in new[] { CardScale.MinimumPercent, CardScale.MaximumPercent })
        {
            var scale = percent / 100.0;
            foreach (var (width, height) in MiniProvenSizes)
                AssertMiniLayoutAtSize(ProviderIds.Claude, state, width * scale, height * scale,
                    $"claude mini {width:0}x{height:0} at {percent} %", percent);
        }
    }

    /// <summary>The mini size matrix, mirroring AssertLayoutAtEverySize's shape for the mini body: the
    /// requested size is the real one, nothing overlaps or clips, the hidden list stays hidden, text sizes
    /// are the mini floor/title/percentage only, and Ready vs. the message block agree with the state.</summary>
    private static void AssertMiniLayoutAtSize(string providerId, ProviderDisplayState? state, double width, double height, string name,
        int scalePercent = 100)
    {
        var window = new ProviderUsageCardWindow(providerId, showMark: true, _ => { });
        try
        {
            window.ApplyMode(CardMode.Mini);
            // The card's own content stays laid out at
            // 1/scale through the LayoutTransformControl, and the window is sized at scale times the unscaled
            // mini size, exactly like AssertLayoutAtEverySize does for the full body.
            window.ApplyCardScale(scalePercent);
            window.MinWidth = width;
            window.MinHeight = height;
            window.Width = width;
            window.Height = height;
            window.UpdateState(state);
            window.Show();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"{name}_render_failed");
            var client = window.ClientSize;
            Assert(Math.Abs(client.Width - width) < 0.5 && Math.Abs(client.Height - height) < 0.5,
                $"{name}: the window really is {width:0}x{height:0} (client {client.Width:0}x{client.Height:0})");

            var ready = state?.Status == ProviderStatus.Ready;
            Assert(window.MiniBody.IsColumnsVisible == ready && window.MiniBody.IsMessageVisible == !ready,
                $"{name}: Ready shows the two columns and hides the message block, anything else the reverse (ready={ready}, columns={window.MiniBody.IsColumnsVisible}, message={window.MiniBody.IsMessageVisible})");

            // Design §4.3's hidden list: the full body carries the chart, chart heading, legend, hint, footer,
            // plan type, Fable row and 5-hour panel, so hiding it hides all of them at once; the header's dot,
            // version text and week buttons are toggled individually by ApplyMode.
            Assert(!window.IsFullBodyVisible, $"{name}: the full body (chart, legend, footer, Fable row, 5-hour panel, plan type) is hidden in mini mode");
            Assert(!window.TitleDotControl.IsVisible && !window.TitleVersionControl.IsVisible,
                $"{name}: the title dot and version text are hidden in mini mode");
            Assert(!window.PreviousWeekControl.IsEffectivelyVisible && !window.NextWeekControl.IsEffectivelyVisible && !window.CurrentWeekControl.IsEffectivelyVisible,
                $"{name}: the week buttons are hidden in mini mode");
            Assert(window.MiniRefreshControl.IsVisible && window.ModeToggleControl.IsVisible,
                $"{name}: Refresh and the toggle show in the mini header");

            var content = window.BoundsWithinCard(window.InnerContentControl);
            foreach (var control in window.InnerContentControl.GetVisualDescendants().OfType<Control>()
                         .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0))
            {
                var bounds = window.BoundsWithinCard(control);
                Assert(bounds.Left >= content.Left - 0.5 && bounds.Right <= content.Right + 0.5 &&
                       bounds.Top >= content.Top - 0.5 && bounds.Bottom <= content.Bottom + 0.5,
                    $"{name}: {Describe(control)} stays inside the card content ({bounds} within {content})");
                if (control is TextBlock block && !string.IsNullOrEmpty(block.Text ?? block.Inlines?.Text))
                {
                    var neededWidth = block.DesiredSize.Width - block.Margin.Left - block.Margin.Right;
                    var neededHeight = block.DesiredSize.Height - block.Margin.Top - block.Margin.Bottom;
                    Assert(neededHeight <= block.Bounds.Height + 0.5 && neededWidth <= block.Bounds.Width + 0.5,
                        $"{name}: '{Describe(block)}' is not clipped ({neededWidth:0}x{neededHeight:0} needs to fit {block.Bounds.Size})");
                }
            }
            AssertNoOverlaps(window, name);

            // Text sizes: only the title (25) and the big percentage (66) are larger than the mini floor (14).
            var sizes = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible && block.FindAncestorOfType<Button>() is null)
                .Select(block => block.FontSize).ToArray();
            Assert(sizes.All(size => size is MiniCardContent.MiniTextFloor or ProviderUsageCardWindow.MiniTitleNameSize or MiniCardContent.MiniPercentSize),
                $"{name}: every visible text is the mini floor, the mini title size or the percentage size (found {string.Join(", ", sizes.Distinct())})");

            if (ready)
            {
                var paceText = window.MiniBody.PaceLineControl.Text ?? string.Empty;
                Assert(paceText == paceText.ToUpper(LocalizedText.UiCulture), $"{name}: the pace line is upper case (got '{paceText}')");
            }

            // Grips still sit at the visible corners (window design §5.1), unaffected by mode.
            foreach (var (grip, edge) in window.ResizeGripEdges)
            {
                switch (edge)
                {
                    case WindowEdge.NorthWest: AssertNear(grip.Bounds, new Rect(0, 0, 20, 20), name, edge); break;
                    case WindowEdge.NorthEast: AssertNear(grip.Bounds, new Rect(client.Width - 20, 0, 20, 20), name, edge); break;
                    case WindowEdge.SouthWest: AssertNear(grip.Bounds, new Rect(0, client.Height - 20, 20, 20), name, edge); break;
                    case WindowEdge.SouthEast: AssertNear(grip.Bounds, new Rect(client.Width - 20, client.Height - 20, 20, 20), name, edge); break;
                }
            }
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>Design §4.3: the remaining bar's tooltip is "{0} remaining", the delta bar's is the pace
    /// line's own natural (non-capitalized) text, both bars' automation names match, and a dash state (no
    /// weekly bucket at all) has no tooltip on either bar.</summary>
    private static void AssertMiniBarTooltips()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        try
        {
            window.ApplyMode(CardMode.Mini);
            var weekly = Bucket("weekly", QuotaBucketRole.Weekly, now);
            // the card computes its pace from its own reading of the clock, so the
            // expected text is computed just before and just after UpdateState. The card's reading lies between the
            // two, so its text is one of them even when a rounding boundary falls in between; comparing against a
            // single later reading could fail at such a boundary.
            var naturalBefore = LocalizedText.PaceSummary(
                PaceCalculator.Calculate(weekly, DateTimeOffset.UtcNow, SnapshotFreshness.Fresh), weekly.Duration);
            window.UpdateState(State(new AccountSnapshot(ProviderIds.Claude, "synthetic-shell-spec-scope", "Synthetic", now, now,
                "synthetic.shell.spec", [weekly])));
            var naturalAfter = LocalizedText.PaceSummary(
                PaceCalculator.Calculate(weekly, DateTimeOffset.UtcNow, SnapshotFreshness.Fresh), weekly.Duration);
            window.Show();
            var expectedRemaining = LocalizedText.CardRemainingTooltip(weekly.RemainingFraction);
            Assert(ToolTip.GetTip(window.MiniBody.RemainingBarControl) as string == expectedRemaining &&
                   AutomationProperties.GetName(window.MiniBody.RemainingBarControl) == expectedRemaining,
                $"the mini remaining bar's tooltip and name read '{expectedRemaining}' (got '{ToolTip.GetTip(window.MiniBody.RemainingBarControl)}')");
            var deltaTip = ToolTip.GetTip(window.MiniBody.DeltaBarControl) as string;
            Assert((deltaTip == naturalBefore || deltaTip == naturalAfter) &&
                   AutomationProperties.GetName(window.MiniBody.DeltaBarControl) == deltaTip,
                $"the mini delta bar's tooltip and name read the pace line's natural text '{naturalBefore}' (got '{deltaTip}')");

            // A dash state (Ready, but no weekly bucket at all) has no tooltip on either bar.
            window.UpdateState(State(new AccountSnapshot(ProviderIds.Claude, "synthetic-shell-spec-scope", "Synthetic", now, now,
                "synthetic.shell.spec", [])));
            Assert(ToolTip.GetTip(window.MiniBody.RemainingBarControl) is null && ToolTip.GetTip(window.MiniBody.DeltaBarControl) is null,
                "a dash mini state (no weekly bucket) has no bar tooltip");

            // the mini body passes the theme on to its two bars (they default to
            // dark, and a light mini card drew them in dark-theme colors).
            window.MiniBody.IsDark = false;
            Assert(!window.MiniBody.DeltaBarControl.IsDark && !window.MiniBody.RemainingBarControl.IsDark,
                "a light mini body draws its bars in light-theme colors");
            window.MiniBody.IsDark = true;
            Assert(window.MiniBody.DeltaBarControl.IsDark && window.MiniBody.RemainingBarControl.IsDark,
                "a dark mini body draws its bars in dark-theme colors");
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>Design §4.2: Refresh, toggle, ↺, Aa, ⚙, × in that order, left to right, so the ↺ … ×
    /// cluster never moves between modes.</summary>
    private static void AssertMiniHeaderOrder()
    {
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        try
        {
            window.ApplyMode(CardMode.Mini);
            window.Show();
            using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException("mini_header_order_render_failed")) { }
            Control[] order = [window.MiniRefreshControl, window.ModeToggleControl, window.ResetSizeControl, window.CardSizeControl, window.SettingsControl];
            for (var i = 1; i < order.Length; i++)
            {
                var previous = window.BoundsWithinCard(order[i - 1]);
                var current = window.BoundsWithinCard(order[i]);
                Assert(previous.Right <= current.Left + 0.5,
                    $"mini header order: {Describe(order[i - 1])} ({previous}) sits left of {Describe(order[i])} ({current})");
            }
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>Design §4.4: ApplyMode never changes the window's own Width, Height, MinWidth or MinHeight;
    /// the coordinator owns geometry, and applies it separately after ApplyMode returns.</summary>
    private static void AssertApplyModeNeverResizes()
    {
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        try
        {
            window.Width = 900;
            window.Height = 700;
            window.MinWidth = 500;
            window.MinHeight = 300;
            var (w, h, minW, minH) = (window.Width, window.Height, window.MinWidth, window.MinHeight);
            window.ApplyMode(CardMode.Mini);
            Assert(window.Width == w && window.Height == h && window.MinWidth == minW && window.MinHeight == minH,
                "ApplyMode(Mini) never changes the window's Width, Height, MinWidth or MinHeight");
            window.ApplyMode(CardMode.Full);
            Assert(window.Width == w && window.Height == h && window.MinWidth == minW && window.MinHeight == minH,
                "ApplyMode(Full) never changes the window's Width, Height, MinWidth or MinHeight");
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>Design §4.4: calling ApplyMode with the card's current mode does nothing (no bodies swapped,
    /// no toggle glyph change, no card_mode_applied log line's worth of side effects).</summary>
    private static void AssertApplyModeSameModeDoesNothing()
    {
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        try
        {
            Assert(window.Mode == CardMode.Full, "a new card starts Full");
            var glyphBefore = window.ModeToggleControl.Content;
            window.ApplyMode(CardMode.Full);
            Assert(window.Mode == CardMode.Full && Equals(window.ModeToggleControl.Content, glyphBefore) &&
                   window.IsFullBodyVisible && !window.IsMiniBodyVisible,
                "ApplyMode with the card's current mode changes nothing");
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>Design §4.4: entering mini while a past week is shown asks the coordinator for step 0 (so a
    /// later return to full shows Now); every other ApplyMode call raises nothing.</summary>
    private static void AssertModeEntersMiniRequestsStepZeroWhilePastWeekShown()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        var steps = new List<int>();
        void OnStep(ProviderUsageCardWindow sender, int step) => steps.Add(step);
        window.HistoryWindowStepRequested += OnStep;
        try
        {
            window.UpdateState(State(Snapshot(now, [])));
            window.Show();
            var current = window.CurrentHistoryWindow ?? throw new InvalidOperationException("mode_step_zero_no_current_window");
            var lastWeek = new UsageWindowIdentity(current.AccountScope, current.ProviderId, current.BucketId,
                current.NominalStartUtc.AddDays(-7), current.NominalStartUtc, current.SourceSemantics);
            window.SetHistoryView(lastWeek, [], TimeSpan.FromMinutes(5), true, offset: 1, count: 2);
            steps.Clear();

            window.ApplyMode(CardMode.Mini);
            Assert(steps.SequenceEqual([0]), $"entering mini while a past week is shown asks for step 0 once (got {string.Join(",", steps)})");
            steps.Clear();
            window.ApplyMode(CardMode.Full);
            Assert(steps.Count == 0, "leaving mini raises nothing by itself");

            // The coordinator's real response to that step-0 request returns the card to the current week;
            // entering mini again then raises nothing, since nothing is being browsed any more.
            window.SetHistoryView(current, [], TimeSpan.FromMinutes(5), true, offset: 0, count: 2);
            steps.Clear();
            window.ApplyMode(CardMode.Mini);
            Assert(steps.Count == 0, "entering mini while showing the current week raises nothing");
        }
        finally
        {
            window.HistoryWindowStepRequested -= OnStep;
            window.CloseProgrammatically();
        }
    }

    /// <summary>Design §8.2's round trip: full to mini to full on one card, then the whole existing full
    /// matrix assertion set passes on it (the swap leaves nothing behind).</summary>
    private static void AssertMiniRoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        try
        {
            window.UpdateState(State(Snapshot(now,
                [Bucket("claude.short", QuotaBucketRole.Short, now), Bucket("claude.fable.weekly", QuotaBucketRole.FeaturedWeekly, now)])));
            window.Show();
            window.ApplyMode(CardMode.Mini);
            Assert(window.Mode == CardMode.Mini && window.IsMiniBodyVisible && !window.IsFullBodyVisible, "entered mini mode");
            window.ApplyMode(CardMode.Full);
            Assert(window.Mode == CardMode.Full && !window.IsMiniBodyVisible && window.IsFullBodyVisible, "returned to full mode");
            AssertLayoutAtEverySize(window, $"{ProviderIds.Claude} post-round-trip");
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>Design §8.1's pure CardScale.Mini bullet (no headless window: these are plain static-method
    /// checks): PercentFor/SetPercent read and write AppSettings.MiniCardScalePercent, normalized the same way
    /// as the full scale, and MinimumSize/DefaultSize scale CardLayoutTiers' measured mini constants.</summary>
    private static void AssertCardScaleMiniCases()
    {
        var settings = AppSettings.CreateDefault();
        Assert(CardScale.PercentFor(settings, CardMode.Mini) == 100, "a fresh AppSettings reads MiniCardScalePercent as 100");
        CardScale.SetPercent(settings, CardMode.Mini, 133);
        Assert(settings.MiniCardScalePercent == 130 && CardScale.PercentFor(settings, CardMode.Mini) == 130,
            $"SetPercent(Mini, 133) normalizes to 130 and PercentFor reads it back (got {settings.MiniCardScalePercent})");
        // Full and Mini are independent settings; setting one does not move the other.
        Assert(settings.FullCardScalePercent == 100, "setting the mini scale leaves the full scale untouched");

        Assert(CardScale.MinimumSize(CardMode.Mini, 100) == new LogicalSize(CardLayoutTiers.MiniMinWidth, CardLayoutTiers.MiniMinHeight),
            "CardScale.MinimumSize(Mini, 100) is the measured mini minimum");
        Assert(CardScale.MinimumSize(CardMode.Mini, 70) == new LogicalSize(CardLayoutTiers.MiniMinWidth * 0.7, CardLayoutTiers.MiniMinHeight * 0.7),
            "CardScale.MinimumSize(Mini, 70) scales the mini minimum");
        Assert(CardScale.DefaultSize(CardMode.Mini, 100) == new LogicalSize(CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight),
            "CardScale.DefaultSize(Mini, 100) is MiniMinWidth + 60 by MiniMinHeight");
        Assert(CardScale.DefaultSize(CardMode.Mini, 150) == new LogicalSize(CardLayoutTiers.MiniDefaultWidth * 1.5, CardLayoutTiers.MiniDefaultHeight * 1.5),
            "CardScale.DefaultSize(Mini, 150) scales the mini default");
        Assert(CardScale.UsableMinimum(CardMode.Mini, 100) == new LogicalSize(CardLayoutTiers.MiniMinWidth - 1, CardLayoutTiers.MiniMinHeight - 1),
            "CardScale.UsableMinimum(Mini, 100) keeps the one-pixel slack below the mini minimum");
    }

    /// <summary>Design §8.2: the pseudo-long locale at MiniDefaultWidth × (MiniMinHeight + 60).</summary>
    private static void AssertMiniPseudoLocaleFits()
    {
        var now = DateTimeOffset.UtcNow;
        using var pseudo = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("qps-ploc"), CultureInfo.GetCultureInfo("en-US"),
            new LocalizationResourceProof.PseudoLocaleResources());
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        try
        {
            window.ApplyMode(CardMode.Mini);
            window.UpdateState(State(new AccountSnapshot(ProviderIds.Claude, "synthetic-shell-spec-scope", "Synthetic", now, now,
                "synthetic.shell.spec", [JustStartedWeekly(now)])));
            var height = CardLayoutTiers.MiniMinHeight + 60;
            window.MinWidth = CardLayoutTiers.MiniDefaultWidth;
            window.MinHeight = height;
            window.Width = CardLayoutTiers.MiniDefaultWidth;
            window.Height = height;
            window.Show();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("mini_pseudo_locale_render_failed");
            Assert(window.MiniRefreshControl.Content as string == LocalizationResourceProof.PseudoLocaleResources.Pseudo("Refresh"),
                $"the pseudo-long locale is in force for mini Refresh (got '{window.MiniRefreshControl.Content}')");
            AssertNoOverlaps(window, "mini pseudo-long");
            var content = window.BoundsWithinCard(window.InnerContentControl);
            foreach (var control in window.InnerContentControl.GetVisualDescendants().OfType<Control>()
                         .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0))
            {
                var bounds = window.BoundsWithinCard(control);
                Assert(bounds.Left >= content.Left - 0.5 && bounds.Right <= content.Right + 0.5 &&
                       bounds.Top >= content.Top - 0.5 && bounds.Bottom <= content.Bottom + 0.5,
                    $"mini pseudo-long: {Describe(control)} stays inside the card content ({bounds} within {content})");
            }
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }
}

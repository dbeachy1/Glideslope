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

// Full-card size, scale, state, and localization layout proofs. Governed by window design §4, §13 Level 2,
// §18, and Appendix G.
internal static partial class CardPresentationProof
{
    /// <summary>
    /// Checks the widest English reset line against the summary column by measuring each weekday, month,
    /// day number, morning and afternoon time, and longest time-left form. Segoe UI digits have equal width,
    /// so only the number of digits affects the result.
    /// </summary>
    private static void AssertWidestEnglishResetLineFits()
    {
        var block = new TextBlock { FontSize = ProviderUsageCardWindow.CardTextFloor, FontWeight = FontWeight.SemiBold };
        var widest = (Width: 0.0, Text: string.Empty);
        var leftForms = new[] { TimeSpan.FromMinutes(58), TimeSpan.FromHours(22), TimeSpan.FromDays(6.8) };
        for (var day = new DateTime(2027, 1, 1); day.Year == 2027; day = day.AddDays(1))
            foreach (var hour in new[] { 10, 22 })
                foreach (var left in leftForms)
                {
                    var reset = new DateTimeOffset(day.AddHours(hour).AddMinutes(58), TimeZoneInfo.Local.GetUtcOffset(day.AddHours(hour)));
                    block.Text = LocalizedText.CardResetAt(reset, reset - left);
                    block.Measure(Size.Infinity);
                    if (block.DesiredSize.Width > widest.Width)
                        widest = (block.DesiredSize.Width, block.Text);
                }
        Console.WriteLine($"presentation proof: widest English reset line {widest.Width:0.0} px, '{widest.Text}'");
        Assert(widest.Width <= CardLayoutTiers.SummaryColumnWidth,
            $"the widest English reset line fits the {CardLayoutTiers.SummaryColumnWidth:0} px summary column on one line ({widest.Width:0.0} px, '{widest.Text}')");
    }

    /// <summary>
    /// Window design §13 Level 2 for the two cards the main Claude pass in Run doesn't cover: Codex with
    /// three credits (and its 5-hour window), and Gemini with a short and a weekly bucket.
    /// </summary>
    private static void AssertCodexAndGeminiAtEverySize()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (providerId, credits) in new[] { (ProviderIds.Codex, Credits(3, now)), (ProviderIds.Gemini, (ResetCreditInventory?)null) })
        {
            var window = new ProviderUsageCardWindow(providerId, showMark: true, _ => { });
            try
            {
                window.UpdateState(State(Snapshot(now, [Bucket($"{providerId}.short", QuotaBucketRole.Short, now)], credits, providerId)));
                Assert(window.IsShortWindowSectionVisible, $"{providerId}: the size matrix runs with the five-hour section shown");
                Assert(window.IsCreditInventoryVisible == (credits is not null),
                    $"{providerId}: the size matrix runs with the credit strip populated exactly when the card has credits");
                window.Show();
                AssertLayoutAtEverySize(window, providerId);
                AssertLayoutAtEveryScale(window, providerId);
            }
            finally
            {
                window.CloseProgrammatically();
            }
        }
    }

    /// <summary>Default and minimum sizes used at 70 % and 150 % scale.</summary>
    private static readonly (double Width, double Height)[] ScaledSizes =
    [
        (ProviderUsageCardWindow.DefaultCardWidth, ProviderUsageCardWindow.DefaultCardHeight),
        (CardLayoutTiers.MinWidth, CardLayoutTiers.MinHeight),
    ];

    private static void AssertLayoutAtEveryScale(ProviderUsageCardWindow window, string name)
    {
        foreach (var percent in new[] { CardScale.MinimumPercent, CardScale.MaximumPercent })
            AssertLayoutAtEverySize(window, name, percent, ScaledSizes);
        window.ApplyCardScale(CardScale.DefaultPercent);
        window.MinWidth = CardLayoutTiers.MinWidth;
        window.MinHeight = CardLayoutTiers.MinHeight;
    }

    private static readonly (double Width, double Height)[] ProvenSizes =
    [
        (ProviderUsageCardWindow.DefaultCardWidth, ProviderUsageCardWindow.DefaultCardHeight),
        (CardLayoutTiers.MinWidth, CardLayoutTiers.MinHeight),
        (1400, 900),
    ];

    /// <summary>Checks layout requirements at the requested sizes and scale. At non-default scale, geometry is
    /// scaled while content rules are checked in unscaled card units. <paramref name="english"/> enables the
    /// summary-width and chart-heading checks for English text.</summary>
    private static void AssertLayoutAtEverySize(ProviderUsageCardWindow window, string providerId, int scalePercent = 100,
        (double Width, double Height)[]? layoutSizes = null, bool english = true, bool localizedMinimum = false)
    {
        var scale = scalePercent / 100.0;
        // The coordinator owns the window's minimum (the card never sets it); this stands in for it.
        window.MinWidth = CardLayoutTiers.MinWidth * scale;
        window.MinHeight = (localizedMinimum ? LocalizedText.CardMinimumHeight : CardLayoutTiers.MinHeight) * scale;
        window.ApplyCardScale(scalePercent);
        Assert(window.CardScalePercent == scalePercent, $"{providerId}: the card applied card size {scalePercent} % (it reports {window.CardScalePercent} %)");
        foreach (var (width, height) in layoutSizes ?? ProvenSizes)
        {
            window.Width = width * scale;
            window.Height = height * scale;
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"{providerId}_{width}x{height}_render_failed");
            var name = scalePercent == 100 ? $"{providerId} {width}x{height}" : $"{providerId} {width}x{height} at {scalePercent} %";
            var client = window.ClientSize;
            Assert(Math.Abs(client.Width - width * scale) < 0.5 && Math.Abs(client.Height - height * scale) < 0.5,
                $"{name}: the window really is {width * scale:0}x{height * scale:0} (client size {client.Width:0}x{client.Height:0}); a minimum larger than the requested size would hide behind a passing row");
            Assert(window.ChartControl.IsVisible && window.FooterControl.IsVisible,
                $"{name}: the chart and footer are always shown (one layout, design doc §4.3)");

            // No letterbox: the frame fills the window (window design §4.1). Card size: the frame is laid out at
            // 1/size of the window and drawn at the size, so its layout size times the size is the client size.
            // LayoutTransformControl rounds the child's layout size to whole pixels, so at 70 % the frame can be one
            // layout pixel (0.7 px on screen) off; the tolerance is one layout pixel at the card size.
            var outer = window.OuterFrameControl.Bounds;
            var frameTolerance = scalePercent == 100 ? 0.5 : 0.5 + scale;
            Assert(Math.Abs(outer.Width * scale - client.Width) < frameTolerance && Math.Abs(outer.Height * scale - client.Height) < frameTolerance,
                $"{name}: the card frame fills the window ({outer.Width:0}x{outer.Height:0} at {scalePercent} % vs {client.Width:0}x{client.Height:0})");

            // Everything visible lies inside the inner content area, and no text is cut short. The big
            // percentage has a deliberate -4 px left margin (optical alignment of the glyphs), so it alone
            // may sit 4 px left of the content edge.
            var content = window.BoundsWithinCard(window.InnerContentControl);
            foreach (var control in window.InnerContentControl.GetVisualDescendants().OfType<Control>()
                         .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0))
            {
                var bounds = window.BoundsWithinCard(control);
                var leftAllowance = ReferenceEquals(control, window.SummaryDragTarget) ? 4.5 : 0.5;
                Assert(bounds.Left >= content.Left - leftAllowance && bounds.Right <= content.Right + 0.5 &&
                       bounds.Top >= content.Top - 0.5 && bounds.Bottom <= content.Bottom + 0.5,
                    $"{name}: {Describe(control)} stays inside the card content ({bounds} within {content})");
                if (control is TextBlock block && !string.IsNullOrEmpty(block.Text ?? block.Inlines?.Text))
                {
                    // DesiredSize includes the block's own Margin; Bounds does not.
                    var neededWidth = block.DesiredSize.Width - block.Margin.Left - block.Margin.Right;
                    var neededHeight = block.DesiredSize.Height - block.Margin.Top - block.Margin.Bottom;
                    Assert(neededHeight <= block.Bounds.Height + 0.5 && neededWidth <= block.Bounds.Width + 0.5,
                        $"{name}: '{Describe(block)}' is not clipped ({neededWidth:0}x{neededHeight:0} needs to fit {block.Bounds.Size})");
                }
            }

            // Blocks don't overlap. A StackPanel's Bounds are clamped to its grid row while its children
            // keep their full height, so the summary's last child must clear the next block, not just its Bounds.
            var summary = window.BoundsWithinCard(window.SummaryControl);
            var summaryLast = window.SummaryControl.GetVisualChildren().OfType<Control>().Where(child => child.IsVisible)
                .Select(child => window.BoundsWithinCard(child)).Max(rect => rect.Bottom);
            var chartGroup = window.BoundsWithinCard(window.ChartGroupControl);
            var chartLast = window.ChartGroupControl.GetVisualChildren().OfType<Control>().Where(child => child.IsVisible)
                .Select(child => window.BoundsWithinCard(child)).Max(rect => rect.Bottom);
            var weeklyPace = window.BoundsWithinCard(window.WeeklyPaceTitleControl);
            Assert(summary.Right <= chartGroup.Left + 0.5, $"{name}: the summary column does not run into the chart column");
            Assert(Math.Max(summaryLast, chartLast) <= weeklyPace.Top + 0.5,
                $"{name}: the body's last child ({Math.Max(summaryLast, chartLast):0}) clears the bars ({weeklyPace.Top:0})");
            // Bounds for stacked panels do not include overflowing children; check every pair of visible blocks.
            AssertNoOverlaps(window, name);
            if (english)
            {
                // At minimum width the summary uses the space remaining after frame, gap, and chart minimum;
                // wider cards use SummaryColumnWidth.
                var summaryLimit = width <= CardLayoutTiers.MinWidth
                    ? CardLayoutTiers.MinWidth - CardLayoutTiers.FrameInsetsX - CardLayoutTiers.ColumnGap - CardLayoutTiers.ChartMinWidth
                    : CardLayoutTiers.SummaryColumnWidth;
                Assert(window.SummaryColumnWidth <= summaryLimit + 0.5 && window.SummaryColumnWidth >= CardLayoutTiers.SummaryColumnWidth - 0.5,
                    $"{name}: English keeps the {CardLayoutTiers.SummaryColumnWidth:0} px summary column, up to {summaryLimit:0} at the minimum width (got {window.SummaryColumnWidth:0}; summary needs {window.SummaryControl.DesiredSize.Height:0} of a body {window.BoundsWithinCard(window.ChartGroupControl).Height:0} tall)");
                var expectStacked = width <= CardLayoutTiers.MinWidth;
                Assert(window.IsChartHeadingStacked == expectStacked,
                    $"{name}: the chart heading uses {(expectStacked ? "two rows at the minimum width" : "one row, Doug's look")} (stacked={window.IsChartHeadingStacked})");
            }

            // Text sizes: only the three headlines are larger than the floor; nothing is smaller. The
            // header's icon buttons (↺ ⚙ ×) draw glyphs at their own sizes inside Button templates and are
            // not card text, so TextBlocks inside a Button are excluded here.
            var sizes = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible && block.FindAncestorOfType<Button>() is null)
                .Select(block => block.FontSize).ToArray();
            Assert(sizes.All(size => size is ProviderUsageCardWindow.CardTextFloor or 19 or 29 or 76),
                $"{name}: every text is the floor or a headline size (found {string.Join(", ", sizes.Distinct())})");
            Assert(sizes.Count(size => size > ProviderUsageCardWindow.CardTextFloor) == 3,
                $"{name}: exactly the title, the big percentage and 'weekly remaining' are larger than the floor");
            Assert(window.RefreshControl.FontSize == ProviderUsageCardWindow.CardTextFloor,
                $"{name}: the Refresh button text is at the floor");

            // Grips sit exactly at the visible corners and along the visible edges (window design §5.1).
            foreach (var (grip, edge) in window.ResizeGripEdges)
            {
                var bounds = grip.Bounds;   // relative to _moveDragSurface, which is the client area
                switch (edge)
                {
                    case WindowEdge.NorthWest: AssertNear(bounds, new Rect(0, 0, 20, 20), name, edge); break;
                    case WindowEdge.NorthEast: AssertNear(bounds, new Rect(client.Width - 20, 0, 20, 20), name, edge); break;
                    case WindowEdge.SouthWest: AssertNear(bounds, new Rect(0, client.Height - 20, 20, 20), name, edge); break;
                    case WindowEdge.SouthEast: AssertNear(bounds, new Rect(client.Width - 20, client.Height - 20, 20, 20), name, edge); break;
                    case WindowEdge.North: AssertNear(bounds, new Rect(20, 0, client.Width - 40, 8), name, edge); break;
                    case WindowEdge.South: AssertNear(bounds, new Rect(20, client.Height - 8, client.Width - 40, 8), name, edge); break;
                    case WindowEdge.West: AssertNear(bounds, new Rect(0, 20, 8, client.Height - 40), name, edge); break;
                    case WindowEdge.East: AssertNear(bounds, new Rect(client.Width - 8, 20, 8, client.Height - 40), name, edge); break;
                }
            }
            AssertCarriedOverLayoutRules(window, name);
            Console.WriteLine($"{name}: content={content} summary={summary} chart={chartGroup}");
        }
    }

    /// <summary>Checks the weekly pace/bar, featured-section/footer, credit-strip, and chart-label constraints.</summary>
    private static void AssertCarriedOverLayoutRules(ProviderUsageCardWindow window, string name)
    {
        var weeklyPace = window.BoundsWithinCard(window.WeeklyPaceTitleControl);
        var weeklyDelta = window.BoundsWithinCard(window.WeeklyDeltaBarControl);
        if (window.IsShortWindowSectionVisible)
        {
            // The summary StackPanel's own bounds are clamped to its grid row, so its children can
            // spill past it; the short-window section's real bottom is what must clear the bars.
            var shortWindow = window.BoundsWithinCard(window.ShortWindowSectionControl);
            Assert(shortWindow.Bottom <= weeklyPace.Top + 0.5,
                $"{name}: the short-window section clears the weekly pace heading ({shortWindow.Bottom:0.0} <= {weeklyPace.Top:0.0})");
        }
        Assert(weeklyPace.Bottom <= weeklyDelta.Top + 0.5, $"{name}: the weekly pace heading clears its bar");
        if (window.FeaturedWeeklySectionControl.IsEffectivelyVisible && window.FooterControl.IsEffectivelyVisible)
        {
            var fable = window.BoundsWithinCard(window.FeaturedWeeklySectionControl);
            var footer = window.BoundsWithinCard(window.FooterControl);
            Assert(fable.Bottom <= footer.Top + 0.5, $"{name}: the featured weekly section clears the footer");
        }

        // Chart labels must meet the card's minimum text size at every supported card size.
        Assert(WeeklyHistoryChart.DayLabelFontSize >= ProviderUsageCardWindow.CardTextFloor &&
               WeeklyHistoryChart.EmptyStateFontSize >= ProviderUsageCardWindow.CardTextFloor,
            $"{name}: the chart's own custom-drawn day labels and empty-state text also meet the floor");

        // Check the last visible line against the strip's padded bottom; the child's arranged height alone
        // does not reveal whether its content overflows. The reserved strip height is a minimum.
        Assert(window.CreditStripControl.Bounds.Height >= CreditStripHeight - 0.5,
            $"{name}: the credit strip keeps its reserved {CreditStripHeight:0} px (got {window.CreditStripControl.Bounds.Height:0.0})");
        if (window.IsCreditInventoryVisible)
        {
            // The strip's child is a Grid that overlays the credit content and the sign-in notice (Claude CLI
            // source design §2 rule 7); the Border is one level up.
            var strip = window.CreditContentControl.Parent?.Parent as Border
                ?? throw new InvalidOperationException($"{name}: the credit content is not hosted in its strip Border");
            var stripBounds = window.BoundsWithinCard(strip);
            var creditLast = window.CreditContentControl.GetVisualChildren().OfType<Control>().Where(child => child.IsVisible)
                .Select(child => window.BoundsWithinCard(child)).Max(rect => rect.Bottom);
            var creditLimit = stripBounds.Bottom - strip.Padding.Bottom;
            Assert(creditLast <= creditLimit + 0.5,
                $"{name}: the credit strip's last visible line ends at {creditLast:0.0}, inside the strip's bottom less its padding ({creditLimit:0.0}; strip height {window.CreditStripReservedHeight:0.0})");
        }
    }

    /// <summary>Checks pairwise layout for connection, sign-in, CLI, rate-limit, offline, not-started, and
    /// past-week states beyond the main provider size matrix.</summary>
    private static void AssertEveryCardStateFits()
    {
        var now = DateTimeOffset.UtcNow;
        ProviderDisplayState Failed(string providerId, ProviderStatus status, string code, DateTimeOffset? retry = null) =>
            new(providerId, 1, true, false, status, null, SnapshotFreshness.Fresh, retry, [], code, TimeSpan.Zero, now);
        var idle = new AccountSnapshot(ProviderIds.Codex, "synthetic-shell-spec-scope", "Synthetic", now, now, "synthetic.shell.spec",
        [
            new QuotaBucket("weekly", QuotaBucketRole.Weekly, 1.0, TimeSpan.FromDays(7), now.AddDays(7), "synthetic.shell.spec", windowStarted: false),
            new QuotaBucket("codex.short", QuotaBucketRole.Short, 1.0, TimeSpan.FromHours(5), now.AddHours(5), "synthetic.shell.spec", windowStarted: false),
        ]);
        var states = new (string Name, string ProviderId, ProviderDisplayState? State)[]
        {
            ("connecting", ProviderIds.Claude, null),
            ("signed out", ProviderIds.Claude, Failed(ProviderIds.Claude, ProviderStatus.NeedsSignIn, "claude_signed_out")),
            ("cli missing", ProviderIds.Codex, Failed(ProviderIds.Codex, ProviderStatus.MissingApplication, "codex_cli_missing")),
            ("rate limited", ProviderIds.Gemini, Failed(ProviderIds.Gemini, ProviderStatus.RateLimited, "gemini_rate_limited", now.AddMinutes(20))),
            ("offline", ProviderIds.Claude, Failed(ProviderIds.Claude, ProviderStatus.Offline, "claude_cli_timeout")),
            ("not started", ProviderIds.Codex, State(idle)),
        };
        foreach (var (stateName, providerId, state) in states)
        {
            var window = new ProviderUsageCardWindow(providerId, showMark: true, _ => { });
            try
            {
                window.UpdateState(state);
                window.Show();
                AssertLayoutAtEverySize(window, $"{providerId} {stateName}");
            }
            finally
            {
                window.CloseProgrammatically();
            }
        }

        var browsing = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        try
        {
            browsing.UpdateState(State(Snapshot(now,
                [Bucket("claude.short", QuotaBucketRole.Short, now), Bucket("claude.fable.weekly", QuotaBucketRole.FeaturedWeekly, now)])));
            browsing.Show();
            var current = browsing.CurrentHistoryWindow ?? throw new InvalidOperationException("state_matrix_no_current_window");
            var lastWeek = new UsageWindowIdentity(current.AccountScope, current.ProviderId, current.BucketId,
                current.NominalStartUtc.AddDays(-7), current.NominalStartUtc, current.SourceSemantics);
            browsing.SetHistoryView(lastWeek, [], TimeSpan.FromMinutes(5), true, offset: 1, count: 2);
            AssertLayoutAtEverySize(browsing, $"{ProviderIds.Claude} past week");
        }
        finally
        {
            browsing.CloseProgrammatically();
        }
    }

    /// <summary>A Fable window that has not started reads "No usage yet" without delta fill; its heading does not
    /// display the moving reset time.</summary>
    private static void AssertFableNotStarted()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        try
        {
            AccountSnapshot Idle(DateTimeOffset at) => new(ProviderIds.Claude, "synthetic-shell-spec-scope", "max", at, at, "synthetic.shell.spec",
            [
                new QuotaBucket("weekly", QuotaBucketRole.Weekly, 0.63, TimeSpan.FromDays(7), now.AddDays(3), "synthetic.shell.spec"),
                new QuotaBucket("claude.short", QuotaBucketRole.Short, 0.63, TimeSpan.FromHours(5), now.AddHours(3), "synthetic.shell.spec"),
                new QuotaBucket("claude.fable.weekly", QuotaBucketRole.FeaturedWeekly, 1.0, TimeSpan.FromDays(7), at.AddDays(7),
                    "synthetic.shell.spec", windowStarted: false),
            ]);
            window.UpdateState(State(Idle(now)));
            window.Show();
            Assert(window.FableDeltaSummaryText == "No usage yet", $"an unused Fable window says no usage yet (got '{window.FableDeltaSummaryText}')");
            Assert(window.FableDeltaBarControl.SignedDelta is null, "an unused Fable window draws no delta fill");
            Assert(window.FableHeadingControl.Text == "FABLE THIS WEEK",
                $"an unused Fable window's sliding reset is not a separate reset (got '{window.FableHeadingControl.Text}')");
            window.UpdateState(State(Idle(now.AddMinutes(5))));
            Assert(window.FableHeadingControl.Text == "FABLE THIS WEEK", "a later poll's sliding reset does not move the heading");
            AssertLayoutAtEverySize(window, $"{ProviderIds.Claude} fable-not-started");
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>Checks that pseudo-localized content fits without overlap or clipping at the minimum, default,
    /// and large widths. Each language's required minimum height comes from the Card_MinimumHeight resource.</summary>
    private static void AssertPseudoLocaleFits()
    {
        var now = DateTimeOffset.UtcNow;
        using var pseudo = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("qps-ploc"), CultureInfo.GetCultureInfo("en-US"),
            new LocalizationResourceProof.PseudoLocaleResources());
        IEnumerable<QuotaBucket> ClaudeExtra() => [Bucket("claude.short", QuotaBucketRole.Short, now), Bucket("claude.fable.weekly", QuotaBucketRole.FeaturedWeekly, now)];
        var cards = new (string Name, string ProviderId, ProviderDisplayState State)[]
        {
            ("history-scope", ProviderIds.Claude, State(Snapshot(now, ClaudeExtra())) with { SafeErrorCode = "claude_history_scope_unavailable" }),
            ("schema-changed", ProviderIds.Claude, State(Snapshot(now, ClaudeExtra())) with { Status = ProviderStatus.SchemaChanged }),
            ("credits", ProviderIds.Codex, State(Snapshot(now, [Bucket("codex.short", QuotaBucketRole.Short, now)], Credits(3, now), ProviderIds.Codex))),
            ("five-hour", ProviderIds.Gemini, State(Snapshot(now, [Bucket("gemini.short", QuotaBucketRole.Short, now)], null, ProviderIds.Gemini))),
        };
        foreach (var (cardName, providerId, state) in cards)
        {
            var window = new ProviderUsageCardWindow(providerId, showMark: true, _ => { });
            try
            {
                window.UpdateState(state);
                window.Show();
                Assert(window.RefreshControl.Content as string == LocalizationResourceProof.PseudoLocaleResources.Pseudo("Refresh"),
                    $"{providerId}: the pseudo-long locale is in force (Refresh reads '{window.RefreshControl.Content}')");
                AssertLayoutAtEverySize(window, $"{providerId} pseudo-long {cardName}", 100, PseudoLocaleSizes, english: false);
            }
            finally
            {
                window.CloseProgrammatically();
            }
        }
    }

    private const double PseudoLocaleMinimumHeight = 790;
    private static readonly (double Width, double Height)[] PseudoLocaleSizes =
    [
        (ProviderUsageCardWindow.DefaultCardWidth, PseudoLocaleMinimumHeight),
        (CardLayoutTiers.MinWidth, PseudoLocaleMinimumHeight),
        (1400, 900),
    ];
}

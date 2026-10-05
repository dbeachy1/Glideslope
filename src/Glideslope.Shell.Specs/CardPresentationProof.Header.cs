using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
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

// The card's header and edge proofs: the title row's centering, week navigation (with and without a current
// window), the sign-in notice, the plan type line, the Fable reset heading, the Undock button and the resize
// grips. Governed by window design §5.1, §5.6, §15.2-§15.6 and §17, and the Claude CLI source design §2.
internal static partial class CardPresentationProof
{
    /// <summary>
    /// Window design §15.4: at the default 940x680 and minimum card sizes, the provider name, dot and week
    /// buttons share a vertical center within 1.5 px in both themes. The version text is 4 logical px lower,
    /// scaled with the card. Theme changes also recolor the dot from white in dark mode to neutral gray in light
    /// mode. Every child's bounds are printed on failure.
    /// </summary>
    private static void AssertTitleRowCentered(ProviderUsageCardWindow window)
    {
        var children = new (string Name, Control Control)[]
        {
            ("provider name", window.TitleNameControl),
            ("dot", window.TitleDotControl),
            ("version", window.TitleVersionControl),
            ("previous week", window.PreviousWeekControl),
            ("next week", window.NextWeekControl),
            ("now", window.CurrentWeekControl),
        };
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            foreach (var (width, height) in new[] { (ProviderUsageCardWindow.DefaultCardWidth, ProviderUsageCardWindow.DefaultCardHeight), (CardLayoutTiers.MinWidth, CardLayoutTiers.MinHeight) })
            {
                window.Width = width;
                window.Height = height;
                using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"title_row_{width}x{height}_{theme}_render_failed")) { }
                Assert(window.ActualThemeVariant == theme, $"title row {width}x{height}: requested theme {theme} is active (got {window.ActualThemeVariant})");
                var measured = children.Select(child => (child.Name, Bounds: window.BoundsWithinCard(child.Control))).ToArray();
                var report = string.Join("; ", measured.Select(m => $"{m.Name} {m.Bounds} center y={m.Bounds.Center.Y:0.00}"));
                Console.WriteLine($"title row {window.ProviderId} {width}x{height} {theme}: {report}");
                var centers = measured.Where(m => m.Name != "version").Select(m => m.Bounds.Center.Y).ToArray();
                Assert(centers.Max() - centers.Min() <= 1.5,
                    $"title row {width}x{height} {theme}: provider name, dot and week buttons share a vertical center within 1.5 px ({report})");
                var versionCenter = measured.Single(m => m.Name == "version").Bounds.Center.Y;
                var expectedVersionCenter = centers.Average() + ProviderUsageCardWindow.TitleVersionOpticalOffsetY;
                Assert(Math.Abs(versionCenter - expectedVersionCenter) <= 1.5,
                    $"title row {width}x{height} {theme}: version text is 4 logical px below the shared center (expected {expectedVersionCenter:0.00}, got {versionCenter:0.00}; {report})");
                Assert(measured.All(m => m.Bounds.Width > 0 && m.Bounds.Height > 0), $"title row {width}x{height}: every title-row child is laid out ({report})");
                var expectedDot = theme == ThemeVariant.Light ? Color.Parse("#596B80") : Color.Parse("#FFFFFF");
                Assert(window.TitleDotControl is Ellipse { Fill: SolidColorBrush dotBrush, Width: 4, Height: 4 } && dotBrush.Color == expectedDot,
                    $"title row {width}x{height} {theme}: the dot color is {expectedDot}");
                var dotBounds = measured.Single(m => m.Name == "dot").Bounds;
                Assert(Math.Abs(dotBounds.Center.Y - centers.Average()) <= 1 && Math.Abs(dotBounds.Width - 4) <= 0.1 && Math.Abs(dotBounds.Height - 4) <= 0.1,
                    $"title row {width}x{height} {theme}: the dot is a 4 px circle centered on its peers ({dotBounds})");
                var nowBounds = measured[5].Bounds;
                Assert(Math.Abs(window.PreviousWeekControl.Bounds.Height - window.SettingsControl.Bounds.Height) < 0.5 &&
                       Math.Abs(nowBounds.Height - window.SettingsControl.Bounds.Height) < 0.5 && nowBounds.Width >= 44 - 0.5,
                    $"title row {width}x{height}: the week buttons are the gear's height and Now is at least 44 wide (now {nowBounds})");
            }
        }
        window.RequestedThemeVariant = ThemeVariant.Default;
        using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException("title_row_default_theme_render_failed")) { }
    }

    private static void AssertRepresentativeTitleRowScales()
    {
        AssertTitleRowAtScale(ProviderIds.Codex, CardScale.MinimumPercent);
        AssertTitleRowAtScale(ProviderIds.Gemini, CardScale.MaximumPercent);
    }

    private static void AssertTitleRowAtScale(string providerId, int scalePercent)
    {
        var window = new ProviderUsageCardWindow(providerId, showMark: false, _ => { });
        try
        {
            var scale = scalePercent / 100.0;
            window.MinWidth = CardLayoutTiers.MinWidth * scale;
            window.MinHeight = CardLayoutTiers.MinHeight * scale;
            window.ApplyCardScale(scalePercent);
            window.UpdateState(State(Snapshot(DateTimeOffset.UtcNow, [])));
            window.Show();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                foreach (var (width, height) in new[] { (ProviderUsageCardWindow.DefaultCardWidth, ProviderUsageCardWindow.DefaultCardHeight), (CardLayoutTiers.MinWidth, CardLayoutTiers.MinHeight) })
                {
                    window.Width = width * scale;
                    window.Height = height * scale;
                    using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"title_row_{providerId}_{scalePercent}_{width}x{height}_{theme}_render_failed")) { }
                    var controls = new (string Name, Control Control)[]
                    {
                        ("provider name", window.TitleNameControl), ("dot", window.TitleDotControl),
                        ("version", window.TitleVersionControl), ("previous week", window.PreviousWeekControl),
                        ("next week", window.NextWeekControl), ("now", window.CurrentWeekControl)
                    };
                    var measured = controls.Select(child => (child.Name, Bounds: window.BoundsWithinCard(child.Control))).ToArray();
                    var commonCenters = measured.Where(child => child.Name != "version").Select(child => child.Bounds.Center.Y).ToArray();
                    var versionCenter = measured.Single(child => child.Name == "version").Bounds.Center.Y;
                    var expectedVersionCenter = commonCenters.Average() + ProviderUsageCardWindow.TitleVersionOpticalOffsetY;
                    Assert(commonCenters.Max() - commonCenters.Min() <= 1.5 && Math.Abs(versionCenter - expectedVersionCenter) <= 1.5,
                        $"title row {providerId} at {scalePercent}% ({width}x{height}) {theme}: peer bounds share a center and version sits 4 logical px lower (peers {commonCenters.Max() - commonCenters.Min():0.00}, version expected {expectedVersionCenter:0.00}, got {versionCenter:0.00})");
                }

                window.ApplyMode(CardMode.Mini);
                window.MinWidth = CardLayoutTiers.MiniMinWidth * scale;
                window.MinHeight = CardLayoutTiers.MiniMinHeight * scale;
                Assert(!window.TitleVersionControl.IsVisible, $"mini title row {providerId} at {scalePercent}% {theme}: the version is hidden");
                foreach (var (width, height) in new[] { (CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight), (CardLayoutTiers.MiniMinWidth, CardLayoutTiers.MiniMinHeight) })
                {
                    window.Width = width * scale;
                    window.Height = height * scale;
                    using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"mini_title_row_{providerId}_{scalePercent}_{width}x{height}_{theme}_render_failed")) { }
                    Assert(window.ActualThemeVariant == theme,
                        $"mini title row {providerId} at {scalePercent}% ({width}x{height}): requested theme {theme} is active (got {window.ActualThemeVariant})");
                }
                window.ApplyMode(CardMode.Full);
            }
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>
    /// Window design §15.2: "less-than and greater-than little square buttons … next week gets
    /// disabled if there's no next week; and a third button that takes you to now"). Drives the Claude card's
    /// week buttons through the offsets and counts, checks each raises HistoryWindowStepRequested with its
    /// step, and checks the status line reads "Past week · {start} – {reset}" while browsing and the live
    /// status again at offset 0 and after a weekly rollover.
    /// </summary>
    private static void AssertWeekNavigation()
    {
        var now = DateTimeOffset.UtcNow;
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var interval = TimeSpan.FromMinutes(5);
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: false, _ => { });
        var steps = new List<int>();
        void OnStep(ProviderUsageCardWindow sender, int step) => steps.Add(step);
        window.HistoryWindowStepRequested += OnStep;
        try
        {
            var state = State(Snapshot(now, [Bucket("claude.short", QuotaBucketRole.Short, now)]));
            window.UpdateState(state);
            window.Show();
            var current = window.CurrentHistoryWindow ?? throw new InvalidOperationException("week_navigation_no_current_window");
            var liveStatus = LocalizedText.ProviderStatus(state) + " · " + LocalizedText.ProviderStatusDetails(state, state.Snapshot);
            string Status() => window.StatusLineControl.Inlines?.Text ?? string.Empty;
            void AssertButtons(bool previous, bool next, bool currentWeek, string when) =>
                Assert(window.PreviousWeekControl.IsEnabled == previous && window.NextWeekControl.IsEnabled == next &&
                       window.CurrentWeekControl.IsEnabled == currentWeek,
                    $"week buttons {when}: expected previous={previous} next={next} now={currentWeek}, got previous={window.PreviousWeekControl.IsEnabled} next={window.NextWeekControl.IsEnabled} now={window.CurrentWeekControl.IsEnabled}");

            Assert(steps.Count == 0, "the first reading of a weekly window is not a rollover and asks for nothing");
            AssertButtons(false, false, false, "with no stored windows");
            Assert(Status() == liveStatus, $"the status line starts live (got '{Status()}')");
            Assert(ToolTip.GetTip(window.PreviousWeekControl) as string == "Previous week" &&
                   ToolTip.GetTip(window.NextWeekControl) as string == "Next week" &&
                   ToolTip.GetTip(window.CurrentWeekControl) as string == "Back to the current week" &&
                   window.CurrentWeekControl.Content as string == "Now",
                "the week buttons carry Doug's tooltips and the Now label");

            Assert(window.SetHistory(current, [], interval, historyAvailable: true, windowCount: 1), "the live history applies at offset 0");
            AssertButtons(false, false, false, "with only the current window stored");
            window.SetHistory(current, [], interval, historyAvailable: true, windowCount: 3);
            AssertButtons(true, false, false, "at offset 0 of 3");

            window.PreviousWeekControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.NextWeekControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.CurrentWeekControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(steps.SequenceEqual([1, -1, 0]),
                $"previous, next and Now raise HistoryWindowStepRequested with +1, -1 and 0 (got {string.Join(",", steps)})");
            steps.Clear();

            var lastWeek = new UsageWindowIdentity(current.AccountScope, current.ProviderId, current.BucketId,
                current.NominalStartUtc.AddDays(-7), current.NominalStartUtc, current.SourceSemantics);
            var twoWeeksAgo = new UsageWindowIdentity(current.AccountScope, current.ProviderId, current.BucketId,
                current.NominalStartUtc.AddDays(-14), current.NominalStartUtc.AddDays(-7), current.SourceSemantics);
            string PastStatus(UsageWindowIdentity past) =>
                $"Past week · {past.NominalStartUtc.ToLocalTime().ToString("MMM d", english)} – {past.ResetAtUtc.ToLocalTime().AddDays(-1).ToString("MMM d", english)}";

            window.SetHistoryView(lastWeek, [new UsageObservation(lastWeek, lastWeek.NominalStartUtc.AddHours(1), 0.8)], interval, true, offset: 1, count: 3);
            AssertButtons(true, true, true, "at offset 1 of 3");
            Assert(Status() == PastStatus(lastWeek), $"while browsing, the status line reads '{PastStatus(lastWeek)}' (got '{Status()}')");
            Assert(window.HistoryWindowOffset == 1 && window.HistoryWindowCount == 3, "the card records the offset and count it shows");

            window.SetHistoryView(twoWeeksAgo, [], interval, true, offset: 2, count: 3);
            AssertButtons(false, true, true, "at offset 2 of 3 (the oldest)");
            Assert(Status() == PastStatus(twoWeeksAgo), $"the status line follows the shown week (got '{Status()}')");

            window.UpdateState(state);
            Assert(Status() == PastStatus(twoWeeksAgo), $"a live update keeps the past-week status while browsing (got '{Status()}')");
            Assert(!window.SetHistory(current, [], interval, historyAvailable: true, windowCount: 4),
                "the live history is ignored while an earlier week is shown");
            AssertButtons(true, true, true, "after the live path reports a fourth window while at offset 2");
            Assert(window.HistoryWindowOffset == 2 && window.HistoryWindowCount == 4, "the ignored live history still updates the count");

            window.SetHistoryView(current, [], interval, true, offset: 0, count: 4);
            AssertButtons(true, false, false, "back at offset 0 of 4");
            Assert(Status() == liveStatus, $"back at offset 0 the live status applies again (got '{Status()}')");

            // A weekly rollover while browsing: offset returns to 0, the live status applies, and the card asks
            // the coordinator to reload with step 0.
            window.SetHistoryView(lastWeek, [], interval, true, offset: 1, count: 4);
            Assert(steps.Count == 0, "showing a window raises nothing by itself");
            var rolledState = State(new AccountSnapshot(ProviderIds.Claude, "synthetic-shell-spec-scope", "Synthetic", now, now,
                "synthetic.shell.spec", [new QuotaBucket("weekly", QuotaBucketRole.Weekly, 0.9, TimeSpan.FromDays(7), now.AddDays(14), "synthetic.shell.spec")]));
            window.UpdateState(rolledState);
            var rolledLive = LocalizedText.ProviderStatus(rolledState) + " · " + LocalizedText.ProviderStatusDetails(rolledState, rolledState.Snapshot);
            Assert(steps.SequenceEqual([0]), $"a weekly rollover raises HistoryWindowStepRequested(0) once (got {string.Join(",", steps)})");
            Assert(window.HistoryWindowOffset == 0 && Status() == rolledLive,
                $"a weekly rollover returns to offset 0 and the live status (offset {window.HistoryWindowOffset}, status '{Status()}')");
            AssertButtons(true, false, false, "after a rollover with four stored windows");
        }
        finally
        {
            window.HistoryWindowStepRequested -= OnStep;
            window.CloseProgrammatically();
        }
    }

    /// <summary>
    /// Window design §17 rule 4: a card whose weekly window has not started (identity null, bucket
    /// known) asks for step 0 exactly once, browses the stored weeks with offset 0 as the newest one and the
    /// "Past week" line, ignores the live path while a stored week is on view, returns to the live view with a
    /// clean browse target when the window starts, and ends browsing when the weekly bucket goes away.
    /// </summary>
    private static void AssertBrowsingWithoutCurrentWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var interval = TimeSpan.FromMinutes(5);
        var window = new ProviderUsageCardWindow(ProviderIds.Codex, showMark: false, _ => { });
        var steps = new List<int>();
        void OnStep(ProviderUsageCardWindow sender, int step) => steps.Add(step);
        window.HistoryWindowStepRequested += OnStep;
        try
        {
            QuotaBucket NotStarted() => new("weekly", QuotaBucketRole.Weekly, 1.0, TimeSpan.FromDays(7), now.AddDays(7),
                "synthetic.shell.spec", windowStarted: false);
            AccountSnapshot IdleSnapshot() => new(ProviderIds.Codex, "synthetic-shell-spec-scope", "Synthetic", now, now,
                "synthetic.shell.spec", [NotStarted()]);
            string Status() => window.StatusLineControl.Inlines?.Text ?? string.Empty;
            string Live(ProviderDisplayState s) => LocalizedText.ProviderStatus(s) + " · " + LocalizedText.ProviderStatusDetails(s, s.Snapshot);
            void AssertButtons(bool previous, bool next, bool currentWeek, string when) =>
                Assert(window.PreviousWeekControl.IsEnabled == previous && window.NextWeekControl.IsEnabled == next &&
                       window.CurrentWeekControl.IsEnabled == currentWeek,
                    $"no-current-window buttons {when}: expected previous={previous} next={next} now={currentWeek}, got previous={window.PreviousWeekControl.IsEnabled} next={window.NextWeekControl.IsEnabled} now={window.CurrentWeekControl.IsEnabled}");

            var idle = State(IdleSnapshot());
            window.UpdateState(idle);
            window.Show();
            Assert(window.CurrentHistoryWindow is null, "a not-started weekly bucket has no current window identity");
            Assert(window.HistoryBucket == new HistoryBucketKey("synthetic-shell-spec-scope", ProviderIds.Codex, "weekly"),
                "the weekly bucket key is known while the window has not started");
            Assert(steps.SequenceEqual([0]), $"a not-started bucket asks for step 0 once (got {string.Join(",", steps)})");
            // A not-started window reads "No usage yet", never "window just started",
            // and its reset is stated as one duration after the first request.
            Assert(window.WeeklySummaryText == "No usage yet · the window starts with your first request",
                $"a not-started window says no usage yet (got '{window.WeeklySummaryText}')");
            Assert(window.WeeklyResetText == "Resets 7 days after first use",
                $"a not-started window's reset is one duration after first use (got '{window.WeeklyResetText}')");
            window.UpdateState(State(IdleSnapshot()));
            window.UpdateState(State(IdleSnapshot()));
            Assert(steps.SequenceEqual([0]), $"repeated not-started polls ask for nothing more (got {string.Join(",", steps)})");
            Assert(Status() == Live(idle), $"before any stored week arrives the status line is live (got '{Status()}')");

            var newest = new UsageWindowIdentity("synthetic-shell-spec-scope", ProviderIds.Codex, "weekly",
                now.AddDays(-8), now.AddDays(-1), "synthetic.shell.spec");
            var pastStatus = $"Past week · {newest.NominalStartUtc.ToLocalTime().ToString("MMM d", english)} – {newest.ResetAtUtc.ToLocalTime().AddDays(-1).ToString("MMM d", english)}";
            window.SetHistoryView(newest, [new UsageObservation(newest, newest.NominalStartUtc.AddHours(1), 0.8)], interval, true, offset: 0, count: 3);
            AssertButtons(true, false, false, "showing the newest stored week at offset 0");
            Assert(Status() == pastStatus, $"with no current window offset 0 reads as a past week (got '{Status()}')");
            Assert(!window.SetHistory(null, [], interval, historyAvailable: true), "the live path does not overwrite a stored week on view");
            Assert(Status() == pastStatus, "the live path leaves the past-week line alone");

            // Two quick ‹ clicks queue a target of 2 (the coordinator's bookkeeping), then the window starts.
            window.PreviousWeekControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.HistoryBrowsePendingOffset = 2;
            var sequenceBefore = window.HistoryBrowseSequence;
            steps.Clear();
            var started = State(Snapshot(now, [], providerId: ProviderIds.Codex));
            window.UpdateState(started);
            Assert(window.CurrentHistoryWindow is not null, "a started window has an identity again");
            Assert(steps.Count == 0, $"the window starting is loaded by the live path, not by a step request (got {string.Join(",", steps)})");
            Assert(window.HistoryWindowOffset == 0 && window.HistoryBrowsePendingOffset == 0 && window.HistoryBrowseSequence > sequenceBefore,
                $"the window starting resets the offset and the queued browse target (offset {window.HistoryWindowOffset}, pending {window.HistoryBrowsePendingOffset})");
            Assert(Status() == Live(started), $"the window starting restores the live status (got '{Status()}')");
            Assert(window.SetHistory(window.CurrentHistoryWindow, [], interval, historyAvailable: true, windowCount: 4), "the live history applies to the started window");
            AssertButtons(true, false, false, "at offset 0 of 4 after the window started");

            // Back to not started (a rollover with no usage yet), a stored week on view, then the bucket goes away.
            steps.Clear();
            window.UpdateState(State(IdleSnapshot()));
            Assert(steps.SequenceEqual([0]), $"a rollover to a not-started window asks for step 0 (got {string.Join(",", steps)})");
            window.SetHistoryView(newest, [], interval, true, offset: 0, count: 4);
            Assert(Status() == pastStatus, "the newest stored week shows again as a past week");
            steps.Clear();
            var noWeekly = State(new AccountSnapshot(ProviderIds.Codex, "synthetic-shell-spec-scope", "Synthetic", now, now,
                "synthetic.shell.spec", [Bucket("codex.short", QuotaBucketRole.Short, now)]));
            window.UpdateState(noWeekly);
            Assert(window.HistoryBucket is null && window.CurrentHistoryWindow is null, "no weekly bucket means no bucket key and no window");
            Assert(steps.SequenceEqual([0]), $"the weekly bucket going away ends browsing with a step 0 (got {string.Join(",", steps)})");
            Assert(Status() == Live(noWeekly), $"the weekly bucket going away restores the live status (got '{Status()}')");
            AssertButtons(false, false, false, "with no weekly bucket");
        }
        finally
        {
            window.HistoryWindowStepRequested -= OnStep;
            window.CloseProgrammatically();
        }
    }

    /// <summary>Claude CLI source design §2 rule 7: while the provider's CLI is signed out
    /// the credit strip shows the orange sign-in notice with the command to run; while the CLI is missing it
    /// shows the install notice; a Ready state with credits shows the green credit box and no notice.</summary>
    private static void AssertSignInNotice()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: false, _ => { });
        try
        {
            ProviderDisplayState Failed(ProviderStatus status, string code) => new(
                ProviderIds.Claude, 1, true, false, status, null, SnapshotFreshness.Fresh, null, [], code, TimeSpan.Zero, now);

            window.UpdateState(Failed(ProviderStatus.NeedsSignIn, "claude_signed_out"));
            window.Show();
            Assert(window.NoticeVisible && !window.CreditBoxVisible, "a signed-out CLI shows the notice and no credit box");
            Assert(window.NoticeText == "SIGN IN REQUIRED Run claude and use /login.",
                $"the Claude sign-in notice names the command (got '{window.NoticeText}')");
            Assert(window.IsCreditStripVisuallyPainted, "the notice strip is filled (orange), not transparent");
            // The notice must fit the 70 px strip without clipping its descenders.
            void AssertNoticeFits(string when)
            {
                window.UpdateLayout();
                var strip = window.NoticeContentControl.Parent?.Parent as Border
                    ?? throw new InvalidOperationException("the notice content is not hosted in the strip Border");
                var stripBounds = window.BoundsWithinCard(strip);
                var last = window.NoticeContentControl.GetVisualChildren().OfType<Control>().Where(child => child.IsVisible)
                    .Select(child => window.BoundsWithinCard(child)).Max(rect => rect.Bottom);
                Assert(last <= stripBounds.Bottom - strip.Padding.Bottom + 0.5,
                    $"{when}: the notice's last line ends at {last:0.0}, inside the strip's bottom less its padding ({stripBounds.Bottom - strip.Padding.Bottom:0.0})");
            }
            AssertNoticeFits("Claude sign-in notice");

            window.UpdateState(Failed(ProviderStatus.AuthenticationExpired, "claude_expired"));
            Assert(window.NoticeVisible, "an expired login shows the same notice");

            window.UpdateState(Failed(ProviderStatus.MissingApplication, "claude_cli_missing"));
            Assert(window.NoticeText == "CLI NOT FOUND Install Claude Code.",
                $"a missing CLI shows the install notice (got '{window.NoticeText}')");
            AssertNoticeFits("Claude install notice");

            window.UpdateState(State(Snapshot(now, [], Credits(1, now))));
            Assert(!window.NoticeVisible && window.CreditBoxVisible, "Ready with a credit shows the green box and no notice");

            window.UpdateState(Failed(ProviderStatus.Offline, "claude_cli_timeout"));
            Assert(!window.NoticeVisible && !window.CreditBoxVisible && !window.IsCreditStripVisuallyPainted,
                "any other failure leaves the strip empty");
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>Window design §15.5: "Plan type" doesn't fit on the heading line): the plan is
    /// its own 16 px line under WEEKLY GLIDESLOPE, reading exactly "Plan type: max", and hidden with no plan.</summary>
    private static void AssertPlanTypeLine()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: false, _ => { });
        try
        {
            window.UpdateState(State(PlanSnapshot(now, "max")));
            window.Show();
            using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException("plan_type_render_failed")) { }
            var plan = window.PlanTypeControl;
            Assert(plan.IsEffectivelyVisible && plan.Text == "Plan type: max", $"with plan 'max' the second heading line reads 'Plan type: max' (got '{plan.Text}', visible {plan.IsEffectivelyVisible})");
            Assert(plan.FontSize == ProviderUsageCardWindow.CardTextFloor && plan.TextWrapping == Avalonia.Media.TextWrapping.Wrap,
                "the plan line is floor-size text that wraps");
            window.UpdateState(State(PlanSnapshot(now, null)));
            Assert(!plan.IsVisible, "with no plan the plan line is hidden");
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>
    /// Window design §15.6: "you're not showing my weekly reset"): the Fable heading names its
    /// reset when it is two days from the weekly reset, and not when the two are a minute apart. The card with
    /// the longer heading still passes the size matrix, since the text
    /// stays on the heading line.
    /// </summary>
    private static void AssertFableReset()
    {
        var now = DateTimeOffset.UtcNow;
        var weeklyReset = now.AddDays(5);
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        try
        {
            var twoDaysApart = weeklyReset.AddDays(2);
            window.UpdateState(State(FableSnapshot(now, weeklyReset, twoDaysApart)));
            var expected = "FABLE THIS WEEK · Resets " +
                twoDaysApart.ToLocalTime().ToString("ddd, MMM d · h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
            Assert(window.FableHeadingControl.Text == expected,
                $"a Fable reset two days from the weekly one shows on the heading ('{expected}', got '{window.FableHeadingControl.Text}')");
            window.Show();
            AssertLayoutAtEverySize(window, $"{ProviderIds.Claude} fable-reset");

            window.UpdateState(State(FableSnapshot(now, weeklyReset, weeklyReset.AddMinutes(-1))));
            Assert(window.FableHeadingControl.Text == "FABLE THIS WEEK",
                $"a Fable reset one minute from the weekly one is the same reset and is not shown (got '{window.FableHeadingControl.Text}')");
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    /// <summary>
    /// Window design §5.6 and Appendix B.8: a button appears when the pointer hovers over
    /// the center of one of the windows where it's docked"). On the Claude card at the default 940x680,
    /// the Undock button shows only near the middle of a docked side, sits on that side, raises
    /// UndockRequested with that side, hides when the side stops being docked, and never starts a drag.
    /// </summary>
    private static void AssertUndockButton(ProviderUsageCardWindow window)
    {
        window.Width = ProviderUsageCardWindow.DefaultCardWidth;
        window.Height = ProviderUsageCardWindow.DefaultCardHeight;
        using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException("undock_default_size_render_failed")) { }
        var client = window.ClientSize;
        var undock = window.UndockControl;
        Assert(!undock.IsVisible && window.CurrentUndockSide is null, "the Undock button is hidden by default");

        window.SetDockedSides(new HashSet<CardDockSide> { CardDockSide.Right });
        window.UpdateUndockButton(new Point(client.Width - 10, client.Height / 2));
        Assert(undock.IsVisible && window.CurrentUndockSide == CardDockSide.Right,
            "hovering near the middle of the docked right edge shows Undock on the right");
        using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException("undock_shown_render_failed")) { }
        var bounds = undock.Bounds;   // relative to the drag surface, which is the client area
        Assert(client.Width - bounds.Right <= 12.5,
            $"the Undock button sits within 12.5 px of the right edge (right={bounds.Right:0.0}, client width={client.Width:0.0})");
        Assert(Math.Abs(bounds.Center.Y - client.Height / 2) <= 1,
            $"the Undock button is vertically centered on the right edge (center y={bounds.Center.Y:0.0}, half height={client.Height / 2:0.0})");

        window.UpdateUndockButton(new Point(client.Width / 2, client.Height / 2));
        Assert(!undock.IsVisible && window.CurrentUndockSide is null, "the middle of the card hides Undock");
        window.UpdateUndockButton(new Point(client.Width - 10, 20));
        Assert(!undock.IsVisible, "near the docked edge but outside its middle 50% hides Undock");
        window.UpdateUndockButton(new Point(10, client.Height / 2));
        Assert(!undock.IsVisible, "the middle of an edge that is not docked hides Undock");

        CardDockSide? requested = null;
        void OnUndock(ProviderUsageCardWindow sender, CardDockSide side) => requested = side;
        window.UndockRequested += OnUndock;
        try
        {
            window.UpdateUndockButton(new Point(client.Width - 10, client.Height / 2));
            undock.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(requested == CardDockSide.Right, $"clicking Undock raises UndockRequested with the shown side (got {requested?.ToString() ?? "nothing"})");
        }
        finally
        {
            window.UndockRequested -= OnUndock;
        }

        window.SetDockedSides(new HashSet<CardDockSide>());
        Assert(!undock.IsVisible && window.CurrentUndockSide is null, "a side that stops being docked hides its Undock button");
        Assert(!window.CanBeginMoveDragFrom(undock), "a press on Undock never starts a card drag");
    }

    /// <summary>
    /// Codex's populated green credit box must not cover any resize grip. Verify for every provider
    /// that all eight grips are
    /// present, mapped to the correct cursor, and are the topmost hit at their own center: nothing,
    /// including a populated credit box, sits above them.
    /// </summary>
    private static void AssertGripsAreTopmostForEveryProviderType()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var providerId in new[] { ProviderIds.Codex, ProviderIds.Claude, ProviderIds.Gemini })
        {
            var window = new ProviderUsageCardWindow(providerId, showMark: true, _ => { });
            try
            {
                var credits = providerId == ProviderIds.Codex ? Credits(3, now) : null;
                window.UpdateState(State(Snapshot(now,
                [
                    Bucket("short", QuotaBucketRole.Short, now),
                    Bucket("fable", QuotaBucketRole.FeaturedWeekly, now)
                ], credits, providerId)));
                if (providerId == ProviderIds.Codex)
                    Assert(window.IsCreditInventoryVisible && window.CreditCountText == "3",
                        "Codex carries a real, populated credit inventory for this proof, not an empty reserved strip");
                window.Show();
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"{providerId}_grip_render_failed");

                Assert(window.ResizeGripControls.Count == 8, $"{providerId}: every card type gets all eight resize grips");
                foreach (var (grip, edge) in window.ResizeGripEdges)
                {
                    Assert(ReferenceEquals(grip.Cursor, ProviderUsageCardWindow.CursorForEdge(edge)),
                        $"{providerId}: the {edge} grip shows its mapped cursor");
                    Assert(grip.ZIndex == ProviderUsageCardWindow.GripZIndex,
                        $"{providerId}: the {edge} grip sits at the explicit grip z-order floor");

                    var center = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)
                        ?? throw new InvalidOperationException($"{providerId}_grip_not_connected_{edge}");
                    var topmost = window.GetVisualsAt(center).FirstOrDefault();
                    Assert(ReferenceEquals(topmost, grip),
                        $"{providerId}: the {edge} grip is the topmost hit at its own center (found {topmost?.GetType().Name ?? "nothing"} instead)");
                }
            }
            finally
            {
                window.CloseProgrammatically();
            }
        }
    }
}

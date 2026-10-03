using Avalonia;
using System.Globalization;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>Exercises the production App/Core geometry adapter against deterministic fake monitors and native windows.</summary>
internal static class CardLayoutPlatformProof
{
    public static void Run()
    {
        using var english = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-US"));
        var codex = new FakeCardWindow(ProviderCatalog.Codex, 940, 663, new PixelPoint(120, 100), "primary");
        var claude = new FakeCardWindow(ProviderCatalog.Claude, 940, 663, new PixelPoint(2070, 90), "secondary");
        var backend = new FakeGeometryBackend();
        var platform = new CardLayoutPlatform(backend);
        IReadOnlyDictionary<string, ICardLayoutWindow> windows = new Dictionary<string, ICardLayoutWindow>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = codex,
            [ProviderCatalog.Claude] = claude,
        };

        var captured = platform.Capture(windows);
        var expectedClaude = new LogicalRect(2020, 60, 940, 663);
        Assert(captured.Rects[ProviderCatalog.Claude] == expectedClaude,
            "capture converts the secondary monitor's physical origin and 150% scale into logical desktop coordinates");
        Assert(captured.MonitorByProvider[ProviderCatalog.Claude] == "secondary",
            "capture retains the native monitor identity for each provider card");

        var proposed = expectedClaude.Translate(30, 20);
        var settings = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, MonitorId = "primary" },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, MonitorId = "secondary" },
            ]
        };
        platform.Apply(windows, settings,
            new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
            {
                [ProviderCatalog.Codex] = captured.Rects[ProviderCatalog.Codex],
                [ProviderCatalog.Claude] = proposed,
            }, captured);
        Assert(claude.Position == new PixelPoint(2115, 120),
            "apply returns proposed logical coordinates to the correct monitor-local physical pixels");
        Assert(claude.Size == new LogicalSize(940, 663),
            "apply keeps window size in logical units while converting only native position");

        // a rectangle is converted with the monitor its center is on. Claude is saved on the
        // primary (100%) but has been carried into the secondary (150%, logical origin 1920): converting with the saved
        // monitor would have put it at physical (2000, 100) instead of (1920 + 80 × 1.5, 100 × 1.5).
        var carried = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, MonitorId = "primary" }] };
        platform.Apply(windows, carried,
            new Dictionary<string, LogicalRect>(StringComparer.Ordinal) { [ProviderCatalog.Claude] = new(2000, 100, 940, 663) }, captured);
        Assert(claude.Position == new PixelPoint(2040, 150),
            $"a card carried past a monitor boundary is placed with that monitor's origin and scale (was {claude.Position})");
        platform.Apply(windows, carried,
            new Dictionary<string, LogicalRect>(StringComparer.Ordinal) { [ProviderCatalog.Claude] = new(-2000, 100, 940, 663) }, captured);
        Assert(claude.Position == new PixelPoint(-2000, 100),
            $"a rectangle on no monitor still uses the saved monitor (was {claude.Position})");

        NoReportedPrimaryInfersTheFirstScreen();
        CascadePlacementUsesLogicalWorkAreas();
        DisplayRevalidationDecision();
        CardScaleSizesAndScaleAndRecover();
        DockModeChangesAndMixedModeGroups();
    }

    /// <summary>2.0 mini mode (design doc §5.6, §5.7, §8.1 "Shell suite, pure statics: DockModeChanges;
    /// MixedModeGroups"): DockModeChanges reports only the incoming ids that actually disagree with the
    /// destination's mode (empty when the incoming group is already that mode); MixedModeGroups finds a group
    /// whose members disagree, and reports none for a layout where every group agrees.</summary>
    private static void DockModeChangesAndMixedModeGroups()
    {
        var modes = new Dictionary<string, CardMode>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = CardMode.Mini,
            [ProviderCatalog.Claude] = CardMode.Mini,
            [ProviderCatalog.Gemini] = CardMode.Full,
        };
        CardMode ModeOf(string providerId) => modes[providerId];

        var changesOntoFull = WindowCoordinator.DockModeChanges(
            [ProviderCatalog.Codex, ProviderCatalog.Claude], CardMode.Full, ModeOf);
        Assert(changesOntoFull.Count == 2 && changesOntoFull[ProviderCatalog.Codex] == CardMode.Full &&
               changesOntoFull[ProviderCatalog.Claude] == CardMode.Full,
            "an incoming mini group dropped onto a full destination changes both incoming cards to full");

        var changesOntoSameMode = WindowCoordinator.DockModeChanges(
            [ProviderCatalog.Codex, ProviderCatalog.Claude], CardMode.Mini, ModeOf);
        Assert(changesOntoSameMode.Count == 0,
            "an incoming group already at the destination's mode needs no mode change at all");

        var layout = new LayoutSettings
        {
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        string[] enabled = [ProviderCatalog.Codex, ProviderCatalog.Claude, ProviderCatalog.Gemini];
        Assert(WindowCoordinator.MixedModeGroups(layout, enabled, ModeOf).Count == 0,
            "a docked group that agrees on mode, and a detached single card, are never reported as mixed");

        // The defect case DockModeChanges and §5.6 exist to prevent: a dock committed with no mode switch at all.
        modes[ProviderCatalog.Claude] = CardMode.Full;
        var mixed = WindowCoordinator.MixedModeGroups(layout, enabled, ModeOf);
        Assert(mixed.Count == 1 && mixed[0].OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(new[] { ProviderCatalog.Claude, ProviderCatalog.Codex }.OrderBy(id => id, StringComparer.Ordinal)),
            $"a docked group whose members disagree on mode is reported (found {mixed.Count})");
    }

    /// <summary>When no screen reports IsPrimary, the first screen is inferred as primary and its work area is marked.</summary>
    private static void NoReportedPrimaryInfersTheFirstScreen()
    {
        var backend = new FakeGeometryBackend(
        [
            new("left", new PhysicalRect(0, 0, 1920, 1080), new PhysicalRect(0, 0, 1920, 1040), 1, false),
            new("right", new PhysicalRect(1920, 0, 1920, 1080), new PhysicalRect(1920, 0, 1920, 1040), 1, false),
        ]);
        var codex = new FakeCardWindow(ProviderCatalog.Codex, 940, 680, new PixelPoint(100, 100), "left");
        var captured = new CardLayoutPlatform(backend).Capture(
            new Dictionary<string, ICardLayoutWindow>(StringComparer.Ordinal) { [ProviderCatalog.Codex] = codex });
        Assert(captured.PrimaryMonitorId == "left" && captured.PrimaryInferred, "the first screen is taken as primary, and that is reported");
        Assert(captured.Monitors["left"].WorkArea.IsPrimary && !captured.Monitors["right"].WorkArea.IsPrimary,
            "exactly the chosen screen's work area is marked primary");
        var restored = LayoutPolicy.RecoverMissingMonitor(new LayoutSettings(), [ProviderCatalog.Codex],
            captured.Monitors.ToDictionary(pair => pair.Key, pair => pair.Value.WorkArea, StringComparer.Ordinal),
            captured.PrimaryMonitorId, new LogicalSize(729, 679));
        Assert(restored.Succeeded, $"a restore succeeds with no reported primary (was {restored.SafeErrorCode})");
    }

    /// <summary>New cards cascade in logical units inside the primary work area.</summary>
    private static void CascadePlacementUsesLogicalWorkAreas()
    {
        var card = new LogicalSize(940, 680);
        // 1920 × 1080 at 150% with a 48 px taskbar: a 1280 × 688 logical work area.
        var at150 = new LogicalRect(0, 0, 1280, 688);
        AssertCascade(at150, card, 0, 170, 4, "the first card is centered");
        AssertCascade(at150, card, 1, 218, 8, "the second card is 48 px right and as far down as fits");
        AssertCascade(at150, card, 2, 266, 8, "the third card is 96 px right, still fully inside");
        // 1366 × 768 at 100% with a 40 px taskbar.
        var small = new LogicalRect(0, 0, 1366, 728);
        AssertCascade(small, card, 0, 213, 24, "centered on a small screen");
        AssertCascade(small, card, 1, 261, 48, "offset, not on top of the first card");
        AssertCascade(small, card, 2, 309, 48, "offset again, still inside");
        // A work area shorter than the card (1080p at 150% with a 72 px taskbar): the card starts at the top.
        AssertCascade(new LogicalRect(0, 0, 1280, 672), card, 1, 218, 0, "a card taller than the work area starts at its top");
    }

    private static void AssertCascade(LogicalRect area, LogicalSize size, int index, double x, double y, string message)
    {
        var rect = WindowCoordinator.CascadeRect(area, size, index);
        Assert(rect.X == x && rect.Y == y && rect.Size == size, $"{message} (expected {x},{y}; got {rect.X},{rect.Y})");
    }

    /// <summary>The display handler restores the layout when a saved monitor is gone or the monitor configuration changes.</summary>
    private static void DisplayRevalidationDecision()
    {
        var primary = LayoutSpecGeometry.Monitor("primary", 0, 0, 1920, 1080, 1040, isPrimary: true);
        var secondary = LayoutSpecGeometry.Monitor("secondary", 1920, 0, 1920, 1080, 1040, isPrimary: false);
        var rects = LayoutSpecGeometry.Rects((ProviderCatalog.Codex, new LogicalRect(100, 100, 940, 680)));
        var both = LayoutSpecGeometry.Snapshot(rects, primary, secondary);
        var onlyPrimary = LayoutSpecGeometry.Snapshot(rects, primary);
        var onSecondary = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, MonitorId = "secondary" }] };
        var onPrimary = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, MonitorId = "primary" }] };
        Assert(WindowCoordinator.DecideDisplayRevalidation(onSecondary, both.Monitors, onlyPrimary) == WindowCoordinator.DisplayLayoutDecision.MonitorRemoved,
            "a card saved on a monitor that is gone is recovered");
        Assert(WindowCoordinator.DecideDisplayRevalidation(onPrimary, both.Monitors, onlyPrimary) == WindowCoordinator.DisplayLayoutDecision.MonitorsChanged,
            "a changed monitor set is recovered even when no card was on the removed monitor");
        Assert(WindowCoordinator.DecideDisplayRevalidation(onPrimary, both.Monitors, both) == WindowCoordinator.DisplayLayoutDecision.Unchanged,
            "the same monitors change nothing");
        var shorter = LayoutSpecGeometry.Snapshot(rects, LayoutSpecGeometry.Monitor("primary", 0, 0, 1920, 1080, 1000, isPrimary: true), secondary);
        Assert(WindowCoordinator.DecideDisplayRevalidation(onPrimary, both.Monitors, shorter) == WindowCoordinator.DisplayLayoutDecision.MonitorsChanged,
            "a changed work area (a taskbar moved or resized) is recovered");
        Assert(WindowCoordinator.DecideDisplayRevalidation(onPrimary, null, both) == WindowCoordinator.DisplayLayoutDecision.MonitorsChanged,
            "with no monitors recorded the layout is revalidated");
    }

    /// <summary>Checks scaled sizes and scale changes: docked groups resize together and remain reachable.</summary>
    private static void CardScaleSizesAndScaleAndRecover()
    {
        Assert(CardScale.Normalize(104) == 100 && CardScale.Normalize(105) == 110 && CardScale.Normalize(60) == 70 &&
               CardScale.Normalize(155) == 150 && CardScale.Normalize(70) == 70 && CardScale.Normalize(150) == 150,
            "a requested scale is clamped to 70–150 and rounded to the 10% steps");
        var englishMinimumHeight = LocalizedText.CardMinimumHeight;
        Assert(CardScale.MinimumSize(CardMode.Full, 100) == new LogicalSize(790, englishMinimumHeight) &&
               CardScale.MinimumSize(CardMode.Full, 70) == new LogicalSize(553, englishMinimumHeight * 70 / 100d) &&
               CardScale.MinimumSize(CardMode.Full, 150) == new LogicalSize(1185, englishMinimumHeight * 150 / 100d),
            "the minimum uses the localized resource height at each scale");
        Assert(CardScale.DefaultSize(CardMode.Full, 120) ==
               new LogicalSize(1128, Math.Max(ProviderUsageCardWindow.DefaultCardHeight, englishMinimumHeight) * 120 / 100d),
            "the ↺ default uses the greater of the design default and localized minimum height");
        Assert(CardScale.UsableMinimum(CardMode.Full, 100) == new LogicalSize(789, englishMinimumHeight - 1),
            "the policy minimum keeps design doc H.3's one-pixel slack");
        using (LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("qps-ploc"), CultureInfo.GetCultureInfo("en-US"),
                   new LocalizationResourceProof.TallerCardMinimumResources()))
        {
            Assert(CardScale.MinimumSize(CardMode.Full, 100) == new LogicalSize(790, 790) &&
                   CardScale.DefaultSize(CardMode.Full, 120) == new LogicalSize(1128, 948),
                "the localized full-card minimum flows through scaled minimum and default sizes");

            var saved = new LayoutSettings
            {
                Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680 }],
            };
            var recoveryArea = new LogicalWorkArea("primary", new LogicalRect(0, 0, 1920, 1000), isPrimary: true);
            var localizedMinimum = CardScale.UsableMinimum(CardMode.Full, 100);
            var sizedForLocale = LayoutPolicy.EnsureMinimumSizes(saved, localizedMinimum);
            var recovered = LayoutPolicy.RecoverMissingMonitor(sizedForLocale, [ProviderCatalog.Codex],
                new Dictionary<string, LogicalWorkArea>(StringComparer.Ordinal) { ["primary"] = recoveryArea },
                "primary", localizedMinimum);
            Assert(recovered.Succeeded && recovered.ProposedRects[ProviderCatalog.Codex].Height >= localizedMinimum.Height &&
                   recovered.ProposedSettings!.Cards.Single(card => card.ProviderId == ProviderCatalog.Codex).Height >= localizedMinimum.Height,
                "restoring an English-sized saved full card after a locale change clamps it to the localized usable minimum");
        }

        var monitor = LayoutSpecGeometry.Monitor("primary", 0, 0, 1920, 1080, 1040, isPrimary: true);
        var layout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 700, Height = 680, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 700, Height = 680, GroupOrder = 1 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Gemini, Width = 940, Height = 680, GroupOrder = 2 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var start = LayoutSpecGeometry.Snapshot(LayoutSpecGeometry.Rects(
            (ProviderCatalog.Codex, new LogicalRect(100, 100, 700, 680)),
            (ProviderCatalog.Claude, new LogicalRect(810, 100, 700, 680)),
            (ProviderCatalog.Gemini, new LogicalRect(900, 300, 940, 680))), monitor);
        string[] enabled = [ProviderCatalog.Codex, ProviderCatalog.Claude, ProviderCatalog.Gemini];
        // 2.0 mini mode: this scale run touches every card, all in Full mode, so onlyProviders is every enabled id
        // and every card shares the one usable minimum, exactly as the pre-2.0 single-minimum call did.
        var allFull = enabled.ToHashSet(StringComparer.Ordinal);

        var smaller = WindowCoordinator.ScaleAndRecover(layout, enabled, start, 0.8, CardMode.Full, allFull,
            CardScale.MinimumSize(CardMode.Full, 80), UniformMinimum(enabled, CardMode.Full, 80));
        Assert(smaller.Succeeded && smaller.ProposedSettings!.Edges.Count == 1, "scaling down keeps the group docked");
        var scaledHeightAt80 = Math.Max(680 * 0.8, CardScale.MinimumSize(CardMode.Full, 80).Height);
        AssertNear(smaller.ProposedRects[ProviderCatalog.Codex], 100, 100, 632, scaledHeightAt80, "the group keeps its top-left at 80%");
        AssertNear(smaller.ProposedRects[ProviderCatalog.Claude], 742, 100, 632, scaledHeightAt80, "the docked card follows with the same 10 px gap");
        AssertNear(smaller.ProposedRects[ProviderCatalog.Gemini], 900, 300, 752, scaledHeightAt80, "a detached card keeps its top-left");

        var larger = WindowCoordinator.ScaleAndRecover(layout, enabled, start, 1.5, CardMode.Full, allFull,
            CardScale.MinimumSize(CardMode.Full, 150), UniformMinimum(enabled, CardMode.Full, 150));
        Assert(larger.Succeeded, $"scaling up past what fits still succeeds (was {larger.SafeErrorCode})");
        Assert(larger.ProposedSettings!.Edges.Count == 0,
            "a docked pair that no longer fits the monitor at 150% even at its minimum is split, so each title row stays on screen");
        foreach (var rect in larger.ProposedRects.Values)
            Assert(LayoutPolicy.IsReachablePlacement(monitor.WorkArea.Bounds, [rect]),
                $"every card is reachable after scaling up ({rect.X},{rect.Y} {rect.Width}x{rect.Height})");

        ScaleAndRecoverScopesByModeAndRescalesReturnSizes();
    }

    private static Dictionary<string, LogicalSize> UniformMinimum(IEnumerable<string> providerIds, CardMode mode, int percent) =>
        providerIds.ToDictionary(id => id, _ => CardScale.UsableMinimum(mode, percent), StringComparer.Ordinal);

    /// <summary>2.0 mini mode (design doc §5.8, §8.1 "Shell suite, pure statics: ScaleAndRecover with a mode"):
    /// scaling the full cards only (onlyProviders) leaves a mini card's own rect untouched and re-scales its
    /// remembered full size by the same ratio (LayoutPolicy.ScaleReturnSizes), and a docked group only partly in
    /// onlyProviders is rejected rather than half-scaled.</summary>
    private static void ScaleAndRecoverScopesByModeAndRescalesReturnSizes()
    {
        var monitor = LayoutSpecGeometry.Monitor("primary", 0, 0, 1920, 1080, 1040, isPrimary: true);
        var layout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 700, Height = 680, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 700, Height = 680, GroupOrder = 1 },
                // Gemini is mini: its live rect is the mini size, and it remembers a full size to return to.
                new CardLayoutSettings
                {
                    ProviderId = ProviderCatalog.Gemini, Width = 530, Height = 270,
                    FullWidth = 940, FullHeight = 680, GroupOrder = 2,
                },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var start = LayoutSpecGeometry.Snapshot(LayoutSpecGeometry.Rects(
            (ProviderCatalog.Codex, new LogicalRect(100, 100, 700, 680)),
            (ProviderCatalog.Claude, new LogicalRect(810, 100, 700, 680)),
            (ProviderCatalog.Gemini, new LogicalRect(200, 700, 530, 270))), monitor);
        string[] enabled = [ProviderCatalog.Codex, ProviderCatalog.Claude, ProviderCatalog.Gemini];
        var fullProviders = new HashSet<string>(StringComparer.Ordinal) { ProviderCatalog.Codex, ProviderCatalog.Claude };
        var usableMinimumByProvider = new Dictionary<string, LogicalSize>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = CardScale.UsableMinimum(CardMode.Full, 120),
            [ProviderCatalog.Claude] = CardScale.UsableMinimum(CardMode.Full, 120),
            [ProviderCatalog.Gemini] = CardScale.UsableMinimum(CardMode.Mini, 100),
        };

        var result = WindowCoordinator.ScaleAndRecover(layout, enabled, start, 1.2, CardMode.Full, fullProviders,
            CardScale.MinimumSize(CardMode.Full, 120), usableMinimumByProvider);
        Assert(result.Succeeded, $"scaling only the full cards succeeds (was {result.SafeErrorCode})");
        // 700 x 680 at a 1.2 ratio is 840 x 816. The width and height are raised independently to the full
        // minimum at 120%, which is based on the active localized resource.
        // The scaled pair is 1906 px wide, so recovery moves it from x=100 to x=14 to keep it within the 1920 px work area.
        var scaledFullMinimum = CardScale.MinimumSize(CardMode.Full, 120);
        AssertNear(result.ProposedRects[ProviderCatalog.Codex], 14, 100,
            scaledFullMinimum.Width, scaledFullMinimum.Height,
            "the full group is scaled by the ratio, raised to the localized minimum, and kept on screen");
        Assert(result.ProposedRects[ProviderCatalog.Gemini] == new LogicalRect(200, 700, 530, 270),
            $"a mini card outside onlyProviders keeps its own rect untouched (got {result.ProposedRects[ProviderCatalog.Gemini]})");
        var geminiCard = result.ProposedSettings!.Cards.Single(card => card.ProviderId == ProviderCatalog.Gemini);
        var expectedReturnWidth = Math.Max(940 * 1.2, CardScale.MinimumSize(CardMode.Full, 120).Width);
        var expectedReturnHeight = Math.Max(680 * 1.2, CardScale.MinimumSize(CardMode.Full, 120).Height);
        Assert(Math.Abs(geminiCard.FullWidth - expectedReturnWidth) < 1e-6 && Math.Abs(geminiCard.FullHeight - expectedReturnHeight) < 1e-6,
            $"a mini card's remembered full size zooms with the full cards ({geminiCard.FullWidth}x{geminiCard.FullHeight}, wanted {expectedReturnWidth}x{expectedReturnHeight})");

        var partialGroup = new HashSet<string>(StringComparer.Ordinal) { ProviderCatalog.Codex };
        var rejected = WindowCoordinator.ScaleAndRecover(layout, enabled, start, 1.2, CardMode.Full, partialGroup,
            CardScale.MinimumSize(CardMode.Full, 120), usableMinimumByProvider);
        Assert(!rejected.Succeeded && rejected.SafeErrorCode == "layout_group_partially_scaled",
            "a docked group only partly in onlyProviders is rejected, since a mixed-mode group must never exist");
    }

    private static void AssertNear(LogicalRect actual, double x, double y, double width, double height, string message) =>
        Assert(Math.Abs(actual.X - x) < 1e-6 && Math.Abs(actual.Y - y) < 1e-6 &&
               Math.Abs(actual.Width - width) < 1e-6 && Math.Abs(actual.Height - height) < 1e-6,
            $"{message} (expected {x},{y} {width}x{height}; got {actual.X},{actual.Y} {actual.Width}x{actual.Height})");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Card layout platform proof failed: {message}.");
    }

    private sealed class FakeCardWindow(string providerId, double width, double height, PixelPoint position, string monitorId) : ICardLayoutWindow
    {
        public string ProviderId { get; } = providerId;
        public object NativeHandle => this;
        public double Width { get; private set; } = width;
        public double Height { get; private set; } = height;
        public LogicalSize Size => new(Width, Height);
        public PixelPoint Position { get; set; } = position;
        public string MonitorId { get; } = monitorId;
        public void SetSize(LogicalSize size) { Width = size.Width; Height = size.Height; }
    }

    private sealed class FakeGeometryBackend(IReadOnlyList<CardLayoutScreen>? screens = null) : ICardWindowGeometryBackend
    {
        private static readonly IReadOnlyList<CardLayoutScreen> DefaultScreens =
        [
            new("primary", new PhysicalRect(0, 0, 1920, 1080), new PhysicalRect(0, 0, 1920, 1040), 1, true),
            new("secondary", new PhysicalRect(1920, 0, 2560, 1440), new PhysicalRect(1920, 0, 2560, 1400), 1.5, false),
        ];

        // a spec can pass its own screens (for example, none reporting IsPrimary).
        private readonly IReadOnlyList<CardLayoutScreen> _screens = screens ?? DefaultScreens;

        public IReadOnlyList<CardLayoutScreen> GetScreens(ICardLayoutWindow window) => _screens;
        public string? GetMonitorId(ICardLayoutWindow window, IReadOnlyList<CardLayoutScreen> screens) =>
            window is FakeCardWindow fake ? fake.MonitorId : null;
        public PixelPoint GetPosition(ICardLayoutWindow window) => ((FakeCardWindow)window).Position;
        public LogicalSize GetSize(ICardLayoutWindow window) => ((FakeCardWindow)window).Size;
        public void SetPosition(ICardLayoutWindow window, PixelPoint position) => ((FakeCardWindow)window).Position = position;
        public void SetSize(ICardLayoutWindow window, LogicalSize size) => ((FakeCardWindow)window).SetSize(size);
    }
}

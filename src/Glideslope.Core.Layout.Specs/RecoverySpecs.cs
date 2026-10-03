using Glideslope.Core;

namespace Glideslope.Core.Layout.Specs;

// Layout specs for LayoutPolicy.Recovery.cs: anchor capture, restore into the current monitors,
// the reachable-placement rule, the disabled-provider reconcile, a missing monitor and the reset
// recovery. Main in Program.cs runs them in its fixed order.
internal static partial class Program
{
    /// <summary>Capture clamps anchors for a card partly outside its work area while preserving other cards'
    /// exact anchors; restore brings the outside card fully into view.</summary>
    private static void CaptureKeepsAPartlyOutsideCardAndRestoresItInside()
    {
        var enabled = new[] { ProviderCatalog.Codex, ProviderCatalog.Claude };
        var area = new LogicalWorkArea("primary", new LogicalRect(0, 0, 1920, 1040), isPrimary: true);
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(1500, 900, 940, 680),
            [ProviderCatalog.Claude] = new(100, 100, 940, 680),
        };
        var areas = enabled.ToDictionary(id => id, _ => area, StringComparer.Ordinal);
        var captured = LayoutPolicy.CaptureAnchors(new LayoutSettings(), enabled, rects, areas);
        Assert(captured.Succeeded, "a card partly outside its work area no longer rejects the whole capture");
        var codex = captured.ProposedSettings!.Cards.Single(card => card.ProviderId == ProviderCatalog.Codex);
        var claude = captured.ProposedSettings.Cards.Single(card => card.ProviderId == ProviderCatalog.Claude);
        Assert(codex.AnchorX == 1 && codex.AnchorY == 1, "the outside card's anchors are clamped to the work area's far edges");
        Assert(Math.Abs(claude.AnchorX - 100d / 980d) < 1e-12 && Math.Abs(claude.AnchorY - 100d / 360d) < 1e-12,
            "a card inside its work area keeps its exact anchors");

        var restored = LayoutPolicy.RecoverMissingMonitor(captured.ProposedSettings, enabled,
            new Dictionary<string, LogicalWorkArea> { [area.MonitorId] = area }, area.MonitorId, Minimum);
        Assert(restored.Succeeded && restored.ProposedRects[ProviderCatalog.Codex].Right == 1920 &&
               restored.ProposedRects[ProviderCatalog.Codex].Bottom == 1040,
            "the restore brings the outside card back fully inside, flush with the edges it crossed");
        Assert(Math.Abs(restored.ProposedRects[ProviderCatalog.Claude].X - 100) < 1e-9 &&
               Math.Abs(restored.ProposedRects[ProviderCatalog.Claude].Y - 100) < 1e-9,
            "the card that was inside restores where it was");
    }

    /// <summary>A docked group too large for its work area is split into single cards and fitted from their
    /// anchors. A single card that still cannot fit is pinned to the top-left so its title row remains visible.</summary>
    private static void RecoverySplitsAGroupAndPinsACardThatCannotFit()
    {
        var pair = new[] { ProviderCatalog.Codex, ProviderCatalog.Claude };
        var area = new LogicalWorkArea("primary", new LogicalRect(0, 0, 1920, 1040), isPrimary: true);
        var areas = new Dictionary<string, LogicalWorkArea> { [area.MonitorId] = area };
        var usable = new LogicalSize(1094, 1019);   // 730 × 680 at 150%, less the one-pixel slack
        var wideGroup = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 1410, Height = 1020, MonitorId = "primary", AnchorX = 0.2, AnchorY = 0.5, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 1410, Height = 1020, MonitorId = "primary", AnchorX = 0.8, AnchorY = 0.5, GroupOrder = 1 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var split = LayoutPolicy.RecoverMissingMonitor(wideGroup, pair, areas, area.MonitorId, usable);
        Assert(split.Succeeded, "a docked row wider than its monitor even at minimum size no longer rejects the restore");
        Assert(split.ProposedSettings!.Edges.Count == 0, "the group that cannot fit is split into single cards");
        Assert(split.ProposedRects[ProviderCatalog.Codex] == new LogicalRect(102, 10, 1410, 1020) &&
               split.ProposedRects[ProviderCatalog.Claude] == new LogicalRect(408, 10, 1410, 1020),
            "each split card is placed from its own anchor at its own size");
        Assert(split.ProposedRects.Values.All(rect => LayoutPolicy.IsReachablePlacement(area.Bounds, [rect])),
            "every split card is inside the work area");

        var shortArea = new LogicalWorkArea("primary", new LogicalRect(0, 0, 1280, 672), isPrimary: true);   // 1080p at 150%
        var tallCard = new LayoutSettings
        {
            Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680, MonitorId = "primary", AnchorX = 0.5, AnchorY = 0.5 }],
        };
        var pinned = LayoutPolicy.RecoverMissingMonitor(tallCard, [ProviderCatalog.Codex],
            new Dictionary<string, LogicalWorkArea> { [shortArea.MonitorId] = shortArea }, shortArea.MonitorId, new LogicalSize(729, 679));
        var rect = pinned.ProposedRects[ProviderCatalog.Codex];
        Assert(pinned.Succeeded && rect.Y == 0 && rect.Height >= 679 && rect.X >= 0 && rect.Right <= 1280,
            "a card taller than the work area even at its minimum is pinned to the top, title row on screen, still inside horizontally");
    }

    /// <summary>Verifies the reachability rule applied by every restore.</summary>
    private static void ReachablePlacementRule()
    {
        var area = new LogicalRect(0, 0, 1000, 800);
        Assert(LayoutPolicy.IsReachablePlacement(area, [new LogicalRect(0, 0, 500, 500)]), "a component inside the work area is reachable");
        Assert(!LayoutPolicy.IsReachablePlacement(area, [new LogicalRect(-10, 0, 500, 500)]), "a component that fits must lie inside");
        Assert(LayoutPolicy.IsReachablePlacement(area, [new LogicalRect(0, 0, 1200, 500)]),
            "a component wider than the work area is reachable when it starts at the left edge");
        Assert(!LayoutPolicy.IsReachablePlacement(area, [new LogicalRect(10, 0, 1200, 500)]),
            "a component wider than the work area must start at the left edge");
        Assert(LayoutPolicy.IsReachablePlacement(area, [new LogicalRect(0, 0, 600, 900)]),
            "a component taller than the work area is reachable when its top (the title row) is at the top edge");
    }

    private static void DisabledProviderRemovalReconcilesGraph()
    {
        var layout = ThreeCardLayout();
        var result = LayoutPolicy.ReconcileEnabledCards(layout, [ProviderCatalog.Codex, ProviderCatalog.Gemini]);
        Assert(result.Succeeded && result.ProposedSettings!.Cards.Count == 2 && result.ProposedSettings.Edges.Count == 0,
            "disabling the middle card removes it and splits its former group into independent nodes");
        var settings = new AppSettings
        {
            EnabledProviderIds = [ProviderCatalog.Codex, ProviderCatalog.Gemini],
            Layout = result.ProposedSettings!,
        };
        Assert(settings.Validate() is null, "reconciled graph remains valid for the newly enabled set");
    }

    private static void MissingMonitorRecoveryClampsAndDoesNotTeleport()
    {
        var layout = new LayoutSettings
        {
            Cards = [new CardLayoutSettings
            {
                ProviderId = ProviderCatalog.Codex,
                Width = 1400,
                Height = 900,
                MonitorId = "removed-monitor",
                AnchorX = 1,
                AnchorY = 1,
            }],
        };
        var primary = new LogicalWorkArea("primary", new LogicalRect(-100, 20, 1200, 800), isPrimary: true);
        var areas = new Dictionary<string, LogicalWorkArea>(StringComparer.Ordinal) { [primary.MonitorId] = primary };
        var recovered = LayoutPolicy.RecoverMissingMonitor(layout, [ProviderCatalog.Codex], areas, primary.MonitorId, Minimum);
        Assert(recovered.Succeeded, "missing monitor recovers onto the current primary work area");
        var rect = recovered.ProposedRects[ProviderCatalog.Codex];
        Assert(rect.Right == primary.Bounds.Right && rect.Bottom == primary.Bounds.Bottom &&
               rect.Width >= Minimum.Width && rect.Height >= Minimum.Height,
            "recovery scales only as needed and clamps the entire card within reachable work-area bounds");
        Assert(recovered.ProposedSettings!.Cards[0].MonitorId == "primary",
            "recovery persists the fallback monitor as the new owner");

        var withOriginalReturned = new Dictionary<string, LogicalWorkArea>(StringComparer.Ordinal)
        {
            ["primary"] = primary,
            ["removed-monitor"] = new LogicalWorkArea("removed-monitor", new LogicalRect(3000, 0, 2000, 1200)),
        };
        var second = LayoutPolicy.RecoverMissingMonitor(recovered.ProposedSettings, [ProviderCatalog.Codex],
            withOriginalReturned, primary.MonitorId, Minimum);
        Assert(second.Succeeded && second.ProposedRects[ProviderCatalog.Codex] == rect,
            "a monitor that returns later does not pull the recovered card away from primary");

        var presentButOffscreen = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings
                {
                    ProviderId = ProviderCatalog.Codex,
                    Width = 1400,
                    Height = 900,
                    MonitorId = primary.MonitorId,
                    AnchorX = 1,
                    AnchorY = 1,
                }
            ]
        };
        var presentMonitorRecovery = LayoutPolicy.RecoverMissingMonitor(presentButOffscreen, [ProviderCatalog.Codex],
            areas, primary.MonitorId, Minimum);
        var repaired = presentMonitorRecovery.ProposedRects[ProviderCatalog.Codex];
        Assert(presentMonitorRecovery.Succeeded &&
               repaired.X >= primary.Bounds.X && repaired.Y >= primary.Bounds.Y &&
               repaired.Right <= primary.Bounds.Right && repaired.Bottom <= primary.Bounds.Bottom &&
               presentMonitorRecovery.ProposedSettings!.Cards[0].MonitorId == primary.MonitorId,
            "Reset/recovery moves an off-screen card back onto its still-present monitor");
    }

    private static void ResetRecoveryPreservesValidGroupsAndSizes()
    {
        var gap = LayoutPolicy.DefaultInterCardGap;
        var layout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 700, Height = 500, MonitorId = "primary", AnchorX = 0.1, AnchorY = 0.1, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 700, Height = 500, MonitorId = "primary", AnchorX = 0.5, AnchorY = 0.1, GroupOrder = 1 },
            ],
            Edges =
            [
                new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = gap },
            ],
        };
        var area = new LogicalWorkArea("primary", new LogicalRect(0, 0, 1920, 1080), isPrimary: true);
        var recovered = LayoutPolicy.RecoverMissingMonitor(layout,
            [ProviderCatalog.Codex, ProviderCatalog.Claude],
            new Dictionary<string, LogicalWorkArea> { [area.MonitorId] = area }, area.MonitorId, Minimum);
        Assert(recovered.Succeeded && recovered.ProposedSettings!.Edges.Count == 1,
            "reset recovery retains a valid connected component and its docking edge");
        var codex = recovered.ProposedRects[ProviderCatalog.Codex];
        var claude = recovered.ProposedRects[ProviderCatalog.Claude];
        Assert(codex.Size == new LogicalSize(700, 500) && claude.Size == codex.Size &&
               claude.X == codex.Right + gap && claude.Y == codex.Y,
            "reset recovery preserves saved group sizes and exact oriented gap when the group fits");

        var missing = LayoutPolicy.RecoverMissingMonitor(new LayoutSettings(),
            [ProviderCatalog.Codex, ProviderCatalog.Claude],
            new Dictionary<string, LogicalWorkArea> { [area.MonitorId] = area }, area.MonitorId, Minimum);
        Assert(missing.Succeeded && missing.ProposedRects.Values.All(rect => rect.Size == new LogicalSize(940, 680)) &&
               missing.ProposedSettings!.Edges.Count == 0,
            "cards without saved geometry recover as detached legacy-default size placements");
    }
}

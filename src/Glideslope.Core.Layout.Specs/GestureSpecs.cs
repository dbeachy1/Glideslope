using Glideslope.Core;

namespace Glideslope.Core.Layout.Specs;

// Layout specs for the gesture operations in LayoutPolicy.Gestures.cs: dock preview, overlap, screen-edge
// snap, the Linux realign and containment (window design §20), group move and resize, detach, and resize
// or detach of a card partly off screen. Main in Program.cs runs them in its fixed order.
internal static partial class Program
{
    private static void DockPreviewUsesDestinationSizeAndIsAtomic()
    {
        var layout = ThreeCardLayout();
        var original = Serialize(layout);
        var rects = CurrentThreeCardRects();
        var workArea = Area("monitor-a", 2300, 1800);
        var result = LayoutPolicy.PreviewDock(layout, Enabled, rects, ProviderCatalog.Codex,
            ProviderCatalog.Claude, CardDockSide.Right, 12, workArea, Minimum);

        Assert(result.Succeeded, "unequal-size incoming component can dock when the resulting group fits");
        Assert(result.ProposedRects[ProviderCatalog.Codex] == rects[ProviderCatalog.Codex],
            "destination group stays at its current rectangle");
        Assert(result.ProposedRects[ProviderCatalog.Claude].Size == new LogicalSize(800, 600) &&
               result.ProposedRects[ProviderCatalog.Gemini].Size == new LogicalSize(800, 600),
            "every incoming card adopts the destination group's size");
        Assert(result.ProposedRects[ProviderCatalog.Claude].X == 912 &&
               result.ProposedRects[ProviderCatalog.Gemini].Y == 720,
            "preview returns the exact recomputed incoming offsets and gap");
        Assert(result.ProposedSettings!.Edges.Count == 2 && Serialize(layout) == original,
            "preview returns a candidate without mutating the persisted input");

        var tooSmallArea = Area("monitor-a", 1600, 1800);
        var rejected = LayoutPolicy.PreviewDock(layout, Enabled, rects, ProviderCatalog.Codex,
            ProviderCatalog.Claude, CardDockSide.Right, 12, tooSmallArea, Minimum);
        Assert(!rejected.Succeeded && rejected.SafeErrorCode == "layout_outside_work_area" &&
               rejected.ProposedSettings is null && Serialize(layout) == original,
            "rejected preview has no candidate and leaves both components unchanged");

        var freeMove = LayoutPolicy.MoveGroup(layout, Enabled, rects, ProviderCatalog.Claude,
            new LogicalPoint(-1100, 0), workArea, Minimum);
        Assert(freeMove.Succeeded && freeMove.ProposedRects[ProviderCatalog.Claude].X == 100 &&
               Serialize(layout) == original,
            "a detached component may remain reachable while overlapping another independent card");
    }

    private static void IndependentCardsMayOverlapButDockGroupsMayNot()
    {
        var layout = ThreeCardLayout();
        layout.Edges.Clear();
        layout.Edges.Add(new DockingEdgeSettings
        {
            FirstProviderId = ProviderCatalog.Codex,
            SecondProviderId = ProviderCatalog.Claude,
            FirstSide = CardDockSide.Right,
            Gap = 10,
        });
        var codexLayout = layout.Cards.Single(card => card.ProviderId == ProviderCatalog.Codex);
        codexLayout.Width = 400;
        codexLayout.Height = 300;
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(100, 100, 400, 300),
            [ProviderCatalog.Claude] = new(120, 100, 400, 300),
            [ProviderCatalog.Gemini] = new(1200, 420, 400, 300),
        };
        var workArea = Area("monitor-a", 2300, 1800);
        var rejected = LayoutPolicy.MoveGroup(layout, Enabled, rects, ProviderCatalog.Codex,
            new LogicalPoint(0, 0), workArea, Minimum);
        Assert(!rejected.Succeeded && rejected.SafeErrorCode == "layout_overlap",
            "overlap between cards in the same connected component is rejected");

        var twoDocked = ThreeCardLayout();
        twoDocked.Edges.RemoveAll(edge => edge.FirstProviderId != ProviderCatalog.Codex && edge.SecondProviderId != ProviderCatalog.Codex);
        twoDocked.Edges.Add(new DockingEdgeSettings
        {
            FirstProviderId = ProviderCatalog.Codex,
            SecondProviderId = ProviderCatalog.Claude,
            FirstSide = CardDockSide.Right,
            Gap = LayoutPolicy.DefaultInterCardGap,
        });
        var dockedClaude = twoDocked.Cards.Single(card => card.ProviderId == ProviderCatalog.Claude);
        dockedClaude.Width = 800;
        dockedClaude.Height = 600;
        var dockedAndDetachedRects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(100, 100, 800, 600),
            [ProviderCatalog.Claude] = new(910, 100, 800, 600),
            [ProviderCatalog.Gemini] = new(100, 100, 400, 300),
        };
        dockedAndDetachedRects[ProviderCatalog.Gemini] = new LogicalRect(100, 100, 400, 300);
        var movedDockedGroup = LayoutPolicy.MoveGroup(twoDocked, Enabled, dockedAndDetachedRects, ProviderCatalog.Codex,
            new LogicalPoint(0, 0), workArea, Minimum);
        Assert(movedDockedGroup.Succeeded &&
               movedDockedGroup.ProposedRects[ProviderCatalog.Codex].X == 100 &&
               movedDockedGroup.ProposedRects[ProviderCatalog.Gemini].X == 100,
            "two docked cards can be moved while an independent third card overlaps them");

        var independentRects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(100, 100, 940, 663),
            [ProviderCatalog.Claude] = new(148, 148, 940, 663),
            [ProviderCatalog.Gemini] = new(196, 196, 940, 663),
        };
        var initialSettings = new LayoutSettings();
        var recovered = LayoutPolicy.RecoverMissingMonitor(initialSettings, Enabled,
            new Dictionary<string, LogicalWorkArea> { ["monitor-a"] = Area("monitor-a", 1920, 1080) },
            "monitor-a", new LogicalSize(620, 440));
        Assert(recovered.Succeeded && recovered.ProposedRects.Count == 3,
            "three default 940-by-680 detached cards restore on one 1920-by-1080 monitor without shrinking or forcing screens");
        // The 680 px default height meets the 16 px text floor (window-interaction design §4.4).
        // These checks read LayoutSettings defaults rather than a fixture.
        Assert(recovered.ProposedRects.Values.All(rect => rect.Size == new LogicalSize(940, 680)) &&
               recovered.ProposedSettings!.Edges.Count == 0,
            "independent groups preserve default size and remain detached even when their rectangles overlap");
        var workAreas = Enabled.ToDictionary(id => id, _ => Area("monitor-a", 1920, 1080), StringComparer.Ordinal);
        var captured = LayoutPolicy.CaptureAnchors(initialSettings, Enabled, independentRects, workAreas);
        Assert(captured.Succeeded, "capture and persistence allow reachable independent-card overlap");
    }

    private static void ScreenSnapUsesLogicalGroupBoundsAndThreshold()
    {
        var ids = new[] { ProviderCatalog.Codex, ProviderCatalog.Claude };
        var layout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 100, Height = 80, MonitorId = "monitor-a" },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 100, Height = 80, MonitorId = "monitor-a" },
            ],
            Edges =
            [
                new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 14 },
            ]
        };
        var workArea = Area("monitor-a", 250, 200);
        var groupAtRight = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(20, 25, 100, 80),
            [ProviderCatalog.Claude] = new(134, 25, 100, 80),
        };
        var right = LayoutPolicy.SnapGroupToScreenEdges(layout, ids, groupAtRight, ProviderCatalog.Codex,
            workArea, new LogicalSize(80, 60), enabled: true);
        Assert(right.Succeeded && right.ProposedRects[ProviderCatalog.Codex].X == 36 &&
               right.ProposedRects[ProviderCatalog.Claude].X == 150,
            "the complete component's outer right edge snaps exactly at the 16-DIP threshold");

        var threshold = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(16, 16, 100, 80),
            [ProviderCatalog.Claude] = new(130, 16, 100, 80),
        };
        var snapped = LayoutPolicy.SnapGroupToScreenEdges(layout, ids, threshold, ProviderCatalog.Codex,
            workArea, new LogicalSize(80, 60), enabled: true);
        Assert(snapped.Succeeded && snapped.ProposedRects[ProviderCatalog.Codex].X == 0 &&
               snapped.ProposedRects[ProviderCatalog.Codex].Y == 0,
            "screen-edge snapping independently selects horizontal and vertical edges at a corner");

        var outsideThreshold = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(17, 17, 100, 80),
            [ProviderCatalog.Claude] = new(131, 17, 100, 80),
        };
        var noSnap = LayoutPolicy.SnapGroupToScreenEdges(layout, ids, outsideThreshold, ProviderCatalog.Codex,
            workArea, new LogicalSize(80, 60), enabled: true);
        var disabled = LayoutPolicy.SnapGroupToScreenEdges(layout, ids, threshold, ProviderCatalog.Codex,
            workArea, new LogicalSize(80, 60), enabled: false);
        Assert(noSnap.Succeeded && noSnap.ProposedRects[ProviderCatalog.Codex].X == 17 &&
               disabled.Succeeded && disabled.ProposedRects[ProviderCatalog.Codex].X == 16,
            "positions beyond the acquisition distance and the disabled setting remain unchanged");
    }

    private static void RealignGroupPutsADriftedRowBackInLine()
    {
        var layout = ThreeCardRowLayout();
        // Reproduces a drifted row: Codex is 3 px left and 12 px low, and Gemini overlaps it by 16 px.
        var drifted = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Claude] = new(189, 232, 940, 680),
            [ProviderCatalog.Codex] = new(1136, 244, 940, 680),
            [ProviderCatalog.Gemini] = new(2060, 246, 940, 680),
        };
        Assert(!LayoutPolicy.MoveGroup(layout, Enabled, drifted, ProviderCatalog.Codex, new LogicalPoint(5, 0),
                Area("monitor-a", 4000, 1200), Minimum).Succeeded,
            "the drifted row is the one every MoveGroup refused (the 1,423 layout_overlap lines)");

        var realigned = LayoutPolicy.RealignGroup(layout, Enabled, drifted, ProviderCatalog.Codex);
        Assert(realigned.Succeeded, "a drifted group can be realigned");
        AssertRect(realigned.ProposedRects[ProviderCatalog.Codex], 1136, 244, 940, 680, "the grabbed card never moves");
        AssertRect(realigned.ProposedRects[ProviderCatalog.Claude], 186, 244, 940, 680, "the left neighbor is rebuilt 10 px to its left, top-aligned");
        AssertRect(realigned.ProposedRects[ProviderCatalog.Gemini], 2086, 244, 940, 680, "the right neighbor is rebuilt 10 px to its right, top-aligned");
        Assert(LayoutPolicy.MoveGroup(layout, Enabled, realigned.ProposedRects, ProviderCatalog.Codex, new LogicalPoint(5, 0),
                Area("monitor-a", 4000, 1200), Minimum).Succeeded,
            "the realigned row moves again");

        var aligned = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Claude] = new(100, 100, 940, 680),
            [ProviderCatalog.Codex] = new(1050, 100, 940, 680),
            [ProviderCatalog.Gemini] = new(2000, 100, 940, 680),
        };
        var unchanged = LayoutPolicy.RealignGroup(layout, Enabled, aligned, ProviderCatalog.Gemini);
        Assert(unchanged.Succeeded && aligned.All(pair => unchanged.ProposedRects[pair.Key] == pair.Value),
            "an aligned group comes back exactly as it was");

        var mismatched = new Dictionary<string, LogicalRect>(aligned, StringComparer.Ordinal)
        {
            [ProviderCatalog.Gemini] = new(2000, 100, 900, 650),
        };
        var resized = LayoutPolicy.RealignGroup(layout, Enabled, mismatched, ProviderCatalog.Codex);
        AssertRect(resized.ProposedRects[ProviderCatalog.Gemini], 2000, 100, 940, 680, "a member of another size takes the grabbed card's size");
    }

    /// <summary>
    /// RealignGroup rejects an out-of-range reflow as layout_invalid_position. The lead is valid, but placing
    /// Gemini 10 px to its right at the same width would exceed double.MaxValue.
    /// </summary>
    private static void RealignGroupRejectsAnOutOfRangeLeadRectangle()
    {
        var layout = ThreeCardRowLayout();
        var lead = new LogicalRect(double.MaxValue * 0.9, 100, double.MaxValue * 0.09, 680);
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Claude] = new(100, 100, 940, 680),
            [ProviderCatalog.Codex] = lead,
            [ProviderCatalog.Gemini] = new(2000, 100, 940, 680),
        };
        LayoutPolicyResult? realigned = null;
        try
        {
            realigned = LayoutPolicy.RealignGroup(layout, Enabled, rects, ProviderCatalog.Codex);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            Assert(false, $"RealignGroup threw ArgumentOutOfRangeException ({exception.ParamName}) instead of rejecting");
        }
        Assert(realigned is { Succeeded: false, SafeErrorCode: "layout_invalid_position", ProposedSettings: null },
            $"an out-of-range lead rectangle is rejected as layout_invalid_position (got {realigned?.SafeErrorCode ?? "no result"})");
    }

    private static void ContainGroupPullsAGroupInsideItsWorkArea()
    {
        var layout = ThreeCardRowLayout();
        // Linux test system's work area: x 67..3000, y 29..1200; the row is 2840 wide.
        var workArea = new LogicalWorkArea("monitor-a", new LogicalRect(67, 29, 2933, 1171), isPrimary: true);
        var pastRight = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Claude] = new(189, 232, 940, 680),
            [ProviderCatalog.Codex] = new(1139, 232, 940, 680),
            [ProviderCatalog.Gemini] = new(2089, 232, 940, 680),
        };
        var contained = LayoutPolicy.ContainGroup(layout, Enabled, pastRight, ProviderCatalog.Codex, workArea);
        Assert(contained.Succeeded, "containment of a row past the right edge succeeds");
        AssertRect(contained.ProposedRects[ProviderCatalog.Gemini], 2060, 232, 940, 680, "the row's right edge lands on the work area's right edge");
        AssertRect(contained.ProposedRects[ProviderCatalog.Claude], 160, 232, 940, 680, "the whole row moves together, keeping its gaps");

        var pastTopLeft = pastRight.ToDictionary(pair => pair.Key, pair => pair.Value.Translate(-200, -250), StringComparer.Ordinal);
        var fromTopLeft = LayoutPolicy.ContainGroup(layout, Enabled, pastTopLeft, ProviderCatalog.Claude, workArea);
        AssertRect(fromTopLeft.ProposedRects[ProviderCatalog.Claude], 67, 29, 940, 680, "a row past the top-left corner comes back to it");

        var narrow = new LogicalWorkArea("monitor-a", new LogicalRect(0, 0, 2000, 1000), isPrimary: true);
        var tooWide = LayoutPolicy.ContainGroup(layout, Enabled, pastRight, ProviderCatalog.Gemini, narrow);
        AssertRect(tooWide.ProposedRects[ProviderCatalog.Claude], 0, 232, 940, 680, "a row wider than the work area is pinned to its left edge");

        var inside = pastRight.ToDictionary(pair => pair.Key, pair => pair.Value.Translate(-100, 0), StringComparer.Ordinal);
        var unchanged = LayoutPolicy.ContainGroup(layout, Enabled, inside, ProviderCatalog.Codex, workArea);
        Assert(unchanged.Succeeded && inside.All(pair => unchanged.ProposedRects[pair.Key] == pair.Value),
            "a group already inside comes back unchanged");
    }

    /// <summary>
    /// A drag may overshoot the work-area edge because the coordinator does not constrain a card during
    /// the native drag. SnapGroupToScreenEdges pulls a nearby overshoot back to the edge, bounded by
    /// LayoutPolicy.ScreenEdgeOvershootPullBackDistance so a card can still be parked farther off screen.
    /// </summary>
    private static void ScreenSnapPullsBackOnlyANearOvershoot()
    {
        var single = new[] { ProviderCatalog.Codex };
        var layout = new LayoutSettings
        {
            Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 100, Height = 80, MonitorId = "monitor-a" }]
        };
        var workArea = Area("monitor-a", 250, 200);
        var usableMinimum = new LogicalSize(80, 60);

        var overshotLeft = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(-40, 50, 100, 80),
        };
        var pulledBackLeft = LayoutPolicy.SnapGroupToScreenEdges(layout, single, overshotLeft, ProviderCatalog.Codex,
            workArea, usableMinimum, enabled: true);
        Assert(pulledBackLeft.Succeeded && pulledBackLeft.ProposedRects[ProviderCatalog.Codex].X == 0 &&
               pulledBackLeft.ProposedRects[ProviderCatalog.Codex].Y == 50,
            "a card dropped well past the left edge (more than the acquisition distance outside) still snaps flush to it");

        var overshotRight = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(200, 50, 100, 80),
        };
        var pulledBackRight = LayoutPolicy.SnapGroupToScreenEdges(layout, single, overshotRight, ProviderCatalog.Codex,
            workArea, usableMinimum, enabled: true);
        Assert(pulledBackRight.Succeeded && pulledBackRight.ProposedRects[ProviderCatalog.Codex].X == 150,
            "a card dropped well past the right edge still snaps flush to it (right edge at the 250-DIP work area boundary)");

        var overshotTop = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(75, -30, 100, 80),
        };
        var pulledBackTop = LayoutPolicy.SnapGroupToScreenEdges(layout, single, overshotTop, ProviderCatalog.Codex,
            workArea, usableMinimum, enabled: true);
        Assert(pulledBackTop.Succeeded && pulledBackTop.ProposedRects[ProviderCatalog.Codex].Y == 0,
            "a card dropped well past the top edge still snaps flush to it");

        var stillReachable = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(75, 60, 100, 80),
        };
        var untouched = LayoutPolicy.SnapGroupToScreenEdges(layout, single, stillReachable, ProviderCatalog.Codex,
            workArea, usableMinimum, enabled: true);
        Assert(untouched.Succeeded && untouched.ProposedRects[ProviderCatalog.Codex] == stillReachable[ProviderCatalog.Codex],
            "a card that is not near any edge and not overshooting is left exactly where it was dropped");

        var pullBack = LayoutPolicy.ScreenEdgeOvershootPullBackDistance;
        var atLimit = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(-pullBack, 50, 100, 80),
        };
        var pulledAtLimit = LayoutPolicy.SnapGroupToScreenEdges(layout, single, atLimit, ProviderCatalog.Codex,
            workArea, usableMinimum, enabled: true);
        Assert(pulledAtLimit.Succeeded && pulledAtLimit.ProposedRects[ProviderCatalog.Codex].X == 0,
            "an overshoot of exactly the pull-back distance still snaps flush");

        var parkedLeft = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(-pullBack - 1, 50, 100, 80),
        };
        var leftAlone = LayoutPolicy.SnapGroupToScreenEdges(layout, single, parkedLeft, ProviderCatalog.Codex,
            workArea, usableMinimum, enabled: true);
        Assert(leftAlone.Succeeded && leftAlone.ProposedRects[ProviderCatalog.Codex] == parkedLeft[ProviderCatalog.Codex],
            "a card dropped farther past the left edge than the pull-back distance stays where it was let go");

        var parkedBottom = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(75, 200, 100, 80),
        };
        var bottomAlone = LayoutPolicy.SnapGroupToScreenEdges(layout, single, parkedBottom, ProviderCatalog.Codex,
            workArea, usableMinimum, enabled: true);
        Assert(bottomAlone.Succeeded && bottomAlone.ProposedRects[ProviderCatalog.Codex] == parkedBottom[ProviderCatalog.Codex],
            "a card whose bottom edge is dropped 80 DIP past the work area's bottom stays where it was let go");

        // A docked pair is dragged with the right-hand card well past the right edge.
        var pair = new[] { ProviderCatalog.Codex, ProviderCatalog.Claude };
        var pairLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 100, Height = 80, MonitorId = "monitor-a" },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 100, Height = 80, MonitorId = "monitor-a" },
            ],
            Edges =
            [
                new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 },
            ]
        };
        var parkedPair = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(140, 50, 100, 80),
            [ProviderCatalog.Claude] = new(250, 50, 100, 80),
        };
        var pairAlone = LayoutPolicy.SnapGroupToScreenEdges(pairLayout, pair, parkedPair, ProviderCatalog.Codex,
            workArea, usableMinimum, enabled: true);
        Assert(pairAlone.Succeeded &&
               pairAlone.ProposedRects[ProviderCatalog.Codex] == parkedPair[ProviderCatalog.Codex] &&
               pairAlone.ProposedRects[ProviderCatalog.Claude] == parkedPair[ProviderCatalog.Claude],
            "a docked group with one card parked off the right edge stays where it was let go");
    }

    private static void RejectsImpossibleLayoutsAndDetachSplitsOnlySelectedNode()
    {
        var layout = ChainLayout();
        var rects = CurrentThreeCardRects();
        var tooSmall = LayoutPolicy.ResizeGroup(layout, Enabled, rects, ProviderCatalog.Claude,
            new LogicalSize(250, 180), Area("monitor-a", 2300, 1800), Minimum);
        Assert(!tooSmall.Succeeded && tooSmall.SafeErrorCode == "layout_too_small",
            "resize below the caller's usable minimum is rejected");

        layout.Edges.Add(new DockingEdgeSettings
        {
            FirstProviderId = ProviderCatalog.Codex,
            SecondProviderId = ProviderCatalog.Gemini,
            FirstSide = CardDockSide.Bottom,
            Gap = 10,
        });
        var cycle = LayoutPolicy.MoveGroup(layout, Enabled, rects, ProviderCatalog.Codex,
            new LogicalPoint(1, 1), Area("monitor-a", 2300, 1800), Minimum);
        Assert(!cycle.Succeeded && cycle.SafeErrorCode == "layout_cycle", "cycles are rejected before geometry is changed");
        layout.Edges.RemoveAt(layout.Edges.Count - 1);

        var detached = LayoutPolicy.DetachCard(layout, Enabled, rects, ProviderCatalog.Gemini,
            Area("monitor-a", 2300, 1800), Minimum);
        Assert(detached.Succeeded && detached.ProposedSettings!.Edges.Count == 1 &&
               detached.ProposedSettings.Edges[0].FirstProviderId == ProviderCatalog.Codex &&
               detached.ProposedSettings.Edges[0].SecondProviderId == ProviderCatalog.Claude,
            "detaching a card removes only its own edge and leaves the other group connection intact");
        var resizedAfterDetach = LayoutPolicy.ResizeGroup(detached.ProposedSettings!, Enabled, detached.ProposedRects,
            ProviderCatalog.Gemini, new LogicalSize(500, 400), Area("monitor-a", 2300, 1800), Minimum);
        Assert(resizedAfterDetach.Succeeded && resizedAfterDetach.ProposedRects[ProviderCatalog.Gemini].Size == new LogicalSize(500, 400) &&
               resizedAfterDetach.ProposedRects[ProviderCatalog.Codex].Size == rects[ProviderCatalog.Codex].Size,
            "a detached card resizes independently after its graph component splits");
    }

    private static void GroupMoveAndResizeAreDeterministic()
    {
        var layout = ThreeCardLayout();
        var rects = CurrentThreeCardRects();
        var area = Area("monitor-a", 2300, 1800);
        var docked = LayoutPolicy.PreviewDock(layout, Enabled, rects, ProviderCatalog.Codex,
            ProviderCatalog.Claude, CardDockSide.Right, 12, area, Minimum);
        var moved = LayoutPolicy.MoveGroup(docked.ProposedSettings!, Enabled, docked.ProposedRects,
            ProviderCatalog.Gemini, new LogicalPoint(50, 30), area, Minimum);
        Assert(moved.Succeeded && moved.ProposedRects[ProviderCatalog.Codex].X == 150 &&
               moved.ProposedRects[ProviderCatalog.Claude].X == 962 && moved.ProposedRects[ProviderCatalog.Gemini].Y == 750,
            "body movement translates every connected card by the same delta");

        var resized = LayoutPolicy.ResizeGroup(moved.ProposedSettings!, Enabled, moved.ProposedRects,
            ProviderCatalog.Claude, new LogicalSize(700, 500), area, Minimum);
        Assert(resized.Succeeded && resized.ProposedRects.Values.All(rect => rect.Size == new LogicalSize(700, 500)),
            "corner resize applies one size to the entire component");
        Assert(resized.ProposedRects[ProviderCatalog.Claude].Y == resized.ProposedRects[ProviderCatalog.Codex].Y &&
               resized.ProposedRects[ProviderCatalog.Gemini].Y == resized.ProposedRects[ProviderCatalog.Claude].Bottom + 20,
            "resize recomputes offsets from the persisted dock sides and gaps");

        var areas = Enabled.ToDictionary(id => id, _ => area, StringComparer.Ordinal);
        var captured = LayoutPolicy.CaptureAnchors(ThreeCardLayout(), Enabled, rects, areas);
        Assert(captured.Succeeded && captured.ProposedSettings!.Cards[0].MonitorId == "monitor-a" &&
               Math.Abs(captured.ProposedSettings.Cards[0].AnchorX - (100d / 1500d)) < 0.000001,
            "anchor capture persists monitor identity and normalized work-area coordinates");
        areas[ProviderCatalog.Gemini] = Area("monitor-b", 2300, 1800);
        var splitMonitor = LayoutPolicy.CaptureAnchors(ThreeCardLayout(), Enabled, rects, areas);
        // A cross-monitor group uses the monitor containing most members; ties use the root card's monitor.
        Assert(splitMonitor.Succeeded &&
               splitMonitor.ProposedSettings!.Cards.Single(card => card.ProviderId == ProviderCatalog.Claude).MonitorId == "monitor-a" &&
               splitMonitor.ProposedSettings.Cards.Single(card => card.ProviderId == ProviderCatalog.Gemini).MonitorId == "monitor-a",
            "a group spread one-to-one over two monitors is captured against its root's monitor instead of rejecting the layout");
        var chainRects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(100, 100, 400, 300),
            [ProviderCatalog.Claude] = new(510, 100, 400, 300),
            [ProviderCatalog.Gemini] = new(510, 420, 400, 300),
        };
        var chainAreas = new Dictionary<string, LogicalWorkArea>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = Area("monitor-a", 2300, 1800),
            [ProviderCatalog.Claude] = Area("monitor-b", 2300, 1800),
            [ProviderCatalog.Gemini] = Area("monitor-b", 2300, 1800),
        };
        var majority = LayoutPolicy.CaptureAnchors(ChainLayout(), Enabled, chainRects, chainAreas);
        Assert(majority.Succeeded && majority.ProposedSettings!.Cards.All(card => card.MonitorId == "monitor-b"),
            "a group with most of its cards on one monitor is captured against that monitor, even when its root is elsewhere");
    }

    /// <summary>With Snap off, resize gestures and detach accept cards partly outside the work area.
    /// The default resize policy still enforces containment for reset-size operations.</summary>
    private static void ResizeAndDetachAcceptACardPartlyOffScreen()
    {
        var single = new[] { ProviderCatalog.Codex };
        var area = new LogicalWorkArea("primary", new LogicalRect(0, 0, 1920, 1040), isPrimary: true);
        var offScreen = new Dictionary<string, LogicalRect>(StringComparer.Ordinal) { [ProviderCatalog.Codex] = new(1500, 100, 940, 680) };
        var gesture = LayoutPolicy.ResizeGroup(new LayoutSettings(), single, offScreen, ProviderCatalog.Codex,
            new LogicalSize(1000, 700), area, Minimum, requireInsideWorkArea: false);
        Assert(gesture.Succeeded && gesture.ProposedRects[ProviderCatalog.Codex].Size == new LogicalSize(1000, 700),
            "a resize gesture on a card partly off screen is accepted");
        var reset = LayoutPolicy.ResizeGroup(new LayoutSettings(), single, offScreen, ProviderCatalog.Codex,
            new LogicalSize(1000, 700), area, Minimum);
        Assert(!reset.Succeeded && reset.SafeErrorCode == "layout_outside_work_area",
            "the default still refuses a resize that leaves the work area (since 2026-09-27 no caller relies on it: ↺ passes false too)");

        var pair = new[] { ProviderCatalog.Codex, ProviderCatalog.Claude };
        var docked = new LayoutSettings
        {
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var groupRects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(1000, 100, 940, 680),
            [ProviderCatalog.Claude] = new(1950, 100, 940, 680),
        };
        var detached = LayoutPolicy.DetachCard(docked, pair, groupRects, ProviderCatalog.Claude, area, Minimum);
        Assert(detached.Succeeded && detached.ProposedSettings!.Edges.Count == 0,
            "Undock and Ctrl-drag release a card from a group that is partly off screen");
    }
}

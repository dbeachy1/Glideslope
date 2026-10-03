using Glideslope.Core;

namespace Glideslope.Core.Layout.Specs;

// Layout specs for mode switching, startup preparation, per-card minimums and return sizes (design §8.1),
// plus group scaling and minimum-size policy.
internal static partial class Program
{
    /// <summary>ScaleGroups resizes a docked group as a unit, keeping its
    /// saved gap and its top-left corner; a detached card keeps its top-left; sizes are raised to the minimum.</summary>
    private static void ScaleGroupsKeepsGroupsDockedAtTheirTopLeft()
    {
        var layout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 940, Height = 680, GroupOrder = 1 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Gemini, Width = 940, Height = 680, GroupOrder = 2 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(100, 100, 940, 680),
            [ProviderCatalog.Claude] = new(1050, 100, 940, 680),
            [ProviderCatalog.Gemini] = new(300, 900, 940, 680),
        };
        var grown = LayoutPolicy.ScaleGroups(layout, Enabled, rects, 1.2, new LogicalSize(876, 816));
        Assert(grown.Succeeded, "scaling up succeeds");
        AssertRect(grown.ProposedRects[ProviderCatalog.Codex], 100, 100, 1128, 816, "the group keeps its top-left corner");
        AssertRect(grown.ProposedRects[ProviderCatalog.Claude], 1238, 100, 1128, 816, "the docked card stays beside it with the unscaled 10 px gap");
        AssertRect(grown.ProposedRects[ProviderCatalog.Gemini], 300, 900, 1128, 816, "a detached card keeps its own top-left");
        Assert(grown.ProposedSettings!.Edges.Count == 1 && grown.ProposedSettings.Edges[0].Gap == 10 &&
               grown.ProposedSettings.Cards.All(card => Math.Abs(card.Width - 1128) < 1e-9 && Math.Abs(card.Height - 816) < 1e-9),
            "the edge and its gap are kept and every saved size is the scaled one");

        var shrunk = LayoutPolicy.ScaleGroups(layout, Enabled, rects, 0.5, new LogicalSize(511, 476));
        AssertRect(shrunk.ProposedRects[ProviderCatalog.Claude], 621, 100, 511, 476,
            "a size below the scaled minimum is raised to it, and the group reflows at that size");
    }

    /// <summary>A restore raises only saved sizes below the current scaled minimum.</summary>
    private static void EnsureMinimumSizesRaisesOnlySmallerSizes()
    {
        var layout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 700, Height = 600 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 1000, Height = 700 },
            ],
        };
        var raised = LayoutPolicy.EnsureMinimumSizes(layout, new LogicalSize(730, 680));
        Assert(raised.Cards[0].Width == 730 && raised.Cards[0].Height == 680, "a size below the minimum is raised to it");
        Assert(raised.Cards[1].Width == 1000 && raised.Cards[1].Height == 700, "a size above the minimum is kept");
        Assert(layout.Cards[0].Width == 700, "the input layout is not changed");
    }

    /// <summary>2.0 mini mode (2.0 design §8.1, SwitchGroupMode bullets).</summary>
    private static void SwitchGroupModeTogglesSizeAndReturnsSize()
    {
        var layout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 940, Height = 680, GroupOrder = 1 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Gemini, Width = 500, Height = 400, GroupOrder = 2 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(100, 100, 940, 680),
            [ProviderCatalog.Claude] = new(1050, 100, 940, 680),
            [ProviderCatalog.Gemini] = new(2500, 1500, 500, 400),
        };
        var workArea = Area("monitor-a", 4000, 3000);
        var miniSize = new LogicalSize(400, 300);
        var miniUsableMinimum = new LogicalSize(399, 299);
        var fullUsableMinimum = new LogicalSize(729, 679);
        var fullFallback = new LogicalSize(940, 680);

        var toMini = LayoutPolicy.SwitchGroupMode(layout, Enabled, rects, ProviderCatalog.Codex, CardMode.Mini,
            miniSize, fullFallback, workArea, miniUsableMinimum);
        Assert(toMini.Succeeded, "switching a full group to mini succeeds");
        AssertRect(toMini.ProposedRects[ProviderCatalog.Codex], 640, 100, 400, 300, "the clicked card keeps its top-right corner at the mini size");
        AssertRect(toMini.ProposedRects[ProviderCatalog.Claude], 1050, 100, 400, 300, "the docked card follows, still 10 px away");
        var codexMini = toMini.ProposedSettings!.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        var claudeMini = toMini.ProposedSettings.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(codexMini.FullWidth == 940 && codexMini.FullHeight == 680 && claudeMini.FullWidth == 940 && claudeMini.FullHeight == 680,
            "every member's remembered full size is the group's size just before the switch");
        Assert(toMini.ProposedRects[ProviderCatalog.Gemini] == rects[ProviderCatalog.Gemini], "an untouched detached card keeps its rect");
        var geminiAfterMini = toMini.ProposedSettings.Cards.Single(c => c.ProviderId == ProviderCatalog.Gemini);
        Assert(geminiAfterMini.Width == 500 && geminiAfterMini.Height == 400 && geminiAfterMini.FullWidth == 0,
            "an untouched detached card keeps its settings entry too");

        var toFull = LayoutPolicy.SwitchGroupMode(toMini.ProposedSettings, Enabled, toMini.ProposedRects, ProviderCatalog.Codex,
            CardMode.Full, miniSize, fullFallback, workArea, fullUsableMinimum);
        Assert(toFull.Succeeded, "switching back to full succeeds");
        Assert(toFull.ProposedRects[ProviderCatalog.Codex] == rects[ProviderCatalog.Codex] &&
               toFull.ProposedRects[ProviderCatalog.Claude] == rects[ProviderCatalog.Claude],
            "toggling twice returns every rect exactly");
        var codexFull = toFull.ProposedSettings!.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        Assert(codexFull.FullWidth == 0 && codexFull.FullHeight == 0, "going full clears the remembered full size");

        // A zero remembered full size (a mini card created with no full size yet) uses the fallback default.
        var freshMiniLayout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300 }] };
        var freshMiniRects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal) { [ProviderCatalog.Codex] = new(200, 200, 400, 300) };
        var freshToFull = LayoutPolicy.SwitchGroupMode(freshMiniLayout, [ProviderCatalog.Codex], freshMiniRects, ProviderCatalog.Codex,
            CardMode.Full, miniSize, fullFallback, workArea, fullUsableMinimum);
        Assert(freshToFull.Succeeded && freshToFull.ProposedRects[ProviderCatalog.Codex].Size == fullFallback,
            "a zero remembered full size uses the fallback default size");

        // A coordinator defect guard: a group already carrying a remembered full size cannot be switched to mini again.
        var alreadyMiniLayout = new LayoutSettings
        {
            Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300, FullWidth = 940, FullHeight = 680 }],
        };
        var alreadyMiniRects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal) { [ProviderCatalog.Codex] = new(200, 200, 400, 300) };
        var rejectedAlreadyMini = LayoutPolicy.SwitchGroupMode(alreadyMiniLayout, [ProviderCatalog.Codex], alreadyMiniRects, ProviderCatalog.Codex,
            CardMode.Mini, miniSize, fullFallback, workArea, miniUsableMinimum);
        Assert(!rejectedAlreadyMini.Succeeded && rejectedAlreadyMini.SafeErrorCode == "layout_mode_already_mini",
            "a group already carrying a remembered full size cannot be switched to mini again");

        // A group grown past the screen edge is moved inside rather than rejected or left hanging over it.
        var edgeWorkArea = Area("monitor-b", 1200, 900);
        var edgeLayout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300 }] };
        var edgeRects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal) { [ProviderCatalog.Codex] = new(1000, 700, 400, 300) };
        var grownPastEdge = LayoutPolicy.SwitchGroupMode(edgeLayout, [ProviderCatalog.Codex], edgeRects, ProviderCatalog.Codex,
            CardMode.Full, miniSize, fullFallback, edgeWorkArea, fullUsableMinimum);
        Assert(grownPastEdge.Succeeded, "a group grown past the screen edge is accepted, moved inside instead of rejected");
        var grownRect = grownPastEdge.ProposedRects[ProviderCatalog.Codex];
        Assert(grownRect.Size == fullFallback, "the group keeps its full size; only its position moved");
        Assert(LayoutPolicy.IsReachablePlacement(edgeWorkArea.Bounds, [grownRect]),
            "the group is moved fully inside the work area rather than left hanging over the edge");

        // A full group too big for the work area even at the minimum is shrunk, then split.
        var splitWorkArea = new LogicalWorkArea("monitor-c", new LogicalRect(0, 0, 1200, 900), isPrimary: true);
        var splitLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 400, Height = 300, GroupOrder = 1 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var splitRects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(100, 100, 400, 300),
            [ProviderCatalog.Claude] = new(510, 100, 400, 300),
        };
        var split = LayoutPolicy.SwitchGroupMode(splitLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude], splitRects, ProviderCatalog.Codex,
            CardMode.Full, miniSize, fullFallback, splitWorkArea, fullUsableMinimum);
        Assert(split.Succeeded, "a docked pair that cannot fit full size even at minimum is accepted, split instead of rejected");
        Assert(split.ProposedSettings!.Edges.Count == 0, "the group that cannot fit full size is split into single cards");
        Assert(LayoutPolicy.IsReachablePlacement(splitWorkArea.Bounds, [split.ProposedRects[ProviderCatalog.Codex]]) &&
               LayoutPolicy.IsReachablePlacement(splitWorkArea.Bounds, [split.ProposedRects[ProviderCatalog.Claude]]),
            "each split card is placed reachably inside its work area");
    }

    /// <summary>2.0 mini mode (2.0 design §3.7, §8.1 "full -> mini -> resize mini -> full -> mini returns the
    /// resized mini size; a card never mini gets the default"): each card remembers its OWN size in each mode,
    /// independently of the other mode's size, indefinitely across repeated switches and a resize in between.</summary>
    private static void SwitchGroupModeRemembersEachModesSize()
    {
        var workArea = Area("monitor-a", 4000, 3000);
        var miniSize = new LogicalSize(400, 300);
        var miniUsableMinimum = new LogicalSize(399, 299);
        var fullUsableMinimum = new LogicalSize(729, 679);
        var fullFallback = new LogicalSize(940, 680);

        var layout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680 }] };
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal) { [ProviderCatalog.Codex] = new(100, 100, 940, 680) };
        string[] ids = [ProviderCatalog.Codex];

        // A card that has never been mini gets the caller's mini default the first time it goes mini.
        var toMini1 = LayoutPolicy.SwitchGroupMode(layout, ids, rects, ProviderCatalog.Codex, CardMode.Mini,
            miniSize, fullFallback, workArea, miniUsableMinimum);
        Assert(toMini1.Succeeded && toMini1.ProposedRects[ProviderCatalog.Codex].Size == miniSize,
            "a card never mini gets the mini default");

        // The user resizes the mini card (the Aa slider, or a drag on the mini grips - either ends in ResizeGroup).
        var resizedMini = LayoutPolicy.ResizeGroup(toMini1.ProposedSettings!, ids, toMini1.ProposedRects, ProviderCatalog.Codex,
            new LogicalSize(450, 350), workArea, miniUsableMinimum);
        Assert(resizedMini.Succeeded && resizedMini.ProposedRects[ProviderCatalog.Codex].Size == new LogicalSize(450, 350),
            "the mini card is resized");

        // Full: the remembered full size (940x680, from before it ever went mini) wins, and the group's just-
        // resized mini size (450x350) is what gets remembered for next time, not the original mini default.
        var toFull = LayoutPolicy.SwitchGroupMode(resizedMini.ProposedSettings!, ids, resizedMini.ProposedRects, ProviderCatalog.Codex,
            CardMode.Full, miniSize, fullFallback, workArea, fullUsableMinimum);
        Assert(toFull.Succeeded && toFull.ProposedRects[ProviderCatalog.Codex].Size == fullFallback,
            "switching back to full restores the remembered full size");
        var codexFull = toFull.ProposedSettings!.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        Assert(codexFull.MiniWidth == 450 && codexFull.MiniHeight == 350,
            "full: the group's just-resized mini size is remembered, superseding the original mini default");

        // Mini again: returns to the RESIZED mini size (450x350), not the caller's mini default (400x300).
        var toMini2 = LayoutPolicy.SwitchGroupMode(toFull.ProposedSettings!, ids, toFull.ProposedRects, ProviderCatalog.Codex,
            CardMode.Mini, miniSize, fullFallback, workArea, miniUsableMinimum);
        Assert(toMini2.Succeeded && toMini2.ProposedRects[ProviderCatalog.Codex].Size == new LogicalSize(450, 350),
            "full -> mini -> resize mini -> full -> mini returns the resized mini size, not the mini default");
    }

    /// <summary>2.0 mini mode (2.0 design §8.1, PreviewDock bullet; §3.7): incoming members adopt the
    /// destination's remembered full size AND its remembered mini size, in all four full/mini combinations.
    /// A destination's remembered mini size (destinationMiniWidth/Height below) is only ever non-zero for a
    /// full destination that has been mini before (the mini-card invariant makes it 0 for a mini destination),
    /// but PreviewDock itself just copies whatever is stored, so both are exercised in every combination to
    /// prove the field travels with FullWidth/FullHeight rather than being forgotten.</summary>
    private static void PreviewDockAdoptsDestinationsReturnSize()
    {
        var workArea = Area("monitor-a", 4000, 3000);
        var minimum = new LogicalSize(300, 200);

        LayoutPolicyResult Dock(double destinationFullWidth, double destinationFullHeight, double incomingFullWidth, double incomingFullHeight,
            double destinationMiniWidth = 0, double destinationMiniHeight = 0)
        {
            var layout = new LayoutSettings
            {
                Cards =
                [
                    new CardLayoutSettings
                    {
                        ProviderId = ProviderCatalog.Codex, Width = 800, Height = 600,
                        FullWidth = destinationFullWidth, FullHeight = destinationFullHeight,
                        MiniWidth = destinationMiniWidth, MiniHeight = destinationMiniHeight,
                    },
                    new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 400, Height = 300, FullWidth = incomingFullWidth, FullHeight = incomingFullHeight },
                ],
            };
            var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
            {
                [ProviderCatalog.Codex] = new(100, 100, 800, 600),
                [ProviderCatalog.Claude] = new(2000, 100, 400, 300),
            };
            return LayoutPolicy.PreviewDock(layout, [ProviderCatalog.Codex, ProviderCatalog.Claude], rects, ProviderCatalog.Codex,
                ProviderCatalog.Claude, CardDockSide.Right, 10, workArea, minimum);
        }

        var fullToFull = Dock(0, 0, 0, 0, destinationMiniWidth: 470, destinationMiniHeight: 330);
        Assert(fullToFull.Succeeded, "full onto full docks");
        var fullToFullIncoming = fullToFull.ProposedSettings!.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(fullToFullIncoming.FullWidth == 0 && fullToFullIncoming.FullHeight == 0, "full onto full: the incoming card stays full");
        Assert(fullToFullIncoming.MiniWidth == 470 && fullToFullIncoming.MiniHeight == 330,
            "full onto full: the incoming card adopts the destination's remembered mini size too");

        var miniToFull = Dock(0, 0, 600, 500, destinationMiniWidth: 470, destinationMiniHeight: 330);
        Assert(miniToFull.Succeeded, "mini onto full docks");
        var miniToFullIncoming = miniToFull.ProposedSettings!.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(miniToFullIncoming.FullWidth == 0 && miniToFullIncoming.FullHeight == 0,
            "mini onto full: the incoming card becomes full and forgets its old remembered size");
        Assert(miniToFullIncoming.MiniWidth == 470 && miniToFullIncoming.MiniHeight == 330,
            "mini onto full: the incoming card adopts the destination's remembered mini size");

        var fullToMini = Dock(940, 680, 0, 0);
        Assert(fullToMini.Succeeded, "full onto mini docks");
        var fullToMiniIncoming = fullToMini.ProposedSettings!.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(fullToMiniIncoming.FullWidth == 940 && fullToMiniIncoming.FullHeight == 680,
            "full onto mini: the incoming card becomes mini and will return to the target group's remembered full size");
        Assert(fullToMiniIncoming.MiniWidth == 0 && fullToMiniIncoming.MiniHeight == 0,
            "full onto mini: the destination (a mini card) has no remembered mini size of its own (the invariant), so the incoming card has none either");

        var miniToMini = Dock(940, 680, 600, 500);
        Assert(miniToMini.Succeeded, "mini onto mini docks");
        var miniToMiniIncoming = miniToMini.ProposedSettings!.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(miniToMiniIncoming.FullWidth == 940 && miniToMiniIncoming.FullHeight == 680,
            "mini onto mini: the incoming card adopts the target's remembered full size, not its own old one");
        Assert(miniToMiniIncoming.MiniWidth == 0 && miniToMiniIncoming.MiniHeight == 0,
            "mini onto mini: the destination has no remembered mini size either (the invariant), so neither does the incoming card");
    }

    /// <summary>2.0 mini mode (2.0 design §8.1, PrepareForMode bullets).</summary>
    private static void PrepareForModePreparesStartupSizes()
    {
        var miniSize = new LogicalSize(400, 300);

        var toFullLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300, FullWidth = 940, FullHeight = 680, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 400, Height = 300, FullWidth = 800, FullHeight = 600, GroupOrder = 1 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var preparedFull = LayoutPolicy.PrepareForMode(toFullLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude], CardMode.Full, miniSize);
        var codexFull = preparedFull.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        var claudeFull = preparedFull.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(codexFull.Width == 940 && codexFull.Height == 680 && claudeFull.Width == 940 && claudeFull.Height == 680,
            "Full: every member takes the root's remembered full size group-wide, even a mismatched sibling");
        Assert(codexFull.FullWidth == 0 && codexFull.FullHeight == 0 && claudeFull.FullWidth == 0 && claudeFull.FullHeight == 0,
            "Full: every member's remembered full size is cleared");
        // 2.0 mini mode (2.0 design §3.7, §8.1 "restart Full after a mini shutdown restores the full size and
        // remembers the mini size"): the group's saved live (mini) size - the root's Width/Height, 400x300,
        // read before it was overwritten above - is stamped group-wide as the new remembered mini size.
        Assert(codexFull.MiniWidth == 400 && codexFull.MiniHeight == 300 && claudeFull.MiniWidth == 400 && claudeFull.MiniHeight == 300,
            "Full: every member's remembered mini size is the group's saved (mini) live size");

        var alreadyFullLayout = new LayoutSettings
        {
            Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680 }],
        };
        var preparedAlreadyFull = LayoutPolicy.PrepareForMode(alreadyFullLayout, [ProviderCatalog.Codex], CardMode.Full, miniSize);
        var codexAlreadyFull = preparedAlreadyFull.Cards.Single();
        Assert(codexAlreadyFull.Width == 940 && codexAlreadyFull.Height == 680,
            "Full: an already-full group (no remembered full size) is left exactly as saved");

        var toMiniLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 800, Height = 600, GroupOrder = 1 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var preparedMini = LayoutPolicy.PrepareForMode(toMiniLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude], CardMode.Mini, miniSize);
        var codexMini = preparedMini.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        var claudeMini = preparedMini.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(codexMini.Width == 400 && codexMini.Height == 300 && claudeMini.Width == 400 && claudeMini.Height == 300,
            "Mini: every member gets the mini size (the root's MiniWidth/MiniHeight was 0, so the caller's mini default is used)");
        Assert(codexMini.FullWidth == 940 && codexMini.FullHeight == 680 && claudeMini.FullWidth == 940 && claudeMini.FullHeight == 680,
            "Mini: the group's full size (the root's Width/Height, since its FullWidth was 0) is stamped group-wide, following a mismatched sibling");
        Assert(codexMini.MiniWidth == 0 && codexMini.MiniHeight == 0 && claudeMini.MiniWidth == 0 && claudeMini.MiniHeight == 0,
            "Mini: every member's remembered mini size is cleared, matching the mini-card invariant");

        // 2.0 mini mode (2.0 design §3.7, §8.1 "restart Mini after a full shutdown uses the remembered mini
        // size"): a group saved full that already carries a remembered mini size (the root's MiniWidth/
        // MiniHeight) uses THAT instead of the caller's mini default.
        var toMiniWithMemoryLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680, MiniWidth = 470, MiniHeight = 330, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 940, Height = 680, GroupOrder = 1 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var preparedMiniWithMemory = LayoutPolicy.PrepareForMode(toMiniWithMemoryLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude], CardMode.Mini, miniSize);
        var codexMiniWithMemory = preparedMiniWithMemory.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        var claudeMiniWithMemory = preparedMiniWithMemory.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(codexMiniWithMemory.Width == 470 && codexMiniWithMemory.Height == 330 &&
               claudeMiniWithMemory.Width == 470 && claudeMiniWithMemory.Height == 330,
            "Mini: the root's remembered mini size wins over the mini default, group-wide");
        Assert(codexMiniWithMemory.MiniWidth == 0 && codexMiniWithMemory.MiniHeight == 0 &&
               claudeMiniWithMemory.MiniWidth == 0 && claudeMiniWithMemory.MiniHeight == 0,
            "Mini: the remembered mini size is consumed (cleared), matching the mini-card invariant");

        var handEditedLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300, FullWidth = 940, FullHeight = 680, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 400, Height = 300, FullWidth = 1200, FullHeight = 900, GroupOrder = 1 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var preparedHandEdited = LayoutPolicy.PrepareForMode(handEditedLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude], CardMode.Full, miniSize);
        Assert(preparedHandEdited.Cards.All(c => c.Width == 940 && c.Height == 680),
            "a hand-edited group with mismatched remembered full sizes restores as one group, following its root, instead of failing the restore");

        // A group saved in mini mode and restarted in mini mode keeps its saved mini size (a resized mini group
        // comes back as large as it was left, so its anchors put it back in the same spot) and its return size.
        var savedMiniLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 470, Height = 330, FullWidth = 940, FullHeight = 680, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 470, Height = 330, FullWidth = 940, FullHeight = 680, GroupOrder = 1 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var preparedSavedMini = LayoutPolicy.PrepareForMode(savedMiniLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude], CardMode.Mini, miniSize);
        Assert(preparedSavedMini.Cards.All(c => c.Width == 470 && c.Height == 330 && c.FullWidth == 940 && c.FullHeight == 680),
            "Mini after a mini shutdown: the saved mini size and the return size are kept, not replaced by the mini default");
    }

    /// <summary>2.0 mini mode (2.0 design §8.1, scaling-one-mode bullets).</summary>
    private static void ScalingOneModeLeavesTheOtherUntouched()
    {
        var layout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 940, Height = 680, GroupOrder = 1 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Gemini, Width = 400, Height = 300, GroupOrder = 2 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(100, 100, 940, 680),
            [ProviderCatalog.Claude] = new(1050, 100, 940, 680),
            [ProviderCatalog.Gemini] = new(300, 900, 400, 300),
        };
        var fullOnly = new HashSet<string>(StringComparer.Ordinal) { ProviderCatalog.Codex, ProviderCatalog.Claude };
        var scaledFullOnly = LayoutPolicy.ScaleGroups(layout, Enabled, rects, 1.2, new LogicalSize(876, 816), fullOnly);
        Assert(scaledFullOnly.Succeeded, "scaling only the full group succeeds");
        AssertRect(scaledFullOnly.ProposedRects[ProviderCatalog.Codex], 100, 100, 1128, 816, "the full group scales");
        Assert(scaledFullOnly.ProposedRects[ProviderCatalog.Gemini] == rects[ProviderCatalog.Gemini],
            "a card outside onlyProviders keeps its rect exactly");
        var geminiCard = scaledFullOnly.ProposedSettings!.Cards.Single(c => c.ProviderId == ProviderCatalog.Gemini);
        Assert(geminiCard.Width == 400 && geminiCard.Height == 300, "a card outside onlyProviders keeps its saved size too");

        var mixedSet = new HashSet<string>(StringComparer.Ordinal) { ProviderCatalog.Codex, ProviderCatalog.Gemini };
        var rejectedPartial = LayoutPolicy.ScaleGroups(layout, Enabled, rects, 1.2, new LogicalSize(876, 816), mixedSet);
        Assert(!rejectedPartial.Succeeded && rejectedPartial.SafeErrorCode == "layout_group_partially_scaled",
            "a group straddling onlyProviders (a mixed-mode group, which must never exist) is rejected rather than half-scaled");

        var returnLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300, FullWidth = 940, FullHeight = 680 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 940, Height = 680, FullWidth = 0, FullHeight = 0 },
            ],
        };
        var scaledReturn = LayoutPolicy.ScaleReturnSizes(returnLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude], 1.2, new LogicalSize(876, 816));
        var codexReturn = scaledReturn.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        var claudeReturn = scaledReturn.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(Math.Abs(codexReturn.FullWidth - 1128) < 1e-6 && Math.Abs(codexReturn.FullHeight - 816) < 1e-6,
            "a non-zero remembered full size is scaled by the ratio");
        Assert(claudeReturn.FullWidth == 0 && claudeReturn.FullHeight == 0, "a zero remembered full size (a full card) is left alone");
        var shrunkReturn = LayoutPolicy.ScaleReturnSizes(returnLayout, [ProviderCatalog.Codex], 0.5, new LogicalSize(500, 400));
        var codexShrunkReturn = shrunkReturn.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        Assert(codexShrunkReturn.FullWidth == 500 && codexShrunkReturn.FullHeight == 400,
            "a remembered full size scaled below the full minimum is raised to it");

        var scaledUp = LayoutPolicy.ScaleReturnSizes(returnLayout, [ProviderCatalog.Codex], 1.2, new LogicalSize(1, 1));
        var roundTrip = LayoutPolicy.ScaleReturnSizes(scaledUp, [ProviderCatalog.Codex], 1 / 1.2, new LogicalSize(1, 1));
        var codexRoundTrip = roundTrip.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        Assert(Math.Abs(codexRoundTrip.FullWidth - 940) < 1e-6 && Math.Abs(codexRoundTrip.FullHeight - 680) < 1e-6,
            "scaling a remembered full size up then back down returns it to what it was");

        // 2.0 mini mode (2.0 design §3.7, §8.1 "mini-scale change scales a full card's remembered mini size
        // and raises it to the minimum"): ScaleReturnSizes with CardMode.Mini is the mirror of the default
        // (Full) case just proven above - it touches MiniWidth/MiniHeight (a full card's remembered mini
        // size) instead of FullWidth/FullHeight, and leaves FullWidth/FullHeight and a zero MiniWidth alone.
        var miniReturnLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680, MiniWidth = 400, MiniHeight = 300 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 400, Height = 300, MiniWidth = 0, MiniHeight = 0 },
            ],
        };
        var scaledMiniReturn = LayoutPolicy.ScaleReturnSizes(miniReturnLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude], 1.2,
            new LogicalSize(376, 316), CardMode.Mini);
        var codexMiniReturn = scaledMiniReturn.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        var claudeMiniReturn = scaledMiniReturn.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(Math.Abs(codexMiniReturn.MiniWidth - 480) < 1e-6 && Math.Abs(codexMiniReturn.MiniHeight - 360) < 1e-6,
            "a non-zero remembered mini size is scaled by the ratio");
        Assert(codexMiniReturn.Width == 940 && codexMiniReturn.Height == 680, "the live (full) size is untouched by a mini-scale return-size rescale");
        Assert(claudeMiniReturn.MiniWidth == 0 && claudeMiniReturn.MiniHeight == 0, "a zero remembered mini size (a mini card) is left alone");
        var shrunkMiniReturn = LayoutPolicy.ScaleReturnSizes(miniReturnLayout, [ProviderCatalog.Codex], 0.5, new LogicalSize(390, 320), CardMode.Mini);
        var codexShrunkMiniReturn = shrunkMiniReturn.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        Assert(codexShrunkMiniReturn.MiniWidth == 390 && codexShrunkMiniReturn.MiniHeight == 320,
            "a remembered mini size scaled below the mini minimum is raised to it");
    }

    /// <summary>2.0 mini mode (2.0 design §8.1, per-card-minimum bullets).</summary>
    private static void PerCardMinimumsHandleMixedModeLayouts()
    {
        var fullMinimum = new LogicalSize(730, 680);
        var miniMinimum = new LogicalSize(400, 300);
        var layout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 700, Height = 600 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 350, Height = 250, FullWidth = 800, FullHeight = 700 },
            ],
        };
        var minimumByProvider = new Dictionary<string, LogicalSize>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = fullMinimum,
            [ProviderCatalog.Claude] = miniMinimum,
        };
        var raised = LayoutPolicy.EnsureMinimumSizes(layout, minimumByProvider, fullMinimum, miniMinimum);
        var codexRaised = raised.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        var claudeRaised = raised.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(codexRaised.Width == 730 && codexRaised.Height == 680, "a full card is raised to its own (full) minimum");
        Assert(claudeRaised.Width == 400 && claudeRaised.Height == 300, "a mini card is raised to its own (mini) minimum, not the full one");
        Assert(claudeRaised.FullWidth == 800 && claudeRaised.FullHeight == 700, "a remembered full size already above the full minimum is left alone");

        var lowReturnLayout = new LayoutSettings
        {
            Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300, FullWidth = 500, FullHeight = 400 }],
        };
        var raisedReturn = LayoutPolicy.EnsureMinimumSizes(lowReturnLayout,
            new Dictionary<string, LogicalSize>(StringComparer.Ordinal) { [ProviderCatalog.Codex] = miniMinimum }, fullMinimum, miniMinimum);
        var codexReturnRaised = raisedReturn.Cards.Single();
        Assert(codexReturnRaised.FullWidth == 730 && codexReturnRaised.FullHeight == 680,
            "a remembered full size below the full minimum is raised to it, even though the card's live size uses the mini minimum");

        // RecoverMissingMonitor: a mini group and a full group on one monitor; the mini card's saved size is
        // below the FULL minimum but must not be rejected, since it is checked against its own (mini) minimum.
        var monitor = new LogicalWorkArea("primary", new LogicalRect(0, 0, 1920, 1080), isPrimary: true);
        var mixedLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680, MonitorId = "primary", AnchorX = 0.1, AnchorY = 0.1 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 400, Height = 300, MonitorId = "primary", AnchorX = 0.8, AnchorY = 0.8 },
            ],
        };
        var mixedMinimumByProvider = new Dictionary<string, LogicalSize>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = fullMinimum,
            [ProviderCatalog.Claude] = miniMinimum,
        };
        var recoveredMixed = LayoutPolicy.RecoverMissingMonitor(mixedLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude],
            new Dictionary<string, LogicalWorkArea> { [monitor.MonitorId] = monitor }, monitor.MonitorId, mixedMinimumByProvider);
        Assert(recoveredMixed.Succeeded, "a mini group below the full minimum (but at or above its own mini minimum) is not rejected");
        Assert(recoveredMixed.ProposedRects[ProviderCatalog.Claude].Size == new LogicalSize(400, 300),
            "the mini group keeps its saved size, since it is checked against its own minimum, not the full one");

        // Validate rejects bad return sizes.
        Assert(new AppSettings { Layout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, FullWidth = -1, FullHeight = 400 }] } }
                .Validate() == "invalid_layout_card", "a negative FullWidth is rejected");
        Assert(new AppSettings { Layout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, FullWidth = 0, FullHeight = 400 }] } }
                .Validate() == "invalid_layout_card", "FullWidth zero while FullHeight is not is rejected");
        Assert(new AppSettings { Layout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, FullWidth = double.NaN, FullHeight = 400 }] } }
                .Validate() == "invalid_layout_card", "a non-finite FullWidth is rejected");
        Assert(new AppSettings { Layout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, FullWidth = 0, FullHeight = 0 }] } }
                .Validate() is null, "both zero (no remembered full size) is valid");

        // 2.0 mini mode (2.0 design §3.7, §8.1 "Validate pair rules"): MiniWidth/MiniHeight follows the
        // identical pair rule as FullWidth/FullHeight above.
        Assert(new AppSettings { Layout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, MiniWidth = -1, MiniHeight = 400 }] } }
                .Validate() == "invalid_layout_card", "a negative MiniWidth is rejected");
        Assert(new AppSettings { Layout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, MiniWidth = 0, MiniHeight = 400 }] } }
                .Validate() == "invalid_layout_card", "MiniWidth zero while MiniHeight is not is rejected");
        Assert(new AppSettings { Layout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, MiniWidth = double.NaN, MiniHeight = 400 }] } }
                .Validate() == "invalid_layout_card", "a non-finite MiniWidth is rejected");
        Assert(new AppSettings { Layout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, MiniWidth = 0, MiniHeight = 0 }] } }
                .Validate() is null, "both zero (no remembered mini size) is valid");
        Assert(new AppSettings { Layout = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, FullWidth = 940, FullHeight = 680, MiniWidth = 400, MiniHeight = 300 }] } }
                .Validate() is null, "a card carrying both a remembered full size and a remembered mini size validates - no cross-field rule between them");
    }

    /// <summary>Ensures mini cards have a nonzero full-size return size, preserving configured values.</summary>
    private static void EnsureReturnSizesFillsZerosOnlyForMiniCards()
    {
        var fullDefault = new LogicalSize(940, 680);

        // A lone mini card with no remembered full size yet gets the full default.
        var freshLayout = new LayoutSettings
        {
            Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300 }],
        };
        var freshMini = new HashSet<string>(StringComparer.Ordinal) { ProviderCatalog.Codex };
        var freshFilled = LayoutPolicy.EnsureReturnSizes(freshLayout, [ProviderCatalog.Codex], freshMini, fullDefault);
        var freshCodex = freshFilled.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        Assert(freshCodex.FullWidth == 940 && freshCodex.FullHeight == 680,
            "a mini card with a zero return size gets the full default");

        // A mini group whose root already carries a remembered full size fills its zero siblings from the root.
        var rootedLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300, FullWidth = 940, FullHeight = 680, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 400, Height = 300, GroupOrder = 1 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var rootedMini = new HashSet<string>(StringComparer.Ordinal) { ProviderCatalog.Codex, ProviderCatalog.Claude };
        var rootedFilled = LayoutPolicy.EnsureReturnSizes(rootedLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude], rootedMini, fullDefault);
        var rootedClaude = rootedFilled.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        var rootedCodex = rootedFilled.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        Assert(rootedClaude.FullWidth == 940 && rootedClaude.FullHeight == 680,
            "a mini group with a non-zero root fills its zero members from the root");
        Assert(rootedCodex.FullWidth == 940 && rootedCodex.FullHeight == 680,
            "the root itself, already non-zero, is left exactly as it was");

        // A group whose root is zero but another member is not fills from that member.
        var donorLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 400, Height = 300, GroupOrder = 0 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 400, Height = 300, FullWidth = 800, FullHeight = 600, GroupOrder = 1 },
            ],
            Edges = [new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 }],
        };
        var donorMini = new HashSet<string>(StringComparer.Ordinal) { ProviderCatalog.Codex, ProviderCatalog.Claude };
        var donorFilled = LayoutPolicy.EnsureReturnSizes(donorLayout, [ProviderCatalog.Codex, ProviderCatalog.Claude], donorMini, fullDefault);
        var donorCodex = donorFilled.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        Assert(donorCodex.FullWidth == 800 && donorCodex.FullHeight == 600,
            "a group whose root is zero but another member is not fills from that member");

        // A full card (not in miniProviderIds) is never touched, even at zero; a mini card outside the
        // enabled set is left exactly as it was too.
        var untouchedLayout = new LayoutSettings
        {
            Cards =
            [
                new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 940, Height = 680 },
                new CardLayoutSettings { ProviderId = ProviderCatalog.Claude, Width = 400, Height = 300 },
            ],
        };
        var onlyClaudeMini = new HashSet<string>(StringComparer.Ordinal) { ProviderCatalog.Claude };
        var untouchedFilled = LayoutPolicy.EnsureReturnSizes(untouchedLayout, [ProviderCatalog.Codex], onlyClaudeMini, fullDefault);
        var untouchedCodex = untouchedFilled.Cards.Single(c => c.ProviderId == ProviderCatalog.Codex);
        var untouchedClaude = untouchedFilled.Cards.Single(c => c.ProviderId == ProviderCatalog.Claude);
        Assert(untouchedCodex.FullWidth == 0 && untouchedCodex.FullHeight == 0, "a full card is never touched");
        Assert(untouchedClaude.FullWidth == 0 && untouchedClaude.FullHeight == 0,
            "a mini card not in the enabled set keeps its values");

        // Idempotent: a second call over the already-filled layout changes nothing.
        var secondPass = LayoutPolicy.EnsureReturnSizes(rootedFilled, [ProviderCatalog.Codex, ProviderCatalog.Claude], rootedMini, fullDefault);
        Assert(Serialize(secondPass) == Serialize(rootedFilled), "a second call finds no zeros left to fill");

        // The result always passes validation.
        Assert(freshFilled.Validate() is null && rootedFilled.Validate() is null && donorFilled.Validate() is null,
            "EnsureReturnSizes always returns a layout that passes LayoutSettings.Validate");
    }
}

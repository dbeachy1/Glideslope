using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>: unchecking "Snap cards together and to screen edges" and saving must break
/// every snapped group, and (20:20) a layout saved with snapping off before that rule existed must lose its
/// groups at startup. WindowCoordinator.LayoutForSnapSetting is the rule both paths apply.</summary>
internal static class SnapSettingSpecs
{
    public static void Run()
    {
        var layout = AppSettings.CreateDefault().Layout.Clone();
        layout.Edges.Add(new DockingEdgeSettings { FirstProviderId = "codex", SecondProviderId = "claude", FirstSide = CardDockSide.Right });
        layout.Edges.Add(new DockingEdgeSettings { FirstProviderId = "claude", SecondProviderId = "gemini", FirstSide = CardDockSide.Right });

        var off = WindowCoordinator.LayoutForSnapSetting(layout, snapping: false);
        Assert(!ReferenceEquals(off, layout) && off.Edges.Count == 0, "snapping off removes every docking edge");
        Assert(layout.Edges.Count == 2, "the committed layout is not changed in place");
        Assert(off.Cards.Count == layout.Cards.Count, "card placements are kept, so nothing moves");

        Assert(ReferenceEquals(WindowCoordinator.LayoutForSnapSetting(layout, snapping: true), layout),
            "snapping on keeps the groups");
        var loose = AppSettings.CreateDefault().Layout.Clone();
        Assert(ReferenceEquals(WindowCoordinator.LayoutForSnapSetting(loose, snapping: false), loose),
            "snapping off with nothing docked changes nothing");

        // every commit and every cancelled gesture's restored graph goes through
        // LayoutForSettings, so a disabled provider's card and, with Snap off, every edge are gone from what is written.
        var placed = AppSettings.CreateDefault().Layout.Clone();
        foreach (var id in new[] { "codex", "claude", "gemini" }) placed.Cards.Add(new CardLayoutSettings { ProviderId = id });
        placed.Edges.Add(new DockingEdgeSettings { FirstProviderId = "codex", SecondProviderId = "claude", FirstSide = CardDockSide.Right });
        placed.Edges.Add(new DockingEdgeSettings { FirstProviderId = "claude", SecondProviderId = "gemini", FirstSide = CardDockSide.Right });
        var withoutClaude = WindowCoordinator.LayoutForSettings(placed, ["codex", "gemini"], snapping: true, out var issue);
        Assert(issue is null && withoutClaude.Cards.Count == 2 && withoutClaude.Cards.All(card => card.ProviderId != "claude") &&
               withoutClaude.Edges.Count == 0, "a disabled provider's card and both of its edges are removed");
        var unsnapped = WindowCoordinator.LayoutForSettings(placed, ["codex", "claude", "gemini"], snapping: false, out _);
        Assert(unsnapped.Edges.Count == 0 && unsnapped.Cards.Count == 3, "with Snap off every edge is removed and every card kept");
        var kept = WindowCoordinator.LayoutForSettings(placed, ["codex", "claude", "gemini"], snapping: true, out _);
        Assert(kept.Edges.Count == 2 && placed.Cards.Count == 3 && placed.Edges.Count == 2,
            "with every provider enabled and Snap on nothing is removed, and the input is not changed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }
}

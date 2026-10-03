using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Level 1 spec for design doc §5.6 / Appendix C.9: WindowCoordinator.DockedSidesOf reads a card's
/// docked sides from the saved docking graph. An edge stores the side on its FIRST card; the second
/// card is docked on the opposite side. These sides drive both Ctrl + body-drag (non-empty means "in a
/// snapped group") and where the Undock button may appear.
/// </summary>
internal static class DockedSidesSpecs
{
    public static void Run()
    {
        // Codex's right side holds Claude, and Claude's bottom side holds Gemini.
        var layout = new LayoutSettings
        {
            Edges =
            [
                new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Codex, SecondProviderId = ProviderCatalog.Claude, FirstSide = CardDockSide.Right, Gap = 10 },
                new DockingEdgeSettings { FirstProviderId = ProviderCatalog.Claude, SecondProviderId = ProviderCatalog.Gemini, FirstSide = CardDockSide.Bottom, Gap = 10 },
            ],
        };
        AssertSides(layout, ProviderCatalog.Codex, [CardDockSide.Right], "the first card of an edge is docked on the edge's side");
        AssertSides(layout, ProviderCatalog.Claude, [CardDockSide.Left, CardDockSide.Bottom],
            "a card in two edges is docked on both: opposite of Codex's Right, and its own Bottom");
        AssertSides(layout, ProviderCatalog.Gemini, [CardDockSide.Top], "the second card of an edge is docked on the opposite side");

        var alone = new LayoutSettings { Edges = [] };
        AssertSides(alone, ProviderCatalog.Claude, [], "a card with no edges has no docked sides");
        AssertSides(layout, "not-a-provider", [], "a card that appears in no edge has no docked sides");

        // Design doc Appendix C.9 / H.2: a docked side is the side the neighbor is on,
        // so Undock nudges the card the opposite way, three inter-card gaps (30 px) away from it.
        AssertNudge(CardDockSide.Right, -30, 0, "undock on Right moves left, away from the neighbor on the right");
        AssertNudge(CardDockSide.Left, 30, 0, "undock on Left moves right, away from the neighbor on the left");
        AssertNudge(CardDockSide.Top, 0, 30, "undock on Top moves down, away from the neighbor above");
        AssertNudge(CardDockSide.Bottom, 0, -30, "undock on Bottom moves up, away from the neighbor below");

        // undocking the middle card of a Codex | Claude | Gemini row on its Right side used
        // to move it left, onto Codex. The nudge now skips any direction that overlaps a card it was docked with.
        var codex = new LogicalRect(0, 0, 940, 680);
        var claude = new LogicalRect(950, 0, 940, 680);
        var gemini = new LogicalRect(1900, 0, 940, 680);
        var middle = WindowCoordinator.ChooseUndockNudge(CardDockSide.Right, claude, [codex, gemini], _ => true);
        Assert(middle is { } m && m.X == 0 && m.Y == 30, $"the middle card is nudged down, since left would overlap Codex (was {middle})");
        var end = WindowCoordinator.ChooseUndockNudge(CardDockSide.Right, codex, [claude], _ => true);
        Assert(end is { } e && e.X == -30 && e.Y == 0, $"an end card still moves away from its only neighbor (was {end})");
        var upOnly = WindowCoordinator.ChooseUndockNudge(CardDockSide.Right, claude, [codex, gemini], delta => delta.Y < 0);
        Assert(upOnly is { } u && u.X == 0 && u.Y == -30, $"a nudge the work area refuses is skipped for the next one (was {upOnly})");
        Assert(WindowCoordinator.ChooseUndockNudge(CardDockSide.Right, claude, [codex, gemini], _ => false) is null,
            "with no acceptable nudge the card is only released");
    }

    private static void AssertNudge(CardDockSide side, double expectedX, double expectedY, string message)
    {
        var actual = WindowCoordinator.UndockNudge(side);
        Assert(actual.X == expectedX && actual.Y == expectedY,
            $"{message} ({side}: expected ({expectedX}, {expectedY}), got ({actual.X}, {actual.Y}))");
    }

    private static void AssertSides(LayoutSettings layout, string providerId, CardDockSide[] expected, string message)
    {
        var actual = WindowCoordinator.DockedSidesOf(layout, providerId);
        Assert(actual.Count == expected.Length && expected.All(actual.Contains),
            $"{message} ({providerId}: expected {{{string.Join(", ", expected)}}}, got {{{string.Join(", ", actual)}}})");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }
}

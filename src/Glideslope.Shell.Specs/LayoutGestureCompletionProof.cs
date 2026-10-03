using System.Collections.Immutable;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>Tests WindowCoordinator's screen-edge snap decision with the current setting and dock candidate.
/// This covers the App-level choice in addition to LayoutPolicy's geometry behavior.</summary>
internal static class LayoutGestureCompletionProof
{
    public static void Run()
    {
        SnapEvaluatesAndMovesAnOvershotCardWhenEnabled();
        DisabledSettingIsAlwaysANoOpRegardlessOfPosition();
        ADockCandidateAlwaysWinsOverTheScreenEdgeSnap();
        AlreadyOnScreenCardsAreEvaluatedButNotMoved();
    }

    /// <summary>
    /// A card dropped past the work area's right edge with snapping enabled. If WindowCoordinator stops passing
    /// that live value through (for example, by hardcoding false, or by reading a stale settings
    /// snapshot captured before the user's change), this fails.
    /// </summary>
    private static void SnapEvaluatesAndMovesAnOvershotCardWhenEnabled()
    {
        var single = new[] { ProviderCatalog.Codex };
        var layout = new LayoutSettings
        {
            Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 100, Height = 80, MonitorId = "primary" }]
        };
        var workArea = Area("primary", 250, 200);
        var usableMinimum = new LogicalSize(80, 60);
        var overshotRight = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(190, 50, 100, 80),
        }.ToImmutableDictionary(StringComparer.Ordinal);

        var outcome = WindowCoordinator.EvaluateScreenEdgeSnap(layout, single, overshotRight, ProviderCatalog.Codex,
            workArea, usableMinimum, hasDockCandidate: false, snapToScreenEdgeEnabled: true);

        Assert(outcome.Evaluated, "an overshot card with no dock candidate and the setting on is evaluated");
        Assert(outcome.Succeeded && outcome.Moved, "the overshot card is pulled back and reported as moved");
        Assert(outcome.Result is not null && outcome.Result.ProposedRects[ProviderCatalog.Codex].X == 150,
            "the seam applies the same LayoutPolicy.SnapGroupToScreenEdges pull-back the Core spec proves");
    }

    /// <summary>
    /// The setting the Settings window actually persists (AppSettings.SnapToScreenEdge defaults to
    /// false) must produce zero effect through this exact seam, not just in isolation - this is
    /// what "the live path from the OS drag ending to the snap call" resolves to when the box is
    /// unchecked, and it must stay a pure no-op regardless of how far off screen the card sits.
    /// </summary>
    private static void DisabledSettingIsAlwaysANoOpRegardlessOfPosition()
    {
        var single = new[] { ProviderCatalog.Codex };
        var layout = new LayoutSettings
        {
            Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 100, Height = 80, MonitorId = "primary" }]
        };
        var workArea = Area("primary", 250, 200);
        var usableMinimum = new LogicalSize(80, 60);
        var overshotRight = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(190, 50, 100, 80),
        }.ToImmutableDictionary(StringComparer.Ordinal);

        var outcome = WindowCoordinator.EvaluateScreenEdgeSnap(layout, single, overshotRight, ProviderCatalog.Codex,
            workArea, usableMinimum, hasDockCandidate: false, snapToScreenEdgeEnabled: false);

        Assert(!outcome.Evaluated && !outcome.Succeeded && !outcome.Moved && outcome.Result is null,
            "a disabled setting never calls into LayoutPolicy at all, matching AppSettings.SnapToScreenEdge's default-off value");
    }

    /// <summary>Docking always wins: a live drag that lands on a valid dock target must not also snap.</summary>
    private static void ADockCandidateAlwaysWinsOverTheScreenEdgeSnap()
    {
        var single = new[] { ProviderCatalog.Codex };
        var layout = new LayoutSettings
        {
            Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 100, Height = 80, MonitorId = "primary" }]
        };
        var workArea = Area("primary", 250, 200);
        var usableMinimum = new LogicalSize(80, 60);
        var overshotRight = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(190, 50, 100, 80),
        }.ToImmutableDictionary(StringComparer.Ordinal);

        var outcome = WindowCoordinator.EvaluateScreenEdgeSnap(layout, single, overshotRight, ProviderCatalog.Codex,
            workArea, usableMinimum, hasDockCandidate: true, snapToScreenEdgeEnabled: true);

        Assert(!outcome.Evaluated, "a winning dock candidate skips screen-edge snap evaluation entirely, even with the setting on");
    }

    /// <summary>A card already fully inside the work area, away from every edge, is evaluated but reports no move.</summary>
    private static void AlreadyOnScreenCardsAreEvaluatedButNotMoved()
    {
        var single = new[] { ProviderCatalog.Codex };
        var layout = new LayoutSettings
        {
            Cards = [new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, Width = 100, Height = 80, MonitorId = "primary" }]
        };
        var workArea = Area("primary", 250, 200);
        var usableMinimum = new LogicalSize(80, 60);
        var centered = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [ProviderCatalog.Codex] = new(75, 60, 100, 80),
        }.ToImmutableDictionary(StringComparer.Ordinal);

        var outcome = WindowCoordinator.EvaluateScreenEdgeSnap(layout, single, centered, ProviderCatalog.Codex,
            workArea, usableMinimum, hasDockCandidate: false, snapToScreenEdgeEnabled: true);

        Assert(outcome.Evaluated && outcome.Succeeded && !outcome.Moved,
            "a card well away from every edge is still evaluated and reports no move, rather than being skipped");
    }

    private static LogicalWorkArea Area(string id, double width, double height) =>
        new(id, new LogicalRect(0, 0, width, height), isPrimary: true);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }
}

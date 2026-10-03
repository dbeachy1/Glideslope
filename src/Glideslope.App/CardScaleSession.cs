using System.Collections.Immutable;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>One run of card-size slider steps. Every step scales from
/// BaselineGeometry/BaselineLayout at BaselinePercent, so returning to the start value restores the start layout
/// exactly instead of compounding rounding and reachability moves. The run continues only while nothing else has
/// changed the layout since the last step (same layout object, same rects, same monitors); otherwise the next step
/// starts a new run from the current state.</summary>
internal sealed record CardScaleSession(
    CardLayoutGeometrySnapshot BaselineGeometry,
    LayoutSettings BaselineLayout,
    int BaselinePercent,
    ImmutableDictionary<string, LogicalRect> LastAppliedRects,
    LayoutSettings LastLayout,
    // The mode this run of slider steps belongs to. A step requested for the
    // other mode is never "current" - see IsCurrent - so it always starts a fresh session at that mode's own
    // baseline instead of scaling from a baseline the wrong mode's cards were captured at.
    CardMode Mode)
{
    public bool IsCurrent(CardLayoutGeometrySnapshot current, LayoutSettings layout, CardMode mode) =>
        mode == Mode &&
        ReferenceEquals(layout, LastLayout) &&
        CardLayoutGeometryComparison.RectsEqual(LastAppliedRects, current.Rects) &&
        CardLayoutGeometryComparison.MonitorsEqual(BaselineGeometry.Monitors, current.Monitors);
}

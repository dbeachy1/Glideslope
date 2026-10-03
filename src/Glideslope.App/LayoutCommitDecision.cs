namespace Glideslope.App;

/// <summary>What WindowCoordinator.CommitGesture does once a gesture settles (design doc S5.4).</summary>
internal enum LayoutCommitAction { Snap, ScreenSnap, ResizeApply, ResizeRevert, Place }

/// <summary>
/// The pure half of the commit decision from design doc S5.4: given the gesture's kind and three
/// facts gathered at commit time - whether Snap is on, whether a magnet candidate is currently
/// valid, and (for a resize) whether the final size still fits - decide what to do. WindowCoordinator
/// gathers those facts against live geometry, settings, and LayoutPolicy, then asks this function
/// what to do; the decision itself needs no window, settings store, or LayoutPolicy call, so
/// LayoutCommitDecisionSpecs covers every combination directly.
/// </summary>
internal static class LayoutCommitDecision
{
    public static LayoutCommitAction Decide(LayoutGestureKind kind, bool snapOn, bool hasCandidate, bool resizeFits)
    {
        // A gesture with no geometry change remains a no-op and keeps the current placement.
        if (kind == LayoutGestureKind.Pending) return LayoutCommitAction.Place;

        // Resizes use the final size-fit result; snapping and magnet candidates apply only to moves.
        if (kind == LayoutGestureKind.Resize)
            return resizeFits ? LayoutCommitAction.ResizeApply : LayoutCommitAction.ResizeRevert;

        // Move or Detach: a winning magnet candidate always takes priority over a screen-edge snap
        // (design doc S5.4 items 2-3), and both require Snap to be on (design doc S2.2).
        if (snapOn && hasCandidate) return LayoutCommitAction.Snap;
        if (snapOn) return LayoutCommitAction.ScreenSnap;
        return LayoutCommitAction.Place;
    }
}

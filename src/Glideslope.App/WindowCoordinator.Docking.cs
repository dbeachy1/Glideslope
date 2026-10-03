using Glideslope.Core;

namespace Glideslope.App;

// WindowCoordinator, docking: the magnet candidate shown while a card is dragged near another, the docked sides
// of each card, and the Undock button's release and nudge.
// Handles dock state, magnet feedback, and undocking.
internal sealed partial class WindowCoordinator
{
    private MagnetCandidate? _magnetCandidate;

    /// <summary>
    /// The candidate search selects the nearest facing edge within the acquisition distance, overlapping along
    /// the edge, then validates it with
    /// PreviewDock. Callers only invoke this while Snap is on; the gating itself lives in
    /// OnCardGeometryChanged and CommitGesture, not here, so the search logic stays a pure function
    /// of geometry.
    /// </summary>
    private MagnetCandidate? FindMagnetCandidate(CardLayoutGeometrySnapshot geometry, string leadId)
    {
        var incoming = geometry.Rects[leadId];
        MagnetCandidate? best = null;
        var sourceLayout = _settings.Layout.Clone();
        var bestDistance = double.PositiveInfinity;
        foreach (var destinationId in _settings.EnabledProviderIds.Where(id => id != leadId))
        {
            var target = geometry.Rects[destinationId];
            foreach (var side in Enum.GetValues<CardDockSide>())
            {
                var distance = side switch
                {
                    CardDockSide.Top => Math.Abs(target.Y - incoming.Bottom),
                    CardDockSide.Right => Math.Abs(target.Right - incoming.X),
                    CardDockSide.Bottom => Math.Abs(target.Bottom - incoming.Y),
                    _ => Math.Abs(target.X - incoming.Right)
                };
                var perpendicularOverlap = side is CardDockSide.Top or CardDockSide.Bottom
                    ? incoming.X < target.Right && incoming.Right > target.X
                    : incoming.Y < target.Bottom && incoming.Bottom > target.Y;
                if (!perpendicularOverlap) continue;
                if (distance > LayoutPolicy.DefaultAcquisitionDistance || distance >= bestDistance) continue;
                var preview = LayoutPolicy.PreviewDock(sourceLayout, _settings.EnabledProviderIds,
                    geometry.Rects, destinationId, leadId, side, LayoutPolicy.DefaultInterCardGap,
                    WorkAreaFor(geometry, destinationId), UsableMinimumFor(destinationId));
                if (!preview.Succeeded) continue;
                var incomingIds = LayoutPolicy.GetComponentProviderIds(sourceLayout,
                    _settings.EnabledProviderIds, leadId);
                best = new MagnetCandidate(preview, leadId, destinationId, side, incomingIds, sourceLayout.Clone(), geometry);
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>
    /// Design doc §6: shows the magnet on the lead's side facing the candidate and on the target's
    /// facing side, and clears it on a candidate change (a different target, or a different side of
    /// the same target). Recomputing the same candidate again (no visible change) still refreshes
    /// the stored snapshot, so IsCurrent at commit compares against up-to-date geometry.
    /// </summary>
    private void UpdateMagnet(CardLayoutGeometrySnapshot geometry, string leadId)
    {
        var candidate = FindMagnetCandidate(geometry, leadId);
        var previous = _magnetCandidate;
        var unchanged = previous is not null && candidate is not null &&
            previous.DestinationProviderId == candidate.DestinationProviderId &&
            previous.DestinationSide == candidate.DestinationSide;
        if (unchanged)
        {
            _magnetCandidate = candidate;
            return;
        }

        ClearMagnetAndCandidate();
        _magnetCandidate = candidate;
        if (candidate is null) return;
        if (_windows.TryGetValue(leadId, out var leadWindow)) leadWindow.ShowMagnet(Opposite(candidate.DestinationSide));
        if (_windows.TryGetValue(candidate.DestinationProviderId, out var targetWindow)) targetWindow.ShowMagnet(candidate.DestinationSide);
        _diagnostics.Record(new DiagnosticEvent("layout_magnet_shown", leadId,
            Status: $"target={candidate.DestinationProviderId},side={candidate.DestinationSide}"));
    }

    /// <summary>Hides every card's magnet bar and forgets the candidate. Design doc §6: clears on a
    /// candidate change (see UpdateMagnet), on commit, and on cancel.</summary>
    private void ClearMagnetAndCandidate()
    {
        var previous = _magnetCandidate;
        if (previous is null) return;
        _magnetCandidate = null;
        foreach (var window in _windows.Values) window.ClearMagnet();
        _diagnostics.Record(new DiagnosticEvent("layout_magnet_cleared", previous.LeadProviderId, previous.DestinationProviderId));
    }

    private static CardDockSide Opposite(CardDockSide side) => side switch
    {
        CardDockSide.Top => CardDockSide.Bottom,
        CardDockSide.Right => CardDockSide.Left,
        CardDockSide.Bottom => CardDockSide.Top,
        CardDockSide.Left => CardDockSide.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(side)),
    };

    /// <summary>Window design §5.6: the sides of one card that carry a docking edge. An edge stores the side
    /// on its FIRST card; the second card is docked on the opposite side.</summary>
    internal static IReadOnlySet<CardDockSide> DockedSidesOf(LayoutSettings layout, string providerId)
    {
        var sides = new HashSet<CardDockSide>();
        foreach (var edge in layout.Edges)
        {
            if (edge.FirstProviderId == providerId) sides.Add(edge.FirstSide);
            else if (edge.SecondProviderId == providerId) sides.Add(Opposite(edge.FirstSide));
        }
        return sides;
    }

    private void OnUndockRequested(ProviderUsageCardWindow window, CardDockSide side)
    {
        if (_closing || !_windows.ContainsKey(window.ProviderId)) return;
        // Finish the active gesture before undocking.
        InterruptActiveGesture("undock_requested");
        if (!TryCaptureGeometry(out var geometry)) return;
        var formerGroup = LayoutPolicy.GetComponentProviderIds(_settings.Layout, _settings.EnabledProviderIds, window.ProviderId);
        var detached = LayoutPolicy.DetachCard(_settings.Layout, _settings.EnabledProviderIds, geometry.Rects,
            window.ProviderId, WorkAreaFor(geometry, window.ProviderId), UsableMinimumFor(window.ProviderId));
        if (!detached.Succeeded || detached.ProposedSettings is null)
        {
            _diagnostics.Record(new DiagnosticEvent("layout_undock_rejected", window.ProviderId, detached.SafeErrorCode));
            return;
        }
        _settings.Layout = detached.ProposedSettings;
        // The nudge must not push the card onto one of the cards it was docked with. Undocking the
        // middle card of A–B–C on its Right side moved it left, into A. ChooseUndockNudge tries away from the side first
        // and then the two directions across it, and MoveGroup still refuses one that leaves the work area.
        var neighbors = formerGroup.Where(id => id != window.ProviderId && geometry.Rects.ContainsKey(id))
            .Select(id => geometry.Rects[id]).ToArray();
        LayoutPolicyResult? moved = null;
        var nudge = ChooseUndockNudge(side, geometry.Rects[window.ProviderId], neighbors, delta =>
        {
            var attempt = LayoutPolicy.MoveGroup(_settings.Layout, _settings.EnabledProviderIds, geometry.Rects,
                window.ProviderId, delta, WorkAreaFor(geometry, window.ProviderId), UsableMinimumFor(window.ProviderId));
            if (!attempt.Succeeded) return false;
            moved = attempt;
            return true;
        });
        if (moved is not null) ApplyLayoutResult(moved, geometry);
        geometry = CaptureCardGeometry();
        _layoutGeometry = geometry;
        RefreshCardGroupState(geometry);
        PersistCapturedLayout(geometry);
        _diagnostics.Record(new DiagnosticEvent("layout_undock", window.ProviderId,
            Status: $"side={side},moved={moved is not null},nudge={(nudge is { } n ? $"{n.X:0},{n.Y:0}" : "none")}"));
    }

    /// <summary>A docked side is the side the
    /// NEIGHBOR is on, so "away from that edge" is the opposite direction. Undock on Right moves LEFT.</summary>
    internal static LogicalPoint UndockNudge(CardDockSide side)
    {
        const double nudge = 3 * LayoutPolicy.DefaultInterCardGap;
        return side switch
        {
            CardDockSide.Top => new LogicalPoint(0, nudge),
            CardDockSide.Bottom => new LogicalPoint(0, -nudge),
            CardDockSide.Left => new LogicalPoint(nudge, 0),
            _ => new LogicalPoint(-nudge, 0),
        };
    }

    /// <summary>The nudges Undock tries, in order: away from the clicked side
    /// (<see cref="UndockNudge"/>), then 30 px across it (down, then up, for Left/Right; right, then left, for Top/Bottom).</summary>
    internal static IReadOnlyList<LogicalPoint> UndockNudgeCandidates(CardDockSide side)
    {
        const double nudge = 3 * LayoutPolicy.DefaultInterCardGap;
        return side is CardDockSide.Left or CardDockSide.Right
            ? [UndockNudge(side), new LogicalPoint(0, nudge), new LogicalPoint(0, -nudge)]
            : [UndockNudge(side), new LogicalPoint(nudge, 0), new LogicalPoint(-nudge, 0)];
    }

    /// <summary>The first candidate nudge that puts <paramref name="card"/> on none of the
    /// cards it was docked with and that <paramref name="accept"/> (the MoveGroup work-area check) takes; null when
    /// none does, and the card is then only released.</summary>
    internal static LogicalPoint? ChooseUndockNudge(CardDockSide side, LogicalRect card,
        IReadOnlyCollection<LogicalRect> formerNeighbors, Func<LogicalPoint, bool> accept)
    {
        ArgumentNullException.ThrowIfNull(formerNeighbors);
        ArgumentNullException.ThrowIfNull(accept);
        foreach (var delta in UndockNudgeCandidates(side))
        {
            var moved = card.Translate(delta.X, delta.Y);
            if (formerNeighbors.Any(neighbor => moved.X < neighbor.Right && neighbor.X < moved.Right &&
                                                moved.Y < neighbor.Bottom && neighbor.Y < moved.Bottom))
                continue;
            if (accept(delta)) return delta;
        }
        return null;
    }
}

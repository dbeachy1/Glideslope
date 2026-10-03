using System.Collections.Immutable;

namespace Glideslope.Core;

// LayoutPolicy, the gesture operations: dock preview, group move, screen-edge snap, the Linux realign and
// containment (window design §20 R3, R4), detach and group resize. Each validates its input, proposes new
// rectangles through the shared reflow in LayoutPolicy.cs, and returns an accepted or rejected result.
// Governed by the window design (§3 card-to-card snapping, §20 Linux).
public static partial class LayoutPolicy
{
    public static LayoutPolicyResult PreviewDock(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        string destinationProviderId,
        string incomingProviderId,
        CardDockSide sideOnDestination,
        double gap,
        LogicalWorkArea workArea,
        LogicalSize usableMinimum)
    {
        var graph = ValidateInput(layout, enabledProviderIds, currentRects);
        if (graph.Error is not null) return Reject(graph.Error);
        if (!graph.Nodes.Contains(destinationProviderId) || !graph.Nodes.Contains(incomingProviderId))
            return Reject("layout_unknown_card");
        if (destinationProviderId == incomingProviderId || !Enum.IsDefined(sideOnDestination) || !double.IsFinite(gap) || gap < 0)
            return Reject("layout_invalid_dock_request");

        var destinationGroup = Component(layout, graph.Nodes, destinationProviderId);
        var incomingGroup = Component(layout, graph.Nodes, incomingProviderId);
        if (destinationGroup.Overlaps(incomingGroup)) return Reject("layout_same_component");

        var destinationRects = destinationGroup.ToDictionary(id => id, id => currentRects[id], StringComparer.Ordinal);
        var destinationSizes = destinationRects.Values.Select(rect => rect.Size).Distinct().ToArray();
        if (destinationSizes.Length != 1) return Reject("layout_destination_size_mismatch");
        var targetSize = destinationSizes[0];
        if (!IsAtLeast(targetSize, usableMinimum)) return Reject("layout_too_small");

        var targetRect = currentRects[destinationProviderId];
        LogicalRect incomingAnchor;
        ReflowResult incomingRects;
        try
        {
            incomingAnchor = PlaceBeside(targetRect, targetSize, sideOnDestination, gap);
            incomingRects = ReflowComponent(layout, incomingGroup, incomingProviderId, incomingAnchor, targetSize);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Reject("layout_invalid_position");
        }
        if (incomingRects.Error is not null) return Reject(incomingRects.Error);

        var proposedRects = currentRects.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (providerId, rect) in incomingRects.Rects) proposedRects[providerId] = rect;
        var affected = destinationGroup.Concat(incomingGroup).ToHashSet(StringComparer.Ordinal);
        var placementError = ValidatePlacements(proposedRects, affected, layout, workArea, usableMinimum);
        if (placementError is not null) return Reject(placementError);

        // 2.0 mini mode (2.0 design §3.2): the destination's remembered full size, read before any card is
        // created below (0 when the destination has no card entry yet). Every incoming member adopts it, the
        // same way every incoming member already adopts the destination's Width/Height above: a full target
        // has 0, so an incoming mini group becomes full and forgets its old full size; a mini target has its
        // group's full size, so an incoming full group becomes mini and will return to the target's full size.
        // §3.7: the destination's remembered MINI size travels the same way, beside FullWidth/FullHeight, so
        // an incoming group that becomes full still remembers the mini size it will return to later.
        var destinationCard = layout.Cards.FirstOrDefault(card => card.ProviderId == destinationProviderId);
        var destinationFullWidth = destinationCard?.FullWidth ?? 0;
        var destinationFullHeight = destinationCard?.FullHeight ?? 0;
        var destinationMiniWidth = destinationCard?.MiniWidth ?? 0;
        var destinationMiniHeight = destinationCard?.MiniHeight ?? 0;

        var proposedSettings = layout.Clone();
        proposedSettings.Edges.Add(new DockingEdgeSettings
        {
            FirstProviderId = destinationProviderId,
            SecondProviderId = incomingProviderId,
            FirstSide = sideOnDestination,
            Gap = gap,
        });
        var graphError = ValidateGraph(proposedSettings, graph.Nodes);
        if (graphError is not null) return Reject(graphError);
        CaptureComponent(proposedSettings, proposedRects, affected, workArea);
        foreach (var member in incomingGroup)
        {
            var card = GetOrCreateCard(proposedSettings, member);
            card.FullWidth = destinationFullWidth;
            card.FullHeight = destinationFullHeight;
            card.MiniWidth = destinationMiniWidth;
            card.MiniHeight = destinationMiniHeight;
        }
        return Accept(proposedSettings, proposedRects);
    }

    public static LayoutPolicyResult MoveGroup(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        string providerId,
        LogicalPoint delta,
        LogicalWorkArea workArea,
        LogicalSize usableMinimum)
    {
        var graph = ValidateInput(layout, enabledProviderIds, currentRects);
        if (graph.Error is not null) return Reject(graph.Error);
        if (!graph.Nodes.Contains(providerId)) return Reject("layout_unknown_card");
        var group = Component(layout, graph.Nodes, providerId);
        if (group.Select(id => currentRects[id].Size).Distinct().Count() != 1)
            return Reject("layout_group_size_mismatch");
        var proposedRects = currentRects.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        try
        {
            foreach (var member in group) proposedRects[member] = currentRects[member].Translate(delta.X, delta.Y);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Reject("layout_invalid_position");
        }
        var error = ValidatePlacements(proposedRects, group, layout, workArea, usableMinimum);
        if (error is not null) return Reject(error);

        var proposedSettings = layout.Clone();
        CaptureComponent(proposedSettings, proposedRects, group, workArea);
        return Accept(proposedSettings, proposedRects);
    }

    /// <summary>
    /// Snaps the moved component's outer bounds to the nearest work-area edge on each axis when it is
    /// within the logical acquisition distance. A disabled setting is a strict no-op.
    /// </summary>
    public static LayoutPolicyResult SnapGroupToScreenEdges(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        string providerId,
        LogicalWorkArea workArea,
        LogicalSize usableMinimum,
        bool enabled,
        double acquisitionDistance = DefaultAcquisitionDistance)
    {
        var graph = ValidateInput(layout, enabledProviderIds, currentRects);
        if (graph.Error is not null) return Reject(graph.Error);
        if (!graph.Nodes.Contains(providerId)) return Reject("layout_unknown_card");
        if (!double.IsFinite(acquisitionDistance) || acquisitionDistance < 0)
            return Reject("layout_invalid_snap_distance");
        if (!enabled) return Accept(layout.Clone(), currentRects.ToImmutableDictionary(StringComparer.Ordinal));

        var group = Component(layout, graph.Nodes, providerId);
        var left = group.Min(id => currentRects[id].X);
        var top = group.Min(id => currentRects[id].Y);
        var right = group.Max(id => currentRects[id].Right);
        var bottom = group.Max(id => currentRects[id].Bottom);
        var deltaX = NearestEdgeDelta(workArea.Bounds.X - left, workArea.Bounds.Right - right, acquisitionDistance);
        var deltaY = NearestEdgeDelta(workArea.Bounds.Y - top, workArea.Bounds.Bottom - bottom, acquisitionDistance);
        if (deltaX == 0 && deltaY == 0)
            return Accept(layout.Clone(), currentRects.ToImmutableDictionary(StringComparer.Ordinal));

        return MoveGroup(layout, enabledProviderIds, currentRects, providerId,
            new LogicalPoint(deltaX, deltaY), workArea, usableMinimum);
    }

    /// <summary>
    /// Rebuilds the lead's group from the lead's
    /// rectangle through the dock graph - every member takes the lead's size, sits on its edge's side at its edge's
    /// gap - so a group whose windows drifted out of line (on GNOME the window manager moves app-placed windows
    /// itself, §20.1) is put back in shape instead of every later MoveGroup rejecting it as layout_overlap. Only the
    /// group's own shape is restored: no containment or cross-group overlap check. The lead never moves. The settings
    /// are returned unchanged; the caller captures anchors from the live geometry afterward as usual.
    /// </summary>
    public static LayoutPolicyResult RealignGroup(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        string leadProviderId)
    {
        var graph = ValidateInput(layout, enabledProviderIds, currentRects);
        if (graph.Error is not null) return Reject(graph.Error);
        if (!graph.Nodes.Contains(leadProviderId)) return Reject("layout_unknown_card");
        var group = Component(layout, graph.Nodes, leadProviderId);
        var leadRect = currentRects[leadProviderId];
        // Reject an out-of-range reflow so the coordinator can continue the gesture without realigning the group.
        ReflowResult reflowed;
        try
        {
            reflowed = ReflowComponent(layout, group, leadProviderId, leadRect, leadRect.Size);
        }
        catch (ArgumentOutOfRangeException) { return Reject("layout_invalid_position"); }
        if (reflowed.Error is not null) return Reject(reflowed.Error);
        var proposedRects = currentRects.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (providerId, rect) in reflowed.Rects) proposedRects[providerId] = rect;
        return Accept(layout.Clone(), proposedRects);
    }

    /// <summary>
    /// Window design doc §20 R4 (2.1.4): translates the lead's group by the least distance that puts its bounds
    /// inside <paramref name="workArea"/>, pinned to the work area's top-left on an axis where the group is larger
    /// (<see cref="ClampOrPinToStart"/>). Used on Linux only, where GNOME pushes app-placed windows back inside the
    /// work area, so a group parked past an edge would lose its shape. A group already inside comes back unchanged.
    /// </summary>
    public static LayoutPolicyResult ContainGroup(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        string leadProviderId,
        LogicalWorkArea workArea)
    {
        ArgumentNullException.ThrowIfNull(workArea);
        var graph = ValidateInput(layout, enabledProviderIds, currentRects);
        if (graph.Error is not null) return Reject(graph.Error);
        if (!graph.Nodes.Contains(leadProviderId)) return Reject("layout_unknown_card");
        var group = Component(layout, graph.Nodes, leadProviderId);
        var left = group.Min(id => currentRects[id].X);
        var top = group.Min(id => currentRects[id].Y);
        var width = group.Max(id => currentRects[id].Right) - left;
        var height = group.Max(id => currentRects[id].Bottom) - top;
        var deltaX = ClampOrPinToStart(left, workArea.Bounds.X, workArea.Bounds.Right - width) - left;
        var deltaY = ClampOrPinToStart(top, workArea.Bounds.Y, workArea.Bounds.Bottom - height) - top;
        var proposedRects = currentRects.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (deltaX == 0 && deltaY == 0) return Accept(layout.Clone(), proposedRects);
        try
        {
            foreach (var member in group) proposedRects[member] = currentRects[member].Translate(deltaX, deltaY);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Reject("layout_invalid_position");
        }
        var proposedSettings = layout.Clone();
        CaptureComponent(proposedSettings, proposedRects, group, workArea);
        return Accept(proposedSettings, proposedRects);
    }

    // Correct a small edge overshoot while allowing a group to be parked farther offscreen. A later restore
    // brings the group back into a reachable position.
    private static double NearestEdgeDelta(double firstDelta, double secondDelta, double threshold)
    {
        var firstDistance = Math.Abs(firstDelta);
        var secondDistance = Math.Abs(secondDelta);
        // firstDelta > 0 means the group's leading (left/top) edge is outside the work area on that
        // side; secondDelta < 0 means the group's trailing (right/bottom) edge is outside on that
        // side. Either overshoot is corrected when it is no more than the pull-back distance.
        var firstOvershoot = firstDelta > 0 && firstDistance <= ScreenEdgeOvershootPullBackDistance;
        var secondOvershoot = secondDelta < 0 && secondDistance <= ScreenEdgeOvershootPullBackDistance;
        var firstEligible = firstOvershoot || firstDistance <= threshold;
        var secondEligible = secondOvershoot || secondDistance <= threshold;
        if (!firstEligible && !secondEligible) return 0;
        if (firstEligible && secondEligible)
        {
            // An overshoot correction always takes precedence over a mere near-threshold snap.
            if (firstOvershoot != secondOvershoot) return firstOvershoot ? firstDelta : secondDelta;
            // First edge wins exact ties: left before right, top before bottom.
            return firstDistance <= secondDistance ? firstDelta : secondDelta;
        }
        return firstEligible ? firstDelta : secondDelta;
    }

    /// <summary>Removes only edges incident to the chosen card; resulting components keep their positions.</summary>
    public static LayoutPolicyResult DetachCard(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        string providerId,
        LogicalWorkArea workArea,
        LogicalSize usableMinimum)
    {
        var graph = ValidateInput(layout, enabledProviderIds, currentRects);
        if (graph.Error is not null) return Reject(graph.Error);
        if (!graph.Nodes.Contains(providerId)) return Reject("layout_unknown_card");
        var affected = Component(layout, graph.Nodes, providerId);
        // Detaching only removes graph edges; it does not move the group, so containment is irrelevant. Size and
        // overlap are still checked.
        var error = ValidatePlacements(currentRects, affected, layout, workArea, usableMinimum, requireInsideWorkArea: false);
        if (error is not null) return Reject(error);

        var proposedSettings = layout.Clone();
        proposedSettings.Edges.RemoveAll(edge => edge.FirstProviderId == providerId || edge.SecondProviderId == providerId);
        CaptureComponent(proposedSettings, currentRects, affected, workArea);
        return Accept(proposedSettings, currentRects);
    }

    /// <summary>Resizes one connected component uniformly, preserving its oriented edge offsets.</summary>
    /// <remarks><paramref name="requireInsideWorkArea"/> false skips only the work-area containment check;
    /// size and overlap are still checked. User resizes and resets allow a group parked partly off screen to be
    /// resized in place.</remarks>
    public static LayoutPolicyResult ResizeGroup(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        string providerId,
        LogicalSize requestedSize,
        LogicalWorkArea workArea,
        LogicalSize usableMinimum,
        bool requireInsideWorkArea = true)
    {
        var graph = ValidateInput(layout, enabledProviderIds, currentRects);
        if (graph.Error is not null) return Reject(graph.Error);
        if (!graph.Nodes.Contains(providerId)) return Reject("layout_unknown_card");
        if (!IsAtLeast(requestedSize, usableMinimum)) return Reject("layout_too_small");
        var group = Component(layout, graph.Nodes, providerId);
        var currentAnchor = currentRects[providerId];
        ReflowResult reflowed;
        try
        {
            var resizedAnchor = new LogicalRect(currentAnchor.X, currentAnchor.Y, requestedSize.Width, requestedSize.Height);
            reflowed = ReflowComponent(layout, group, providerId, resizedAnchor, requestedSize);
        }
        catch (ArgumentOutOfRangeException) { return Reject("layout_invalid_position"); }
        if (reflowed.Error is not null) return Reject(reflowed.Error);
        var proposedRects = currentRects.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (member, rect) in reflowed.Rects) proposedRects[member] = rect;
        var error = ValidatePlacements(proposedRects, group, layout, workArea, usableMinimum, requireInsideWorkArea);
        if (error is not null) return Reject(error);

        var proposedSettings = layout.Clone();
        CaptureComponent(proposedSettings, proposedRects, group, workArea);
        return Accept(proposedSettings, proposedRects);
    }
}

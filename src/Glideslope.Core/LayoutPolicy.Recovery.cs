using System.Collections.Immutable;

namespace Glideslope.Core;

// LayoutPolicy, capture and restore: the reachable-placement rule (Requirements §4), reconciling disabled
// cards, capturing normalized anchors per group, and restoring them into the current monitors, fitting,
// shrinking, splitting or pinning a group that does not fit its work area.
public static partial class LayoutPolicy
{
    /// <summary>
    /// On each axis, a placed component no
    /// larger than the work area lies inside it, and one larger than the work area starts at the work area's start
    /// (left or top), so its title row and top-left corner are on screen and it can be dragged. Only a single card
    /// can be that large: RecoverMissingMonitor splits a docked group that does not fit, so the title row of every
    /// card is on screen. This is the guarantee every
    /// restore gives (startup, Reset window positions, a display change, a card-size change). Between restores a
    /// user's own placement is kept as dropped, even partly off screen; it is saved with clamped anchors, so the
    /// next restore makes it reachable. ReachTolerance absorbs the floating-point error of translating a reflowed
    /// group (a millionth of a logical pixel).
    /// </summary>
    public static bool IsReachablePlacement(LogicalRect area, IEnumerable<LogicalRect> groupRects)
    {
        ArgumentNullException.ThrowIfNull(groupRects);
        var rects = groupRects.ToArray();
        if (rects.Length == 0) return true;
        return AxisReachable(rects.Min(rect => rect.X), rects.Max(rect => rect.Right), area.X, area.Right) &&
               AxisReachable(rects.Min(rect => rect.Y), rects.Max(rect => rect.Bottom), area.Y, area.Bottom);
    }

    private const double ReachTolerance = 1e-6;

    private static bool AxisReachable(double start, double end, double areaStart, double areaEnd) =>
        end - start <= areaEnd - areaStart + ReachTolerance
            ? start >= areaStart - ReachTolerance && end <= areaEnd + ReachTolerance
            : Math.Abs(start - areaStart) <= ReachTolerance;

    /// <summary>Removes disabled graph nodes and their placements; surviving groups split at those nodes.</summary>
    public static LayoutPolicyResult ReconcileEnabledCards(LayoutSettings layout, IEnumerable<string> enabledProviderIds)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(enabledProviderIds);
        var enabled = enabledProviderIds.ToHashSet(StringComparer.Ordinal);
        if (enabled.Count == 0 || enabled.Any(id => !ProviderCatalog.IsKnown(id))) return Reject("layout_invalid_enabled_set");
        var layoutIssue = layout.Validate();
        if (layoutIssue is not null) return Reject(layoutIssue);
        var proposedSettings = layout.Clone();
        proposedSettings.Cards.RemoveAll(card => !enabled.Contains(card.ProviderId));
        proposedSettings.Edges.RemoveAll(edge => !enabled.Contains(edge.FirstProviderId) || !enabled.Contains(edge.SecondProviderId));
        var error = ValidateGraph(proposedSettings, enabled);
        return error is not null ? Reject(error) : Accept(proposedSettings, ImmutableDictionary<string, LogicalRect>.Empty);
    }

    /// <summary>Captures actual logical sizes and normalized top-left anchors for later restoration.</summary>
    /// <remarks>Each connected group is captured against one work area and its anchors are clamped to [0, 1]
    /// so a later <see cref="RecoverMissingMonitor"/> can restore it. Size mismatches and overlaps within a
    /// group remain invalid.</remarks>
    public static LayoutPolicyResult CaptureAnchors(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        IReadOnlyDictionary<string, LogicalWorkArea> workAreasByProvider)
    {
        var graph = ValidateInput(layout, enabledProviderIds, currentRects);
        if (graph.Error is not null) return Reject(graph.Error);
        ArgumentNullException.ThrowIfNull(workAreasByProvider);
        foreach (var group in Components(layout, graph.Nodes))
        {
            if (group.Select(providerId => currentRects[providerId].Size).Distinct().Count() != 1)
                return Reject("layout_group_size_mismatch");
        }
        foreach (var providerId in graph.Nodes)
            if (!workAreasByProvider.ContainsKey(providerId)) return Reject("layout_work_area_missing");

        var groupAreaByProvider = new Dictionary<string, LogicalWorkArea>(StringComparer.Ordinal);
        foreach (var group in Components(layout, graph.Nodes))
        {
            var members = group.ToArray();
            for (var left = 0; left < members.Length; left++)
            for (var right = left + 1; right < members.Length; right++)
                if (Overlaps(currentRects[members[left]], currentRects[members[right]]))
                    return Reject("layout_overlap");
            var groupArea = GroupWorkArea(layout, group, workAreasByProvider);
            foreach (var member in members) groupAreaByProvider.Add(member, groupArea);
        }

        var proposedSettings = layout.Clone();
        // Iterated in enabled-provider order, as before, so a newly created card entry lands in the same place in
        // the saved list.
        foreach (var providerId in graph.Nodes)
        {
            var card = GetOrCreateCard(proposedSettings, providerId);
            SetPlacement(card, currentRects[providerId], groupAreaByProvider[providerId]);
        }
        return Accept(proposedSettings, currentRects);
    }

    /// <summary>The work area a connected group is captured against: the monitor
    /// that holds most of the group's cards, and the group root's monitor on a tie (the root is the card
    /// <see cref="RecoverMissingMonitor"/> positions the whole group by). A detached card is its own group, so it
    /// keeps its own monitor.</summary>
    private static LogicalWorkArea GroupWorkArea(LayoutSettings layout, IReadOnlySet<string> group,
        IReadOnlyDictionary<string, LogicalWorkArea> workAreasByProvider)
    {
        var rootMonitorId = workAreasByProvider[ChooseGroupRoot(layout, group)].MonitorId;
        return group
            .GroupBy(providerId => workAreasByProvider[providerId].MonitorId, StringComparer.Ordinal)
            .OrderByDescending(members => members.Count())
            .ThenBy(members => string.Equals(members.Key, rootMonitorId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(members => members.Key, StringComparer.Ordinal)
            .Select(members => workAreasByProvider[members.First()])
            .First();
    }

    /// <summary>
    /// Restores normalized anchors into current monitor work areas. Missing monitors map once to primary;
    /// the returned settings record that monitor so a later reappearance cannot teleport the group back.
    /// Every card uses the same minimum: the pre-2.0 caller, delegating to the per-card overload below with
    /// <paramref name="usableMinimum"/> repeated for every enabled provider.
    /// </summary>
    public static LayoutPolicyResult RecoverMissingMonitor(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalWorkArea> workAreasById,
        string primaryMonitorId,
        LogicalSize usableMinimum)
    {
        if (enabledProviderIds is null) return Reject("layout_invalid_enabled_set");
        var ids = enabledProviderIds.ToList();
        var usableMinimumByProvider = ids.ToDictionary(id => id, _ => usableMinimum, StringComparer.Ordinal);
        return RecoverMissingMonitor(layout, ids, workAreasById, primaryMonitorId, usableMinimumByProvider);
    }

    /// <summary>2.0 mini mode (2.0 design §3.1): the per-card overload. A whole-layout restore sees full and
    /// mini groups side by side, so each card is checked against its own usable minimum
    /// (<paramref name="usableMinimumByProvider"/>) instead of one minimum for every card.</summary>
    public static LayoutPolicyResult RecoverMissingMonitor(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalWorkArea> workAreasById,
        string primaryMonitorId,
        IReadOnlyDictionary<string, LogicalSize> usableMinimumByProvider)
    {
        ArgumentNullException.ThrowIfNull(workAreasById);
        ArgumentNullException.ThrowIfNull(usableMinimumByProvider);
        var enabled = enabledProviderIds?.ToHashSet(StringComparer.Ordinal);
        if (enabled is null || enabled.Count == 0) return Reject("layout_invalid_enabled_set");
        var graphError = ValidateGraph(layout, enabled);
        if (graphError is not null) return Reject(graphError);
        if (!workAreasById.TryGetValue(primaryMonitorId, out var primary) || !primary.IsPrimary)
            return Reject("layout_primary_monitor_missing");

        var proposedRects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal);
        var proposedSettings = layout.Clone();
        var monitorByProvider = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in Components(layout, enabled))
        {
            var root = ChooseGroupRoot(layout, group);
            var rootMonitorId = GetOrCreateCard(proposedSettings, root).MonitorId;
            var monitorId = rootMonitorId is { } savedId && workAreasById.ContainsKey(savedId)
                ? savedId
                : primaryMonitorId;
            var workArea = workAreasById[monitorId];
            // FitGroupToWorkArea's minimum, defined as the largest of the group's members' minimums (they are
            // equal in a valid layout: mixed-mode groups never exist, see CardModes and MixedModeGroups).
            var groupMinimum = GroupMinimum(group, usableMinimumByProvider);
            var issue = RecoverGroup(layout, proposedSettings, group, workArea, monitorId, groupMinimum,
                usableMinimumByProvider, proposedRects, monitorByProvider);
            if (issue is not null) return Reject(issue);
        }

        // Components of the proposal, not of the input: a split group's cards are independent now and may overlap.
        foreach (var group in Components(proposedSettings, enabled))
        {
            var members = group.ToArray();
            for (var left = 0; left < members.Length; left++)
            for (var right = left + 1; right < members.Length; right++)
                if (Overlaps(proposedRects[members[left]], proposedRects[members[right]]))
                    return Reject("layout_overlap");
        }
        foreach (var providerId in enabled)
        {
            var card = GetOrCreateCard(proposedSettings, providerId);
            SetPlacement(card, proposedRects[providerId], workAreasById[monitorByProvider[providerId]]);
        }
        return Accept(proposedSettings, proposedRects);
    }

    /// <summary>The largest of the group's members' per-card minimums. Equal for every member in a valid
    /// layout (a group is always all one mode), so this is that one value; the max is only a safety margin
    /// against a mixed-mode group that should never exist.</summary>
    private static LogicalSize GroupMinimum(IReadOnlySet<string> group, IReadOnlyDictionary<string, LogicalSize> minimumByProvider) =>
        new(group.Max(id => minimumByProvider[id].Width), group.Max(id => minimumByProvider[id].Height));

    /// <summary>
    /// 2.0 mini mode (2.0 design §3.3): the per-group body of <see cref="RecoverMissingMonitor"/>, extracted
    /// so <see cref="SwitchGroupMode"/> can reuse the exact same "fit into the work area, shrink toward the
    /// minimum, split into single cards, pin a card that still does not fit" rule (§3.3 step 5) that every
    /// other restore uses. <paramref name="sourceLayout"/> supplies the group's current stored sizes and the
    /// root's saved anchor (<see cref="EffectiveSize"/>, <see cref="ChooseGroupRoot"/>); <paramref name="proposedSettings"/>
    /// is mutated when the group must be split (its docking edges are removed). Returns null on success, with
    /// <paramref name="proposedRects"/> and <paramref name="monitorByProvider"/> filled for every member of
    /// <paramref name="group"/>; otherwise the rejection code.
    /// </summary>
    private static string? RecoverGroup(
        LayoutSettings sourceLayout,
        LayoutSettings proposedSettings,
        IReadOnlySet<string> group,
        LogicalWorkArea workArea,
        string monitorId,
        LogicalSize usableMinimum,
        IReadOnlyDictionary<string, LogicalSize> usableMinimumByProvider,
        IDictionary<string, LogicalRect> proposedRects,
        IDictionary<string, string> monitorByProvider)
    {
        var root = ChooseGroupRoot(sourceLayout, group);
        var rootCard = GetOrCreateCard(proposedSettings, root);
        var rootSize = EffectiveSize(sourceLayout, root);
        if (group.Select(providerId => EffectiveSize(sourceLayout, providerId)).Distinct().Count() != 1)
            return "layout_group_size_mismatch";
        var reflowed = FitGroupToWorkArea(sourceLayout, group, root, rootSize,
            rootCard.AnchorX, rootCard.AnchorY, workArea, usableMinimum, pinWhenTooLarge: group.Count == 1);
        // Split a group that cannot fit at its minimum so each card can be restored independently. Pin an
        // oversized card at the work area's top-left to keep its title row reachable.
        if (reflowed.Error == "layout_outside_work_area" && group.Count > 1)
        {
            proposedSettings.Edges.RemoveAll(edge => group.Contains(edge.FirstProviderId) || group.Contains(edge.SecondProviderId));
            foreach (var member in group.Order(StringComparer.Ordinal))
            {
                var memberCard = GetOrCreateCard(proposedSettings, member);
                // The caller's map is keyed by every member of this group (RecoverMissingMonitor's map covers
                // every enabled provider; SwitchGroupMode's covers this whole group), so the indexer is safe.
                var memberMinimum = usableMinimumByProvider[member];
                var single = FitGroupToWorkArea(sourceLayout, new HashSet<string>(StringComparer.Ordinal) { member }, member,
                    EffectiveSize(sourceLayout, member), memberCard.AnchorX, memberCard.AnchorY, workArea, memberMinimum,
                    pinWhenTooLarge: true);
                if (single.Error is not null) return single.Error;
                var memberIssue = AddRecovered(single.Rects, workArea, monitorId, memberMinimum, proposedRects, monitorByProvider);
                if (memberIssue is not null) return memberIssue;
            }
            return null;
        }
        if (reflowed.Error is not null) return reflowed.Error;
        return AddRecovered(reflowed.Rects, workArea, monitorId, usableMinimum, proposedRects, monitorByProvider);
    }

    private static string? AddRecovered(IReadOnlyDictionary<string, LogicalRect> rects, LogicalWorkArea area, string monitorId,
        LogicalSize usableMinimum, IDictionary<string, LogicalRect> proposedRects, IDictionary<string, string> monitorByProvider)
    {
        if (rects.Values.Any(rect => !IsAtLeast(rect.Size, usableMinimum))) return "layout_too_small";
        if (!IsReachablePlacement(area.Bounds, rects.Values)) return "layout_outside_work_area";
        foreach (var (providerId, rect) in rects)
        {
            proposedRects.Add(providerId, rect);
            monitorByProvider.Add(providerId, monitorId);
        }
        return null;
    }

    private static ReflowResult FitGroupToWorkArea(
        LayoutSettings layout,
        IReadOnlySet<string> group,
        string root,
        LogicalSize originalSize,
        double anchorX,
        double anchorY,
        LogicalWorkArea workArea,
        LogicalSize usableMinimum,
        bool pinWhenTooLarge)
    {
        if (!IsAtLeast(originalSize, usableMinimum)) return new ReflowResult([], "layout_too_small");
        ReflowResult AtScale(double scale)
        {
            try
            {
                var size = new LogicalSize(originalSize.Width * scale, originalSize.Height * scale);
                return ReflowComponent(layout, group, root, new LogicalRect(0, 0, size.Width, size.Height), size);
            }
            catch (ArgumentOutOfRangeException)
            {
                return new ReflowResult([], "layout_invalid_position");
            }
        }
        var full = AtScale(1);
        if (full.Error is not null) return full;
        var (minX, minY, maxX, maxY) = Bounds(full.Rects);
        var scaleFactor = 1d;
        if (maxX - minX > workArea.Bounds.Width || maxY - minY > workArea.Bounds.Height)
        {
            var minimumScale = Math.Max(usableMinimum.Width / originalSize.Width, usableMinimum.Height / originalSize.Height);
            var minimum = AtScale(minimumScale);
            if (minimum.Error is not null) return minimum;
            var minimumBounds = Bounds(minimum.Rects);
            if (minimumBounds.MaxX - minimumBounds.MinX > workArea.Bounds.Width ||
                minimumBounds.MaxY - minimumBounds.MinY > workArea.Bounds.Height)
            {
                // The caller splits an oversized group into individual cards; an oversized card is pinned below.
                if (!pinWhenTooLarge) return new ReflowResult([], "layout_outside_work_area");
                scaleFactor = minimumScale;
            }
            else
            {
                var low = minimumScale;
                var high = 1d;
                for (var iteration = 0; iteration < 48; iteration++)
                {
                    var middle = (low + high) / 2;
                    var candidate = AtScale(middle);
                    if (candidate.Error is not null) return candidate;
                    var bounds = Bounds(candidate.Rects);
                    if (bounds.MaxX - bounds.MinX <= workArea.Bounds.Width &&
                        bounds.MaxY - bounds.MinY <= workArea.Bounds.Height)
                        low = middle;
                    else
                        high = middle;
                }
                scaleFactor = low;
            }
        }

        var fittedSize = new LogicalSize(originalSize.Width * scaleFactor, originalSize.Height * scaleFactor);
        var fitted = AtScale(scaleFactor);
        if (fitted.Error is not null) return fitted;
        var fittedBounds = Bounds(fitted.Rects);
        var desiredX = workArea.Bounds.X + (workArea.Bounds.Width - fittedSize.Width) * anchorX;
        var desiredY = workArea.Bounds.Y + (workArea.Bounds.Height - fittedSize.Height) * anchorY;
        var rootX = ClampOrPinToStart(desiredX,
            workArea.Bounds.X - fittedBounds.MinX,
            workArea.Bounds.Right - fittedBounds.MaxX);
        var rootY = ClampOrPinToStart(desiredY,
            workArea.Bounds.Y - fittedBounds.MinY,
            workArea.Bounds.Bottom - fittedBounds.MaxY);
        var translated = fitted.Rects.ToDictionary(pair => pair.Key,
            pair => pair.Value.Translate(rootX, rootY), StringComparer.Ordinal);
        return new ReflowResult(translated, null);
    }

    /// <summary>When low exceeds high, the card is larger than the work area and is pinned to its start so its
    /// top-left corner and title row remain reachable.</summary>
    private static double ClampOrPinToStart(double value, double low, double high) =>
        high < low ? low : Math.Clamp(value, low, high);
}

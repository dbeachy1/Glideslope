using System.Collections.Immutable;

namespace Glideslope.Core;

/// <summary>A complete immutable proposal. Callers commit only a successful proposal they accept.</summary>
public sealed record LayoutPolicyResult(
    bool Succeeded,
    string? SafeErrorCode,
    LayoutSettings? ProposedSettings,
    ImmutableDictionary<string, LogicalRect> ProposedRects);

/// <summary>
/// Pure graph and geometry policy for docked cards. The host supplies current logical rectangles,
/// work areas, and the usable minimum size established by platform visual proofs.
/// </summary>
public static partial class LayoutPolicy
{
    public const double DefaultInterCardGap = 10;
    public const double DefaultAcquisitionDistance = 16;

    /// <summary>
    /// How far a group's edge may pass a screen edge and still be pulled flush by screen-edge snap. Larger offsets
    /// are treated as deliberate placement and left unchanged. See <see cref="NearestEdgeDelta"/>.
    /// </summary>
    public const double ScreenEdgeOvershootPullBackDistance = 64;

    /// <summary>Returns the graph component for UI projections without duplicating docking traversal.</summary>
    public static ImmutableArray<string> GetComponentProviderIds(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(enabledProviderIds);
        var enabled = enabledProviderIds.ToHashSet(StringComparer.Ordinal);
        if (!enabled.Contains(providerId) || ValidateGraph(layout, enabled) is not null)
            return ImmutableArray<string>.Empty;
        return Component(layout, enabled, providerId).Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static InputGraph ValidateInput(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(enabledProviderIds);
        ArgumentNullException.ThrowIfNull(currentRects);
        var enabled = enabledProviderIds.ToHashSet(StringComparer.Ordinal);
        var error = ValidateGraph(layout, enabled);
        if (error is not null) return new InputGraph(enabled, error);
        if (currentRects.Count != enabled.Count || enabled.Any(id => !currentRects.ContainsKey(id)))
            return new InputGraph(enabled, "layout_rects_incomplete");
        return new InputGraph(enabled, null);
    }

    private static string? ValidateGraph(LayoutSettings layout, IReadOnlySet<string> enabled)
    {
        var error = layout.Validate();
        if (error is not null) return error;
        if (enabled.Count == 0 || enabled.Any(id => !ProviderCatalog.IsKnown(id))) return "layout_invalid_enabled_set";
        if (layout.Cards.Any(card => !enabled.Contains(card.ProviderId)) ||
            layout.Edges.Any(edge => !enabled.Contains(edge.FirstProviderId) || !enabled.Contains(edge.SecondProviderId)))
            return "layout_disabled_graph_node";

        var parent = enabled.ToDictionary(id => id, id => id, StringComparer.Ordinal);
        foreach (var edge in layout.Edges)
        {
            var firstRoot = Find(parent, edge.FirstProviderId);
            var secondRoot = Find(parent, edge.SecondProviderId);
            if (firstRoot == secondRoot) return "layout_cycle";
            parent[secondRoot] = firstRoot;
        }
        return null;
    }

    private static string Find(IDictionary<string, string> parent, string providerId)
    {
        var root = providerId;
        while (parent[root] != root) root = parent[root];
        while (parent[providerId] != providerId)
        {
            var next = parent[providerId];
            parent[providerId] = root;
            providerId = next;
        }
        return root;
    }

    private static HashSet<string> Component(LayoutSettings layout, IReadOnlySet<string> nodes, string start)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { start };
        var pending = new Queue<string>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var current))
        {
            foreach (var edge in layout.Edges)
            {
                var next = edge.FirstProviderId == current ? edge.SecondProviderId :
                    edge.SecondProviderId == current ? edge.FirstProviderId : null;
                if (next is not null && nodes.Contains(next) && result.Add(next)) pending.Enqueue(next);
            }
        }
        return result;
    }

    private static IEnumerable<HashSet<string>> Components(LayoutSettings layout, IReadOnlySet<string> nodes)
    {
        var remaining = new HashSet<string>(nodes, StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var start = remaining.OrderBy(id => id, StringComparer.Ordinal).First();
            var component = Component(layout, nodes, start);
            remaining.ExceptWith(component);
            yield return component;
        }
    }

    private static string ChooseGroupRoot(LayoutSettings layout, IEnumerable<string> group)
    {
        var order = layout.Cards.ToDictionary(card => card.ProviderId, card => card.GroupOrder, StringComparer.Ordinal);
        return group.OrderBy(id => order.TryGetValue(id, out var groupOrder) ? groupOrder : int.MaxValue)
            .ThenBy(id => id, StringComparer.Ordinal).First();
    }

    private static ReflowResult ReflowComponent(
        LayoutSettings layout,
        IReadOnlySet<string> group,
        string anchorProviderId,
        LogicalRect anchorRect,
        LogicalSize? sizeOverride)
    {
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal) { [anchorProviderId] = anchorRect };
        var pending = new Queue<string>();
        pending.Enqueue(anchorProviderId);
        while (pending.TryDequeue(out var current))
        {
            var currentRect = rects[current];
            foreach (var edge in layout.Edges)
            {
                string? neighbor = null;
                var side = edge.FirstSide;
                if (edge.FirstProviderId == current) neighbor = edge.SecondProviderId;
                else if (edge.SecondProviderId == current)
                {
                    neighbor = edge.FirstProviderId;
                    side = Opposite(edge.FirstSide);
                }
                if (neighbor is null || !group.Contains(neighbor) || rects.ContainsKey(neighbor)) continue;
                var size = sizeOverride ?? EffectiveSize(layout, neighbor);
                rects.Add(neighbor, PlaceBeside(currentRect, size, side, edge.Gap));
                pending.Enqueue(neighbor);
            }
        }
        return rects.Count == group.Count
            ? new ReflowResult(rects, null)
            : new ReflowResult(rects, "layout_graph_disconnected");
    }

    private static LogicalRect PlaceBeside(LogicalRect anchor, LogicalSize size, CardDockSide side, double gap) => side switch
    {
        CardDockSide.Top => new LogicalRect(anchor.X, anchor.Y - gap - size.Height, size.Width, size.Height),
        CardDockSide.Right => new LogicalRect(anchor.Right + gap, anchor.Y, size.Width, size.Height),
        CardDockSide.Bottom => new LogicalRect(anchor.X, anchor.Bottom + gap, size.Width, size.Height),
        CardDockSide.Left => new LogicalRect(anchor.X - gap - size.Width, anchor.Y, size.Width, size.Height),
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };

    private static CardDockSide Opposite(CardDockSide side) => side switch
    {
        CardDockSide.Top => CardDockSide.Bottom,
        CardDockSide.Right => CardDockSide.Left,
        CardDockSide.Bottom => CardDockSide.Top,
        CardDockSide.Left => CardDockSide.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };

    private static LogicalSize EffectiveSize(LayoutSettings layout, string providerId)
    {
        var card = layout.Cards.FirstOrDefault(candidate => candidate.ProviderId == providerId);
        return card is null ? new LogicalSize(layout.CardWidth, layout.CardHeight) : new LogicalSize(card.Width, card.Height);
    }

    /// <summary>2.0 mini mode: a card's remembered full size, (0, 0) when it has no saved card entry or has
    /// not gone mini yet. Unlike <see cref="EffectiveSize"/> this is not a <see cref="LogicalSize"/>: both
    /// values can legitimately be zero (a full card, or a mini card with no remembered size yet), which
    /// LogicalSize's constructor rejects.</summary>
    private static (double Width, double Height) EffectiveFullSize(LayoutSettings layout, string providerId)
    {
        var card = layout.Cards.FirstOrDefault(candidate => candidate.ProviderId == providerId);
        return card is null ? (0, 0) : (card.FullWidth, card.FullHeight);
    }

    /// <summary>2.0 mini mode (2.0 design §3.7): the mirror of <see cref="EffectiveFullSize"/> - a card's
    /// remembered mini size, (0, 0) when it has no saved card entry or has never gone mini.</summary>
    private static (double Width, double Height) EffectiveMiniSize(LayoutSettings layout, string providerId)
    {
        var card = layout.Cards.FirstOrDefault(candidate => candidate.ProviderId == providerId);
        return card is null ? (0, 0) : (card.MiniWidth, card.MiniHeight);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyDictionary<string, LogicalRect> rects) =>
        (rects.Values.Min(rect => rect.X), rects.Values.Min(rect => rect.Y),
            rects.Values.Max(rect => rect.Right), rects.Values.Max(rect => rect.Bottom));

    private static string? ValidatePlacements(
        IReadOnlyDictionary<string, LogicalRect> allRects,
        IReadOnlySet<string> affected,
        LayoutSettings layout,
        LogicalWorkArea workArea,
        LogicalSize usableMinimum,
        bool requireInsideWorkArea = true)
    {
        foreach (var providerId in affected)
        {
            var rect = allRects[providerId];
            if (!IsAtLeast(rect.Size, usableMinimum)) return "layout_too_small";
            // Only the user resize gesture skips work-area containment; see ResizeGroup.
            if (requireInsideWorkArea && !Contains(workArea.Bounds, rect)) return "layout_outside_work_area";
        }

        var affectedArray = affected.ToArray();
        for (var left = 0; left < affectedArray.Length; left++)
        for (var right = left + 1; right < affectedArray.Length; right++)
            if (Overlaps(allRects[affectedArray[left]], allRects[affectedArray[right]])) return "layout_overlap";
        return null;
    }

    private static bool IsAtLeast(LogicalSize value, LogicalSize minimum) =>
        value.Width >= minimum.Width && value.Height >= minimum.Height;

    private static bool Contains(LogicalRect area, LogicalRect rect) =>
        rect.X >= area.X && rect.Y >= area.Y && rect.Right <= area.Right && rect.Bottom <= area.Bottom;

    private static bool Overlaps(LogicalRect first, LogicalRect second) =>
        first.X < second.Right && second.X < first.Right && first.Y < second.Bottom && second.Y < first.Bottom;

    private static void CaptureComponent(LayoutSettings layout, IReadOnlyDictionary<string, LogicalRect> rects,
        IEnumerable<string> providers, LogicalWorkArea workArea)
    {
        foreach (var providerId in providers)
        {
            var card = GetOrCreateCard(layout, providerId);
            SetPlacement(card, rects[providerId], workArea);
        }
    }

    private static void SetPlacement(CardLayoutSettings card, LogicalRect rect, LogicalWorkArea workArea)
    {
        var freeX = workArea.Bounds.Width - rect.Width;
        var freeY = workArea.Bounds.Height - rect.Height;
        card.Width = rect.Width;
        card.Height = rect.Height;
        card.MonitorId = workArea.MonitorId;
        card.AnchorX = freeX <= 0 ? 0 : Math.Clamp((rect.X - workArea.Bounds.X) / freeX, 0, 1);
        card.AnchorY = freeY <= 0 ? 0 : Math.Clamp((rect.Y - workArea.Bounds.Y) / freeY, 0, 1);
    }

    private static CardLayoutSettings GetOrCreateCard(LayoutSettings layout, string providerId)
    {
        var card = layout.Cards.FirstOrDefault(candidate => candidate.ProviderId == providerId);
        if (card is not null) return card;
        card = new CardLayoutSettings
        {
            ProviderId = providerId,
            Width = layout.CardWidth,
            Height = layout.CardHeight,
            GroupOrder = Array.IndexOf(ProviderCatalog.DefaultEnabledProviderIds.ToArray(), providerId),
        };
        layout.Cards.Add(card);
        return card;
    }

    private static LayoutPolicyResult Accept(LayoutSettings settings, IReadOnlyDictionary<string, LogicalRect> rects) =>
        new(true, null, settings, rects.ToImmutableDictionary(StringComparer.Ordinal));

    private static LayoutPolicyResult Reject(string error) =>
        new(false, error, null, ImmutableDictionary<string, LogicalRect>.Empty);

    private sealed record InputGraph(HashSet<string> Nodes, string? Error);
    private sealed record ReflowResult(Dictionary<string, LogicalRect> Rects, string? Error);
}

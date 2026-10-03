using System.Collections.Immutable;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// A valid docking candidate whose lead card is within acquisition distance and whose proposed group passes
/// PreviewDock. The source layout and geometry allow the coordinator to revalidate it before commit.
/// </summary>
internal sealed record MagnetCandidate(
    LayoutPolicyResult Proposal,
    string LeadProviderId,
    string DestinationProviderId,
    CardDockSide DestinationSide,
    ImmutableArray<string> IncomingProviderIds,
    LayoutSettings SourceLayout,
    CardLayoutGeometrySnapshot SourceGeometry)
{
    /// <summary>True while the layout graph and screen geometry are exactly what this candidate was
    /// computed from; a stale candidate (a layout edit, a window move, or a screen-scale change
    /// since) must be recomputed rather than committed as-is.</summary>
    public bool IsCurrent(LayoutSettings layout, CardLayoutGeometrySnapshot geometry) =>
        LayoutsEqual(SourceLayout, layout) && GeometryEqual(SourceGeometry, geometry);

    private static bool LayoutsEqual(LayoutSettings first, LayoutSettings second) =>
        first.CardWidth.Equals(second.CardWidth) && first.CardHeight.Equals(second.CardHeight) &&
        first.Cards.OrderBy(card => card.ProviderId, StringComparer.Ordinal).SequenceEqual(
            second.Cards.OrderBy(card => card.ProviderId, StringComparer.Ordinal), CardLayoutSettingsComparer.Instance) &&
        first.Edges.OrderBy(edge => edge.FirstProviderId, StringComparer.Ordinal)
            .ThenBy(edge => edge.SecondProviderId, StringComparer.Ordinal)
            .SequenceEqual(second.Edges.OrderBy(edge => edge.FirstProviderId, StringComparer.Ordinal)
                .ThenBy(edge => edge.SecondProviderId, StringComparer.Ordinal), DockingEdgeSettingsComparer.Instance);

    private static bool GeometryEqual(CardLayoutGeometrySnapshot first, CardLayoutGeometrySnapshot second) =>
        first.PrimaryMonitorId == second.PrimaryMonitorId &&
        DictionaryEqual(first.Rects, second.Rects) &&
        DictionaryEqual(first.MonitorByProvider, second.MonitorByProvider) &&
        first.Monitors.Count == second.Monitors.Count && first.Monitors.All(pair =>
            second.Monitors.TryGetValue(pair.Key, out var other) && pair.Value == other);

    private static bool DictionaryEqual<T>(IReadOnlyDictionary<string, T> first, IReadOnlyDictionary<string, T> second)
        where T : IEquatable<T> =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value));

    private sealed class CardLayoutSettingsComparer : IEqualityComparer<CardLayoutSettings>
    {
        public static CardLayoutSettingsComparer Instance { get; } = new();
        // Include remembered full and mini sizes so a candidate is stale if either mode's return size changes.
        public bool Equals(CardLayoutSettings? x, CardLayoutSettings? y) => x is not null && y is not null &&
            x.ProviderId == y.ProviderId && x.Width.Equals(y.Width) && x.Height.Equals(y.Height) &&
            x.MonitorId == y.MonitorId && x.AnchorX.Equals(y.AnchorX) && x.AnchorY.Equals(y.AnchorY) && x.GroupOrder == y.GroupOrder &&
            x.FullWidth.Equals(y.FullWidth) && x.FullHeight.Equals(y.FullHeight) &&
            x.MiniWidth.Equals(y.MiniWidth) && x.MiniHeight.Equals(y.MiniHeight);
        // HashCode.Combine accepts at most eight arguments, so combine the size and ordering fields in a sub-hash.
        public int GetHashCode(CardLayoutSettings obj) => HashCode.Combine(obj.ProviderId, obj.Width, obj.Height,
            obj.MonitorId, obj.AnchorX, obj.AnchorY,
            HashCode.Combine(obj.GroupOrder, obj.FullWidth, obj.FullHeight, obj.MiniWidth, obj.MiniHeight));
    }

    private sealed class DockingEdgeSettingsComparer : IEqualityComparer<DockingEdgeSettings>
    {
        public static DockingEdgeSettingsComparer Instance { get; } = new();
        public bool Equals(DockingEdgeSettings? x, DockingEdgeSettings? y) => x is not null && y is not null &&
            x.FirstProviderId == y.FirstProviderId && x.SecondProviderId == y.SecondProviderId &&
            x.FirstSide == y.FirstSide && x.Gap.Equals(y.Gap);
        public int GetHashCode(DockingEdgeSettings obj) => HashCode.Combine(obj.FirstProviderId, obj.SecondProviderId, obj.FirstSide, obj.Gap);
    }
}

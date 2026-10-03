using System.Collections.Immutable;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>
/// builds CardLayoutGeometrySnapshot values for the pure
/// coordinator decisions (DecideUntrackedChange, DecideDisplayRevalidation, ScaleAndRecover), so they are proven
/// without a WindowCoordinator or a window (a headless coordinator hung the Shell suite on. Monitors are
/// at 100% scaling, so their physical and logical rectangles are the same numbers.
/// </summary>
internal static class LayoutSpecGeometry
{
    public static CardLayoutMonitorTransform Monitor(string id, int x, int y, int width, int height, int workHeight, bool isPrimary)
    {
        var screen = new CardLayoutScreen(id, new PhysicalRect(x, y, width, height), new PhysicalRect(x, y, width, workHeight), 1, isPrimary);
        return new CardLayoutMonitorTransform(screen, new LogicalPoint(x, y),
            new LogicalWorkArea(id, new LogicalRect(x, y, width, workHeight), isPrimary));
    }

    /// <summary>A snapshot of <paramref name="rects"/> on <paramref name="monitors"/>; each card is on the monitor whose
    /// work area holds its center, else on the primary.</summary>
    public static CardLayoutGeometrySnapshot Snapshot(IReadOnlyDictionary<string, LogicalRect> rects, params CardLayoutMonitorTransform[] monitors)
    {
        var primary = monitors.First(monitor => monitor.WorkArea.IsPrimary).Screen.Id;
        var monitorByProvider = rects.ToImmutableDictionary(pair => pair.Key, pair =>
        {
            var centerX = pair.Value.X + pair.Value.Width / 2;
            var centerY = pair.Value.Y + pair.Value.Height / 2;
            return monitors.FirstOrDefault(monitor =>
                centerX >= monitor.WorkArea.Bounds.X && centerX < monitor.WorkArea.Bounds.Right &&
                centerY >= monitor.WorkArea.Bounds.Y && centerY < monitor.WorkArea.Bounds.Bottom)?.Screen.Id ?? primary;
        }, StringComparer.Ordinal);
        return new CardLayoutGeometrySnapshot(rects.ToImmutableDictionary(StringComparer.Ordinal), monitorByProvider,
            monitors.ToImmutableDictionary(monitor => monitor.Screen.Id, StringComparer.Ordinal), primary);
    }

    public static Dictionary<string, LogicalRect> Rects(params (string ProviderId, LogicalRect Rect)[] cards) =>
        cards.ToDictionary(card => card.ProviderId, card => card.Rect, StringComparer.Ordinal);
}

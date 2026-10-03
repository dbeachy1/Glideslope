using System.Collections.Immutable;
using Avalonia;
using Glideslope.Core;

namespace Glideslope.App;

internal sealed record CardLayoutScreen(
    string Id,
    PhysicalRect Bounds,
    PhysicalRect WorkingArea,
    double Scaling,
    bool IsPrimary);

internal sealed record CardLayoutMonitorTransform(
    CardLayoutScreen Screen,
    LogicalPoint LogicalOrigin,
    LogicalWorkArea WorkArea)
{
    /// <summary>The monitor's bounds in the layout's logical coordinates. Mixed-DPI monitors require using the
    /// transform of the monitor containing the rectangle.</summary>
    public LogicalRect LogicalBounds => new(LogicalOrigin.X, LogicalOrigin.Y,
        Screen.Bounds.Width / Screen.Scaling, Screen.Bounds.Height / Screen.Scaling);
}

internal sealed record CardLayoutGeometrySnapshot(
    ImmutableDictionary<string, LogicalRect> Rects,
    ImmutableDictionary<string, string> MonitorByProvider,
    ImmutableDictionary<string, CardLayoutMonitorTransform> Monitors,
    string PrimaryMonitorId)
{
    /// <summary>True when the first screen was selected as primary because none reported itself primary.</summary>
    public bool PrimaryInferred { get; init; }
}

/// <summary>Value comparisons shared by layout-change, display-change, and card-scale decisions.</summary>
internal static class CardLayoutGeometryComparison
{
    public static bool MonitorsEqual(IReadOnlyDictionary<string, CardLayoutMonitorTransform> first,
        IReadOnlyDictionary<string, CardLayoutMonitorTransform> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var other) && pair.Value == other);

    public static bool RectsEqual(IReadOnlyDictionary<string, LogicalRect> first, IReadOnlyDictionary<string, LogicalRect> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var other) && pair.Value == other);

    /// <summary>True when <paramref name="rect"/> shares a positive area with any monitor's work area.</summary>
    public static bool IntersectsAnyWorkArea(LogicalRect rect, CardLayoutGeometrySnapshot geometry) =>
        geometry.Monitors.Values.Any(monitor =>
        {
            var area = monitor.WorkArea.Bounds;
            return rect.X < area.Right && area.X < rect.Right && rect.Y < area.Bottom && area.Y < rect.Bottom;
        });
}

internal interface ICardLayoutWindow
{
    string ProviderId { get; }
    object NativeHandle { get; }
}

/// <summary>Native window access boundary; specs can supply screens and geometry without starting a desktop window system.</summary>
internal interface ICardWindowGeometryBackend
{
    IReadOnlyList<CardLayoutScreen> GetScreens(ICardLayoutWindow window);
    string? GetMonitorId(ICardLayoutWindow window, IReadOnlyList<CardLayoutScreen> screens);
    PixelPoint GetPosition(ICardLayoutWindow window);
    LogicalSize GetSize(ICardLayoutWindow window);
    void SetPosition(ICardLayoutWindow window, PixelPoint position);
    void SetSize(ICardLayoutWindow window, LogicalSize size);

    /// <summary>Sets size and position together when possible to avoid displaying the new size at the old position.
    /// Used only on the current monitor; cross-monitor moves may change DPI.</summary>
    void SetBounds(ICardLayoutWindow window, PhysicalRect physical, LogicalSize size)
    {
        SetSize(window, size);
        SetPosition(window, new PixelPoint(physical.X, physical.Y));
    }

    /// <summary>Called with each reported card position. Says
    /// whether it answers a move this backend requested (Echo when it landed there, Adjusted when the window manager
    /// put it elsewhere) or is unrelated (None). The default, for a platform whose moves take effect at once (Windows)
    /// and for specs' fakes, is None.</summary>
    PlacementConfirmation ConfirmPosition(ICardLayoutWindow window, PixelPoint reported, out PixelPoint requested)
    {
        requested = reported;
        return PlacementConfirmation.None;
    }

    /// <summary>Classifies a reported card size (a Width/Height
    /// change). None by default.</summary>
    PlacementConfirmation ConfirmSize(ICardLayoutWindow window, LogicalSize reported) => PlacementConfirmation.None;

    /// <summary>True when the window manager confines app-placed windows to the work area.</summary>
    bool WindowManagerConfinesPlacedWindows => false;
}

/// <summary>Window design doc §20 R1: what a reported window position means for a move the app requested.</summary>
internal enum PlacementConfirmation
{
    None,
    Echo,
    Adjusted,
}

internal interface ICardLayoutPlatform
{
    CardLayoutGeometrySnapshot Capture(IReadOnlyDictionary<string, ICardLayoutWindow> windows);
    void Apply(IReadOnlyDictionary<string, ICardLayoutWindow> windows,
        LayoutSettings proposedSettings, IReadOnlyDictionary<string, LogicalRect> proposedRects,
        CardLayoutGeometrySnapshot currentGeometry);

    /// <summary>2.1 Ctrl peek (2.1 design §5.2 step 4): sets one window's size and position through the same
    /// monitor selection and DPI conversion <see cref="Apply"/> uses, with no LayoutSettings/proposed-rects
    /// map involved - a peek never touches the saved layout. <paramref name="savedMonitorId"/> is the card's
    /// saved monitor id, when known, the same preference <see cref="Apply"/> gives ChooseMonitorForRect.</summary>
    void ApplyOneCard(ICardLayoutWindow window, LogicalRect rect, string? savedMonitorId,
        CardLayoutGeometrySnapshot currentGeometry);

    /// <summary>Window design doc §20 R1; see <see cref="ICardWindowGeometryBackend.ConfirmPosition"/>.</summary>
    PlacementConfirmation ConfirmPosition(ICardLayoutWindow window, PixelPoint reported, out PixelPoint requested)
    {
        requested = reported;
        return PlacementConfirmation.None;
    }

    /// <summary>Window design doc §20.2b; see <see cref="ICardWindowGeometryBackend.ConfirmSize"/>.</summary>
    PlacementConfirmation ConfirmSize(ICardLayoutWindow window, LogicalSize reported) => PlacementConfirmation.None;

    /// <summary>Window design doc §20 R4; see <see cref="ICardWindowGeometryBackend.WindowManagerConfinesPlacedWindows"/>.</summary>
    bool WindowManagerConfinesPlacedWindows => false;
}

internal sealed class CardLayoutPlatform(ICardWindowGeometryBackend backend) : ICardLayoutPlatform
{
    public PlacementConfirmation ConfirmPosition(ICardLayoutWindow window, PixelPoint reported, out PixelPoint requested) =>
        backend.ConfirmPosition(window, reported, out requested);

    public PlacementConfirmation ConfirmSize(ICardLayoutWindow window, LogicalSize reported) =>
        backend.ConfirmSize(window, reported);

    public bool WindowManagerConfinesPlacedWindows => backend.WindowManagerConfinesPlacedWindows;

    public CardLayoutGeometrySnapshot Capture(IReadOnlyDictionary<string, ICardLayoutWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0) throw new InvalidOperationException("layout_windows_empty");
        var representative = windows.Values.First();
        var screens = backend.GetScreens(representative);
        if (screens.Count == 0) throw new InvalidOperationException("layout_screens_unavailable");
        var reportedPrimary = screens.FirstOrDefault(screen => screen.IsPrimary);
        var primary = reportedPrimary ?? screens[0];
        var primaryScale = ValidateScale(primary.Scaling);
        var monitors = ImmutableDictionary.CreateBuilder<string, CardLayoutMonitorTransform>(StringComparer.Ordinal);
        foreach (var screen in screens)
        {
            var scale = ValidateScale(screen.Scaling);
            var logicalOrigin = new LogicalPoint(
                (screen.Bounds.X - primary.Bounds.X) / primaryScale,
                (screen.Bounds.Y - primary.Bounds.Y) / primaryScale);
            var logicalWorkArea = LayoutDpiTransform.ToLogical(screen.WorkingArea,
                new PhysicalPoint(screen.Bounds.X, screen.Bounds.Y), logicalOrigin, scale);
            // Use the selected primary screen consistently when marking its work area.
            var workArea = new LogicalWorkArea(screen.Id, logicalWorkArea, isPrimary: ReferenceEquals(screen, primary));
            monitors.Add(screen.Id, new CardLayoutMonitorTransform(screen, logicalOrigin, workArea));
        }

        var rects = ImmutableDictionary.CreateBuilder<string, LogicalRect>(StringComparer.Ordinal);
        var monitorByProvider = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (providerId, window) in windows)
        {
            var monitorId = backend.GetMonitorId(window, screens);
            if (monitorId is null || !monitors.TryGetValue(monitorId, out var monitor))
                monitor = monitors[primary.Id];
            var position = backend.GetPosition(window);
            var localPoint = LayoutDpiTransform.ToLogical(new PhysicalRect(position.X, position.Y, 1, 1),
                new PhysicalPoint(monitor.Screen.Bounds.X, monitor.Screen.Bounds.Y), monitor.LogicalOrigin,
                monitor.Screen.Scaling);
            var size = backend.GetSize(window);
            rects.Add(providerId, new LogicalRect(localPoint.X, localPoint.Y, size.Width, size.Height));
            monitorByProvider.Add(providerId, monitor.Screen.Id);
        }

        return new CardLayoutGeometrySnapshot(rects.ToImmutable(), monitorByProvider.ToImmutable(),
            monitors.ToImmutable(), primary.Id)
        {
            PrimaryInferred = reportedPrimary is null,
        };
    }

    public void Apply(IReadOnlyDictionary<string, ICardLayoutWindow> windows,
        LayoutSettings proposedSettings, IReadOnlyDictionary<string, LogicalRect> proposedRects,
        CardLayoutGeometrySnapshot currentGeometry)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(proposedSettings);
        ArgumentNullException.ThrowIfNull(proposedRects);
        ArgumentNullException.ThrowIfNull(currentGeometry);
        foreach (var (providerId, rect) in proposedRects)
        {
            if (!windows.TryGetValue(providerId, out var window)) continue;
            var savedMonitor = proposedSettings.Cards.FirstOrDefault(card => card.ProviderId == providerId)?.MonitorId;
            var currentMonitorId = currentGeometry.MonitorByProvider.GetValueOrDefault(providerId);
            var monitor = ChooseMonitorForRect(rect, savedMonitor, currentMonitorId, currentGeometry);

            var physical = LayoutDpiTransform.ToPhysical(rect,
                new PhysicalPoint(monitor.Screen.Bounds.X, monitor.Screen.Bounds.Y),
                monitor.LogicalOrigin, monitor.Screen.Scaling);
            SetWindowRect(window, physical, rect.Size, sameMonitor: monitor.Screen.Id == currentMonitorId);
        }
    }

    /// <summary>Uses one step on the current monitor. Across monitors, set size before position so Windows can
    /// apply the destination monitor's DPI.</summary>
    private void SetWindowRect(ICardLayoutWindow window, PhysicalRect physical, LogicalSize size, bool sameMonitor)
    {
        if (sameMonitor)
        {
            backend.SetBounds(window, physical, size);
            return;
        }
        backend.SetSize(window, size);
        backend.SetPosition(window, new PixelPoint(physical.X, physical.Y));
    }

    /// <summary>2.1 Ctrl peek (2.1 design §5.2 step 4): the one-card overload of <see cref="Apply"/>, used to
    /// place a peeking card and, in reverse, to put a card back to its exact pre-peek rect. Monitor selection
    /// and the physical conversion are exactly <see cref="Apply"/>'s, with one window and one rect instead of a
    /// map.</summary>
    public void ApplyOneCard(ICardLayoutWindow window, LogicalRect rect, string? savedMonitorId,
        CardLayoutGeometrySnapshot currentGeometry)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(currentGeometry);
        var currentMonitorId = currentGeometry.MonitorByProvider.GetValueOrDefault(window.ProviderId);
        var monitor = ChooseMonitorForRect(rect, savedMonitorId, currentMonitorId, currentGeometry);
        var physical = LayoutDpiTransform.ToPhysical(rect,
            new PhysicalPoint(monitor.Screen.Bounds.X, monitor.Screen.Bounds.Y),
            monitor.LogicalOrigin, monitor.Screen.Scaling);
        SetWindowRect(window, physical, rect.Size, sameMonitor: monitor.Screen.Id == currentMonitorId);
    }

    /// <summary>
    /// Selects the monitor transform for converting <paramref name="rect"/> to physical pixels. Prefer the saved
    /// or current monitor when it contains the rectangle's center; otherwise use a containing monitor, then fall
    /// back to saved, current, or primary. This keeps mixed-DPI coordinates correct when a follower crosses monitors.
    /// </summary>
    internal static CardLayoutMonitorTransform ChooseMonitorForRect(LogicalRect rect, string? savedMonitorId,
        string? currentMonitorId, CardLayoutGeometrySnapshot geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var centerX = rect.X + rect.Width / 2;
        var centerY = rect.Y + rect.Height / 2;
        bool HoldsCenter(CardLayoutMonitorTransform monitor)
        {
            var bounds = monitor.LogicalBounds;
            return centerX >= bounds.X && centerX < bounds.Right && centerY >= bounds.Y && centerY < bounds.Bottom;
        }

        foreach (var preferred in new[] { savedMonitorId, currentMonitorId })
            if (preferred is not null && geometry.Monitors.TryGetValue(preferred, out var candidate) && HoldsCenter(candidate))
                return candidate;
        var holding = geometry.Monitors.Values.Where(HoldsCenter)
            .OrderBy(monitor => monitor.Screen.Id, StringComparer.Ordinal).FirstOrDefault();
        if (holding is not null) return holding;
        foreach (var fallback in new[] { savedMonitorId, currentMonitorId, geometry.PrimaryMonitorId })
            if (fallback is not null && geometry.Monitors.TryGetValue(fallback, out var candidate))
                return candidate;
        return geometry.Monitors.Values.First();
    }

    private static double ValidateScale(double scaling) =>
        double.IsFinite(scaling) && scaling > 0 ? scaling : throw new InvalidOperationException("layout_screen_scale_invalid");
}

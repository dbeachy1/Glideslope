using Avalonia;
using Avalonia.Controls;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>Window design doc §20 (2.1.4): <paramref name="asynchronousPlacement"/> is true on every OS but Windows. There a
/// move is a request the window manager answers later (§20.1), so requested positions are remembered until answered
/// (<see cref="PendingPlacements"/>), and the window manager also keeps app-placed windows inside the work area.
/// <paramref name="time"/> (§20.2a) is the clock requests expire on; the coordinator passes its own.</summary>
internal sealed class AvaloniaCardWindowGeometryBackend(bool asynchronousPlacement, TimeProvider? time = null) : ICardWindowGeometryBackend
{
    private readonly PendingPlacements _pending = new(time ?? TimeProvider.System);

    public AvaloniaCardWindowGeometryBackend() : this(!OperatingSystem.IsWindows()) { }

    public bool WindowManagerConfinesPlacedWindows => asynchronousPlacement;

    public PlacementConfirmation ConfirmPosition(ICardLayoutWindow window, PixelPoint reported, out PixelPoint requested)
    {
        requested = reported;
        return asynchronousPlacement ? _pending.Confirm(window.ProviderId, reported, out requested) : PlacementConfirmation.None;
    }

    public PlacementConfirmation ConfirmSize(ICardLayoutWindow window, LogicalSize reported) =>
        asynchronousPlacement ? _pending.ConfirmSize(window.ProviderId, reported) : PlacementConfirmation.None;

    /// <summary>§20.2b (2.1.8): remembers a requested resize, when it differs from the window's current client size (an
    /// unchanged size gets no answer from X11).</summary>
    private void RecordSizeRequest(ICardLayoutWindow window, Window nativeWindow, LogicalSize requested)
    {
        if (!asynchronousPlacement) return;
        var current = nativeWindow.ClientSize;
        if (Math.Abs(current.Width - requested.Width) > 0.5 || Math.Abs(current.Height - requested.Height) > 0.5)
            _pending.RecordSize(window.ProviderId, requested);
    }

    /// <summary>§20 R1: remembers a requested move. A request for where the window already is gets no answer from X11
    /// (no ConfigureNotify for a no-op move), so it is not remembered; one still pending is dropped then too.</summary>
    private void RecordRequest(ICardLayoutWindow window, Window nativeWindow, PixelPoint requested)
    {
        if (!asynchronousPlacement) return;
        if (nativeWindow.Position == requested)
            _pending.Confirm(window.ProviderId, requested, out _);
        else
            _pending.Record(window.ProviderId, requested);
    }

    public IReadOnlyList<CardLayoutScreen> GetScreens(ICardLayoutWindow window)
    {
        var nativeWindow = RequireNativeWindow(window);
        var screens = nativeWindow.Screens.All;
        var result = new List<CardLayoutScreen>(screens.Count);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in screens)
        {
            var id = string.IsNullOrWhiteSpace(screen.DisplayName)
                ? $"display-{screen.Bounds.X}-{screen.Bounds.Y}-{screen.Bounds.Width}x{screen.Bounds.Height}"
                : screen.DisplayName;
            if (!usedIds.Add(id))
            {
                id = $"{id}-{screen.Bounds.Width}x{screen.Bounds.Height}-{screen.Bounds.X}-{screen.Bounds.Y}";
                usedIds.Add(id);
            }
            result.Add(new CardLayoutScreen(id,
                new PhysicalRect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height),
                new PhysicalRect(screen.WorkingArea.X, screen.WorkingArea.Y,
                    screen.WorkingArea.Width, screen.WorkingArea.Height),
                screen.Scaling, screen.IsPrimary));
        }
        return result;
    }

    public string? GetMonitorId(ICardLayoutWindow window, IReadOnlyList<CardLayoutScreen> screens)
    {
        var nativeWindow = RequireNativeWindow(window);
        var current = nativeWindow.Screens.ScreenFromWindow(nativeWindow);
        if (current is null) return null;
        var matching = screens.FirstOrDefault(screen =>
            screen.Bounds.X == current.Bounds.X && screen.Bounds.Y == current.Bounds.Y &&
            screen.Bounds.Width == current.Bounds.Width && screen.Bounds.Height == current.Bounds.Height);
        return matching?.Id;
    }

    /// <summary>§20 R1: a card with a move in flight reports the requested position, not Avalonia's stale one.</summary>
    public PixelPoint GetPosition(ICardLayoutWindow window) =>
        asynchronousPlacement && _pending.TryGet(window.ProviderId, out var requested)
            ? requested
            : RequireNativeWindow(window).Position;

    public LogicalSize GetSize(ICardLayoutWindow window)
    {
        var nativeWindow = RequireNativeWindow(window);
        return new LogicalSize(nativeWindow.Width, nativeWindow.Height);
    }

    public void SetPosition(ICardLayoutWindow window, PixelPoint position)
    {
        var nativeWindow = RequireNativeWindow(window);
        RecordRequest(window, nativeWindow, position);
        nativeWindow.Position = position;
    }

    public void SetSize(ICardLayoutWindow window, LogicalSize size)
    {
        var nativeWindow = RequireNativeWindow(window);
        RecordSizeRequest(window, nativeWindow, size);
        nativeWindow.Width = size.Width;
        nativeWindow.Height = size.Height;
    }

    /// <summary>On Windows, update native bounds together to avoid an intermediate frame at the old position.
    /// Then synchronize Avalonia's size and position properties. Other platforms use Avalonia's property updates.</summary>
    public void SetBounds(ICardLayoutWindow window, PhysicalRect physical, LogicalSize size)
    {
        var nativeWindow = RequireNativeWindow(window);
        var handle = OperatingSystem.IsWindows() ? nativeWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero : IntPtr.Zero;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, IntPtr.Zero, physical.X, physical.Y, physical.Width, physical.Height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        RecordSizeRequest(window, nativeWindow, size);
        nativeWindow.Width = size.Width;
        nativeWindow.Height = size.Height;
        var position = new PixelPoint(physical.X, physical.Y);
        RecordRequest(window, nativeWindow, position);
        nativeWindow.Position = position;
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static Window RequireNativeWindow(ICardLayoutWindow window) =>
        window.NativeHandle as Window ?? throw new InvalidOperationException("layout_native_window_unavailable");
}

using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Glideslope.App;

/// <summary>Raises a card in z-order without giving it focus (design doc §7).</summary>
internal interface IWindowRaiser
{
    /// <summary>Design doc Appendix H.7: true when the raise was issued, false when it could not be (no
    /// native handle, or the OS refused it), so the coordinator can log failed raises.</summary>
    bool RaiseWithoutActivating(Window window);

    /// <summary>True when raising activates the window and produces an asynchronous Activated event
    /// (<see cref="ActivateWindowRaiser"/>). The coordinator uses this to handle activation echoes and preserve
    /// Settings and peek z-order.</summary>
    bool ActivatesWindow => false;
}

internal static class WindowRaiser
{
    /// <summary>Windows: SetWindowPos. Linux: XRaiseWindow over the app's own X connection; the activating
    /// fallback only when no X connection could be opened.</summary>
    public static IWindowRaiser Create(X11Connection? x11) =>
        OperatingSystem.IsWindows() ? new Win32WindowRaiser()
        : x11 is not null ? new X11WindowRaiser(x11)
        : new ActivateWindowRaiser();
}

/// <summary>SetWindowPos to the top of the non-topmost band (or the topmost band, if the window is
/// topmost) without activating. Raises no Avalonia Activated event, so there is no re-entry.</summary>
internal sealed class Win32WindowRaiser : IWindowRaiser
{
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    public bool RaiseWithoutActivating(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return false;
        return SetWindowPos(handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Reapply native topmost after Avalonia changes a card's taskbar ownership. Its cached
    /// Topmost property can remain true even when the Win32 window has left the topmost band.</summary>
    internal static bool ApplyTopmost(Window window, bool enabled)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return false;
        return SetWindowPos(handle, enabled ? HWND_TOPMOST : HWND_NOTOPMOST,
            0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

/// <summary>
/// Uses XRaiseWindow to raise the card without activation. X11 window managers may ignore client restacks when
/// another application's window has focus; keep-above is reasserted for topmost cards in that case.
/// </summary>
internal sealed class X11WindowRaiser(X11Connection connection) : IWindowRaiser
{
    /// <summary>
    /// X11 window managers can ignore client restacks when another application has focus. A keep-above window is
    /// raised by reasserting keep-above (Topmost off, then
    /// on): that goes through the window manager's _NET_WM_STATE handling, which mutter does not gate on focus, and
    /// making a window keep-above raises it. XRaiseWindow is still sent; it covers a card that is not keep-above, and is
    /// honored after a click on one of our cards. The result is "request sent", not what the window manager did.
    /// </summary>
    public bool RaiseWithoutActivating(Window window)
    {
        if (window.Topmost)
        {
            window.Topmost = false;
            window.Topmost = true;
        }
        return connection.Raise(window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }
}

/// <summary>Linux without an X connection: Avalonia offers no raise-without-focus, so each card is activated in order;
/// the coordinator's _raisingGroup window absorbs the Activated echoes. GNOME may refuse
/// these activations, see <see cref="X11WindowRaiser"/>.)</summary>
internal sealed class ActivateWindowRaiser : IWindowRaiser
{
    public bool ActivatesWindow => true;

    public bool RaiseWithoutActivating(Window window)
    {
        window.Activate();
        return true;
    }
}

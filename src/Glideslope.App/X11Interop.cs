using System.Runtime.InteropServices;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Opens one connection to the X server for pointer and modifier state queries and non-activating raises.
/// Calls run on the UI thread and make a local round trip to the X server.
/// </summary>
internal sealed class X11Connection : IDisposable
{
    private const string LibX11 = "libX11.so.6";
    private IntPtr _display;
    private readonly nuint _root;

    private X11Connection(IntPtr display)
    {
        _display = display;
        _root = XDefaultRootWindow(display);
    }

    /// <summary>Null on Windows, when libX11 is missing, or when DISPLAY names no reachable server.</summary>
    public static X11Connection? TryOpen()
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            return display == IntPtr.Zero ? null : new X11Connection(display);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>The pointer's root position (physical pixels, Avalonia's window-position space) and the key and button
    /// modifier mask. False when the connection is closed or the server refused.</summary>
    public bool QueryPointer(out PhysicalPoint rootPosition, out uint mask)
    {
        rootPosition = default;
        mask = 0;
        if (_display == IntPtr.Zero) return false;
        if (!XQueryPointer(_display, _root, out _, out _, out var rootX, out var rootY, out _, out _, out mask)) return false;
        rootPosition = new PhysicalPoint(rootX, rootY);
        return true;
    }

    /// <summary>Asks the window manager to put <paramref name="window"/> on top of its stacking band, without focus.</summary>
    public bool Raise(IntPtr window)
    {
        if (_display == IntPtr.Zero || window == IntPtr.Zero) return false;
        XRaiseWindow(_display, (nuint)(nint)window);
        XFlush(_display);
        return true;
    }

    public void Dispose()
    {
        if (_display == IntPtr.Zero) return;
        XCloseDisplay(_display);
        _display = IntPtr.Zero;
    }

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern nuint XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] private static extern int XRaiseWindow(IntPtr display, nuint window);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr display);

    [DllImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XQueryPointer(IntPtr display, nuint window, out nuint rootReturn, out nuint childReturn,
        out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
}

/// <summary>
/// Reads pointer, modifier, and button state independently of the focused window.
/// </summary>
internal sealed class X11PeekInputProbe(X11Connection connection) : IPeekInputProbe
{
    private const uint ShiftMask = 1 << 0;
    private const uint ControlMask = 1 << 2;
    private const uint Button1Mask = 1 << 8;
    private const uint Button3Mask = 1 << 10;

    public bool CtrlHeld => connection.QueryPointer(out _, out var mask) && (mask & ControlMask) != 0;
    public bool ShiftHeld => connection.QueryPointer(out _, out var mask) && (mask & ShiftMask) != 0;

    // The physical left and right buttons, as the Windows probe reads them (a peek ends on either going down).
    public bool AnyButtonHeld => connection.QueryPointer(out _, out var mask) && (mask & (Button1Mask | Button3Mask)) != 0;

    public PhysicalPoint? Cursor => connection.QueryPointer(out var position, out _) ? position : null;
}

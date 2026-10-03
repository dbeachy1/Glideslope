using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Wires the operating system's own "drag finished" signal into a card's LayoutGestureTracker, per
/// design doc S5.2. Windows has an authoritative one: the native interactive move/resize loop that
/// Window.BeginMoveDrag/BeginResizeDrag hands off to sends WM_ENTERSIZEMOVE when it starts and
/// WM_EXITSIZEMOVE when it ends, regardless of how long the user pauses mid-drag. Linux has no such
/// hook in Avalonia, so there the quiet period is the primary signal and a PointerReleased delivered
/// to the card after the drag call - when Avalonia does deliver one - is only a faster secondary one.
/// </summary>
internal static class GestureEndSources
{
    // Win32 message identifiers for the native interactive move/resize loop's start and end.
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE = 0x0232;

    /// <summary>
    /// Attaches this window's OS-level end (and, on Windows, start) signal. <paramref name="isCurrentLead"/>
    /// is asked at the moment a signal arrives, since a signal for a window that is not (or is no
    /// longer) the active gesture's lead - for example a stray WM_EXITSIZEMOVE for an unrelated
    /// window move - must not end someone else's gesture. <paramref name="onEnd"/> is expected to be
    /// LayoutGestureTracker.End; this method never calls it for a window that is not the lead.
    /// <paramref name="beginPending"/> begins a Pending gesture for this window. WM_ENTERSIZEMOVE calls it only when
    /// this window is not already the active
    /// gesture's lead, so a keyboard move (Alt+Space), Windows Snap, or the Level 3 harness driving
    /// the OS move loop directly can still snap and dock, exactly as a pointer-press-started drag
    /// does. When the user's own pointer press already began the gesture, WM_ENTERSIZEMOVE only logs.
    /// </summary>
    public static void Attach(Window window, string providerId, Func<bool> isCurrentLead,
        Action beginPending, Action<GestureEndSource> onEnd, IDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(isCurrentLead);
        ArgumentNullException.ThrowIfNull(beginPending);
        ArgumentNullException.ThrowIfNull(onEnd);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWndProcHookCallback(window, (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_ENTERSIZEMOVE)
                {
                    if (isCurrentLead())
                    {
                        diagnostics.Record(new DiagnosticEvent("layout_gesture_sizemove_entered", providerId, Status: "gesture_already_active"));
                    }
                    else
                    {
                        diagnostics.Record(new DiagnosticEvent("layout_gesture_sizemove_entered", providerId, Status: "gesture_started_pending"));
                        beginPending();
                    }
                }
                else if (msg == WM_EXITSIZEMOVE)
                {
                    if (isCurrentLead())
                    {
                        onEnd(GestureEndSource.ExitSizeMove);
                    }
                    else
                    {
                        // Design doc Appendix H.7: a move loop ended on a card that is not the lead (no gesture of
                        // ours, or someone else's); nothing to end, but say so.
                        diagnostics.Record(new DiagnosticEvent("layout_gesture_sizemove_exited", providerId, Status: "no_active_gesture"));
                    }
                }

                // Never handled or swallowed (design doc S5.2): this hook only observes messages so
                // Avalonia's own WndProc, and every other hook on the same window, keeps running.
                return IntPtr.Zero;
            });
            return;
        }

        // Linux fallback: no native move/resize loop hook exists, so PointerReleased is the best
        // secondary signal available; the quiet period (design doc S5.1) covers the rest.
        // The button probe is not reliable on every platform; the quiet period remains the fallback end signal.
        window.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs _) =>
        {
            if (isCurrentLead()) onEnd(GestureEndSource.PointerRelease);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }
}

/// <summary>
/// Reads whether the primary mouse button is physically held right now, used by
/// LayoutGestureTracker's quiet period so pausing mid-drag never commits a snap or dock
/// (design doc S5.1).
/// </summary>
internal static class PrimaryPointerButtonProbe
{
    private const int SM_SWAPBUTTON = 23;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    /// <summary>
    /// Windows: GetAsyncKeyState reports the physical buttons, so a swapped-button setup reads the
    /// right button as primary. Elsewhere (and on a desktop that is not the input desktop, where
    /// GetAsyncKeyState reads zero) this reports false and the quiet period alone ends a gesture.
    /// </summary>
    public static bool IsHeld()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var virtualKey = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

namespace Glideslope.NativeProof;

/// <summary>A single planned step of a driven gesture: the card's next real window rectangle.</summary>
internal readonly record struct GestureStep(int X, int Y, int Width, int Height);

/// <summary>
/// Drives one card's HWND through the real OS move/size envelope: WM_ENTERSIZEMOVE, a series
/// of SetWindowPos calls with a real pump between each (so the app's own UI-thread dispatcher has drained
/// each WM_WINDOWPOSCHANGED before the next SetWindowPos arrives, the same way it would between real
/// mouse-move samples), then WM_EXITSIZEMOVE. SendInput only reaches the desktop the user is looking at,
/// so it cannot drive a hidden desktop,
/// and this harness's whole point is to run on one.
/// </summary>
internal static class GestureDriver
{
    /// <summary>Hang watchdog for the message-queue round trip after each gesture step. It does not set a
    /// timing deadline; it only bounds a UI thread that stops processing messages.</summary>
    private static readonly TimeSpan GestureStepPumpHangWatchdog = TimeSpan.FromSeconds(60);

    public static void Drive(nint hwnd, IReadOnlyList<GestureStep> steps, Action<string> log, string label)
    {
        if (steps.Count == 0) throw new ArgumentException("Gesture needs at least one step.", nameof(steps));

        log($"gesture_drive_begin label={label} hwnd={hwnd:X} steps={steps.Count}");
        NativeMethods.SendMessageW(hwnd, NativeMethods.WM_ENTERSIZEMOVE, 0, 0);
        foreach (var step in steps)
        {
            var moved = NativeMethods.SetWindowPos(hwnd, 0, step.X, step.Y, step.Width, step.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            log($"gesture_drive_step label={label} x={step.X} y={step.Y} width={step.Width} height={step.Height} setWindowPosSucceeded={moved}");
            // Wait for the window queue to process the position change before sending the next step.
            var delivered = NativeMethods.SendMessageTimeoutW(hwnd, NativeMethods.WM_NULL, 0, 0,
                NativeMethods.SMTO_NORMAL, (uint)GestureStepPumpHangWatchdog.TotalMilliseconds, out _);
            log($"gesture_drive_step_pump label={label} delivered={delivered != 0}");
        }

        NativeMethods.SendMessageW(hwnd, NativeMethods.WM_EXITSIZEMOVE, 0, 0);
        log($"gesture_drive_end label={label}");
    }

    /// <summary>Builds a linear series of intermediate rectangles from <paramref name="start"/> to
    /// <paramref name="end"/>, matching how a real drag samples several points along the way rather
    /// than jumping straight to the destination.</summary>
    public static IReadOnlyList<GestureStep> Interpolate(NativeMethods.RECT start, NativeMethods.RECT end, int stepCount)
    {
        var steps = new List<GestureStep>(stepCount);
        var startWidth = start.Right - start.Left;
        var startHeight = start.Bottom - start.Top;
        var endWidth = end.Right - end.Left;
        var endHeight = end.Bottom - end.Top;
        for (var i = 1; i <= stepCount; i++)
        {
            var t = (double)i / stepCount;
            steps.Add(new GestureStep(
                Lerp(start.Left, end.Left, t),
                Lerp(start.Top, end.Top, t),
                Lerp(startWidth, endWidth, t),
                Lerp(startHeight, endHeight, t)));
        }

        return steps;
    }

    private static int Lerp(int from, int to, double t) => (int)Math.Round(from + (to - from) * t);
}

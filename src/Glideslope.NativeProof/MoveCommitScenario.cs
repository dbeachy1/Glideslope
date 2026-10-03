namespace Glideslope.NativeProof;

/// <summary>Moves the Claude card through the native move/size envelope and verifies that a pure move
/// reaches layout_gesture_committed. Avalonia's position change does not raise a property-change
/// notification, so the gesture tracker must observe the native move completion directly.</summary>
internal static class MoveCommitScenario
{
    public static ScenarioResult Run(ScenarioContext context)
    {
        const string providerId = "claude";
        var log = context.Diagnostics;
        if (!context.CardWindows.TryGetValue(providerId, out var hwnd))
            return new ScenarioResult("move_commit", false, $"card window for {providerId} was never found", []);

        if (!NativeMethods.GetWindowRect(hwnd, out var start))
            return new ScenarioResult("move_commit", false, "GetWindowRect failed before driving the move", []);

        var end = new NativeMethods.RECT
        {
            Left = start.Left + 150,
            Top = start.Top + 80,
            Right = start.Right + 150,
            Bottom = start.Bottom + 80,
        };

        var sinceOffset = context.Log.CurrentLength();
        GestureDriver.Drive(hwnd, GestureDriver.Interpolate(start, end, stepCount: 5), log, "move_commit");

        var committed = context.Log.WaitForLine(sinceOffset,
            line => line.Code == "layout_gesture_committed" && line.ProviderId == providerId);
        if (committed is not null)
            return new ScenarioResult("move_commit", true, "layout_gesture_committed observed for the moved card",
                [committed.Raw]);

        var tail = context.Log.TailSince(sinceOffset).Select(line => line.Raw).ToList();
        return new ScenarioResult("move_commit", false,
            $"app_silent_60s waiting for layout_gesture_committed for provider={providerId} after a pure move",
            tail);
    }
}

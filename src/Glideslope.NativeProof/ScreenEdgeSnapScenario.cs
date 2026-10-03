namespace Glideslope.NativeProof;

/// <summary>
/// Dragging past the left screen edge snaps to the work area. This proof drags Claude 30 pixels past
/// the primary monitor's work
/// area left edge and releases; with Snap on (seeded true — see ProofSettingsSeeder) the card should
/// come to rest with its left edge exactly at the work area's left edge. Asserted on
/// layout_gesture_committed plus the card's real post-gesture GetWindowRect, which is the authoritative
/// observation for where the OS actually put the window (independent of whether the commit also
/// persisted it to settings.json). Claude is the group's leftmost card, so snapping it preserves the
/// group's position at the work-area edge.
/// </summary>
internal static class ScreenEdgeSnapScenario
{
    private const int PastEdgeBy = 30;
    // The inter-card gap the workflow scenario's docks leave between Claude, Codex and Gemini.
    private const int GroupGap = 10;

    public static ScenarioResult Run(ScenarioContext context)
    {
        const string providerId = "claude";
        var log = context.Diagnostics;
        if (!context.CardWindows.TryGetValue(providerId, out var hwnd))
            return new ScenarioResult("screen_edge_snap", false, $"card window for {providerId} was never found", []);

        if (!NativeMethods.GetWindowRect(hwnd, out var start))
            return new ScenarioResult("screen_edge_snap", false, "GetWindowRect failed before driving the drag", []);

        var workArea = ProofSettingsSeeder.ReadPrimaryWorkArea();
        var width = start.Right - start.Left;
        var height = start.Bottom - start.Top;
        var end = new NativeMethods.RECT
        {
            Left = workArea.Left - PastEdgeBy,
            Top = start.Top,
            Right = workArea.Left - PastEdgeBy + width,
            Bottom = start.Top + height,
        };

        var sinceOffset = context.Log.CurrentLength();
        GestureDriver.Drive(hwnd, GestureDriver.Interpolate(start, end, stepCount: 6), log, "screen_edge_snap");

        var committed = context.Log.WaitForLine(sinceOffset,
            line => line.Code == "layout_gesture_committed" && line.ProviderId == providerId);
        var evidence = context.Log.TailSince(sinceOffset).Select(line => line.Raw).ToList();
        if (committed is null)
            return new ScenarioResult("screen_edge_snap", false,
                $"app_silent_60s waiting for layout_gesture_committed for provider={providerId} after dragging past the left work-area edge",
                evidence);

        NativeMethods.GetWindowRect(hwnd, out var final);
        evidence.Add($"work_area_left={workArea.Left} final_left={final.Left} dragged_to={end.Left}");

        // The workflow leaves all three cards docked in one row; snapping the lead must preserve both gaps.
        if (!context.CardWindows.TryGetValue("codex", out var codexHwnd) ||
            !context.CardWindows.TryGetValue("gemini", out var geminiHwnd))
            return new ScenarioResult("screen_edge_snap", false, "Codex or Gemini card window was never found", evidence);
        NativeMethods.GetWindowRect(codexHwnd, out var codexFinal);
        NativeMethods.GetWindowRect(geminiHwnd, out var geminiFinal);
        evidence.Add($"claude_right={final.Right} codex_left={codexFinal.Left} codex_right={codexFinal.Right} gemini_left={geminiFinal.Left}");

        if (final.Left != workArea.Left)
            return new ScenarioResult("screen_edge_snap", false,
                $"card settled at left={final.Left}, not the work area's left edge ({workArea.Left})", evidence);

        if (codexFinal.Left != final.Right + GroupGap || geminiFinal.Left != codexFinal.Right + GroupGap)
            return new ScenarioResult("screen_edge_snap", false,
                $"group came apart past the screen edge: codex_left={codexFinal.Left} (expected {final.Right + GroupGap}), " +
                $"gemini_left={geminiFinal.Left} (expected {codexFinal.Right + GroupGap})", evidence);

        return new ScenarioResult("screen_edge_snap", true, "card snapped back to the work area's left edge with its group intact", evidence);
    }
}

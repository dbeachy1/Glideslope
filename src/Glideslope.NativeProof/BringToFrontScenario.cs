namespace Glideslope.NativeProof;

/// <summary>Activates Codex on the hidden desktop and verifies that the coordinator raises the card group.
/// Because a hidden desktop is not the input desktop, the proof sends WM_ACTIVATE directly after attempting
/// SetForegroundWindow. It verifies the app's activation response and card z-order; an OS foreground change
/// and ordering above the helper window cannot be established on a hidden desktop.</summary>
internal static class BringToFrontScenario
{
    public static ScenarioResult Run(ScenarioContext context, nint hiddenDesktop, HelperMarkerWindow helper)
    {
        var log = context.Diagnostics;
        var evidence = new List<string>();
        if (helper.StartupFailure is not null)
            return new ScenarioResult("bring_to_front", false, $"helper marker window failed to start: {helper.StartupFailure}", evidence);
        if (!context.CardWindows.TryGetValue("codex", out var codexHwnd))
            return new ScenarioResult("bring_to_front", false, "card window for codex was never found", evidence);

        // Raise the helper to the top of the ordinary (non-topmost) band first, so the "cards end up
        // above it" check means something. It must NOT be HWND_TOPMOST: a topmost window stays above
        // every ordinary window no matter what the app does, so the run with HWND_TOPMOST
        // showed the cards correctly raised (Codex, then Claude, then Gemini) and still "failed".
        NativeMethods.SetWindowPos(helper.Handle, NativeMethods.HWND_TOP, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        var before = ZOrderIndex(hiddenDesktop, [helper.Handle, .. context.CardWindows.Values]);
        evidence.Add($"z_order_before helper={before[helper.Handle]} " +
            string.Join(" ", context.CardWindows.Select(pair => $"{pair.Key}={before[pair.Value]}")));

        var sinceOffset = context.Log.CurrentLength();
        // The handoff must come from a thread on the hidden desktop that owns its foreground window
        // (the helper), not from this thread on the Default desktop; see HelperMarkerWindow.ActivateFromHelperThread.
        var activated = helper.ActivateFromHelperThread(codexHwnd, TimeSpan.FromSeconds(5));
        evidence.Add($"set_foreground_window_succeeded={activated}");
        // The direct message is synchronous; the coordinator's response is awaited on its log line below.
        var raisedBeforeSend = context.Log.TailSince(sinceOffset).Any(line => line.Code == "layout_group_raised");
        evidence.Add($"layout_group_raised_before_direct_activate={raisedBeforeSend}");

        // A hidden desktop has no input foreground window, so SetForegroundWindow may not activate Avalonia's
        // window. Deliver WM_ACTIVATE directly as the hidden-desktop equivalent of a user activation.
        NativeMethods.SendMessageW(codexHwnd, NativeMethods.WM_ACTIVATE, NativeMethods.WA_ACTIVE, 0);

        var raised = context.Log.WaitForLine(sinceOffset,
            line => line.Code == "layout_group_raised");
        evidence.AddRange(context.Log.TailSince(sinceOffset).Select(line => line.Raw));

        var after = ZOrderIndex(hiddenDesktop, [helper.Handle, .. context.CardWindows.Values]);
        evidence.Add($"z_order_after helper={after[helper.Handle]} " +
            string.Join(" ", context.CardWindows.Select(pair => $"{pair.Key}={after[pair.Value]}")));

        // Preserve the full app log in failure evidence because session cleanup removes the proof directory.
        if (raised is null)
        {
            NativeMethods.GetWindowThreadProcessId(codexHwnd, out var codexProcessId);
            evidence.Add($"codex_still_visible={NativeMethods.IsWindowVisible(codexHwnd)} codex_owner_process={codexProcessId}");
            evidence.Add("full_session_app_log_follows (captured before this session's cleanup deletes the proof root):");
            evidence.AddRange(context.Log.TailSince(0).Select(line => $"  full_log: {line.Raw}"));
            return new ScenarioResult("bring_to_front", false,
                "app_silent_60s waiting for layout_group_raised after activating Codex", evidence);
        }

        // Check the supported proof contract: the coordinator raised all cards and Codex is above its peers.
        var raisedAll = raised.Status?.Contains("count=3", StringComparison.Ordinal) == true;
        var codexOnTop = context.CardWindows.Values.All(hwnd => hwnd == codexHwnd || after[codexHwnd] < after[hwnd]);
        if (!raisedAll || !codexOnTop)
            return new ScenarioResult("bring_to_front", false,
                "layout_group_raised was logged but not for all three cards, or Codex did not end above the other two cards",
                evidence);

        return new ScenarioResult("bring_to_front", true,
            "Codex's activation (delivered by a direct WM_ACTIVATE send) raised all three cards with Codex on top of the other two " +
            "(an OS foreground change and above-the-helper are not provable on a hidden desktop)", evidence);
    }

    /// <summary>Maps each of <paramref name="windows"/> to its position in the hidden desktop's
    /// EnumDesktopWindows order (0 = topmost), for the before/after z-order comparison.</summary>
    private static Dictionary<nint, int> ZOrderIndex(nint hiddenDesktop, IEnumerable<nint> windows)
    {
        var wanted = new HashSet<nint>(windows);
        var order = new List<nint>();
        bool Callback(nint hwnd, nint _)
        {
            if (wanted.Contains(hwnd)) order.Add(hwnd);
            return true;
        }

        NativeMethods.EnumDesktopWindows(hiddenDesktop, Callback, 0);
        var index = new Dictionary<nint, int>();
        for (var i = 0; i < order.Count; i++) index[order[i]] = i;
        foreach (var hwnd in wanted.Where(hwnd => !index.ContainsKey(hwnd))) index[hwnd] = int.MaxValue;
        return index;
    }
}

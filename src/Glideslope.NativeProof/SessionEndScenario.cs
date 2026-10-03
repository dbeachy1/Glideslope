using System.Diagnostics;

namespace Glideslope.NativeProof;

/// <summary>
/// A Windows restart sends
/// WM_QUERYENDSESSION to every top-level window; Avalonia answers it by closing every window, and the cards took
/// each close as the user's X, so Codex and Claude were turned off (the last card cannot be) and the cancelled
/// closes made the app refuse the restart. This sends the same WM_QUERYENDSESSION (lParam 0: restart or shutdown)
/// to every top-level window the app owns on the hidden desktop, as Windows does, and checks that the app agrees
/// to end the session, closes each card for shutdown, turns no provider off, exits on its own, and leaves all
/// three providers enabled in settings.json.
/// </summary>
internal static class SessionEndScenario
{
    private const string Name = "session_end";
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;

    public static ScenarioResult Run(ScenarioContext context, nint desktopHandle)
    {
        var log = context.Diagnostics;
        var anyCard = context.CardWindows.Values.First();
        NativeMethods.GetWindowThreadProcessId(anyCard, out var processId);
        using var process = Process.GetProcessById((int)processId);
        var windows = new List<nint>();
        NativeMethods.EnumDesktopWindows(desktopHandle, (hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == processId) windows.Add(hwnd);
            return true;
        }, 0);
        log($"session_end_windows pid={processId} count={windows.Count}");

        var sinceOffset = context.Log.CurrentLength();
        var evidence = new List<string>();
        var refused = new List<string>();
        foreach (var hwnd in windows)
        {
            // A window already destroyed by the shutdown the first reply started simply fails the send; only an
            // explicit FALSE reply is a refusal.
            var sent = NativeMethods.SendMessageTimeoutW(hwnd, WM_QUERYENDSESSION, 0, 0, NativeMethods.SMTO_NORMAL,
                10_000, out var reply);
            evidence.Add($"query_end_session hwnd=0x{hwnd:X} sent={sent != 0} reply={reply}");
            if (sent != 0 && reply == 0) refused.Add($"0x{hwnd:X}");
        }
        // Windows follows an all-TRUE query with WM_ENDSESSION(TRUE); the app is normally gone by then.
        if (refused.Count == 0)
            foreach (var hwnd in windows)
                NativeMethods.SendMessageTimeoutW(hwnd, WM_ENDSESSION, 1, 0, NativeMethods.SMTO_NORMAL, 10_000, out _);

        var requested = context.Log.WaitForLine(sinceOffset, line => line.Code == "session_end_requested");
        if (requested is not null) evidence.Add(requested.Raw);
        // Hang watchdog only, the same 60 s bound ProofLog uses; the wait itself is on the process handle.
        var exited = process.WaitForExit(TimeSpan.FromSeconds(60));
        evidence.Add($"app_exited={exited}");

        var tail = context.Log.TailSince(sinceOffset);
        var closedForShutdown = tail.Where(line => line.Code == "card_closed_for_shutdown")
            .Select(line => line.ProviderId).ToHashSet(StringComparer.Ordinal);
        var disabled = tail.Where(line => line.Code is "provider_disabled" or "provider_disable_save_failed"
            or "user_closed_final_card").Select(line => line.Raw).ToList();
        evidence.AddRange(tail.Where(line => line.Code is "card_closed_for_shutdown").Select(line => line.Raw));
        evidence.AddRange(disabled);

        var settings = ProofSettingsReader.TryRead(context.SettingsFile, log);
        var enabled = settings?.EnabledProviderIds ?? [];
        evidence.Add($"settings_enabled_providers=[{string.Join(",", enabled)}]");

        var failures = new List<string>();
        if (refused.Count > 0) failures.Add($"the app refused the session end on {string.Join(",", refused)}");
        if (requested is null) failures.Add("no session_end_requested line");
        if (!exited) failures.Add("the app did not exit after agreeing to end the session");
        foreach (var providerId in CardWindowLocator.ExpectedProviderIds)
        {
            if (!closedForShutdown.Contains(providerId)) failures.Add($"no card_closed_for_shutdown for {providerId}");
            if (!enabled.Contains(providerId, StringComparer.Ordinal)) failures.Add($"{providerId} is no longer enabled");
        }
        if (disabled.Count > 0) failures.Add("a card close was taken as the user's X");

        return failures.Count == 0
            ? new ScenarioResult(Name, true, "the session end closed every card, turned nothing off, and the app exited", evidence)
            : new ScenarioResult(Name, false, string.Join("; ", failures), evidence.Concat(tail.Select(line => line.Raw)).ToList());
    }
}

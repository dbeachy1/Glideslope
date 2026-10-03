namespace Glideslope.NativeProof;

/// <summary>
/// 2.0 design §8.3, item 3: "Full onto mini," the reverse of
/// MiniOntoFullDockScenario. Runs alone, in its own app session, on the same
/// ProofSettingsSeeder.SeedModeScenario seed: Claude and Codex already docked (full, 1000x720), Gemini
/// detached to their right (full, 960x700, its own one-card group). The sizes differ so "Gemini's return size
/// equals Codex's" can only pass when the dock adopted the target's, not when it kept Gemini's own.
///
/// Invokes Claude's "Mini mode" button (the Claude+Codex group goes mini), then drags Gemini (still full)
/// next to Codex's right edge with the existing gesture driver so the magnet docks it. Expects
/// layout_dock_committed with targetMode=Mini and modeChanged=1, the dragged window (Gemini) at the
/// group's mini size, and its saved return size (FullWidth/FullHeight) equal to Codex's (2.0 design §3.2:
/// an incoming full card takes the mini destination's remembered full size).
/// </summary>
internal static class FullOntoMiniDockScenario
{
    private const int MiniDefaultWidth = 530;
    private const int MiniDefaultHeight = 270;

    public static ScenarioResult Run(ScenarioContext context)
    {
        const string name = "full_onto_mini_dock";
        var log = context.Diagnostics;
        var evidence = new List<string>();

        if (!context.CardWindows.TryGetValue("claude", out var claudeHwnd) ||
            !context.CardWindows.TryGetValue("codex", out var codexHwnd) ||
            !context.CardWindows.TryGetValue("gemini", out var geminiHwnd))
            return new ScenarioResult(name, false, "one or more card windows were never found", evidence);
        if (!CardModeScenarioSupport.WaitForSeededLayout(context, evidence))
            return new ScenarioResult(name, false, "the app did not restore the seeded card sizes", evidence);

        // Step 1: toggle the Claude+Codex group to mini.
        var sinceToggle = context.Log.CurrentLength();
        if (!AutomationInterop.InvokeButtonByName(claudeHwnd, "Mini mode", log))
            return new ScenarioResult(name, false, "could not invoke Claude's \"Mini mode\" button via UI Automation", evidence);

        var toMini = context.Log.WaitForLine(sinceToggle,
            line => line.Code == "card_mode_switched" &&
                (line.Status?.Contains("reason=toggle", StringComparison.Ordinal) ?? false) &&
                (line.Status?.Contains("to=Mini", StringComparison.Ordinal) ?? false));
        evidence.AddRange(context.Log.TailSince(sinceToggle).Select(line => line.Raw));
        if (toMini is null)
            return new ScenarioResult(name, false,
                "app_silent_60s waiting for card_mode_switched reason=toggle to=Mini after invoking Claude's Mini mode button",
                evidence);
        if (!toMini.Status!.Contains("cards=claude", StringComparison.Ordinal) || !toMini.Status.Contains("codex", StringComparison.Ordinal))
            return new ScenarioResult(name, false, "card_mode_switched to=Mini did not cover both Claude and Codex", evidence);

        // Wait for the durable save before reading Codex's saved return size.
        var afterToggle = CardModeScenarioSupport.WaitForSavedSettings(context, sinceToggle, log);
        var codexCardAfterToggle = afterToggle?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "codex");
        evidence.Add($"codex_return_size_after_toggle=[{codexCardAfterToggle?.FullWidth},{codexCardAfterToggle?.FullHeight}]");
        if (codexCardAfterToggle is null)
            return new ScenarioResult(name, false, "could not read Codex's card entry from settings.json after the toggle", evidence);
        if (codexCardAfterToggle.FullWidth <= 0)
            return new ScenarioResult(name, false, "Codex's saved return size was not committed (still 0) after the toggle to mini", evidence);

        // Step 2: drag Gemini (still full, untouched) next to Codex's (now mini) right edge so the magnet docks it.
        var codexMini = CardModeScenarioSupport.RectAfterLayoutSaved(context, sinceToggle, codexHwnd);
        evidence.Add($"codex_rect_before_dock=[{codexMini.Left},{codexMini.Top},{codexMini.Right},{codexMini.Bottom}]");
        if (CardModeScenarioSupport.Size(codexMini) != (MiniDefaultWidth, MiniDefaultHeight))
            return new ScenarioResult(name, false, $"Codex is not at the mini default size {MiniDefaultWidth}x{MiniDefaultHeight} before the dock drag", evidence);

        var sinceDock = context.Log.CurrentLength();
        var committed = CardModeScenarioSupport.DockToRightOf(geminiHwnd, "gemini", codexMini, context, "full_onto_mini_dock");
        evidence.Add($"dock_gesture_committed={committed is not null} line=\"{committed?.Raw}\"");
        if (committed is null)
            return new ScenarioResult(name, false,
                "app_silent_60s waiting for layout_gesture_committed while dragging Gemini next to Codex",
                evidence);

        var dockCommitted = context.Log.TailSince(sinceDock).LastOrDefault(line =>
            line.Code == "layout_dock_committed" && line.ProviderId == "gemini" &&
            (line.Status?.StartsWith("codex,targetMode=Mini,modeChanged=1", StringComparison.Ordinal) ?? false));
        evidence.Add($"layout_dock_committed_found={dockCommitted is not null} line=\"{dockCommitted?.Raw}\"");
        if (dockCommitted is null)
            return new ScenarioResult(name, false,
                "no layout_dock_committed provider=gemini status=codex,targetMode=Mini,modeChanged=1 was logged", evidence);

        if (!NativeMethods.GetWindowRect(codexHwnd, out var codexAfterDock))
            return new ScenarioResult(name, false, "GetWindowRect failed for Codex after the dock committed", evidence);
        // Read once after layout_saved, not off layout_dock_committed itself - see
        // CardModeScenarioSupport.RectAfterLayoutSaved's remarks.
        var geminiAfterDock = CardModeScenarioSupport.RectAfterLayoutSaved(context, sinceDock, geminiHwnd);
        var geminiMatchesCodex = CardModeScenarioSupport.Size(geminiAfterDock) == CardModeScenarioSupport.Size(codexAfterDock);
        evidence.Add($"after_dock gemini_size={CardModeScenarioSupport.Size(geminiAfterDock)} codex_size={CardModeScenarioSupport.Size(codexAfterDock)}");
        if (!geminiMatchesCodex)
            return new ScenarioResult(name, false, "Gemini did not end at the group's mini size after the dock", evidence);

        // Wait for the durable save before checking Gemini's saved return size.
        bool GeminiReturnSizeMatchesCodex(Glideslope.Core.AppSettings settings)
        {
            var card = settings.Layout.Cards.FirstOrDefault(c => c.ProviderId == "gemini");
            return card is not null &&
                Math.Abs(card.FullWidth - codexCardAfterToggle.FullWidth) < 0.5 && Math.Abs(card.FullHeight - codexCardAfterToggle.FullHeight) < 0.5;
        }
        var afterDockSettings = CardModeScenarioSupport.WaitForSavedSettings(context, sinceDock, log);
        var geminiCard = afterDockSettings?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "gemini");
        evidence.Add($"gemini_return_size=[{geminiCard?.FullWidth},{geminiCard?.FullHeight}] " +
            $"expected=[{codexCardAfterToggle.FullWidth},{codexCardAfterToggle.FullHeight}]");
        if (afterDockSettings is null || !GeminiReturnSizeMatchesCodex(afterDockSettings))
            return new ScenarioResult(name, false, "Gemini's saved return size does not equal Codex's (the target's) return size", evidence);

        return new ScenarioResult(name, true,
            "toggling Claude+Codex to mini and docking Gemini onto Codex committed targetMode=Mini modeChanged=1 with Gemini at the group's mini size and return size",
            evidence);
    }
}

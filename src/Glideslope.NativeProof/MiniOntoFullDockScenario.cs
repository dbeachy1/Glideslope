namespace Glideslope.NativeProof;

/// <summary>
/// 2.0 design §8.3, item 2: "Mini onto full." Runs alone, in its own app
/// session, on ProofSettingsSeeder.SeedModeScenario's seed: Claude and Codex already docked (full,
/// 1000x720), Gemini detached to their right (full, 960x700, its own one-card group).
///
/// Invokes Gemini's "Mini mode" button (a single-card group), then drags Gemini next to Codex's right
/// edge with the existing gesture driver so the magnet docks it. Expects layout_dock_committed with
/// targetMode=Full and modeChanged=1, the dragged window (Gemini) at Codex's (full) size, in full mode -
/// checked externally by its toggle button reading "Mini mode" again (only a full card offers to switch
/// to mini) - and its saved return size (FullWidth/FullHeight) clearing to 0 (2.0 design §3.2: a full
/// destination has no return size, so an incoming mini card forgets its own).
/// </summary>
internal static class MiniOntoFullDockScenario
{
    private const int MiniDefaultWidth = 530;
    private const int MiniDefaultHeight = 270;

    public static ScenarioResult Run(ScenarioContext context)
    {
        const string name = "mini_onto_full_dock";
        var log = context.Diagnostics;
        var evidence = new List<string>();

        if (!context.CardWindows.TryGetValue("codex", out var codexHwnd) ||
            !context.CardWindows.TryGetValue("gemini", out var geminiHwnd))
            return new ScenarioResult(name, false, "one or more card windows were never found", evidence);
        if (!CardModeScenarioSupport.WaitForSeededLayout(context, evidence))
            return new ScenarioResult(name, false, "the app did not restore the seeded card sizes", evidence);

        // Step 1: toggle Gemini (a standalone full card) to mini.
        if (!NativeMethods.GetWindowRect(geminiHwnd, out var geminiStart))
            return new ScenarioResult(name, false, "GetWindowRect failed for Gemini at the start", evidence);
        var geminiStartSize = CardModeScenarioSupport.Size(geminiStart);
        var sinceToggle = context.Log.CurrentLength();
        if (!AutomationInterop.InvokeButtonByName(geminiHwnd, "Mini mode", log))
            return new ScenarioResult(name, false, "could not invoke Gemini's \"Mini mode\" button via UI Automation", evidence);

        var toMini = context.Log.WaitForLine(sinceToggle,
            line => line.Code == "card_mode_switched" &&
                (line.Status?.Contains("reason=toggle", StringComparison.Ordinal) ?? false) &&
                (line.Status?.Contains("cards=gemini", StringComparison.Ordinal) ?? false) &&
                (line.Status?.Contains("to=Mini", StringComparison.Ordinal) ?? false));
        evidence.AddRange(context.Log.TailSince(sinceToggle).Select(line => line.Raw));
        if (toMini is null)
            return new ScenarioResult(name, false,
                "app_silent_60s waiting for card_mode_switched reason=toggle cards=gemini to=Mini after invoking Gemini's Mini mode button",
                evidence);

        // Read once after layout_saved, not off the card_mode_switched line itself - see
        // CardModeScenarioSupport.RectAfterLayoutSaved's remarks.
        var geminiMini = CardModeScenarioSupport.RectAfterLayoutSaved(context, sinceToggle, geminiHwnd);
        evidence.Add($"after_toggle_to_mini gemini_size={CardModeScenarioSupport.Size(geminiMini)}");
        if (CardModeScenarioSupport.Size(geminiMini) != (MiniDefaultWidth, MiniDefaultHeight))
            return new ScenarioResult(name, false, $"Gemini did not land at the mini default size {MiniDefaultWidth}x{MiniDefaultHeight}", evidence);

        // the toggle must first have stamped Gemini's own full size as its
        // return size, or the "cleared to 0" check after the dock would only be reading the seed's 0.
        bool GeminiReturnSizeIsItsStart(Glideslope.Core.AppSettings settings)
        {
            var card = settings.Layout.Cards.FirstOrDefault(c => c.ProviderId == "gemini");
            return card is not null && Math.Abs(card.FullWidth - geminiStartSize.Width) < 0.5 &&
                Math.Abs(card.FullHeight - geminiStartSize.Height) < 0.5;
        }
        var afterToggleSettings = CardModeScenarioSupport.WaitForSavedSettings(context, sinceToggle, log);
        var geminiAfterToggle = afterToggleSettings?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "gemini");
        evidence.Add($"after_toggle_to_mini gemini_return=[{geminiAfterToggle?.FullWidth},{geminiAfterToggle?.FullHeight}] expected={geminiStartSize}");
        if (afterToggleSettings is null || !GeminiReturnSizeIsItsStart(afterToggleSettings))
            return new ScenarioResult(name, false, "Gemini's saved return size is not its pre-toggle full size after going mini", evidence);

        // Step 2: drag Gemini (now mini) next to Codex's right edge so the magnet docks it.
        if (!NativeMethods.GetWindowRect(codexHwnd, out var codexRect))
            return new ScenarioResult(name, false, "GetWindowRect failed for Codex before the dock drag", evidence);
        var sinceDock = context.Log.CurrentLength();
        var committed = CardModeScenarioSupport.DockToRightOf(geminiHwnd, "gemini", codexRect, context, "mini_onto_full_dock");
        evidence.Add($"dock_gesture_committed={committed is not null} line=\"{committed?.Raw}\"");
        if (committed is null)
            return new ScenarioResult(name, false,
                "app_silent_60s waiting for layout_gesture_committed while dragging Gemini next to Codex",
                evidence);

        var dockCommitted = context.Log.TailSince(sinceDock).LastOrDefault(line =>
            line.Code == "layout_dock_committed" && line.ProviderId == "gemini" &&
            (line.Status?.StartsWith("codex,targetMode=Full,modeChanged=1", StringComparison.Ordinal) ?? false));
        evidence.Add($"layout_dock_committed_found={dockCommitted is not null} line=\"{dockCommitted?.Raw}\"");
        if (dockCommitted is null)
            return new ScenarioResult(name, false,
                "no layout_dock_committed provider=gemini status=codex,targetMode=Full,modeChanged=1 was logged", evidence);

        if (!NativeMethods.GetWindowRect(codexHwnd, out var codexAfterDock))
            return new ScenarioResult(name, false, "GetWindowRect failed for Codex after the dock committed", evidence);
        // Read once after layout_saved, not off layout_dock_committed itself - see
        // CardModeScenarioSupport.RectAfterLayoutSaved's remarks.
        var geminiAfterDock = CardModeScenarioSupport.RectAfterLayoutSaved(context, sinceDock, geminiHwnd);
        var geminiMatchesCodex = CardModeScenarioSupport.Size(geminiAfterDock) == CardModeScenarioSupport.Size(codexAfterDock);
        evidence.Add($"after_dock gemini_size={CardModeScenarioSupport.Size(geminiAfterDock)} codex_size={CardModeScenarioSupport.Size(codexAfterDock)}");
        if (!geminiMatchesCodex)
            return new ScenarioResult(name, false, "Gemini did not end at Codex's size after the dock", evidence);

        // "in full mode" is checked externally: only a full card's toggle offers to switch to mini, and
        // a full card's saved return size is 0 (2.0 design §3.2/§3.3). By this point layout_saved for the
        // dock has already been observed (RectAfterLayoutSaved above waited on it), so a single automation
        // attempt is safe (window design "Level 3 waits" item 6).
        var geminiIsFull = AutomationInterop.ButtonExists(geminiHwnd, "Mini mode", log);
        evidence.Add($"gemini_toggle_reads_mini_mode={geminiIsFull}");
        if (!geminiIsFull)
            return new ScenarioResult(name, false, "Gemini's toggle button does not read \"Mini mode\" after the dock, so it is not in full mode", evidence);

        // Wait for the durable save before checking Gemini's saved return size.
        bool GeminiReturnSizeCleared(Glideslope.Core.AppSettings settings)
        {
            var card = settings.Layout.Cards.FirstOrDefault(c => c.ProviderId == "gemini");
            return card is not null && card.FullWidth == 0 && card.FullHeight == 0;
        }
        var afterDockSettings = CardModeScenarioSupport.WaitForSavedSettings(context, sinceDock, log);
        var geminiCard = afterDockSettings?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "gemini");
        evidence.Add($"gemini_return_size=[{geminiCard?.FullWidth},{geminiCard?.FullHeight}]");
        if (afterDockSettings is null || !GeminiReturnSizeCleared(afterDockSettings))
            return new ScenarioResult(name, false, "Gemini's saved return size did not clear to 0 after docking onto a full card", evidence);

        return new ScenarioResult(name, true,
            "toggling Gemini to mini and docking it onto Codex committed targetMode=Full modeChanged=1 with Gemini at Codex's size",
            evidence);
    }
}

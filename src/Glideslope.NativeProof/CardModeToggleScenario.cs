using Glideslope.Core;

namespace Glideslope.NativeProof;

/// <summary>
/// 2.0 design §8.3, item 1: "Toggle a group." Runs alone, in its own app
/// session, on ProofSettingsSeeder.SeedModeScenario's seed: Claude and Codex already docked side by side
/// (Claude left, Codex right, LayoutPolicy.DefaultInterCardGap), both full at 1000x720 (deliberately not the
/// full default, so going back to full proves the remembered size and not the fallback), Gemini detached to
/// their right (untouched by this scenario). No setup gesture or undock is
/// needed - the pair is already docked at seed time.
///
/// Invokes Claude's "Mini mode" button through UI Automation (SendInput cannot reach a hidden desktop);
/// expects card_mode_switched reason=toggle covering both cards, both windows at the mini default size,
/// still LayoutPolicy.DefaultInterCardGap apart, and both cards' saved return size (FullWidth/FullHeight
/// in settings.json) equal to their pre-toggle size (1000x720), with the Claude-Codex edge still saved and no
/// split. Then invokes "Full mode" and expects both windows back at their exact pre-toggle rects.
/// 2.0 design §3.7 ("every card remembers its size in each mode"): after that second toggle, the saved file
/// must also hold both cards' remembered MINI size (MiniWidth/MiniHeight) equal to the mini default size they
/// were just at (530x270) - since this is each card's first time going mini, SwitchGroupMode's To Full step
/// stamps the group's just-left (mini default) size as the new remembered mini size, per §3.7.
/// </summary>
internal static class CardModeToggleScenario
{
    // 2.0 mini mode design §4.5: measured by CardPresentationProof's mini size matrix, and defined as
    // CardLayoutTiers.MiniDefaultWidth/MiniDefaultHeight (src/Glideslope.App/CardLayoutTiers.cs: 470+60
    // and 270 at 100% mini scale). Duplicated here rather than referenced, because this harness only
    // references Glideslope.Core (see the .csproj remarks) - the same reason ProofSettingsSeeder
    // duplicates CardLayoutTiers.MinWidth/MinHeight for the full-card minimum.
    private const int MiniDefaultWidth = 530;
    private const int MiniDefaultHeight = 270;

    public static ScenarioResult Run(ScenarioContext context)
    {
        const string name = "card_mode_toggle_group";
        var log = context.Diagnostics;
        var evidence = new List<string>();

        if (!context.CardWindows.TryGetValue("claude", out var claudeHwnd) ||
            !context.CardWindows.TryGetValue("codex", out var codexHwnd))
            return new ScenarioResult(name, false, "one or more card windows were never found", evidence);
        if (!CardModeScenarioSupport.WaitForSeededLayout(context, evidence))
            return new ScenarioResult(name, false, "the app did not restore the seeded card sizes", evidence);

        if (!NativeMethods.GetWindowRect(claudeHwnd, out var claudeStart) ||
            !NativeMethods.GetWindowRect(codexHwnd, out var codexStart))
            return new ScenarioResult(name, false, "GetWindowRect failed for Claude or Codex at the start", evidence);
        evidence.Add($"start claude=[{claudeStart.Left},{claudeStart.Top},{claudeStart.Right},{claudeStart.Bottom}] " +
            $"codex=[{codexStart.Left},{codexStart.Top},{codexStart.Right},{codexStart.Bottom}]");

        // Step 1: invoke Claude's "Mini mode" toggle; the group (Claude+Codex) should switch to mini.
        var sinceToggleToMini = context.Log.CurrentLength();
        if (!AutomationInterop.InvokeButtonByName(claudeHwnd, "Mini mode", log))
            return new ScenarioResult(name, false, "could not invoke Claude's \"Mini mode\" button via UI Automation", evidence);

        var toMini = context.Log.WaitForLine(sinceToggleToMini,
            line => line.Code == "card_mode_switched" &&
                (line.Status?.Contains("reason=toggle", StringComparison.Ordinal) ?? false) &&
                (line.Status?.Contains("to=Mini", StringComparison.Ordinal) ?? false));
        evidence.AddRange(context.Log.TailSince(sinceToggleToMini).Select(line => line.Raw));
        if (toMini is null)
            return new ScenarioResult(name, false,
                "app_silent_60s waiting for card_mode_switched reason=toggle to=Mini after invoking Claude's Mini mode button",
                evidence);
        if (!toMini.Status!.Contains("cards=claude", StringComparison.Ordinal) || !toMini.Status.Contains("codex", StringComparison.Ordinal))
            return new ScenarioResult(name, false, "card_mode_switched to=Mini did not cover both Claude and Codex", evidence);
        if (toMini.Status.Contains("split=True", StringComparison.Ordinal))
            return new ScenarioResult(name, false, "the toggle to mini split the group to fit the work area", evidence);

        // ApplyCardGeometry can lag one dispatcher tick behind the log line above, so both members' real
        // rects are read once after layout_saved (CardModeScenarioSupport.RectAfterLayoutSaved's remarks),
        // not read right off the card_mode_switched line itself.
        var claudeMini = CardModeScenarioSupport.RectAfterLayoutSaved(context, sinceToggleToMini, claudeHwnd);
        var codexMini = CardModeScenarioSupport.RectAfterLayoutSaved(context, sinceToggleToMini, codexHwnd);
        var claudeMiniSize = CardModeScenarioSupport.Size(claudeMini);
        var codexMiniSize = CardModeScenarioSupport.Size(codexMini);
        var gap = (int)LayoutPolicy.DefaultInterCardGap;
        evidence.Add($"after_toggle_to_mini claude_size={claudeMiniSize} codex_size={codexMiniSize} " +
            $"claude_rect=[{claudeMini.Left},{claudeMini.Top},{claudeMini.Right},{claudeMini.Bottom}] " +
            $"codex_rect=[{codexMini.Left},{codexMini.Top},{codexMini.Right},{codexMini.Bottom}]");

        var claudeStartSize = CardModeScenarioSupport.Size(claudeStart);
        var codexStartSize = CardModeScenarioSupport.Size(codexStart);
        // Wait for the durable save before checking return sizes and the preserved Claude-Codex edge.
        bool ReturnSizesMatchStart(AppSettings settings)
        {
            var claudeCard = settings.Layout.Cards.FirstOrDefault(card => card.ProviderId == "claude");
            var codexCard = settings.Layout.Cards.FirstOrDefault(card => card.ProviderId == "codex");
            var edgeKept = settings.Layout.Edges.Any(edge =>
                (edge.FirstProviderId == "claude" && edge.SecondProviderId == "codex") ||
                (edge.FirstProviderId == "codex" && edge.SecondProviderId == "claude"));
            return edgeKept && claudeCard is not null && codexCard is not null &&
                Math.Abs(claudeCard.FullWidth - claudeStartSize.Width) < 0.5 && Math.Abs(claudeCard.FullHeight - claudeStartSize.Height) < 0.5 &&
                Math.Abs(codexCard.FullWidth - codexStartSize.Width) < 0.5 && Math.Abs(codexCard.FullHeight - codexStartSize.Height) < 0.5;
        }
        var afterToMini = CardModeScenarioSupport.WaitForSavedSettings(context, sinceToggleToMini, log);
        var claudeCardMini = afterToMini?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "claude");
        var codexCardMini = afterToMini?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "codex");
        evidence.Add($"settings_after_toggle_to_mini claude_live=[{claudeCardMini?.Width},{claudeCardMini?.Height}] " +
            $"codex_live=[{codexCardMini?.Width},{codexCardMini?.Height}] " +
            $"claude_return=[{claudeCardMini?.FullWidth},{claudeCardMini?.FullHeight}] " +
            $"codex_return=[{codexCardMini?.FullWidth},{codexCardMini?.FullHeight}] " +
            $"expected_claude={claudeStartSize} expected_codex={codexStartSize}");

        if (claudeMiniSize != (MiniDefaultWidth, MiniDefaultHeight) || codexMiniSize != (MiniDefaultWidth, MiniDefaultHeight))
            return new ScenarioResult(name, false,
                $"cards did not land at the mini default size {MiniDefaultWidth}x{MiniDefaultHeight}", evidence);
        if (codexMini.Left != claudeMini.Right + gap)
            return new ScenarioResult(name, false,
                $"Claude and Codex are no longer {gap}px apart after toggling to mini (codex_left={codexMini.Left}, expected={claudeMini.Right + gap})",
                evidence);

        if (afterToMini is null || !ReturnSizesMatchStart(afterToMini))
            return new ScenarioResult(name, false, "the saved return size (FullWidth/FullHeight) does not match the pre-toggle size for both cards", evidence);

        // Step 2: invoke Claude's toggle again; it now reads "Full mode" (ApplyMode swapped the tooltip/name).
        var sinceToggleToFull = context.Log.CurrentLength();
        if (!AutomationInterop.InvokeButtonByName(claudeHwnd, "Full mode", log))
            return new ScenarioResult(name, false, "could not invoke Claude's \"Full mode\" button via UI Automation", evidence);

        var toFull = context.Log.WaitForLine(sinceToggleToFull,
            line => line.Code == "card_mode_switched" &&
                (line.Status?.Contains("reason=toggle", StringComparison.Ordinal) ?? false) &&
                (line.Status?.Contains("to=Full", StringComparison.Ordinal) ?? false));
        evidence.AddRange(context.Log.TailSince(sinceToggleToFull).Select(line => line.Raw));
        if (toFull is null)
            return new ScenarioResult(name, false,
                "app_silent_60s waiting for card_mode_switched reason=toggle to=Full after invoking Claude's Full mode button",
                evidence);
        if (toFull.Status?.Contains("split=True", StringComparison.Ordinal) ?? false)
            return new ScenarioResult(name, false, "the toggle back to full split the group to fit the work area", evidence);

        // Read once after layout_saved, not off the card_mode_switched line itself - see
        // CardModeScenarioSupport.RectAfterLayoutSaved's remarks (the same lag applies symmetrically to
        // this second toggle).
        var claudeFinal = CardModeScenarioSupport.RectAfterLayoutSaved(context, sinceToggleToFull, claudeHwnd);
        var codexFinal = CardModeScenarioSupport.RectAfterLayoutSaved(context, sinceToggleToFull, codexHwnd);
        evidence.Add($"after_toggle_to_full claude_rect=[{claudeFinal.Left},{claudeFinal.Top},{claudeFinal.Right},{claudeFinal.Bottom}] " +
            $"codex_rect=[{codexFinal.Left},{codexFinal.Top},{codexFinal.Right},{codexFinal.Bottom}]");
        var claudeExact = claudeFinal.Left == claudeStart.Left && claudeFinal.Top == claudeStart.Top &&
            claudeFinal.Right == claudeStart.Right && claudeFinal.Bottom == claudeStart.Bottom;
        var codexExact = codexFinal.Left == codexStart.Left && codexFinal.Top == codexStart.Top &&
            codexFinal.Right == codexStart.Right && codexFinal.Bottom == codexStart.Bottom;
        if (!claudeExact || !codexExact)
            return new ScenarioResult(name, false, "toggling back to Full did not restore the exact pre-toggle rects", evidence);

        // 2.0 design §3.7: the saved file must hold both cards' remembered mini size (MiniWidth/MiniHeight)
        // equal to the mini default they were just switched away from (530x270), since this was each card's
        // first time going mini (SwitchGroupMode's To Full step stamps the group's pre-switch, i.e. mini,
        // size as the new remembered mini size for every member).
        bool MiniSizesMatchMiniDefault(AppSettings settings)
        {
            var claudeCard = settings.Layout.Cards.FirstOrDefault(card => card.ProviderId == "claude");
            var codexCard = settings.Layout.Cards.FirstOrDefault(card => card.ProviderId == "codex");
            return claudeCard is not null && codexCard is not null &&
                Math.Abs(claudeCard.MiniWidth - MiniDefaultWidth) < 0.5 && Math.Abs(claudeCard.MiniHeight - MiniDefaultHeight) < 0.5 &&
                Math.Abs(codexCard.MiniWidth - MiniDefaultWidth) < 0.5 && Math.Abs(codexCard.MiniHeight - MiniDefaultHeight) < 0.5;
        }
        var afterToFull = CardModeScenarioSupport.WaitForSavedSettings(context, sinceToggleToFull, log);
        var claudeCardFull = afterToFull?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "claude");
        var codexCardFull = afterToFull?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "codex");
        evidence.Add($"settings_after_toggle_to_full claude_mini=[{claudeCardFull?.MiniWidth},{claudeCardFull?.MiniHeight}] " +
            $"codex_mini=[{codexCardFull?.MiniWidth},{codexCardFull?.MiniHeight}] expected={MiniDefaultWidth}x{MiniDefaultHeight}");
        if (afterToFull is null || !MiniSizesMatchMiniDefault(afterToFull))
            return new ScenarioResult(name, false,
                "the saved remembered mini size (MiniWidth/MiniHeight) does not equal the mini default for both cards after toggling back to full",
                evidence);

        return new ScenarioResult(name, true,
            "toggling Claude's group to mini and back committed both cards at the mini default size, back to their exact start rects, " +
            "and both cards' remembered mini size in the saved file",
            evidence);
    }
}

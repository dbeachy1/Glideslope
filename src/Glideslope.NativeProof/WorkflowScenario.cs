namespace Glideslope.NativeProof;

/// <summary>Exercises the card workflow: dock Codex to Claude, dock Gemini to Codex, then resize Claude
/// and verify both followers. Each gesture is checked against its commit event and persisted layout graph.</summary>
internal static class WorkflowScenario
{
    private const int SnapGap = 10;

    public static ScenarioResult Run(ScenarioContext context)
    {
        var log = context.Diagnostics;
        var evidence = new List<string>();

        if (!context.CardWindows.TryGetValue("claude", out var claudeHwnd) ||
            !context.CardWindows.TryGetValue("codex", out var codexHwnd) ||
            !context.CardWindows.TryGetValue("gemini", out var geminiHwnd))
            return new ScenarioResult("workflow", false, "one or more card windows were never found", evidence);

        if (!NativeMethods.GetWindowRect(claudeHwnd, out var claudeRect) ||
            !NativeMethods.GetWindowRect(codexHwnd, out var codexRect) ||
            !NativeMethods.GetWindowRect(geminiHwnd, out var geminiRect))
            return new ScenarioResult("workflow", false, "GetWindowRect failed before driving the workflow", evidence);

        // Step 1: drag Codex (B) to within SnapGap of Claude's (A) right edge.
        var codexTarget = new NativeMethods.RECT
        {
            Left = claudeRect.Right + SnapGap,
            Top = claudeRect.Top,
            Right = claudeRect.Right + SnapGap + (codexRect.Right - codexRect.Left),
            Bottom = claudeRect.Top + (codexRect.Bottom - codexRect.Top),
        };
        var sinceStep1 = context.Log.CurrentLength();
        GestureDriver.Drive(codexHwnd, GestureDriver.Interpolate(codexRect, codexTarget, stepCount: 6), log, "workflow_dock_b_onto_a");
        var step1 = context.Log.WaitForLine(sinceStep1,
            line => line.Code == "layout_gesture_committed" && line.ProviderId == "codex");
        evidence.AddRange(context.Log.TailSince(sinceStep1).Select(line => line.Raw));
        if (step1 is null)
            return new ScenarioResult("workflow", false,
                "app_silent_60s waiting for step 1 (dock Codex onto Claude) to reach layout_gesture_committed",
                evidence);

        // The commit event precedes the asynchronous disk write; wait for layout_saved before reading settings.
        bool Step1Committed(Glideslope.Core.AppSettings settings)
        {
            var codex = settings.Layout.Cards.FirstOrDefault(c => c.ProviderId == "codex");
            var claude = settings.Layout.Cards.FirstOrDefault(c => c.ProviderId == "claude");
            var hasEdge = settings.Layout.Edges.Any(edge =>
                (edge.FirstProviderId == "claude" && edge.SecondProviderId == "codex") ||
                (edge.FirstProviderId == "codex" && edge.SecondProviderId == "claude"));
            return hasEdge && codex is not null && claude is not null &&
                Math.Abs(codex.Width - claude.Width) < 0.5 && Math.Abs(codex.Height - claude.Height) < 0.5;
        }
        var afterStep1 = CardModeScenarioSupport.WaitForSavedSettings(context, sinceStep1, log);
        var codexCard = afterStep1?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "codex");
        var claudeCard = afterStep1?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "claude");
        var hasClaudeCodexEdge = afterStep1?.Layout.Edges.Any(edge =>
            (edge.FirstProviderId == "claude" && edge.SecondProviderId == "codex") ||
            (edge.FirstProviderId == "codex" && edge.SecondProviderId == "claude")) ?? false;
        evidence.Add($"after_step1 edge_claude_codex={hasClaudeCodexEdge} codex_size=[{codexCard?.Width},{codexCard?.Height}] claude_size=[{claudeCard?.Width},{claudeCard?.Height}]");
        if (afterStep1 is null || !Step1Committed(afterStep1))
            return new ScenarioResult("workflow", false,
                "step 1 committed but the settings graph does not show Codex docked to Claude's size/edge",
                evidence);

        // Step 2: Codex moved in step 1, so read its new rectangle before positioning Gemini.
        if (!NativeMethods.GetWindowRect(codexHwnd, out var codexAfterStep1))
            return new ScenarioResult("workflow", false, "GetWindowRect failed for Codex before step 2", evidence);
        evidence.Add($"before_step2 codex_rect=[{codexAfterStep1.Left},{codexAfterStep1.Top},{codexAfterStep1.Right},{codexAfterStep1.Bottom}] " +
            $"gemini_rect=[{geminiRect.Left},{geminiRect.Top},{geminiRect.Right},{geminiRect.Bottom}]");
        var geminiTarget = new NativeMethods.RECT
        {
            Left = codexAfterStep1.Right + SnapGap,
            Top = codexAfterStep1.Top,
            Right = codexAfterStep1.Right + SnapGap + (geminiRect.Right - geminiRect.Left),
            Bottom = codexAfterStep1.Top + (geminiRect.Bottom - geminiRect.Top),
        };
        var sinceStep2 = context.Log.CurrentLength();
        GestureDriver.Drive(geminiHwnd, GestureDriver.Interpolate(geminiRect, geminiTarget, stepCount: 6), log, "workflow_dock_c_onto_b");
        var step2 = context.Log.WaitForLine(sinceStep2,
            line => line.Code == "layout_gesture_committed" && line.ProviderId == "gemini");
        evidence.AddRange(context.Log.TailSince(sinceStep2).Select(line => line.Raw));
        if (step2 is null)
            return new ScenarioResult("workflow", false,
                "app_silent_60s waiting for step 2 (dock Gemini onto Codex's right side) to reach layout_gesture_committed",
                evidence);

        // Wait for the durable save before checking the committed graph.
        bool Step2Committed(Glideslope.Core.AppSettings settings)
        {
            var gemini = settings.Layout.Cards.FirstOrDefault(c => c.ProviderId == "gemini");
            var codex = settings.Layout.Cards.FirstOrDefault(c => c.ProviderId == "codex");
            var hasEdge = settings.Layout.Edges.Any(edge =>
                (edge.FirstProviderId == "codex" && edge.SecondProviderId == "gemini") ||
                (edge.FirstProviderId == "gemini" && edge.SecondProviderId == "codex"));
            return hasEdge && gemini is not null && codex is not null && claudeCard is not null &&
                Math.Abs(gemini.Width - codex.Width) < 0.5 && Math.Abs(gemini.Height - codex.Height) < 0.5 &&
                Math.Abs(gemini.Width - claudeCard.Width) < 0.5 && Math.Abs(gemini.Height - claudeCard.Height) < 0.5;
        }
        var afterStep2 = CardModeScenarioSupport.WaitForSavedSettings(context, sinceStep2, log);
        var geminiCard = afterStep2?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "gemini");
        var codexCardAfterStep2 = afterStep2?.Layout.Cards.FirstOrDefault(card => card.ProviderId == "codex");
        var hasCodexGeminiEdge = afterStep2?.Layout.Edges.Any(edge =>
            (edge.FirstProviderId == "codex" && edge.SecondProviderId == "gemini") ||
            (edge.FirstProviderId == "gemini" && edge.SecondProviderId == "codex")) ?? false;
        // Gemini must take the size of the card it snapped to (Codex), and that size must still be
        // Claude's from step 1: the target never changes, so all three now match (window design §3.1).
        evidence.Add($"after_step2 edge_codex_gemini={hasCodexGeminiEdge} gemini_size=[{geminiCard?.Width},{geminiCard?.Height}] " +
            $"codex_size=[{codexCardAfterStep2?.Width},{codexCardAfterStep2?.Height}] claude_size=[{claudeCard?.Width},{claudeCard?.Height}]");
        if (afterStep2 is null || !Step2Committed(afterStep2))
            return new ScenarioResult("workflow", false,
                "step 2 committed but the settings graph does not show Gemini docked to Codex's size/edge", evidence);

        // Step 3: resize Claude; Codex and Gemini should follow.
        if (!NativeMethods.GetWindowRect(claudeHwnd, out var claudeCurrentRect))
            return new ScenarioResult("workflow", false, "GetWindowRect failed before the resize step", evidence);
        var resizedClaude = new NativeMethods.RECT
        {
            Left = claudeCurrentRect.Left,
            Top = claudeCurrentRect.Top,
            Right = claudeCurrentRect.Left + 800,
            Bottom = claudeCurrentRect.Top + 720,
        };
        var sinceStep3 = context.Log.CurrentLength();
        GestureDriver.Drive(claudeHwnd, GestureDriver.Interpolate(claudeCurrentRect, resizedClaude, stepCount: 6), log, "workflow_resize_a");
        var step3 = context.Log.WaitForLine(sinceStep3,
            line => line.Code == "layout_gesture_committed" && line.ProviderId == "claude");
        evidence.AddRange(context.Log.TailSince(sinceStep3).Select(line => line.Raw));
        if (step3 is null)
            return new ScenarioResult("workflow", false,
                "app_silent_60s waiting for step 3 (resize Claude) to reach layout_gesture_committed",
                evidence);

        // layout_gesture_committed can precede native geometry updates for group followers; wait for layout_saved.
        var codexAfterResize = CardModeScenarioSupport.RectAfterLayoutSaved(context, sinceStep3, codexHwnd);
        var geminiAfterResize = CardModeScenarioSupport.RectAfterLayoutSaved(context, sinceStep3, geminiHwnd);
        var codexFollowed = Size(codexAfterResize) == Size(resizedClaude);
        var geminiFollowed = Size(geminiAfterResize) == Size(resizedClaude);
        evidence.Add($"after_step3 codex_size={Size(codexAfterResize)} gemini_size={Size(geminiAfterResize)} claude_target_size={Size(resizedClaude)}");
        if (!codexFollowed || !geminiFollowed)
            return new ScenarioResult("workflow", false, "resizing Claude did not resize Codex and Gemini to match", evidence);

        return new ScenarioResult("workflow", true, "dock, dock, and group resize all committed as Doug's workflow expects", evidence);
    }

    private static (int Width, int Height) Size(NativeMethods.RECT rect) => (rect.Right - rect.Left, rect.Bottom - rect.Top);
}

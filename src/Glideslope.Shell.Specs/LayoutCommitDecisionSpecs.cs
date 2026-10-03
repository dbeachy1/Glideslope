using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Level 1 specs for design doc S5.4: LayoutCommitDecision.Decide against every combination of
/// kind, Snap, candidate and fit.
/// </summary>
internal static class LayoutCommitDecisionSpecs
{
    public static void Run()
    {
        foreach (var kind in new[] { LayoutGestureKind.Move, LayoutGestureKind.Resize, LayoutGestureKind.Detach, LayoutGestureKind.Pending })
        foreach (var snapOn in new[] { false, true })
        foreach (var hasCandidate in new[] { false, true })
        foreach (var resizeFits in new[] { false, true })
        {
            var actual = LayoutCommitDecision.Decide(kind, snapOn, hasCandidate, resizeFits);
            var expected = Expected(kind, snapOn, hasCandidate, resizeFits);
            Assert(actual == expected,
                $"kind={kind},snapOn={snapOn},hasCandidate={hasCandidate},resizeFits={resizeFits}: expected {expected}, got {actual}");
        }

        // the coordinator's other commit decisions, proven as pure functions.
        InterruptCommitsAGestureWhoseButtonIsUp();
        UntrackedChangesBeginAGestureOnlyForARealMove();
        SettingsRequestsAreRebasedOverLayoutSavesOnly();
        SettingsSaveOutcomeTreatsRegistrationAsAWarning();
        SettingsCardScaleWinsOnlyWhenItsSliderMoved();
        SettingsFingerprintIgnoresOnlyTheRevision();
    }

    /// <summary>Esc, a closed lead card and app exit always move the gesture back; a superseding
    /// gesture always commits the old one; every other interrupt commits when the button is up and cancels when it
    /// is held.</summary>
    private static void InterruptCommitsAGestureWhoseButtonIsUp()
    {
        foreach (var held in new[] { false, true })
        {
            foreach (var reason in new[] { "user_cancelled", "card_closed", "app_exit" })
                Assert(WindowCoordinator.DecideGestureInterrupt(reason, held) == WindowCoordinator.GestureInterruptAction.Cancel,
                    $"{reason} always cancels (held={held})");
            Assert(WindowCoordinator.DecideGestureInterrupt("superseded", held) == WindowCoordinator.GestureInterruptAction.Commit,
                $"a superseded gesture commits: the new press means its button was released (held={held})");
            foreach (var reason in new[] { "settings_opened", "configuration_changed", "provider_selection_changed",
                         "window_set_changed", "undock_requested", "layout_reset", "size_reset", "scale_changed" })
                Assert(WindowCoordinator.DecideGestureInterrupt(reason, held) ==
                       (held ? WindowCoordinator.GestureInterruptAction.Cancel : WindowCoordinator.GestureInterruptAction.Commit),
                    $"{reason} commits with the button up and cancels with it held (held={held})");
        }
    }

    /// <summary>A card change without an active gesture begins Pending only for a single-card move or resize;
    /// apply echoes, display changes, and multi-card relocations are ignored.</summary>
    private static void UntrackedChangesBeginAGestureOnlyForARealMove()
    {
        var monitor = LayoutSpecGeometry.Monitor("primary", 0, 0, 1920, 1080, 1040, isPrimary: true);
        var codexAt = new LogicalRect(100, 100, 940, 680);
        var claudeAt = new LogicalRect(900, 300, 940, 680);
        var before = LayoutSpecGeometry.Snapshot(LayoutSpecGeometry.Rects(("codex", codexAt), ("claude", claudeAt)), monitor);
        var moved = LayoutSpecGeometry.Snapshot(LayoutSpecGeometry.Rects(("codex", codexAt.Translate(40, 0)), ("claude", claudeAt)), monitor);
        UntrackedChange(before, moved, WindowCoordinator.UntrackedChangeDecision.BeginPending,
            "one card moved by something other than our press starts a Pending gesture (a Linux drag resumed after a quiet-period commit, a window-manager move, Win+Arrow)");
        UntrackedChange(before, moved, WindowCoordinator.UntrackedChangeDecision.IgnoreApplyEcho,
            "the late echo of our own apply never starts a gesture", echo: true);
        UntrackedChange(before, moved, WindowCoordinator.UntrackedChangeDecision.IgnoreNotNormal,
            "a minimized, maximized or hidden card never starts a gesture", normal: false);
        UntrackedChange(null, moved, WindowCoordinator.UntrackedChangeDecision.IgnoreNoBaseline, "with no earlier geometry there is nothing to move from");
        var shorterWorkArea = LayoutSpecGeometry.Snapshot(LayoutSpecGeometry.Rects(("codex", codexAt.Translate(40, 0)), ("claude", claudeAt)),
            LayoutSpecGeometry.Monitor("primary", 0, 0, 1920, 1080, 1000, isPrimary: true));
        UntrackedChange(before, shorterWorkArea, WindowCoordinator.UntrackedChangeDecision.IgnoreDisplayChanged,
            "a changed work area or DPI is the display handler's, not a gesture");
        var bothMoved = LayoutSpecGeometry.Snapshot(LayoutSpecGeometry.Rects(("codex", codexAt.Translate(40, 0)), ("claude", claudeAt.Translate(40, 0))), monitor);
        UntrackedChange(before, bothMoved, WindowCoordinator.UntrackedChangeDecision.IgnoreOthersMoved,
            "several cards moving at once (the OS relocating windows) is not a gesture");
        var fewerCards = LayoutSpecGeometry.Snapshot(LayoutSpecGeometry.Rects(("codex", codexAt.Translate(40, 0))), monitor);
        UntrackedChange(before, fewerCards, WindowCoordinator.UntrackedChangeDecision.IgnoreWindowSetChanged,
            "a change of the card set is not a gesture");
        UntrackedChange(before, before, WindowCoordinator.UntrackedChangeDecision.IgnoreUnchanged, "a change that moves nothing is not a gesture");
        var minimized = LayoutSpecGeometry.Snapshot(LayoutSpecGeometry.Rects(("codex", new LogicalRect(-32000, -32000, 940, 680)), ("claude", claudeAt)), monitor);
        UntrackedChange(before, minimized, WindowCoordinator.UntrackedChangeDecision.IgnoreOffScreen,
            "moving to Windows' minimized position is not a gesture");
        UntrackedChange(minimized, before, WindowCoordinator.UntrackedChangeDecision.IgnoreOffScreen,
            "coming back from the minimized position is not a gesture");
    }

    private static void UntrackedChange(CardLayoutGeometrySnapshot? baseline, CardLayoutGeometrySnapshot current,
        WindowCoordinator.UntrackedChangeDecision expected, string message, bool normal = true, bool echo = false)
    {
        var actual = WindowCoordinator.DecideUntrackedChange("codex", normal, echo, baseline, current);
        Assert(actual == expected, $"{message} (expected {expected}, got {actual})");
    }

    /// <summary>Rebases Settings requests made stale by layout or card-scale saves; Settings-field changes and
    /// unknown revisions remain conflicts.</summary>
    private static void SettingsRequestsAreRebasedOverLayoutSavesOnly()
    {
        Assert(WindowCoordinator.DecideSettingsRequestRevision(7, 7, 5) == WindowCoordinator.SettingsRequestRevision.Current,
            "a request at the current revision applies as it is");
        Assert(WindowCoordinator.DecideSettingsRequestRevision(7, 9, 7) == WindowCoordinator.SettingsRequestRevision.Rebased,
            "layout saves 8 and 9 after the window's revision 7 rebase the request instead of failing Save");
        Assert(WindowCoordinator.DecideSettingsRequestRevision(7, 9, 8) == WindowCoordinator.SettingsRequestRevision.Conflict,
            "a Settings-field commit (8) after the window's revision stays a conflict");
        Assert(WindowCoordinator.DecideSettingsRequestRevision(10, 9, 5) == WindowCoordinator.SettingsRequestRevision.Conflict,
            "a revision the coordinator never committed is a conflict");
    }

    /// <summary>Registration failure is a warning when a save succeeds; a history-retention failure fails the save.</summary>
    private static void SettingsSaveOutcomeTreatsRegistrationAsAWarning()
    {
        var warned = WindowCoordinator.SettingsSaveOutcome(null, "startup_registration_io_error", 12);
        Assert(warned is { Succeeded: true, IssueCode: null, WarningCode: "startup_registration_io_error", CommittedRevision: 12 },
            $"saved and applied with a registration failure is a success with a warning (was {warned})");
        var failed = WindowCoordinator.SettingsSaveOutcome("history_unavailable", "startup_registration_io_error", 12);
        Assert(failed is { Succeeded: false, IssueCode: "history_unavailable", CommittedRevision: 12 },
            $"a retention failure is still a failure, so the next Save retries the prune (was {failed})");
        var clean = WindowCoordinator.SettingsSaveOutcome(null, null, 12);
        Assert(clean is { Succeeded: true, IssueCode: null, WarningCode: null, CommittedRevision: 12 }, $"a clean save (was {clean})");
    }

    /// <summary>Card size 1.1: Settings sets the scale only when its slider was moved; otherwise the live value, which
    /// the card's size button may have changed after Settings opened, stays.</summary>
    private static void SettingsCardScaleWinsOnlyWhenItsSliderMoved()
    {
        Assert(WindowCoordinator.DecideSettingsCardScale(requestedPercent: 130, windowBaselinePercent: 100, livePercent: 120) == 130,
            "a slider moved in Settings wins on Save");
        Assert(WindowCoordinator.DecideSettingsCardScale(requestedPercent: 100, windowBaselinePercent: 100, livePercent: 120) == 120,
            "an untouched Settings slider keeps the newer card-button value");
    }

    /// <summary>Skips a coordinator save when the revision is the only field that differs.</summary>
    private static void SettingsFingerprintIgnoresOnlyTheRevision()
    {
        var settings = AppSettings.CreateDefault();
        var revised = settings.Clone();
        revised.Revision = 9;
        Assert(WindowCoordinator.SettingsFingerprint(settings) == WindowCoordinator.SettingsFingerprint(revised),
            "the revision alone does not count as a change");
        var scaled = settings.Clone();
        scaled.FullCardScalePercent = 120;
        Assert(WindowCoordinator.SettingsFingerprint(settings) != WindowCoordinator.SettingsFingerprint(scaled),
            "a card-scale change is a change");
        var moved = settings.Clone();
        moved.Layout.Cards.Add(new CardLayoutSettings { ProviderId = ProviderCatalog.Codex, AnchorX = 0.25 });
        Assert(WindowCoordinator.SettingsFingerprint(settings) != WindowCoordinator.SettingsFingerprint(moved),
            "a layout change is a change");
    }

    /// <summary>
    /// The design doc S5.4 rule, restated independently of LayoutCommitDecision's own switch
    /// statement, so this spec is a real cross-check rather than a tautology.
    /// </summary>
    private static LayoutCommitAction Expected(LayoutGestureKind kind, bool snapOn, bool hasCandidate, bool resizeFits)
    {
        // A gesture that never resolved out of Pending (design doc S5.2) had no geometry
        // change to decide anything from: always a no-op placement.
        if (kind == LayoutGestureKind.Pending) return LayoutCommitAction.Place;

        // A resize cares only about whether the final size fits; Snap and the magnet candidate are
        // Move/Detach-only concepts (design doc S5.4 item 4).
        if (kind == LayoutGestureKind.Resize)
            return resizeFits ? LayoutCommitAction.ResizeApply : LayoutCommitAction.ResizeRevert;

        // Move or Detach: a winning candidate beats a screen-edge snap (design doc S5.4 items 2-3),
        // and both require Snap to be on (design doc S2.2).
        if (!snapOn) return LayoutCommitAction.Place;
        return hasCandidate ? LayoutCommitAction.Snap : LayoutCommitAction.ScreenSnap;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }
}

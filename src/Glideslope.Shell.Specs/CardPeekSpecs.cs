using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;
// Design doc §10.2/§11: the same fake clock LayoutGestureTrackerSpecs.cs already defines (nested there, so
// referenced here by an alias rather than a second copy) - synchronous, its timers fire on Advance, on the
// calling thread.
using ManualTimeProvider = Glideslope.Shell.Specs.LayoutGestureTrackerSpecs.ManualTimeProvider;

namespace Glideslope.Shell.Specs;

/// <summary>Exercises Ctrl-peek decisions and placement, headless card controls, and CardPeekController with
/// fake window, probe, and geometry backends. Time advances through a ManualTimeProvider.</summary>
internal static class CardPeekSpecs
{
    public static void Run()
    {
        PureDecisionStartConditions();
        PureDecisionEndReasonsInPriorityOrder();
        PureDecisionOnlyOnePeekAtATime();
        PlacementKeepsTopLeftWhenItFits();
        PlacementShiftsIntoTheWorkAreaOnEachEdge();
        PlacementPinsWhenLargerThanTheWorkArea();
        SizeUsesRememberedFullSizeOrDefault();
        HeadlessCardSwallowsAPressAndHidesUndockWhilePeeking();
        ShiftModifiersTrackPressAndRelease();
        TunnelHandlerSwallowsPressesOnGripsAndButtonsWhilePeeking();
        ControllerStartThenEndRestoresExactly();
        ControllerEndsForLayoutBusy();
        ControllerProbeDrivesStartAndEnd();
        HoveredCardTurningFullNeverStartsAPeek();
        PeekStartsInstantlyAndASecondCtrlPressReachesTheCard();
        PeekEndsOnceWhenThePointerLeavesWithNoProbe();
        EndDisarmsUntilCtrlIsSeenReleasedThenStartsAgain();
        PeekSurvivesHoverChangesWhileCtrlIsHeld();
        LayoutBusyFuncBlocksAStartAndEndsAPeek();
        CardRemovedStopsPolling();
    }

    private static void PureDecisionStartConditions()
    {
        var canStart = new PeekInputs(SettingOn: true, HoveredCard: "claude", HoveredCardMode: CardMode.Mini,
            CtrlHeld: true, AnyButtonHeld: false, LayoutBusy: false, PeekingCard: null, Armed: true, HoldElapsed: true);
        Assert(CardPeekDecision.Decide(canStart) == PeekAction.Start("claude"),
            "setting on, a mini card hovered, Ctrl held, no button, layout free, armed, hold elapsed: starts on that card");

        Assert(CardPeekDecision.Decide(canStart with { SettingOn = false }) == PeekAction.None, "the setting off never starts a peek");
        Assert(CardPeekDecision.Decide(canStart with { HoveredCard = null, HoveredCardMode = null }) == PeekAction.None, "no hovered card never starts a peek");
        Assert(CardPeekDecision.Decide(canStart with { HoveredCardMode = CardMode.Full }) == PeekAction.None, "a full card never starts a peek");
        Assert(CardPeekDecision.Decide(canStart with { CtrlHeld = false }) == PeekAction.None, "Ctrl not held never starts a peek");
        Assert(CardPeekDecision.Decide(canStart with { AnyButtonHeld = true }) == PeekAction.None, "a button already held never starts a peek");
        Assert(CardPeekDecision.Decide(canStart with { LayoutBusy = true }) == PeekAction.None, "a busy layout never starts a peek");
        // Design doc §10.2 (the two extra Start conditions.
        Assert(CardPeekDecision.Decide(canStart with { Armed = false }) == PeekAction.None,
            "not armed (a peek just ended for a reason other than setting_off, or a start was skipped) blocks a new start until Ctrl is seen released");
        Assert(CardPeekDecision.Decide(canStart with { HoldElapsed = false }) == PeekAction.None,
            "less than 250 ms of continuous Ctrl blocks the start, so a Ctrl + press inside that window still reaches the card");
    }

    private static void PureDecisionEndReasonsInPriorityOrder()
    {
        var peeking = new PeekInputs(SettingOn: true, HoveredCard: "claude", HoveredCardMode: CardMode.Mini,
            CtrlHeld: true, AnyButtonHeld: false, LayoutBusy: false, PeekingCard: "claude", Armed: true, HoldElapsed: true);
        Assert(CardPeekDecision.Decide(peeking) == PeekAction.None, "nothing ends an untouched peek");

        // End reasons are checked in priority order; each case also enables reasons that follow it.
        // Armed and HoldElapsed affect starting, not ending, so they stay true throughout.
        Assert(CardPeekDecision.Decide(peeking with { SettingOn = false, AnyButtonHeld = true, CtrlHeld = false, LayoutBusy = true })
            == PeekAction.End("setting_off"), "setting_off takes priority over every other end reason");
        Assert(CardPeekDecision.Decide(peeking with { AnyButtonHeld = true, CtrlHeld = false, LayoutBusy = true })
            == PeekAction.End("button_down"), "button_down beats ctrl_released and layout_busy");
        Assert(CardPeekDecision.Decide(peeking with { CtrlHeld = false, LayoutBusy = true })
            == PeekAction.End("ctrl_released"), "ctrl_released beats layout_busy");
        Assert(CardPeekDecision.Decide(peeking with { LayoutBusy = true }) == PeekAction.End("layout_busy"), "layout_busy fires alone");

        // While Ctrl remains held, pointer movement to another card or empty space does not end the peek.
        Assert(CardPeekDecision.Decide(peeking with { HoveredCard = null, HoveredCardMode = null }) == PeekAction.None,
            "the pointer leaving the peeking card does not end the peek while Ctrl is held");
        // Without a platform key-state probe, leaving the card ends the peek because Ctrl release may be missed.
        Assert(CardPeekDecision.Decide(peeking with { HoveredCard = null, EndWhenPointerLeaves = true, LayoutBusy = true })
            == PeekAction.End("pointer_left_no_probe"), "with no probe, the pointer leaving ends the peek, ahead of layout_busy");
        Assert(CardPeekDecision.Decide(peeking with { HoveredCard = null, EndWhenPointerLeaves = true, CtrlHeld = false })
            == PeekAction.End("ctrl_released"), "ctrl_released still comes first");
    }

    private static void PureDecisionOnlyOnePeekAtATime()
    {
        // A second mini card, hovered with Ctrl held and nothing else busy, looks exactly like a fresh Start
        // condition - except a peek is already active. Decide leaves the first peek alone and never starts a
        // The second reposition must not end the active peek as pointer_left.
        var inputs = new PeekInputs(SettingOn: true, HoveredCard: "other", HoveredCardMode: CardMode.Mini,
            CtrlHeld: true, AnyButtonHeld: false, LayoutBusy: false, PeekingCard: "claude", Armed: true, HoldElapsed: true);
        Assert(CardPeekDecision.Decide(inputs) == PeekAction.None, "only one peek at a time: a second card's own start conditions neither end the first nor start a second");
    }

    /// <summary>Design doc §3.2: the peek keeps the mini card's top-left corner and grows right and down,
    /// so a card with room does not move.</summary>
    private static void PlacementKeepsTopLeftWhenItFits()
    {
        var mini = new LogicalRect(600, 100, 530, 270);
        var full = new LogicalSize(940, 680);
        var workArea = new LogicalRect(0, 0, 1920, 1040);
        var placed = CardPeekPlacement.Compute(mini, full, workArea);
        Assert(placed == new LogicalRect(mini.X, mini.Y, full.Width, full.Height),
            $"the mini card's top-left corner is kept when the full size fits (got {placed})");
        Assert(!CardPeekPlacement.WouldShift(mini, full, workArea), "no shift is reported when the corner already fits");
    }

    private static void PlacementShiftsIntoTheWorkAreaOnEachEdge()
    {
        var full = new LogicalSize(940, 680);
        var workArea = new LogicalRect(0, 0, 1920, 1040);

        // Left: a mini card partly off the work area's left edge (design doc A1: capture never rejects where a
        // card sits), so the kept top-left corner is off screen.
        var nearLeft = new LogicalRect(-100, 100, 530, 270);
        var placedLeft = CardPeekPlacement.Compute(nearLeft, full, workArea);
        Assert(placedLeft.X == workArea.X && placedLeft.Y == nearLeft.Y && placedLeft.Width == full.Width && placedLeft.Height == full.Height,
            $"a left overflow is shifted to the work area's left edge, size unchanged (got {placedLeft})");
        Assert(CardPeekPlacement.WouldShift(nearLeft, full, workArea), "the left-edge case reports a shift");

        // Right: a mini card near the right edge, so growing right from its top-left corner would overflow.
        var nearRight = new LogicalRect(1300, 100, 530, 270);
        var placedRight = CardPeekPlacement.Compute(nearRight, full, workArea);
        Assert(placedRight.X == workArea.Right - full.Width && placedRight.Width == full.Width,
            $"a right overflow is shifted to the work area's right edge (got {placedRight})");
        Assert(CardPeekPlacement.WouldShift(nearRight, full, workArea), "the right-edge case reports a shift");

        // Top: a mini card partly above the work area (dragged there, per design doc A1: capture never
        // rejects where a card sits).
        var nearTop = new LogicalRect(600, -50, 530, 270);
        var placedTop = CardPeekPlacement.Compute(nearTop, full, workArea);
        Assert(placedTop.Y == workArea.Y && placedTop.Height == full.Height, $"a top overflow is shifted to the work area's top edge (got {placedTop})");

        // Bottom: a mini card low enough that the full rect would run past the bottom edge.
        var nearBottom = new LogicalRect(600, 900, 530, 270);
        var placedBottom = CardPeekPlacement.Compute(nearBottom, full, workArea);
        Assert(placedBottom.Y == workArea.Bottom - full.Height && placedBottom.Height == full.Height,
            $"a bottom overflow is shifted to the work area's bottom edge (got {placedBottom})");
    }

    private static void PlacementPinsWhenLargerThanTheWorkArea()
    {
        // The full size itself is wider and taller than the work area: least-movement clamping cannot apply
        // (low > high on both axes), so the rect pins to the work area's start, size unchanged.
        var mini = new LogicalRect(600, 100, 530, 270);
        var oversizedFull = new LogicalSize(2000, 1200);
        var workArea = new LogicalRect(0, 0, 1920, 1040);
        var placed = CardPeekPlacement.Compute(mini, oversizedFull, workArea);
        Assert(placed == new LogicalRect(workArea.X, workArea.Y, oversizedFull.Width, oversizedFull.Height),
            $"a full size larger than the work area pins to its top-left start, size unchanged (got {placed})");
    }

    private static void SizeUsesRememberedFullSizeOrDefault()
    {
        var remembered = CardPeekSize.Compute(1200, 800, 100);
        Assert(remembered == new LogicalSize(1200, 800), $"a remembered full size above the minimum is used as-is (got {remembered})");

        var none = CardPeekSize.Compute(0, 0, 100);
        Assert(none == CardScale.DefaultSize(CardMode.Full, 100), $"no remembered size falls back to the full default at the applied scale (got {none})");

        var raised = CardPeekSize.Compute(200, 150, 100);
        var minimum = CardScale.UsableMinimum(CardMode.Full, 100);
        Assert(raised.Width >= minimum.Width && raised.Height >= minimum.Height,
            $"a remembered size below the full usable minimum is raised to it (got {raised}, minimum {minimum})");
    }

    /// <summary>Design doc §7, "Shell, headless card": IsPeeking swallows a press (no MoveDragRequested, no
    /// detach), and the Undock button is hidden while peeking. Needs no Show()/render: HandlePeekAwarePress and
    /// UpdateUndockButton's peeking guard touch neither the visual tree nor Bounds.</summary>
    private static void HeadlessCardSwallowsAPressAndHidesUndockWhilePeeking()
    {
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: false, _ => { });
        try
        {
            var dragOrDetachCount = 0;
            CardDockSide? undockRequested = null;
            var observed = new List<(KeyModifiers Modifiers, bool AnyButton)>();
            window.MoveDragStarted += (_, _) => dragOrDetachCount++;
            window.DetachDragStarted += (_, _) => dragOrDetachCount++;
            window.UndockRequested += (_, side) => undockRequested = side;
            window.PeekInputObserved += (_, modifiers, anyButton) => observed.Add((modifiers, anyButton));
            window.SetDockedSides(new HashSet<CardDockSide> { CardDockSide.Right });

            window.IsPeeking = true;
            var handled = window.HandlePeekAwarePress(KeyModifiers.None);
            Assert(handled, "a press while peeking is reported as swallowed");
            Assert(dragOrDetachCount == 0 && undockRequested is null, "a press while peeking starts no move-drag and no Ctrl-detach");
            Assert(observed is [(KeyModifiers.None, true)], $"the press still raises PeekInputObserved(anyButton: true) first (got {observed.Count} events)");

            window.UpdateUndockButton(new Point(1, 1));
            Assert(!window.UndockControl.IsVisible, "the Undock button is hidden while peeking, wherever the pointer is");

            window.IsPeeking = false;
            var handledAfterEnd = window.HandlePeekAwarePress(KeyModifiers.None);
            Assert(!handledAfterEnd, "a press is no longer swallowed once the peek has ended");
        }
        finally
        {
            window.Close();
        }
    }

    private static void ShiftModifiersTrackPressAndRelease()
    {
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: false, _ => { });
        try
        {
            var observed = new List<KeyModifiers>();
            var platform = new CardLayoutPlatform(new SinglePeekMonitorBackend());
            var controller = new CardPeekController(platform, probe: null, new RecordingSink(), _ => null,
                _ => CardMode.Full, () => false, new ManualTimeProvider(DateTimeOffset.UtcNow));
            window.PeekInputObserved += (_, modifiers, _) =>
            {
                observed.Add(modifiers);
                controller.OnInputObserved(modifiers.HasFlag(KeyModifiers.Control), modifiers.HasFlag(KeyModifiers.Shift), false);
            };
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.LeftShift, Source = window });
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.RightShift, Source = window });
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.LeftShift, Source = window });
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.RightShift, Source = window });
            Assert(observed.Count == 4 && observed.Take(3).All(modifiers => modifiers.HasFlag(KeyModifiers.Shift)) &&
                   !observed[^1].HasFlag(KeyModifiers.Shift),
                "both physical Shift keys keep the modifier active until the last key is released");
            Assert(!controller.ShiftHeld, "focused key events clear projection state on the final Shift release");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Design doc §10.4 (unlike the direct-dispatch test above, this drives real
    /// Avalonia routed events (Avalonia.Headless's MouseDown/MouseUp) through the actual visual tree, so it is
    /// the one proof that the window-level tunnel handler - not just HandlePeekAwarePress in isolation - reaches
    /// a grip, a header button and the drag surface before their own Bubble handlers do. AppBuilder is already
    /// configured (CardPresentationProof.Run(), earlier in Program.cs's list).
    /// </summary>
    private static void TunnelHandlerSwallowsPressesOnGripsAndButtonsWhilePeeking()
    {
        var window = new ProviderUsageCardWindow(ProviderIds.Codex, showMark: false, _ => { });
        try
        {
            window.Show();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("card_peek_tunnel_render_failed");

            var resizeStarted = 0;
            var moveStarted = 0;
            var toggleRequested = 0;
            var observedAnyButton = new List<bool>();
            window.ResizeDragStarted += (_, _) => resizeStarted++;
            window.MoveDragStarted += (_, _) => moveStarted++;
            window.ModeToggleRequested += (_, _) => toggleRequested++;
            window.PeekInputObserved += (_, _, anyButton) => observedAnyButton.Add(anyButton);

            // Nothing here ever sets IsPeeking back to false (no CardPeekController is involved), so one set at
            // the top covers all three presses below.
            window.IsPeeking = true;

            var grip = window.ResizeGripControls.First();
            var gripCenter = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("card_peek_grip_not_connected");
            window.MouseDown(gripCenter, MouseButton.Left);
            window.MouseUp(gripCenter, MouseButton.Left);
            Assert(resizeStarted == 0, "a press on a grip while peeking starts no resize");

            var toggle = window.ModeToggleControl;
            var toggleCenter = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("card_peek_toggle_not_connected");
            window.MouseDown(toggleCenter, MouseButton.Left);
            window.MouseUp(toggleCenter, MouseButton.Left);
            Assert(toggleRequested == 0, "a press on a header button (the mode toggle) while peeking requests no mode change");

            // Design doc: "ordinary summary content routes a body drag to the card mover" (CardPresentationProof),
            // so this exact control is a real drag-eligible point, not an arbitrary one that would never have
            // started a drag anyway.
            var dragTarget = window.SummaryDragTarget;
            var dragPoint = dragTarget.TranslatePoint(new Point(dragTarget.Bounds.Width / 2, dragTarget.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("card_peek_drag_target_not_connected");
            window.MouseDown(dragPoint, MouseButton.Left);
            window.MouseUp(dragPoint, MouseButton.Left);
            Assert(moveStarted == 0, "a press on the drag surface while peeking starts no move-drag");

            Assert(observedAnyButton.Count == 3 && observedAnyButton.All(anyButton => anyButton),
                $"all three presses reach PeekInputObserved(anyButton: true) through the tunnel handler (got {observedAnyButton.Count})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Design doc §7: CardPeekController against ICardModeWindow-and-ICardLayoutWindow fake (modeled on
    /// CardModeApplierSpecs' FakeCardModeWindow, extended with Topmost/IsPeeking for IPeekableCardWindow) driven
    /// through the real CardLayoutPlatform with a fake single-monitor backend - the coordinator's own next step
    /// after CardModeApplier.Apply, exactly as CardModeApplierSpecs proves the 2.0 toggle.</summary>
    private static void ControllerStartThenEndRestoresExactly()
    {
        var backend = new SinglePeekMonitorBackend();
        var platform = new CardLayoutPlatform(backend);
        var window = new FakePeekWindow("claude", CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        window.Calls.Clear();
        backend.SetPosition(window, new PixelPoint(500, 500));
        window.SetSize(new LogicalSize(530, 270));

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingSink();
        CardPeekStartContext? Context(string id) => id == "claude" ? new CardPeekStartContext(window, 0, 0, 100, 100) : null;
        var controller = new CardPeekController(platform, probe: null, sink, Context, _ => CardMode.Mini, () => false, clock);
        controller.UpdateSetting(true);

        controller.OnHoverEntered("claude");
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard == "claude", "Ctrl held over a hovered mini card starts a peek on it");
        Assert(window.Calls.SequenceEqual(["mode=Full", "scale=100"]), $"Start applies mode then scale (calls: {string.Join(",", window.Calls)})");
        var fullMinimum = CardScale.MinimumSize(CardMode.Full, 100);
        Assert(window.MinWidth == fullMinimum.Width && window.MinHeight == fullMinimum.Height,
            $"the minimum becomes the full minimum at the applied full scale (got {window.MinWidth}x{window.MinHeight})");
        Assert(window.Topmost, "the peeking card is made topmost");
        var expectedFullSize = CardScale.DefaultSize(CardMode.Full, 100);
        Assert(window.Size == expectedFullSize, $"a card with no remembered full size peeks at the full default (got {window.Size})");
        Assert(controller.PeekingPreRect == new LogicalRect(500, 500, 530, 270), $"PeekingPreRect is the exact pre-peek rect (got {controller.PeekingPreRect})");
        Assert(sink.Events.Any(e => e.Code == "card_peek_started"), "card_peek_started is logged");

        window.Calls.Clear();
        controller.OnInputObserved(ctrlHeld: false, anyButtonHeld: false);
        Assert(controller.PeekingCard is null, "releasing Ctrl ends the peek");
        Assert(window.Calls.SequenceEqual(["mode=Mini", "scale=100"]), $"End applies mode then scale, in reverse (calls: {string.Join(",", window.Calls)})");
        var miniMinimum = CardScale.MinimumSize(CardMode.Mini, 100);
        Assert(window.MinWidth == miniMinimum.Width && window.MinHeight == miniMinimum.Height,
            $"the minimum is back to the mini minimum (got {window.MinWidth}x{window.MinHeight})");
        Assert(!window.Topmost, "Topmost is restored to its pre-peek value");
        Assert(window.Size == new LogicalSize(530, 270), $"the exact pre-peek size is restored (got {window.Size})");
        Assert(backend.GetPosition(window) == new PixelPoint(500, 500), $"the exact pre-peek position is restored (got {backend.GetPosition(window)})");
        Assert(sink.Events.Any(e => e.Code == "card_peek_ended" && e.Status == "reason=ctrl_released"), "card_peek_ended is logged with the ctrl_released reason");
    }

    /// <summary>Design doc §5.4: EndForLayoutBusy is what every layout-changing entry point calls explicitly; it
    /// ends an active peek with reason layout_busy, and is a no-op (no capture, no log) when nothing is peeking.
    /// The controller's own layoutBusy func is fixed false here, so only the explicit call is under test (design
    /// doc §10.5's continuous check has its own test, LayoutBusyFuncBlocksAStartAndEndsAPeek, below).</summary>
    private static void ControllerEndsForLayoutBusy()
    {
        var backend = new SinglePeekMonitorBackend();
        var platform = new CardLayoutPlatform(backend);
        var window = new FakePeekWindow("gemini", CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        backend.SetPosition(window, new PixelPoint(10, 10));
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingSink();
        CardPeekStartContext? Context(string id) => id == "gemini" ? new CardPeekStartContext(window, 0, 0, 100, 100) : null;
        var controller = new CardPeekController(platform, probe: null, sink, Context, _ => CardMode.Mini, () => false, clock);

        controller.EndForLayoutBusy();
        Assert(sink.Events.Count == 0, "EndForLayoutBusy is a no-op with nothing peeking");

        controller.UpdateSetting(true);
        controller.OnHoverEntered("gemini");
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard == "gemini", "test setup: a peek is active");

        controller.EndForLayoutBusy();
        Assert(controller.PeekingCard is null, "EndForLayoutBusy ends the active peek");
        Assert(sink.Events.Any(e => e.Code == "card_peek_ended" && e.Status == "reason=layout_busy"), "the reason logged is layout_busy");
    }

    /// <summary>The platform probe drives the same start and end decisions as card input. A cursor outside
    /// the card does not end an active peek; with no active peek, it clears the hover.</summary>
    private static void ControllerProbeDrivesStartAndEnd()
    {
        var backend = new SinglePeekMonitorBackend();
        var platform = new CardLayoutPlatform(backend);
        var window = new FakePeekWindow("codex", CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        backend.SetPosition(window, new PixelPoint(200, 200));
        window.SetSize(new LogicalSize(530, 270));
        var probe = new FakePeekProbe();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingSink();
        CardPeekStartContext? Context(string id) => id == "codex" ? new CardPeekStartContext(window, 0, 0, 100, 100) : null;
        var controller = new CardPeekController(platform, probe, sink, Context, _ => CardMode.Mini, () => false, clock);
        controller.UpdateSetting(true);
        controller.OnHoverEntered("codex");
        probe.ShiftHeld = true;
        controller.OnInputObserved(ctrlHeld: false, shiftHeld: false, anyButtonHeld: false);
        Assert(controller.ShiftHeld, "the global probe reports Shift even when input events do not");
        probe.ShiftHeld = false;
        controller.OnInputObserved(ctrlHeld: false, shiftHeld: true, anyButtonHeld: false);
        Assert(!controller.ShiftHeld, "the global probe's released state clears a missed Shift key event");
        Assert(controller.NeedsPolling, "hovering a mini card with the setting on needs the Windows poll");
        Assert(sink.Events.Count(e => e.Code == "card_peek_probe") == 1, "card_peek_probe is logged once per hover");

        // The probe's cursor sits inside the mini card, so Poll's pre-peek rect test keeps the hover.
        var inside = CoveringPointFor(new PixelPoint(200, 200), new LogicalSize(530, 270));
        probe.Cursor = new PhysicalPoint((int)inside.X, (int)inside.Y);
        probe.CtrlHeld = true;
        controller.Poll();
        Assert(controller.PeekingCard == "codex", "the probe's own Ctrl-held tick starts a peek with no card event at all");

        probe.Cursor = new PhysicalPoint(2000, 2000);   // well outside the card's rect on the single 1920x1080 monitor
        controller.Poll();
        Assert(controller.PeekingCard == "codex", "a cursor outside the peeking card ends nothing while Ctrl is held");

        probe.CtrlHeld = false;
        controller.Poll();
        Assert(controller.PeekingCard is null, "Ctrl released ends the peek, wherever the cursor is");
        Assert(sink.Events.Any(e => e.Code == "card_peek_ended" && e.Status == "reason=ctrl_released"), "the reason logged is ctrl_released");
        controller.Poll();   // no peek now, so the rect test runs again and the outside cursor clears the hover
        Assert(!controller.NeedsPolling, "with the hover cleared and no peek, the poll is no longer needed");
    }

    /// <summary>Reads the hovered card's mode on each decision, so a card that becomes full under a stationary
    /// pointer is not treated as a mini card.</summary>
    private static void HoveredCardTurningFullNeverStartsAPeek()
    {
        var backend = new SinglePeekMonitorBackend();
        var platform = new CardLayoutPlatform(backend);
        var window = new FakePeekWindow("claude", CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        backend.SetPosition(window, new PixelPoint(500, 500));
        window.SetSize(new LogicalSize(530, 270));

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingSink();
        var mode = CardMode.Mini;
        CardPeekStartContext? Context(string id) => id == "claude" ? new CardPeekStartContext(window, 0, 0, 100, 100) : null;
        var controller = new CardPeekController(platform, probe: null, sink, Context, _ => mode, () => false, clock);
        controller.UpdateSetting(true);

        controller.OnHoverEntered("claude");
        mode = CardMode.Full;   // the card turns full under a pointer that never moved and a hover that never changed
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard is null, "a card that turns full under a still pointer never starts a peek");
    }

    /// <summary>Without a platform probe, leaving the card ends one peek; hover transitions while Ctrl remains
    /// held do not restart it until Ctrl is released.</summary>
    private static void PeekEndsOnceWhenThePointerLeavesWithNoProbe()
    {
        var backend = new SinglePeekMonitorBackend();
        var platform = new CardLayoutPlatform(backend);
        var window = new FakePeekWindow("claude", CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        backend.SetPosition(window, new PixelPoint(500, 500));
        window.SetSize(new LogicalSize(530, 270));
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingSink();
        CardPeekStartContext? Context(string id) => id == "claude" ? new CardPeekStartContext(window, 0, 0, 100, 100) : null;
        var controller = new CardPeekController(platform, probe: null, sink, Context, _ => CardMode.Mini, () => false, clock);
        controller.UpdateSetting(true);
        controller.OnHoverEntered("claude");
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard == "claude", "test setup: a peek is active");

        for (var i = 0; i < 3; i++)
        {
            controller.OnHoverExited("claude");
            controller.OnHoverEntered("claude");
            controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        }
        Assert(controller.PeekingCard is null, "the pointer leaving ended it, and Ctrl still held never restarted it");
        Assert(sink.Events.Count(e => e.Code == "card_peek_started") == 1 &&
               sink.Events.Count(e => e.Code == "card_peek_ended" && e.Status == "reason=pointer_left_no_probe") == 1,
            "one start and one pointer_left_no_probe end: no loop");

        controller.OnInputObserved(ctrlHeld: false, anyButtonHeld: false);
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard == "claude", "after Ctrl is released and pressed again, it peeks again");
    }
    /// <summary>Starts a peek immediately and verifies the first Ctrl-click ends it while a second reaches the card.</summary>
    private static void PeekStartsInstantlyAndASecondCtrlPressReachesTheCard()
    {
        var backend = new SinglePeekMonitorBackend();
        var platform = new CardLayoutPlatform(backend);
        var window = new FakePeekWindow("claude", CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        window.Calls.Clear();
        backend.SetPosition(window, new PixelPoint(500, 500));
        window.SetSize(new LogicalSize(530, 270));

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingSink();
        CardPeekStartContext? Context(string id) => id == "claude" ? new CardPeekStartContext(window, 0, 0, 100, 100) : null;
        var controller = new CardPeekController(platform, probe: null, sink, Context, _ => CardMode.Mini, () => false, clock);
        controller.UpdateSetting(true);
        controller.OnHoverEntered("claude");

        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard == "claude", "Ctrl over a hovered mini card starts the peek at once, with no hold delay");

        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: true);   // first Ctrl + press
        Assert(controller.PeekingCard is null && !window.IsPeeking, "the first press only ends the peek");
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);   // released, Ctrl still down
        Assert(controller.PeekingCard is null, "disarmed: Ctrl still held does not restart it");
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: true);   // second Ctrl + press
        Assert(controller.PeekingCard is null && !window.IsPeeking, "the second press finds no peek, so it reaches the card (Ctrl-detach)");
    }

    /// <summary>Every end but setting_off disarms until Ctrl is next seen
    /// released - proven here with button_down, where Ctrl stays held right through the end, so the pointer
    /// sitting still afterward must not loop straight back into a second peek. After Ctrl is finally seen
    /// released, then held again for 250 ms, it starts again (design doc §10.7's exact worked example).</summary>
    private static void EndDisarmsUntilCtrlIsSeenReleasedThenStartsAgain()
    {
        var backend = new SinglePeekMonitorBackend();
        var platform = new CardLayoutPlatform(backend);
        var window = new FakePeekWindow("claude", CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        backend.SetPosition(window, new PixelPoint(500, 500));
        window.SetSize(new LogicalSize(530, 270));

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingSink();
        CardPeekStartContext? Context(string id) => id == "claude" ? new CardPeekStartContext(window, 0, 0, 100, 100) : null;
        var controller = new CardPeekController(platform, probe: null, sink, Context, _ => CardMode.Mini, () => false, clock);
        controller.UpdateSetting(true);
        controller.OnHoverEntered("claude");
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard == "claude", "test setup: a peek is active");

        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: true);   // a button goes down, Ctrl still held
        Assert(controller.PeekingCard is null, "test setup: the peek ended for button_down");
        Assert(sink.Events.Last(e => e.Code == "card_peek_ended").Status == "reason=button_down", "ended for button_down, with Ctrl still held");

        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);   // button released, Ctrl held throughout
        clock.Advance(TimeSpan.FromMilliseconds(250));
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard is null, "disarmed after the end: Ctrl staying held the whole time never restarts it");

        controller.OnInputObserved(ctrlHeld: false, anyButtonHeld: false);   // Ctrl finally seen released
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);    // pressed again
        clock.Advance(TimeSpan.FromMilliseconds(250));
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard == "claude", "after Ctrl is seen released, then held again for 250 ms, it starts again");
    }

    /// <summary>A peek survives placement shifts and hover changes while Ctrl remains held, then ends on Ctrl up.</summary>
    private static void PeekSurvivesHoverChangesWhileCtrlIsHeld()
    {
        var backend = new SinglePeekMonitorBackend();
        var platform = new CardLayoutPlatform(backend);
        var window = new FakePeekWindow("claude", CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        window.Calls.Clear();
        // Near the monitor's right edge the full card cannot keep the mini card's top-left corner, so it shifts.
        backend.SetPosition(window, new PixelPoint(1300, 500));
        window.SetSize(new LogicalSize(530, 270));

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingSink();
        CardPeekStartContext? Context(string id) => id == "claude" ? new CardPeekStartContext(window, 0, 0, 100, 100) : null;
        // Design doc §5.2: the peeking card is raised to the front once, after Topmost is set,
        // because with "always on top" on every card is already topmost.
        var raises = new List<(IPeekableCardWindow Window, bool TopmostAtRaise)>();
        // A probe is present (Windows): that is where the pointer may go anywhere. Linux is PeekEndsOnceWhenThePointerLeavesWithNoProbe.
        // With a probe, its Ctrl state is authoritative; card events only prompt a decision.
        var probe = new FakePeekProbe { CtrlHeld = true };
        var controller = new CardPeekController(platform, probe, sink, Context, _ => CardMode.Mini, () => false, clock,
            raise: w => { raises.Add((w, w.Topmost)); return true; });
        controller.UpdateSetting(true);
        controller.OnHoverEntered("claude");
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard == "claude", "a peek whose placement is shifted to fit still starts");
        Assert(raises.Count == 1 && ReferenceEquals(raises[0].Window, window) && raises[0].TopmostAtRaise,
            "the peeking card is raised to the front once, after it is made topmost");
        Assert(sink.Events.Any(e => e.Code == "card_peek_started" && e.Status!.EndsWith("raised=true", StringComparison.Ordinal)),
            "the raise is logged");
        Assert(sink.Events.Any(e => e.Code == "card_peek_started" && e.Status!.Contains("shifted=True,", StringComparison.Ordinal)),
            "test setup: the placement really was shifted");

        for (var i = 0; i < 5; i++)
        {
            controller.OnHoverExited("claude");
            controller.OnHoverEntered("claude");
        }
        controller.OnHoverExited("claude");
        Assert(controller.PeekingCard == "claude", "hover changes while Ctrl is held never end the peek");
        Assert(sink.Events.Count(e => e.Code == "card_peek_started") == 1 && !sink.Events.Any(e => e.Code == "card_peek_ended"),
            "no end and restart loop: one start, no end");
        Assert(window.Calls.SequenceEqual(["mode=Full", "scale=100"]), $"the card was switched once and never back (calls: {string.Join(",", window.Calls)})");

        // The platform probe is authoritative when card event modifier state disagrees with it.
        controller.OnInputObserved(ctrlHeld: false, anyButtonHeld: false);
        Assert(controller.PeekingCard == "claude" && !sink.Events.Any(e => e.Code == "card_peek_ended"),
            "an event that disagrees with the probe about Ctrl does not end the peek");

        probe.CtrlHeld = false;
        controller.OnInputObserved(ctrlHeld: false, anyButtonHeld: false);
        Assert(controller.PeekingCard is null, "Ctrl up ends it, with the pointer off the card");
        Assert(sink.Events.Single(e => e.Code == "card_peek_ended").Status == "reason=ctrl_released", "ended for ctrl_released");
    }

    /// <summary>Design doc §10.5 (the coordinator's layoutBusy func is read fresh on
    /// every decision - unlike EndForLayoutBusy (an explicit, one-shot push), this blocks a start (even past the
    /// hold delay) and ends an active peek the moment it turns true, with no explicit call needed at all.</summary>
    private static void LayoutBusyFuncBlocksAStartAndEndsAPeek()
    {
        var backend = new SinglePeekMonitorBackend();
        var platform = new CardLayoutPlatform(backend);
        var window = new FakePeekWindow("claude", CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        backend.SetPosition(window, new PixelPoint(500, 500));
        window.SetSize(new LogicalSize(530, 270));

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingSink();
        var busy = false;
        CardPeekStartContext? Context(string id) => id == "claude" ? new CardPeekStartContext(window, 0, 0, 100, 100) : null;
        var controller = new CardPeekController(platform, probe: null, sink, Context, _ => CardMode.Mini, () => busy, clock);
        controller.UpdateSetting(true);
        controller.OnHoverEntered("claude");

        busy = true;
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard is null, "layout busy blocks a start even after the hold delay elapses, with no explicit end call at all");

        busy = false;
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard == "claude", "once no longer busy, the same held Ctrl starts it - the hold already elapsed and it stayed armed");

        busy = true;
        controller.OnInputObserved(ctrlHeld: true, anyButtonHeld: false);
        Assert(controller.PeekingCard is null, "busy again ends the active peek, with no explicit end call at all");
        Assert(sink.Events.Any(e => e.Code == "card_peek_ended" && e.Status == "reason=layout_busy"), "the reason logged is layout_busy");
    }

    /// <summary>Design doc §10.6 (a card that closes is forgotten - its hover is cleared and a
    /// peek still on it ends directly, without touching the (gone) window - through both the coordinator's own
    /// direct call (OnCardClosed) and Poll's defensive fallback (a context that has gone missing between ticks).
    /// Either way NeedsPolling turns false at once.</summary>
    private static void CardRemovedStopsPolling()
    {
        var backend = new SinglePeekMonitorBackend();
        var platform = new CardLayoutPlatform(backend);
        var window = new FakePeekWindow("gemini", CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        backend.SetPosition(window, new PixelPoint(10, 10));
        var probe = new FakePeekProbe();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingSink();
        var exists = true;
        CardPeekStartContext? Context(string id) => exists && id == "gemini" ? new CardPeekStartContext(window, 0, 0, 100, 100) : null;
        var controller = new CardPeekController(platform, probe, sink, Context, _ => CardMode.Mini, () => false, clock);
        controller.UpdateSetting(true);
        controller.OnHoverEntered("gemini");
        Assert(controller.NeedsPolling, "test setup: hovering a mini card with the setting on needs polling");

        exists = false;   // the card has closed; its context is now gone
        controller.Poll();
        Assert(!controller.NeedsPolling, "Poll's defensive fallback (OnCardRemoved) clears the hover once the context is gone");

        controller.OnHoverEntered("gemini");   // the hover is recorded again, context still missing
        Assert(controller.NeedsPolling, "test setup: the hover is tracked again");
        controller.OnCardRemoved("gemini");   // the same call WindowCoordinator.OnCardClosed makes directly
        Assert(!controller.NeedsPolling, "OnCardRemoved clears the hover directly, the same way WindowCoordinator.OnCardClosed calls it");
    }

    /// <summary>Returns a point inside the mini card for hover-state checks.</summary>
    private static LogicalPoint CoveringPointFor(PixelPoint miniPosition, LogicalSize miniSize) =>
        new(miniPosition.X + miniSize.Width - 1, miniPosition.Y + 1);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    /// <summary>Keeps every diagnostic the controller records, so a spec can check what was logged.</summary>
    private sealed class RecordingSink : IDiagnosticSink
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public void Record(DiagnosticEvent diagnostic) => Events.Add(diagnostic);
    }

    private sealed class FakePeekProbe : IPeekInputProbe
    {
        public bool CtrlHeld { get; set; }
        public bool ShiftHeld { get; set; }
        public bool AnyButtonHeld { get; set; }
        public PhysicalPoint? Cursor { get; set; }
    }

    /// <summary>
    /// A fake that behaves like a real window for the order rule, the same way CardModeApplierSpecs'
    /// FakeCardModeWindow does (that class is private to CardModeApplierSpecs, so this is a second copy rather
    /// than a shared one, a deliberate duplicate): setting MinWidth/MinHeight above the current size
    /// grows the window at once, and SetSize clamps a size set below the current minimum up to it. Topmost and
    /// IsPeeking are plain auto-properties, the same shape ProviderUsageCardWindow gives them.
    /// </summary>
    private sealed class FakePeekWindow : IPeekableCardWindow
    {
        private double _width;
        private double _height;
        private double _minWidth;
        private double _minHeight;

        public FakePeekWindow(string providerId, double width, double height, LogicalSize startingMinimum)
        {
            ProviderId = providerId;
            _minWidth = startingMinimum.Width;
            _minHeight = startingMinimum.Height;
            _width = Math.Max(width, _minWidth);
            _height = Math.Max(height, _minHeight);
        }

        public List<string> Calls { get; } = [];
        public string ProviderId { get; }
        public object NativeHandle => this;
        public LogicalSize Size => new(_width, _height);
        public bool Topmost { get; set; }
        public bool IsPeeking { get; set; }

        public double MinWidth
        {
            get => _minWidth;
            set
            {
                _minWidth = value;
                if (_width < value) _width = value;
            }
        }

        public double MinHeight
        {
            get => _minHeight;
            set
            {
                _minHeight = value;
                if (_height < value) _height = value;
            }
        }

        public void ApplyMode(CardMode mode) => Calls.Add($"mode={mode}");
        public void ApplyCardScale(int percent) => Calls.Add($"scale={percent}");

        public void SetSize(LogicalSize size)
        {
            _width = Math.Max(size.Width, _minWidth);
            _height = Math.Max(size.Height, _minHeight);
        }
    }

    /// <summary>A single 1920 x 1080 monitor at 100%, keyed per window by provider id (unlike
    /// CardModeApplierSpecs' single-window version) so a spec's window is placed and read back correctly.</summary>
    private sealed class SinglePeekMonitorBackend : ICardWindowGeometryBackend
    {
        private static readonly CardLayoutScreen Screen =
            new("primary", new PhysicalRect(0, 0, 1920, 1080), new PhysicalRect(0, 0, 1920, 1040), 1, true);
        private readonly Dictionary<string, PixelPoint> _positions = new(StringComparer.Ordinal);

        public IReadOnlyList<CardLayoutScreen> GetScreens(ICardLayoutWindow window) => [Screen];
        public string? GetMonitorId(ICardLayoutWindow window, IReadOnlyList<CardLayoutScreen> screens) => "primary";
        public PixelPoint GetPosition(ICardLayoutWindow window) => _positions.GetValueOrDefault(window.ProviderId, new PixelPoint(100, 100));
        public LogicalSize GetSize(ICardLayoutWindow window) => ((FakePeekWindow)window).Size;
        public void SetPosition(ICardLayoutWindow window, PixelPoint position) => _positions[window.ProviderId] = position;
        public void SetSize(ICardLayoutWindow window, LogicalSize size) => ((FakePeekWindow)window).SetSize(size);
    }
}

using System.Runtime.InteropServices;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// 2.1 Ctrl peek (2.1 design §5): the window a peek needs beyond <see cref="ICardModeWindow"/> (mode, scale,
/// minimum) and <see cref="ICardLayoutWindow"/> (identity for the geometry backend) - the window's own
/// Topmost, and IsPeeking, which <see cref="ProviderUsageCardWindow"/> uses to swallow a press and hide the
/// Undock button (2.1 design §4.1). <see cref="ProviderUsageCardWindow"/> implements it; CardPeekSpecs drives
/// <see cref="CardPeekController"/> against a fake, so no Avalonia window is ever needed to prove this file.
/// </summary>
internal interface IPeekableCardWindow : ICardModeWindow, ICardLayoutWindow
{
    bool Topmost { get; set; }
    bool IsPeeking { get; set; }
}

/// <summary>
/// Supplies the Windows or X11 input probe to the coordinator. If no probe is available, card hover, pointer and
/// key events drive the decision. With a probe, it reports global Ctrl, Shift and button state.
/// </summary>
internal interface IPeekInputProbe
{
    bool CtrlHeld { get; }
    bool ShiftHeld { get; }
    bool AnyButtonHeld { get; }

    /// <summary>
    /// The design gives this member as <c>GetCursorPos, converted to logical coordinates on the card's
    /// screen</c>. That conversion needs the card's own monitor transform (its origin and DPI scale), which
    /// only <see cref="CardPeekController"/>'s already-captured <see cref="CardLayoutGeometrySnapshot"/> has -
    /// duplicating a monitor enumeration inside the probe would re-derive <see cref="CardLayoutPlatform"/>'s
    /// own, already-tested logic for one caller. So this reports the raw physical cursor position instead
    /// (null when the OS call fails), and <see cref="CardPeekController.Poll"/> converts it with
    /// <see cref="LayoutDpiTransform"/> against the same geometry snapshot it captures for that tick's rect
    /// test - the identical formula <see cref="CardLayoutPlatform.Capture"/> uses for a window's own position.
    /// This is a deliberate departure from the design, whose own text types this member <c>LogicalPoint?</c>.
    /// </summary>
    PhysicalPoint? Cursor { get; }
}

/// <summary>
/// 2.1 Ctrl peek (2.1 design §4.2): the Windows probe, polled by WindowCoordinator's shared timer while peek
/// input or a visible full-card projection needs it. GetAsyncKeyState is the same interop
/// <see cref="PrimaryPointerButtonProbe"/> uses.
/// </summary>
internal sealed class WindowsPeekInputProbe : IPeekInputProbe
{
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyLeftShift = 0xA0;
    private const int VirtualKeyRightShift = 0xA1;
    private const int VirtualKeyLeftButton = 0x01;
    private const int VirtualKeyRightButton = 0x02;

    public bool CtrlHeld => (GetAsyncKeyState(VirtualKeyControl) & 0x8000) != 0;
    public bool ShiftHeld => (GetAsyncKeyState(VirtualKeyLeftShift) & 0x8000) != 0 ||
                             (GetAsyncKeyState(VirtualKeyRightShift) & 0x8000) != 0;

    // 2.1 design §4.2: "the primary and secondary buttons (GetAsyncKeyState, the same interop as
    // PrimaryPointerButtonProbe)". Unlike PrimaryPointerButtonProbe (swap-aware: it reads whichever button the
    // OS currently treats as primary, for the gesture quiet period), a peek ends on either physical button
    // going down, so both virtual keys are read directly.
    public bool AnyButtonHeld => (GetAsyncKeyState(VirtualKeyLeftButton) & 0x8000) != 0 ||
                                  (GetAsyncKeyState(VirtualKeyRightButton) & 0x8000) != 0;

    public PhysicalPoint? Cursor => GetCursorPos(out var point) ? new PhysicalPoint(point.X, point.Y) : null;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

/// <summary>2.1 Ctrl peek (2.1 design §5.1): everything <see cref="CardPeekController.Start"/> needs for one
/// card, supplied by the coordinator (or a spec) on demand - never cached by the controller between calls,
/// since the coordinator is the only thing that knows a card's window, its saved return size and the applied
/// scale for each mode.</summary>
internal readonly record struct CardPeekStartContext(
    IPeekableCardWindow Window,
    double SavedFullWidth,
    double SavedFullHeight,
    int FullScalePercent,
    int MiniScalePercent);

/// <summary>
/// Coordinates peek state and applies <see cref="CardPeekDecision.Decide"/> results through the layout platform,
/// input probe, and card window interfaces. Mode and layout-busy state are read live; time controls the hold and
/// re-arm rules. Dependencies that inspect coordinator state are invoked only after construction.
/// </summary>
internal sealed class CardPeekController
{
    /// <summary>Delay before a peek starts. Zero gives immediate feedback; the hold check remains available for
    /// tuning. A press ends an active peek before the card handles it.</summary>
    private static readonly TimeSpan PeekHoldDelay = TimeSpan.Zero;

    private readonly ICardLayoutPlatform _layoutPlatform;
    private readonly IPeekInputProbe? _probe;
    private readonly IDiagnosticSink _diagnostics;
    private readonly Func<string, CardPeekStartContext?> _contextFor;
    private readonly Func<string, CardMode> _modeOf;
    private readonly Func<bool> _layoutBusy;
    private readonly TimeProvider _time;
    // Optional non-activating raise; absent when the platform cannot provide one.
    private readonly Func<IPeekableCardWindow, bool>? _raise;
    private readonly string _probePlatform;

    private bool _settingOn;
    private string? _hoveredCard;
    private bool _ctrlHeld;
    private bool _shiftHeld;
    private bool _anyButtonHeld;
    private PeekState? _peek;
    private bool _probeLoggedForHover;
    // Whether card_peek_start_blocked has been logged for the current Ctrl press.
    private bool _startBlockedLogged;
    // Whether card_peek_first_poll has been logged for the current hover.
    private bool _pollLoggedForHover;
    // A peek end while Ctrl remains held disarms starts until Ctrl is released.
    private bool _armed = true;
    // 2.1 design §10.2: when Ctrl was first seen held, continuously, on the current hover; null whenever Ctrl
    // is not currently held, or the hover has just changed (a new hover always needs a fresh hold delay).
    private DateTimeOffset? _ctrlHeldSince;

    public CardPeekController(ICardLayoutPlatform layoutPlatform, IPeekInputProbe? probe, IDiagnosticSink diagnostics,
        Func<string, CardPeekStartContext?> contextFor, Func<string, CardMode> modeOf, Func<bool> layoutBusy,
        TimeProvider timeProvider, Func<IPeekableCardWindow, bool>? raise = null)
    {
        _raise = raise;
        _layoutPlatform = layoutPlatform ?? throw new ArgumentNullException(nameof(layoutPlatform));
        _probe = probe;
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _contextFor = contextFor ?? throw new ArgumentNullException(nameof(contextFor));
        _modeOf = modeOf ?? throw new ArgumentNullException(nameof(modeOf));
        _layoutBusy = layoutBusy ?? throw new ArgumentNullException(nameof(layoutBusy));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _probePlatform = probe switch { null => "events", X11PeekInputProbe => "x11", _ => "windows" };
    }

    /// <summary>The card currently shown full by a peek, or null.</summary>
    internal string? PeekingCard => _peek?.ProviderId;
    internal bool ShiftHeld => _shiftHeld;

    /// <summary>2.1 design §5.4: the peeking card's pre-peek rect, for CaptureCardGeometry to report in place
    /// of its live (full-size) one. Null exactly when <see cref="PeekingCard"/> is null.</summary>
    internal LogicalRect? PeekingPreRect => _peek?.PreRect;

    /// <summary>True while polling is needed: peeking or hovering a mini card, with the setting on and a probe
    /// available. Mode is read live so a card that becomes mini under the pointer starts polling.</summary>
    internal bool NeedsPolling => _probe is not null && _settingOn &&
        ((_hoveredCard is not null && _modeOf(_hoveredCard) == CardMode.Mini) || _peek is not null);

    /// <summary>2.1 design §2: the coordinator calls this from Settings (start-up load and every save). Turning
    /// the setting off ends an active peek at once, through the same Decide priority order every other input
    /// change uses (setting_off comes before layout_busy, so the reason logged is the right one even when the
    /// coordinator is also about to push a Settings-save busy notice).</summary>
    internal void UpdateSetting(bool on)
    {
        _settingOn = on;
        Act(CardPeekDecision.Decide(BuildInputs()));
    }

    /// <summary>Records a hover. The card's mode is read live when needed, so a card that becomes mini under the
    /// pointer is still detected.</summary>
    internal void OnHoverEntered(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (_hoveredCard != providerId)
        {
            // 2.1 design §10.2: a new hover starts its own hold delay.
            _hoveredCard = providerId;
            _probeLoggedForHover = false;
            _pollLoggedForHover = false;
            _ctrlHeldSince = null;
        }
        if (_settingOn) MaybeLogProbeStart(providerId);
        Act(CardPeekDecision.Decide(BuildInputs()));
    }

    /// <summary>Forgets a hover. Pointer exit does not end a probed peek while Ctrl remains held.</summary>
    internal void OnHoverExited(string providerId)
    {
        if (_hoveredCard == providerId)
        {
            _hoveredCard = null;
            _ctrlHeldSince = null;
        }
        Act(CardPeekDecision.Decide(BuildInputs()));
    }

    /// <summary>Updates input state after a card pointer or key event.</summary>
    internal void OnInputObserved(bool ctrlHeld, bool anyButtonHeld)
        => OnInputObserved(ctrlHeld, false, anyButtonHeld);

    internal void OnInputObserved(bool ctrlHeld, bool shiftHeld, bool anyButtonHeld)
    {
        // The probe is authoritative for current state. Preserve a button-down event so a press is handled immediately.
        if (_probe is not null)
        {
            ctrlHeld = _probe.CtrlHeld;
            shiftHeld = _probe.ShiftHeld;
            anyButtonHeld = anyButtonHeld || _probe.AnyButtonHeld;
        }
        _shiftHeld = shiftHeld;
        UpdateCtrlHeld(ctrlHeld);
        _anyButtonHeld = anyButtonHeld;
        Act(CardPeekDecision.Decide(BuildInputs()));
    }

    /// <summary>
    /// 2.1 design §5.4: called by WindowCoordinator at every layout-changing entry point, before that operation
    /// proceeds. A no-op when nothing is peeking (the overwhelmingly common case), so callers do not need to
    /// guard the call themselves.
    /// </summary>
    internal void EndForLayoutBusy()
    {
        if (_peek is null) return;
        Act(CardPeekDecision.Decide(BuildInputs() with { LayoutBusy = true }));
    }

    /// <summary>
    /// A card that has closed (or is closing) is forgotten - its hover is
    /// cleared, and a peek still active on it ends directly, without touching the window (End() would call
    /// CardModeApplier.Apply and the geometry backend against a window that may already be gone). Called by
    /// WindowCoordinator.OnCardClosed for the closing card, and defensively by <see cref="Poll"/> when a poll
    /// tick finds the tested card's context already gone.
    /// </summary>
    internal void OnCardRemoved(string providerId)
    {
        if (_hoveredCard == providerId)
        {
            _hoveredCard = null;
            _ctrlHeldSince = null;
        }
        if (_peek is not { } peek || peek.ProviderId != providerId) return;
        _peek = null;
        // 2.1 design §10.2: a card_closed end is not setting_off, so it disarms like any other end.
        _armed = false;
        _diagnostics.Record(new DiagnosticEvent("card_peek_ended", providerId, "reason=card_closed"));
    }

    /// <summary>
    /// Polls Ctrl and button state and checks the cursor against the hovered card's current rect. Pointer checks
    /// are skipped during a peek; a missing card context is removed instead of polled again.
    /// </summary>
    internal void Poll()
    {
        if (_probe is null) return;
        var testCard = _peek?.ProviderId ?? _hoveredCard;
        if (testCard is null) return;
        var context = _contextFor(testCard);
        if (context is null)
        {
            OnCardRemoved(testCard);
            return;
        }

        var ctrlHeld = _probe.CtrlHeld;
        _shiftHeld = _probe.ShiftHeld;
        var anyButtonHeld = _probe.AnyButtonHeld;
        var firstPollOfHover = !_pollLoggedForHover;
        _pollLoggedForHover = true;

        // Check pointer exit only before a peek starts; afterward pointer location does not end the peek.
        if (_peek is null && firstPollOfHover && _probe.Cursor is null)
            _diagnostics.Record(new DiagnosticEvent("card_peek_first_poll", testCard, $"ctrl={ctrlHeld},cursor=unavailable"));
        if (_peek is null && _probe.Cursor is { } cursor)
        {
            var singleWindowMap = new Dictionary<string, ICardLayoutWindow> { [testCard] = context.Value.Window };
            var geometry = _layoutPlatform.Capture(singleWindowMap);
            var rect = geometry.Rects[testCard];
            var logicalCursor = ToLogicalCursor(cursor, geometry);
            var pointerLeft = logicalCursor.X < rect.X || logicalCursor.X >= rect.Right ||
                               logicalCursor.Y < rect.Y || logicalCursor.Y >= rect.Bottom;
            // Record the probe and rectangle-test results on the first poll to diagnose peeks that do not start.
            if (firstPollOfHover)
                _diagnostics.Record(new DiagnosticEvent("card_peek_first_poll", testCard,
                    $"ctrl={ctrlHeld},cursor={cursor.X},{cursor.Y},logical={logicalCursor.X:0},{logicalCursor.Y:0}," +
                    $"rect={rect.X:0},{rect.Y:0},{rect.Width:0}x{rect.Height:0},left={pointerLeft}"));
            if (pointerLeft)
            {
                UpdateCtrlHeld(ctrlHeld);
                _anyButtonHeld = anyButtonHeld;
                OnHoverExited(testCard);
                return;
            }
        }

        UpdateCtrlHeld(ctrlHeld);
        _anyButtonHeld = anyButtonHeld;
        Act(CardPeekDecision.Decide(BuildInputs()));
    }

    private PeekInputs BuildInputs()
    {
        var hoveredMode = _hoveredCard is null ? (CardMode?)null : _modeOf(_hoveredCard);
        var holdElapsed = _ctrlHeldSince is { } since && _time.GetUtcNow() - since >= PeekHoldDelay;
        return new PeekInputs(_settingOn, _hoveredCard, hoveredMode, _ctrlHeld, _anyButtonHeld,
            _layoutBusy(), _peek?.ProviderId, _armed, holdElapsed, EndWhenPointerLeaves: _probe is null);
    }

    /// <summary>2.1 design §10.2: records when Ctrl was first seen continuously held on the current hover (for
    /// HoldElapsed above), and re-arms the controller (finding 4's re-arm rule) the moment Ctrl is observed
    /// released - the only place <see cref="_armed"/> is set back to true.</summary>
    private void UpdateCtrlHeld(bool ctrlHeld)
    {
        if (!ctrlHeld)
        {
            _ctrlHeldSince = null;
            _armed = true;
        }
        else if (_ctrlHeldSince is null)
        {
            _ctrlHeldSince = _time.GetUtcNow();
        }
        _ctrlHeld = ctrlHeld;
    }

    private void MaybeLogProbeStart(string providerId)
    {
        if (_probeLoggedForHover) return;
        _probeLoggedForHover = true;
        _diagnostics.Record(new DiagnosticEvent("card_peek_probe", providerId, $"platform={_probePlatform}"));
    }

    private void Act(PeekAction action)
    {
        switch (action.Kind)
        {
            case PeekActionKind.Start:
                var context = _contextFor(action.Card!);
                if (context is null) return;
                Start(action.Card!, context.Value);
                break;
            case PeekActionKind.End:
                End(action.Reason!);
                break;
            case PeekActionKind.None:
            default:
                LogStartBlockedOnce();
                break;
        }
    }

    /// <summary>Logs why a Ctrl-held peek could not start once per press; logging is re-armed on release.</summary>
    private void LogStartBlockedOnce()
    {
        if (!_ctrlHeld) { _startBlockedLogged = false; return; }
        if (_peek is not null || _startBlockedLogged) return;
        _startBlockedLogged = true;
        var inputs = BuildInputs();
        _diagnostics.Record(new DiagnosticEvent("card_peek_start_blocked", inputs.HoveredCard,
            $"setting={inputs.SettingOn},hovered={inputs.HoveredCard ?? "none"},mode={inputs.HoveredCardMode?.ToString() ?? "none"}," +
            $"button={inputs.AnyButtonHeld},busy={inputs.LayoutBusy},armed={inputs.Armed},held={inputs.HoldElapsed},probe={_probePlatform}"));
    }

    /// <summary>Applies mode, scale, minimum size, geometry, and Topmost in order. WindowCoordinator wraps this
    /// call with _applyingLayout set.</summary>
    private void Start(string providerId, CardPeekStartContext context)
    {
        // Include the geometry apply in the reported peek-start duration.
        var startedAt = _time.GetTimestamp();
        var window = context.Window;
        var singleWindowMap = new Dictionary<string, ICardLayoutWindow> { [providerId] = window };
        var geometry = _layoutPlatform.Capture(singleWindowMap);
        var miniRect = geometry.Rects[providerId];
        var workArea = geometry.Monitors[geometry.MonitorByProvider[providerId]].WorkArea.Bounds;
        var fullSize = CardPeekSize.Compute(context.SavedFullWidth, context.SavedFullHeight, context.FullScalePercent);
        var placement = CardPeekPlacement.Compute(miniRect, fullSize, workArea);

        window.IsPeeking = true;                                                     // step 1
        var preTopmost = window.Topmost;                                             // step 2
        // Raise the card before growing it so it appears above its neighbors immediately.
        window.Topmost = true;                                                       // step 3
        var raised = _raise?.Invoke(window);
        // Apply content and geometry before the full minimum to avoid an intermediate resize at the old position.
        var target = new[] { ((ICardModeWindow)window, CardMode.Full, context.FullScalePercent) };
        CardModeApplier.ApplyBeforeGeometry(target);                                 // step 4
        var savedMonitorId = geometry.MonitorByProvider.GetValueOrDefault(providerId);
        _layoutPlatform.ApplyOneCard(window, placement, savedMonitorId, geometry);    // step 5
        CardModeApplier.ApplyMinimum(target);                                        // step 6
        _peek = new PeekState(providerId, window, miniRect, preTopmost, context.MiniScalePercent);
        var shifted = CardPeekPlacement.WouldShift(miniRect, fullSize, workArea);
        _diagnostics.Record(new DiagnosticEvent("card_peek_started", providerId,       // step 7
            $"size={fullSize.Width:0}x{fullSize.Height:0},shifted={shifted}," +
            $"raised={(raised is null ? "n/a" : raised.Value ? "true" : "false")}",
            (long)_time.GetElapsedTime(startedAt).TotalMilliseconds));
    }

    /// <summary>2.1 design §5.3: the steps run in reverse, all effectively under the caller's _applyingLayout.
    /// Nothing is captured or saved.</summary>
    private void End(string reason)
    {
        if (_peek is not { } peek) return;
        var singleWindowMap = new Dictionary<string, ICardLayoutWindow> { [peek.ProviderId] = peek.Window };
        var geometry = _layoutPlatform.Capture(singleWindowMap);
        // Lower the minimum before restoring the mini geometry.
        var target = new[] { ((ICardModeWindow)peek.Window, CardMode.Mini, peek.MiniScalePercent) };
        CardModeApplier.ApplyBeforeGeometry(target);                                                      // step 1
        var savedMonitorId = geometry.MonitorByProvider.GetValueOrDefault(peek.ProviderId);
        _layoutPlatform.ApplyOneCard(peek.Window, peek.PreRect, savedMonitorId, geometry);                // step 2
        CardModeApplier.ApplyMinimum(target);
        peek.Window.Topmost = peek.PreTopmost;                                                            // step 3
        peek.Window.IsPeeking = false;                                                                    // step 4
        _peek = null;
        // Disarm after an end while Ctrl remains held to prevent an immediate restart. A Ctrl-release end is
        // already re-armed by the release itself.
        if (reason != "setting_off" && _ctrlHeld) _armed = false;
        _diagnostics.Record(new DiagnosticEvent("card_peek_ended", peek.ProviderId, $"reason={reason}"));  // step 5
    }

    /// <summary>The same physical-to-logical conversion CardLayoutPlatform.Capture uses for a window's own
    /// position, applied to a raw cursor point instead: the monitor whose physical bounds contain the cursor
    /// (falling back to the snapshot's primary monitor, which cannot happen for a real on-screen cursor but
    /// keeps this total), then LayoutDpiTransform with that monitor's origin and scale.</summary>
    private static LogicalPoint ToLogicalCursor(PhysicalPoint cursor, CardLayoutGeometrySnapshot geometry)
    {
        var monitor = geometry.Monitors.Values.FirstOrDefault(candidate =>
            cursor.X >= candidate.Screen.Bounds.X && cursor.X < candidate.Screen.Bounds.X + candidate.Screen.Bounds.Width &&
            cursor.Y >= candidate.Screen.Bounds.Y && cursor.Y < candidate.Screen.Bounds.Y + candidate.Screen.Bounds.Height)
            ?? geometry.Monitors[geometry.PrimaryMonitorId];
        var logical = LayoutDpiTransform.ToLogical(new PhysicalRect(cursor.X, cursor.Y, 1, 1),
            new PhysicalPoint(monitor.Screen.Bounds.X, monitor.Screen.Bounds.Y), monitor.LogicalOrigin, monitor.Screen.Scaling);
        return new LogicalPoint(logical.X, logical.Y);
    }

    /// <summary>2.1 design §5.1: the card, its pre-peek rect, its pre-peek Topmost, and the mini scale percent
    /// applied when the peek started (End restores the mode at exactly this percent, never whatever the live
    /// applied mini scale happens to be when the peek ends).</summary>
    private sealed record PeekState(string ProviderId, IPeekableCardWindow Window, LogicalRect PreRect, bool PreTopmost, int MiniScalePercent);
}

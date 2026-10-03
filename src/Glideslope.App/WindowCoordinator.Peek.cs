using Avalonia.Input;
using Avalonia.Threading;
using Glideslope.Core;

namespace Glideslope.App;

// Connects card hover/input events and the input probe to CardPeekController.
internal sealed partial class WindowCoordinator
{
    // Global input is probed on Windows and through X11 on Linux. Without a probe, card events provide input.
    // Poll only while a mini card is hovered or a peek is active and the setting is enabled.
    private readonly IPeekInputProbe? _peekProbe;
    // Shared by the Linux input probe and window raiser; null when no X connection is available.
    private readonly X11Connection? _x11;
    private readonly CardPeekController _peekController;
    private DispatcherTimer? _peekProbeTimer;
    private static readonly TimeSpan PeekProbePeriod = TimeSpan.FromMilliseconds(50);

    // Controller calls apply through ICardLayoutPlatform. The wrapper marks synchronous geometry changes as
    // layout applies and refreshes tracked positions so delayed X11 echoes cannot start a gesture.

    private void InvokePeekController(Action action)
    {
        var before = _peekController.PeekingCard;
        // Restore the previous flag so a nested call preserves its caller's layout-apply scope.
        var wasApplyingLayout = _applyingLayout;
        _applyingLayout = true;
        try { action(); }
        finally { _applyingLayout = wasApplyingLayout; }
        RefreshAppliedPositionForPeek(before);
        RefreshAppliedPositionForPeek(_peekController.PeekingCard);
        // Restore Settings' z-order after the peek ends when raising does not activate a window.
        if (!_raiser.ActivatesWindow && before is not null && _peekController.PeekingCard is null)
            RaiseSettingsAboveCards("card_peek_ended");
        UpdatePeekProbeTimer();
    }

    private void RefreshAppliedPositionForPeek(string? providerId)
    {
        if (providerId is not null && _windows.TryGetValue(providerId, out var window))
            _appliedPositions[providerId] = window.Position;
    }

    private void OnCardPeekHoverChanged(ProviderUsageCardWindow window, bool inside) =>
        InvokePeekController(() =>
        {
            // The controller reads the current mode when deciding whether to peek.
            if (inside) _peekController.OnHoverEntered(window.ProviderId);
            else _peekController.OnHoverExited(window.ProviderId);
        });

    private void OnCardPeekInputObserved(ProviderUsageCardWindow window, KeyModifiers modifiers, bool anyButton) =>
        InvokePeekController(() => _peekController.OnInputObserved(modifiers.HasFlag(KeyModifiers.Control), anyButton));

    /// <summary>2.1 design §5.4: called at every layout-changing entry point, before that operation proceeds
    /// (InterruptActiveGesture, the card-size settle, closing and exit). A no-op when nothing is peeking.</summary>
    private void EndPeekForLayoutBusy() => InvokePeekController(_peekController.EndForLayoutBusy);

    /// <summary>2.1 design §2: pushed at startup and on every Settings save.</summary>
    private void UpdatePeekSetting(bool on) => InvokePeekController(() => _peekController.UpdateSetting(on));

    /// <summary>The coordinator's layout-busy signal, read fresh
    /// by CardPeekController on every decision - true while a layout gesture is active, a card-size settle is
    /// still pending, the app is closing, or a Settings save is between its disk commit and its live apply
    /// (<see cref="_applyingSettingsSave"/>) - so a peek can neither start nor stay up while any of them hold,
    /// even in the gap between the explicit EndPeekForLayoutBusy calls at each of §5.4's entry points.</summary>
    private bool IsLayoutBusy() =>
        _gestures.IsActive || _leadWindow is not null || _scaleSettle.HasPending || _closing || _applyingSettingsSave;

    /// <summary>2.1 design §5.1: everything CardPeekController.Start needs for one card - its window, its
    /// saved return size (0 when it has never been full) and the applied scale of each mode.</summary>
    private CardPeekStartContext? PeekContextFor(string providerId)
    {
        if (!_windows.TryGetValue(providerId, out var window)) return null;
        var card = _settings.Layout.Cards.FirstOrDefault(c => c.ProviderId == providerId);
        return new CardPeekStartContext(window, card?.FullWidth ?? 0, card?.FullHeight ?? 0,
            AppliedScale(CardMode.Full), AppliedScale(CardMode.Mini));
    }

    /// <summary>2.1 design §4.2: the timer runs only while a mini card is hovered, or a peek is active, and the
    /// setting is on; it stops the moment none of that holds. Called after every event that could change
    /// CardPeekController.NeedsPolling.</summary>
    private void UpdatePeekProbeTimer()
    {
        if (_peekProbe is null) return;
        if (_peekController.NeedsPolling)
        {
            if (_peekProbeTimer is null)
            {
                _peekProbeTimer = new DispatcherTimer { Interval = PeekProbePeriod };
                _peekProbeTimer.Tick += (_, _) => InvokePeekController(_peekController.Poll);
            }
            if (!_peekProbeTimer.IsEnabled)
            {
                _peekProbeTimer.Start();
                // Record polling transitions to diagnose unresponsive peeks.
                _diagnostics.Record(new DiagnosticEvent("card_peek_poll", _peekController.PeekingCard, "state=started"));
            }
        }
        else if (_peekProbeTimer is { IsEnabled: true })
        {
            _peekProbeTimer.Stop();
            _diagnostics.Record(new DiagnosticEvent("card_peek_poll", Status: "state=stopped"));
        }
    }
}

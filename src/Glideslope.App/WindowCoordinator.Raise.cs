using Avalonia.Controls;
using Avalonia.Threading;
using Glideslope.Core;

namespace Glideslope.App;

// Raises visible cards together while preserving focus and Settings z-order.
internal sealed partial class WindowCoordinator
{
    private readonly IWindowRaiser _raiser;
    private readonly List<ProviderUsageCardWindow> _activationOrder = new();   // most recent first
    private bool _raisingGroup;

    /// <summary>Window design §7: activating any card raises every visible card, least recently activated
    /// first, then the activated card on top. Pure, so BringToFrontOrderSpecs can check the order.</summary>
    internal static IReadOnlyList<T> RaiseOrder<T>(T activated, IEnumerable<T> visible, IReadOnlyList<T> mostRecentFirst) where T : class
    {
        // IReadOnlyList<T> has no IndexOf, so rank by hand.
        int Rank(T window)
        {
            for (var index = 0; index < mostRecentFirst.Count; index++)
                if (EqualityComparer<T>.Default.Equals(mostRecentFirst[index], window)) return index;
            return int.MaxValue;
        }
        var others = visible.Where(window => !ReferenceEquals(window, activated))
            .OrderByDescending(Rank)
            .ToList();
        others.Add(activated);
        return others;
    }

    private void OnCardActivated(object? sender, EventArgs e)
    {
        if (sender is not ProviderUsageCardWindow activated || _closing) return;
        if (_raisingGroup)
        {
            // Window design Appendix H.7: an Activated echo from our own group raise is ignored, and said so.
            _diagnostics.Record(new DiagnosticEvent("layout_group_raise_suppressed", activated.ProviderId));
            return;
        }
        _activationOrder.Remove(activated);
        _activationOrder.Insert(0, activated);
        var visible = _windows.Values.Where(window => window.IsVisible && window.WindowState != WindowState.Minimized).ToList();
        if (visible.Count >= 2)
        {
            var order = RaiseOrder(activated, visible, _activationOrder);
            _raisingGroup = true;
            // Window design Appendix H.7: the raiser reports whether each raise took; count the failures.
            var failed = 0;
            foreach (var window in order)
                if (!_raiser.RaiseWithoutActivating(window)) failed++;
            if (failed > 0)
                _diagnostics.Record(new DiagnosticEvent("layout_group_raise_failed", activated.ProviderId, Status: $"failed={failed}"));
            // With the activating fallback used when no X connection opens, raises produce asynchronous
            // Activated events. Ignore them briefly so cards do not activate each other in a loop. Windows and
            // X11 raises do not activate.
            // Windows raises synchronously, so clear the activation guard immediately.
            var echoWindow = GroupRaiseEchoWindow(_raiser.ActivatesWindow);
            if (echoWindow > TimeSpan.Zero) DispatcherTimer.RunOnce(() => _raisingGroup = false, echoWindow);
            else _raisingGroup = false;
            _diagnostics.Record(new DiagnosticEvent("layout_group_raised", activated.ProviderId, Status: $"count={order.Count}"));
        }
        RaiseSettingsAboveCards("card_activated");
    }

    /// <summary>How long activation echoes are ignored after a group raise. Non-activating raises need no delay;
    /// the activating fallback uses 250 ms for asynchronous events.</summary>
    internal static TimeSpan GroupRaiseEchoWindow(bool raiserActivates) => raiserActivates ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero;

    /// <summary>
    /// Keep Settings above the cards while it is open. When cards are also topmost, activating a card or raising
    /// the group can put them above Settings, so restore its z-order without activating it. The activating fallback
    /// is used only when cards are also topmost, preserving focus for normal cards.
    /// </summary>
    private void RaiseSettingsAboveCards(string reason)
    {
        var settings = _settingsWindow;
        if (settings is null || !settings.IsVisible) return;
        // With a non-activating raiser, Settings can be raised without changing focus. The activating fallback
        // preserves focus unless cards are configured to stay above ordinary windows.
        if (_raiser.ActivatesWindow && !_settings.AlwaysOnTop) return;
        var raised = _raiser.RaiseWithoutActivating(settings);
        _diagnostics.Record(new DiagnosticEvent("settings_window_raised", Status: $"reason={reason},raised={raised}"));
    }
}

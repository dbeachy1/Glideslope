using Glideslope.Core;

namespace Glideslope.App;

// Applies a card scale to every card of one mode and saves after the slider settles.
internal sealed partial class WindowCoordinator
{
    // Current scale shown by each card mode. _settings may differ briefly between a Settings commit and its apply.
    private int _appliedFullScalePercent = 100;
    private int _appliedMiniScalePercent = 100;
    private int AppliedScale(CardMode mode) => mode == CardMode.Full ? _appliedFullScalePercent : _appliedMiniScalePercent;
    private void SetAppliedScale(CardMode mode, int percent)
    {
        if (mode == CardMode.Full) _appliedFullScalePercent = percent;
        else _appliedMiniScalePercent = percent;
    }
    // Each slider step scales from the session's start value, so returning to it restores the original layout.
    // A session belongs to one mode; a step for the other mode starts a new session.
    private CardScaleSession? _scaleSession;
    // The mode of the last card-size step that asked for a settle; read by SettleCardScale's log line when the
    // session was cleared before the settle ran.
    private CardMode _lastSettleMode = CardMode.Full;
    // Saved once, 500 ms after the last slider step or when the flyout closes (window design §18).
    private static readonly TimeSpan CardScaleSettlePeriod = TimeSpan.FromMilliseconds(500);
    private readonly QuietPeriodDebouncer _scaleSettle;

    private void OnCardScaleRequested(ProviderUsageCardWindow window, int percent)
    {
        if (_closing) return;
        // Use the card's mode for scaling. If finishing a drag docks it into another mode, discard the stale step.
        var modeBefore = _modes.Of(window.ProviderId);
        InterruptActiveGesture("scale_changed");
        var mode = _modes.Of(window.ProviderId);
        if (mode != modeBefore)
        {
            _diagnostics.Record(new DiagnosticEvent("card_scale_rejected", window.ProviderId,
                $"mode_changed_by_interrupt,from={modeBefore},now={mode},requested={percent}"));
            return;
        }
        var normalized = CardScale.Normalize(percent);
        _diagnostics.Record(new DiagnosticEvent("card_scale_requested", window.ProviderId,
            $"mode={mode},requested={percent},normalized={normalized},current={AppliedScale(mode)}"));
        ApplyCardScale(mode, normalized, "card_button", persistNow: false);
    }

    private void OnCardScaleChangeEnded(object? sender, EventArgs e)
    {
        _diagnostics.Record(new DiagnosticEvent("card_scale_change_ended", (sender as ProviderUsageCardWindow)?.ProviderId,
            $"pending={_scaleSettle.HasPending}"));
        _scaleSettle.Flush();
    }

    /// <summary>
    /// Applies a new card scale to every card of
    /// <paramref name="mode"/>, live. Order, and why: (1) a gesture in progress is finished first, like any
    /// layout change (A7); (2) the sizes come from ScaleAndRecover, starting from the geometry and layout at the
    /// start of this run of slider steps, so dragging the slider back restores the start exactly: each docked
    /// group is resized as a unit (gaps unchanged, top-left kept), a detached card keeps its top-left, and then
    /// every card is made reachable on its monitor by the same recovery a restore uses; (3) each card's content
    /// scale and minimum size are set before its new size, so the window never clamps the size it is given; (4) a
    /// card-button step is saved once, 500 ms after the last step or when the flyout closes (SettleCardScale),
    /// together with the layout; a Settings change saves at once.
    /// 2.0 mini mode (2.0 design §5.8): only that mode's windows get scaled and resized. Cards of the other mode
    /// keep their size and move only if the recovery must make them reachable (ScaleAndRecover, per-card minimums).
    /// </summary>
    private void ApplyCardScale(CardMode mode, int requestedPercent, string reason, bool persistNow)
    {
        var toPercent = CardScale.Normalize(requestedPercent);
        if (_closing) return;
        if (toPercent == AppliedScale(mode))
        {
            _diagnostics.Record(new DiagnosticEvent("card_scale_unchanged", Status: $"mode={mode},percent={toPercent},reason={reason}"));
            return;
        }
        // Finish the gesture before building the mode's card list, since
        // finishing it can commit a dock that switches cards between modes.
        InterruptActiveGesture("scale_changed");
        var modeProviders = (mode == CardMode.Full ? _modes.FullIds : _modes.MiniIds).ToHashSet(StringComparer.Ordinal);
        if (modeProviders.Count == 0)
        {
            // No card of this mode to rescale; a card created later at this mode opens at this scale, and the
            // restore raises any saved size below its minimum.
            // Keep remembered full sizes scaled even when every card is mini.
            // 2.0 mini mode (2.0 design §3.7): the mirror case - a mini-scale change with no mini card open still
            // zooms every full card's remembered mini size, or it would be stale the next time that card goes
            // mini. otherIds is always the OTHER mode's cards (the only ones that can carry a remembered size for
            // THIS mode), so one ScaleReturnSizes call, keyed by mode, handles both directions.
            var previousPercent = AppliedScale(mode);
            var otherIds = (mode == CardMode.Full ? _modes.MiniIds : _modes.FullIds).ToArray();
            if (otherIds.Length > 0)
            {
                _settings.Layout = LayoutPolicy.ScaleReturnSizes(_settings.Layout, otherIds,
                    (double)toPercent / previousPercent, CardScale.MinimumSize(mode, toPercent), mode);
            }
            SetAppliedScale(mode, toPercent);
            CardScale.SetPercent(_settings, mode, toPercent);
            _diagnostics.Record(new DiagnosticEvent("card_scale_applied_without_cards", Status:
                $"mode={mode},from={previousPercent},percent={toPercent},reason={reason},returnSizesScaled={otherIds.Length}"));
            if (otherIds.Length > 0) RequestCoordinatorSave(_settings.Layout, reason);
            return;
        }
        if (!TryCaptureGeometry(out var geometry)) return;
        var session = _scaleSession is { } open && open.IsCurrent(geometry, _settings.Layout, mode)
            ? open
            : new CardScaleSession(geometry, _settings.Layout.Clone(), AppliedScale(mode), geometry.Rects, _settings.Layout, mode);
        var proposal = ScaleAndRecover(session.BaselineLayout, _settings.EnabledProviderIds, session.BaselineGeometry,
            (double)toPercent / session.BaselinePercent, mode, modeProviders, CardScale.MinimumSize(mode, toPercent),
            UsableMinimumsByProviderForScale(mode, toPercent));
        if (!proposal.Succeeded || proposal.ProposedSettings is null)
        {
            _diagnostics.Record(new DiagnosticEvent("card_scale_rejected", Status:
                $"mode={mode},from={AppliedScale(mode)},to={toPercent},reason={reason},{proposal.SafeErrorCode}"));
            return;
        }
        var fromPercent = AppliedScale(mode);
        _applyingLayout = true;
        try
        {
            foreach (var providerId in modeProviders)
                if (_windows.TryGetValue(providerId, out var window))
                    ApplyScaleToWindow(window, mode, toPercent);
            ApplyCardGeometry(proposal.ProposedSettings, proposal.ProposedRects, geometry);
            _settings.Layout = proposal.ProposedSettings;
        }
        finally { _applyingLayout = false; }
        SetAppliedScale(mode, toPercent);
        CardScale.SetPercent(_settings, mode, toPercent);
        if (!TryCaptureGeometry(out var applied)) return;
        _layoutGeometry = applied;
        RefreshCardGroupState(applied);
        _diagnostics.Record(new DiagnosticEvent("card_scale_applied", Status:
            $"mode={mode},from={fromPercent},to={toPercent},baseline={session.BaselinePercent},reason={reason},cards={applied.Rects.Count},edges={_settings.Layout.Edges.Count}"));
        if (persistNow)
        {
            _scaleSession = null;
            if (!PersistCapturedLayout(applied)) RequestCoordinatorSave(null, reason);
            return;
        }
        _scaleSession = session with { LastAppliedRects = applied.Rects, LastLayout = _settings.Layout };
        _lastSettleMode = mode;
        _scaleSettle.Request(reason);
    }

    /// <summary>Applies the content scale and window minimum for the card's mode. The coordinator owns window
    /// size and minimum; CardModeApplier applies scale before minimum so the card remains valid throughout.</summary>
    private static void ApplyScaleToWindow(ProviderUsageCardWindow window, CardMode mode, int percent) =>
        CardModeApplier.Apply([((ICardModeWindow)window, mode, percent)]);

    /// <summary>After the slider settles, capture and save the layout with its scale. Read the session mode before
    /// clearing it; ApplyCardScale has already committed the new percentage.</summary>
    private void SettleCardScale(string source, int steps)
    {
        if (_closing) return;
        // 2.1 Ctrl peek (2.1 design §5.4): the card-size settle changes the layout (CaptureLayoutForSave below).
        EndPeekForLayoutBusy();
        // Wait for any new gesture before capturing layout, so cancelled drag positions are not persisted.
        if (_gestures.IsActive || _leadWindow is not null)
        {
            _diagnostics.Record(new DiagnosticEvent("card_scale_settle_deferred", _leadWindow?.ProviderId, $"source={source},active_gesture"));
            _scaleSettle.Request("deferred_active_gesture");
            return;
        }
        // The last-step mode remains available for logging if another operation cleared the session.
        var mode = _scaleSession?.Mode ?? _lastSettleMode;
        var session = _scaleSession;
        _scaleSession = null;
        var captured = TryCaptureGeometry(out var geometry) && CaptureLayoutForSave(geometry);
        if (captured) _layoutGeometry = geometry;
        // Preserve the original geometry across slider pauses; another layout change ends the session.
        if (captured && session is not null && CardLayoutGeometryComparison.RectsEqual(session.LastAppliedRects, geometry.Rects))
            _scaleSession = session with { LastAppliedRects = geometry.Rects, LastLayout = _settings.Layout };
        RequestCoordinatorSave(null, "card_scale_settled");
        _diagnostics.Record(new DiagnosticEvent("card_scale_settled", Status:
            $"mode={mode},percent={AppliedScale(mode)},steps={steps},source={source},layoutCaptured={captured}"));
    }

    /// <summary>Scales each group in <paramref name="modeProviders"/> from <paramref name="geometry"/> and restores
    /// the result into the work areas. A group that no longer fits is split using the recovery rule used by other
    /// restores. The other mode's remembered return sizes are scaled by the same ratio and raised to that mode's
    /// new minimum, so both full and mini cards retain correctly scaled return sizes.</summary>
    internal static LayoutPolicyResult ScaleAndRecover(LayoutSettings layout, IEnumerable<string> enabledProviderIds,
        CardLayoutGeometrySnapshot geometry, double ratio, CardMode mode, IReadOnlySet<string> modeProviders,
        LogicalSize minimum, IReadOnlyDictionary<string, LogicalSize> usableMinimumByProvider)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var enabled = enabledProviderIds.ToArray();
        var scaled = LayoutPolicy.ScaleGroups(layout, enabled, geometry.Rects, ratio, minimum, modeProviders);
        if (!scaled.Succeeded || scaled.ProposedSettings is null) return scaled;
        var withReturnSizes = LayoutPolicy.ScaleReturnSizes(scaled.ProposedSettings,
            enabled.Where(id => !modeProviders.Contains(id)), ratio, minimum, mode);
        var captured = LayoutPolicy.CaptureAnchors(withReturnSizes, enabled, scaled.ProposedRects, WorkAreasByProvider(geometry));
        if (!captured.Succeeded || captured.ProposedSettings is null) return captured;
        return LayoutPolicy.RecoverMissingMonitor(captured.ProposedSettings, enabled, WorkAreasById(geometry),
            geometry.PrimaryMonitorId, usableMinimumByProvider);
    }
}

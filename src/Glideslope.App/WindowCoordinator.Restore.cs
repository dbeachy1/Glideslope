using System.Collections.Immutable;
using Glideslope.Core;
using Glideslope.Domain;

namespace Glideslope.App;

internal sealed record GeminiLayoutRecoveryResult(LayoutPolicyResult Result, bool DetachedGemini);

// WindowCoordinator, restore: the startup layout restore, Gemini collision recovery, Reset window positions, the
// reset-size button, and revalidating the layout after a display change. Every restore-like path goes through
// LayoutPolicy.RecoverMissingMonitor, so every card's title row is reachable.
// Restores and revalidates card layout against current displays.
internal sealed partial class WindowCoordinator
{
    private bool _layoutInitialized;
    // Coalesce display and scaling changes before revalidating the layout.
    private static readonly TimeSpan DisplayChangeQuietPeriod = TimeSpan.FromMilliseconds(750);
    private readonly QuietPeriodDebouncer _displayChanges;
    // The monitors the live layout was last restored or revalidated against.
    private ImmutableDictionary<string, CardLayoutMonitorTransform>? _validatedMonitors;
    private bool _primaryInferredLogged;

    private void RestoreCardLayout()
    {
        _layoutInitialized = true;
        try
        {
            var geometry = CaptureCardGeometry();
            LogInferredPrimary(geometry);
            // Keep saved sizes, raising each card and its remembered return size to that mode's scaled minimum.
            var sized = LayoutPolicy.EnsureMinimumSizes(_settings.Layout, MinimumSizesByProvider(),
                CardScale.MinimumSize(CardMode.Full, AppliedScale(CardMode.Full)),
                CardScale.MinimumSize(CardMode.Mini, AppliedScale(CardMode.Mini)));
            var layout = AddDefaultPlacementsForMissingCards(sized, geometry);

            var recovery = RecoverGeminiLayout(layout, _settings.EnabledProviderIds,
                WorkAreasById(geometry), geometry.PrimaryMonitorId, UsableMinimumsByProvider());
            var restored = recovery.Result;
            if (recovery.DetachedGemini)
                _diagnostics.Record(new DiagnosticEvent("settings_gemini_layout_detached", Status: "layout_recovered_after_validation"));

            if (!restored.Succeeded || restored.ProposedSettings is null)
            {
                _diagnostics.Record(new DiagnosticEvent("layout_restore_rejected", Status: restored.SafeErrorCode));
                // Rebuild a fresh detached graph from the cascade placements so a rejected saved graph cannot
                // prevent later captures and saves.
                var fresh = _settings.Layout.Clone();
                fresh.Edges.Clear();
                fresh.Cards.Clear();
                _settings.Layout = fresh;
                _diagnostics.Record(new DiagnosticEvent("layout_restore_reset", Status: "edges_and_placements_dropped"));
                RefreshCardGroupState(geometry);
                _layoutGeometry = geometry;
                _validatedMonitors = geometry.Monitors;
                PersistCapturedLayout(geometry);
                return;
            }

            if (restored.ProposedSettings.Edges.Count < layout.Edges.Count)
                _diagnostics.Record(new DiagnosticEvent("layout_group_split_to_fit", Status:
                    $"restore,edgesBefore={layout.Edges.Count},edgesAfter={restored.ProposedSettings.Edges.Count}"));
            // 2.0 mini mode (2.0 design §3.6): the restored layout gets its mini cards' return sizes filled
            // before it becomes the live settings and is saved, on the same restored.ProposedSettings before
            // it becomes _settings.Layout.
            var restoredWithReturnSizes = LayoutPolicy.EnsureReturnSizes(restored.ProposedSettings, _settings.EnabledProviderIds,
                _modes.MiniIds.ToHashSet(StringComparer.Ordinal), CardScale.DefaultSize(CardMode.Full, AppliedScale(CardMode.Full)));
            LogReturnSizesFilledIfAny(restored.ProposedSettings, restoredWithReturnSizes);
            _settings.Layout = restoredWithReturnSizes;
            _applyingLayout = true;
            try { ApplyCardGeometry(restoredWithReturnSizes, restored.ProposedRects, geometry); }
            finally { _applyingLayout = false; }
            _layoutGeometry = CaptureCardGeometry();
            _validatedMonitors = _layoutGeometry.Monitors;
            RefreshCardGroupState(_layoutGeometry);
            RequestCoordinatorSave(restoredWithReturnSizes, "layout_restored");
        }
        catch (Exception ex)
        {
            _diagnostics.Record(new DiagnosticEvent("layout_restore_failed", Status: $"layout_unavailable,{ex.GetType().Name}"));
        }
    }

    /// <summary>Uses one minimum for every enabled card by delegating to the per-card overload below.
    /// Retained for callers that recover a layout with a shared minimum.</summary>
    internal static GeminiLayoutRecoveryResult RecoverGeminiLayout(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalWorkArea> workAreasById,
        string primaryMonitorId,
        LogicalSize usableMinimum)
    {
        var ids = enabledProviderIds.ToList();
        return RecoverGeminiLayout(layout, ids, workAreasById, primaryMonitorId,
            ids.ToDictionary(id => id, _ => usableMinimum, StringComparer.Ordinal));
    }

    /// <summary>Passes each card's minimum to LayoutPolicy.RecoverMissingMonitor, so a mini group beside a
    /// full group on one monitor is recovered against each one's own minimum instead of one minimum for the
    /// whole layout.</summary>
    internal static GeminiLayoutRecoveryResult RecoverGeminiLayout(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalWorkArea> workAreasById,
        string primaryMonitorId,
        IReadOnlyDictionary<string, LogicalSize> usableMinimumByProvider)
    {
        LayoutPolicyResult Attempt(LayoutSettings candidate)
            => LayoutPolicy.RecoverMissingMonitor(candidate, enabledProviderIds, workAreasById, primaryMonitorId, usableMinimumByProvider);

        var initial = Attempt(layout);
        if (initial.Succeeded) return new(initial, false);
        var detached = layout.Clone();
        detached.Edges.RemoveAll(edge =>
            edge.FirstProviderId == ProviderIds.Gemini || edge.SecondProviderId == ProviderIds.Gemini);
        var result = Attempt(detached);
        if (result.Succeeded) return new(result, true);

        // A malformed Gemini size or anchor cannot be recovered by splitting its group.
        // Drop only that saved placement and let the current window geometry seed a fresh one.
        detached.Cards.RemoveAll(card => card.ProviderId == ProviderIds.Gemini);
        result = Attempt(detached);
        return new(result, result.Succeeded);
    }

    private async Task ResetCardPositionsAsync()
    {
        if (_closing) return;
        InterruptActiveGesture("layout_reset");
        _scaleSession = null;
        if (!TryCaptureGeometry(out var geometry)) return;
        RecoverLayoutToDisplays(geometry, "reset");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Resets the clicked card's mode-specific scale and size. A docked card resizes its group through
    /// <see cref="LayoutPolicy.ResizeGroup"/>; rejected proposals leave the layout unchanged and are logged.
    /// </summary>
    private async Task ResetCardSizeAsync(string providerId)
    {
        if (_closing || !_windows.ContainsKey(providerId)) return;
        InterruptActiveGesture("size_reset");
        // 2.0 mini mode (2.0 design §5.9): ↺ on a mini card resets the mini scale and the mini size; ↺ on a full
        // card does the 1.1 thing and never touches mini cards - each mode's ↺ acts only on that card's own mode.
        var mode = _modes.Of(providerId);
        if (AppliedScale(mode) != CardScale.DefaultPercent)
        {
            _scaleSettle.CancelPending();
            _scaleSession = null;
            ApplyCardScale(mode, CardScale.DefaultPercent, "reset_size", persistNow: true);
        }
        if (!TryCaptureGeometry(out var geometry)) return;
        var defaultSize = CardScale.DefaultSize(mode, CardScale.DefaultPercent);
        // Match manual resizing: a parked group can remain partly outside the work area.
        var resized = LayoutPolicy.ResizeGroup(_settings.Layout, _settings.EnabledProviderIds, geometry.Rects,
            providerId, defaultSize, WorkAreaFor(geometry, providerId), UsableMinimumFor(providerId),
            requireInsideWorkArea: false);
        if (!resized.Succeeded || resized.ProposedSettings is null)
        {
            _diagnostics.Record(new DiagnosticEvent("layout_reset_size_rejected", providerId, resized.SafeErrorCode));
            return;
        }

        ApplyLayoutResult(resized, geometry);
        if (TryCaptureGeometry(out var applied))
        {
            _layoutGeometry = applied;
            PersistCapturedLayout(applied);
        }
        _diagnostics.Record(new DiagnosticEvent("layout_reset_size_applied", providerId,
            Status: $"width={defaultSize.Width:0},height={defaultSize.Height:0}"));
        await Task.CompletedTask;
    }

    private LayoutSettings AddDefaultPlacementsForMissingCards(LayoutSettings existing, CardLayoutGeometrySnapshot geometry)
    {
        var layout = existing.Clone();
        foreach (var providerId in _settings.EnabledProviderIds.Where(id => layout.Cards.All(card => card.ProviderId != id)))
        {
            var area = geometry.Monitors[geometry.MonitorByProvider[providerId]].WorkArea;
            var seed = new LayoutSettings
            {
                CardWidth = geometry.Rects[providerId].Width,
                CardHeight = geometry.Rects[providerId].Height
            };
            var captured = LayoutPolicy.CaptureAnchors(seed, [providerId],
                new Dictionary<string, LogicalRect>(StringComparer.Ordinal) { [providerId] = geometry.Rects[providerId] },
                new Dictionary<string, LogicalWorkArea>(StringComparer.Ordinal) { [providerId] = area });
            if (captured.Succeeded && captured.ProposedSettings is not null)
                layout.Cards.AddRange(captured.ProposedSettings.Cards.Select(card => card.Clone()));
            else
                _diagnostics.Record(new DiagnosticEvent("layout_default_placement_capture_failed", providerId, captured.SafeErrorCode));
        }
        return layout;
    }

    /// <summary>
    /// Restores cards from saved anchors into current work areas. Missing monitors map to the primary, undersized
    /// cards are raised to their scaled minimum, and groups that no longer fit are split so title rows remain
    /// reachable. Used by Reset window positions and display-change handling.
    /// </summary>
    private bool RecoverLayoutToDisplays(CardLayoutGeometrySnapshot geometry, string reason)
    {
        LogInferredPrimary(geometry);
        // Apply each mode's minimum to current and remembered sizes.
        var sized = LayoutPolicy.EnsureMinimumSizes(_settings.Layout, MinimumSizesByProvider(),
            CardScale.MinimumSize(CardMode.Full, AppliedScale(CardMode.Full)),
            CardScale.MinimumSize(CardMode.Mini, AppliedScale(CardMode.Mini)));
        var layout = AddDefaultPlacementsForMissingCards(sized, geometry);
        var recovered = LayoutPolicy.RecoverMissingMonitor(layout, _settings.EnabledProviderIds,
            WorkAreasById(geometry), geometry.PrimaryMonitorId, UsableMinimumsByProvider());
        if (!recovered.Succeeded || recovered.ProposedSettings is null)
        {
            _diagnostics.Record(new DiagnosticEvent(reason == "reset" ? "layout_reset_rejected" : "layout_display_recovery_rejected",
                Status: recovered.SafeErrorCode));
            return false;
        }

        if (recovered.ProposedSettings.Edges.Count < layout.Edges.Count)
            _diagnostics.Record(new DiagnosticEvent("layout_group_split_to_fit", Status:
                $"{reason},edgesBefore={layout.Edges.Count},edgesAfter={recovered.ProposedSettings.Edges.Count}"));
        ApplyLayoutResult(recovered, geometry);
        if (TryCaptureGeometry(out var applied))
        {
            _layoutGeometry = applied;
            _validatedMonitors = applied.Monitors;
            PersistCapturedLayout(applied);
        }
        _diagnostics.Record(new DiagnosticEvent("layout_recovered_to_displays", Status:
            $"reason={reason},cards={recovered.ProposedRects.Count},edges={recovered.ProposedSettings.Edges.Count},monitors={geometry.Monitors.Count}"));
        return true;
    }

    private void LogInferredPrimary(CardLayoutGeometrySnapshot geometry)
    {
        if (!geometry.PrimaryInferred || _primaryInferredLogged) return;
        _primaryInferredLogged = true;
        _diagnostics.Record(new DiagnosticEvent("layout_primary_monitor_inferred",
            Status: $"monitor={geometry.PrimaryMonitorId},monitors={geometry.Monitors.Count}"));
    }

    private void OnScreensChanged(object? sender, EventArgs e) => RequestDisplayRevalidation("screens_changed");

    private void OnCardScalingChanged(object? sender, EventArgs e) => RequestDisplayRevalidation("scaling_changed");

    private void RequestDisplayRevalidation(string source)
    {
        if (_closing) return;
        _diagnostics.Record(new DiagnosticEvent("display_change_signaled", Status: source));
        _displayChanges.Request(source);
    }

    /// <summary>
    /// Revalidates layout after display changes settle. A burst of
    /// display events settles, the live layout is revalidated: when a saved monitor is gone or any monitor's work
    /// area, bounds or scale changed since the layout was last restored, every card is restored from its saved
    /// anchors (RecoverLayoutToDisplays, the same recovery as Reset). Never during a gesture: it is deferred until
    /// one more quiet period after the gesture.
    /// </summary>
    private void RevalidateLayoutForDisplays(string source, int signals)
    {
        // End an active peek before display revalidation changes card placement or size.
        EndPeekForLayoutBusy();
        if (_closing || _windows.Count == 0 || !_layoutInitialized)
        {
            _diagnostics.Record(new DiagnosticEvent("display_revalidation_skipped", Status:
                $"source={source},closing={_closing},cards={_windows.Count},initialized={_layoutInitialized}"));
            return;
        }
        if (_gestures.IsActive || _leadWindow is not null)
        {
            _diagnostics.Record(new DiagnosticEvent("display_revalidation_deferred", _leadWindow?.ProviderId, $"source={source},active_gesture"));
            _displayChanges.Request("deferred_active_gesture");
            return;
        }
        if (!TryCaptureGeometry(out var geometry)) return;
        var decision = DecideDisplayRevalidation(_settings.Layout, _validatedMonitors, geometry);
        _diagnostics.Record(new DiagnosticEvent("display_revalidated", Status:
            $"source={source},signals={signals},decision={decision},monitors={geometry.Monitors.Count}"));
        if (decision == DisplayLayoutDecision.Unchanged)
        {
            _layoutGeometry = geometry;
            return;
        }
        _scaleSession = null;
        RecoverLayoutToDisplays(geometry, decision == DisplayLayoutDecision.MonitorRemoved ? "monitor_removed" : "monitors_changed");
    }

    internal enum DisplayLayoutDecision { Unchanged, MonitorRemoved, MonitorsChanged }

    /// <summary>MonitorRemoved when a card's saved monitor is not among the current ones;
    /// MonitorsChanged when the monitors differ in any way (set, bounds, work area, scale) from those the layout was
    /// last restored against, or none were recorded; Unchanged otherwise.</summary>
    internal static DisplayLayoutDecision DecideDisplayRevalidation(LayoutSettings layout,
        IReadOnlyDictionary<string, CardLayoutMonitorTransform>? validatedMonitors, CardLayoutGeometrySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(current);
        if (layout.Cards.Any(card => card.MonitorId is { } id && !current.Monitors.ContainsKey(id)))
            return DisplayLayoutDecision.MonitorRemoved;
        if (validatedMonitors is null || !CardLayoutGeometryComparison.MonitorsEqual(validatedMonitors, current.Monitors))
            return DisplayLayoutDecision.MonitorsChanged;
        return DisplayLayoutDecision.Unchanged;
    }
}

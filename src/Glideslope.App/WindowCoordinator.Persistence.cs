using System.Text.Json;
using Glideslope.Core;

namespace Glideslope.App;

// Persists committed settings and the live card layout.
internal sealed partial class WindowCoordinator
{
    // Settings-store contents after load and each commit. Saves write only owned fields and skip unchanged layouts.
    private AppSettings _persisted = AppSettings.CreateDefault();
    // The latest captured layout not yet written, and whether a coordinator save (layout and card scale) is due.
    private LayoutSettings? _pendingLayoutSave;
    private bool _coordinatorSaveRequested;
    // The revision of the last commit that changed a field the Settings window
    // edits. Commits after it are layout or card-size saves only, which a Settings request is rebased over.
    private long _lastSettingsFieldsRevision;

    /// <summary>When snapping is off, remove docking edges without changing card placements. Otherwise return the
    /// original layout. Applied at startup and whenever settings or a layout are saved.</summary>
    internal static LayoutSettings LayoutForSnapSetting(LayoutSettings layout, bool snapping)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (snapping || layout.Edges.Count == 0) return layout;
        var detached = layout.Clone();
        detached.Edges.Clear();
        return detached;
    }

    /// <summary>Removes disabled providers and, when snapping is off, all docking edges. If reconciliation fails,
    /// retain the layout and report the issue so store validation can decide whether to accept it.</summary>
    internal static LayoutSettings LayoutForSettings(LayoutSettings layout, IEnumerable<string> enabledProviderIds,
        bool snapping, out string? reconcileIssue)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var reconciled = LayoutPolicy.ReconcileEnabledCards(layout, enabledProviderIds);
        reconcileIssue = reconciled.Succeeded ? null : reconciled.SafeErrorCode;
        var basis = reconciled.Succeeded && reconciled.ProposedSettings is not null ? reconciled.ProposedSettings : layout;
        return LayoutForSnapSetting(basis, snapping);
    }

    /// <summary>Captures the live layout's anchors and queues a coordinator save. Returns false when the capture is
    /// rejected (a broken group: size mismatch or overlap); card position does not reject it.</summary>
    private bool PersistCapturedLayout(CardLayoutGeometrySnapshot geometry)
    {
        if (!CaptureLayoutForSave(geometry)) return false;
        _ = SaveLayoutSettingsAsync("layout_captured");
        return true;
    }

    /// <summary>Captures layout without starting a save so exit can write it while holding the gate.</summary>
    private bool CaptureLayoutForSave(CardLayoutGeometrySnapshot geometry)
    {
        var capture = LayoutPolicy.CaptureAnchors(_settings.Layout, _settings.EnabledProviderIds, geometry.Rects,
            WorkAreasByProvider(geometry));
        if (!capture.Succeeded || capture.ProposedSettings is null)
        {
            _diagnostics.Record(new DiagnosticEvent("layout_capture_rejected", Status: capture.SafeErrorCode));
            return false;
        }
        // Fill missing full-size return values for mini cards before using or saving the captured layout.
        var filled = LayoutPolicy.EnsureReturnSizes(capture.ProposedSettings, _settings.EnabledProviderIds,
            _modes.MiniIds.ToHashSet(StringComparer.Ordinal), CardScale.DefaultSize(CardMode.Full, AppliedScale(CardMode.Full)));
        LogReturnSizesFilledIfAny(capture.ProposedSettings, filled);
        _settings.Layout = filled;
        _pendingLayoutSave = filled.Clone();
        _coordinatorSaveRequested = true;
        return true;
    }

    /// <summary>Logs the number of return sizes filled and a representative size when the capture adds any.</summary>
    private void LogReturnSizesFilledIfAny(LayoutSettings before, LayoutSettings after)
    {
        var beforeById = before.Cards.ToDictionary(card => card.ProviderId, StringComparer.Ordinal);
        var filledCards = after.Cards.Where(card =>
            beforeById.TryGetValue(card.ProviderId, out var priorCard) && priorCard.FullWidth == 0 && card.FullWidth != 0).ToList();
        if (filledCards.Count == 0) return;
        var sample = filledCards[0];
        _diagnostics.Record(new DiagnosticEvent("layout_return_sizes_filled", Status:
            $"count={filledCards.Count},size={sample.FullWidth:0}x{sample.FullHeight:0}"));
    }

    /// <summary>Asks for a coordinator save (layout and card scale). <paramref name="layout"/>
    /// null saves the card scale with the last written layout.</summary>
    private void RequestCoordinatorSave(LayoutSettings? layout, string source)
    {
        if (layout is not null) _pendingLayoutSave = layout.Clone();
        _coordinatorSaveRequested = true;
        _ = SaveLayoutSettingsAsync(source);
    }

    private async Task SaveLayoutSettingsAsync(string source)
    {
        CoordinatorIntentGate.GateLease lease;
        try
        {
            lease = await _intentGate.EnterAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException ex)
        {
            _diagnostics.Record(new DiagnosticEvent("layout_save_skipped", Status: $"source={source},gate_disposed,{ex.GetType().Name}"));
            return;
        }
        await using (lease)
        {
            if (_closing)
            {
                _diagnostics.Record(new DiagnosticEvent("layout_save_skipped", Status: $"source={source},closing"));
                return;
            }
            await WriteCoordinatorSaveUnderGateAsync(source).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Writes coordinator-owned fields, layout, and card scale while holding the gate. Capture the newest layout
    /// inside the gate and take other fields from current settings so concurrent gesture or Settings changes survive.
    /// Normalize for current providers and Snap setting, then adopt only the revision; skip an unchanged candidate.
    /// </summary>
    private async Task WriteCoordinatorSaveUnderGateAsync(string source)
    {
        if (!_coordinatorSaveRequested)
        {
            _diagnostics.Record(new DiagnosticEvent("layout_save_coalesced", Status: $"source={source}"));
            return;
        }
        var pending = _pendingLayoutSave;
        _pendingLayoutSave = null;
        _coordinatorSaveRequested = false;
        var candidate = _settings.Clone();
        candidate.Layout = LayoutForSettings(pending ?? _persisted.Layout, candidate.EnabledProviderIds,
            candidate.SnapToScreenEdge, out var issue);
        if (issue is not null)
            _diagnostics.Record(new DiagnosticEvent("layout_reconcile_failed", Status: $"source={source},{issue}"));
        if (SettingsFingerprint(candidate) == SettingsFingerprint(_persisted))
        {
            _diagnostics.Record(new DiagnosticEvent("layout_save_unchanged", Status: $"source={source},revision={_settings.Revision}"));
            return;
        }
        var expectedRevision = _settings.Revision;
        var save = await _settingsStore.SaveAsync(candidate, expectedRevision).ConfigureAwait(true);
        if (!save.Succeeded)
        {
            _diagnostics.Record(new DiagnosticEvent("layout_save_failed", Status: $"{save.IssueCode},source={source}"));
            return;
        }
        candidate.Revision = save.CommittedRevision ?? candidate.Revision;
        _settings.Revision = candidate.Revision;
        _persisted = candidate;
        // Keep the Settings window synchronized with layout-save revisions.
        PushCommittedSettingsToWindow("layout_saved");
        // 2.0 mini mode (2.0 design §7): fullScale/miniScale replace the single cardScale field.
        _diagnostics.Record(new DiagnosticEvent("layout_saved", Status:
            $"source={source},revision={candidate.Revision},cards={candidate.Layout.Cards.Count},edges={candidate.Layout.Edges.Count}," +
            $"fullScale={CardScale.PercentFor(candidate, CardMode.Full)},miniScale={CardScale.PercentFor(candidate, CardMode.Mini)}"));
    }

    private static readonly JsonSerializerOptions FingerprintJson = new(JsonSerializerDefaults.Web);

    /// <summary>Settings serialized with the revision zeroed, to tell whether a coordinator
    /// save would change anything on disk.</summary>
    internal static string SettingsFingerprint(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        copy.Revision = 0;
        return JsonSerializer.Serialize(copy, FingerprintJson);
    }
}

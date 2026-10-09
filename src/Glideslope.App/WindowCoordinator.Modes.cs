using System.Collections.Immutable;
using Glideslope.Core;

namespace Glideslope.App;

// WindowCoordinator, full and mini mode: the title-row toggle, the one place a card's mode changes
// (ApplyModesAndGeometry), the mode change a dock makes, and the mixed-group check.
// Governed by 2.0 design §5.3 to §5.7 and §5.10.
internal sealed partial class WindowCoordinator
{
    /// <summary>2.0 mini mode (2.0 design §5.10): how many groups <see cref="LayoutPolicy.PrepareForMode"/>
    /// restored to a remembered full size when starting full, for the card_mode_startup log line.
    /// LayoutPolicy.PrepareForMode reports only the resulting settings, not a count, so this compares the
    /// layout from just before that call against the enabled providers: a group (by its component membership)
    /// counts once when any of its members held a non-zero FullWidth going in.</summary>
    internal static int CountGroupsRestoredToFullSize(LayoutSettings before, IEnumerable<string> enabledProviderIds)
    {
        ArgumentNullException.ThrowIfNull(before);
        var enabled = enabledProviderIds.ToArray();
        var cardsById = before.Cards.ToDictionary(card => card.ProviderId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var providerId in enabled)
        {
            if (!seen.Add(providerId)) continue;
            var group = LayoutPolicy.GetComponentProviderIds(before, enabled, providerId);
            foreach (var member in group) seen.Add(member);
            if (group.Any(member => cardsById.TryGetValue(member, out var card) && card.FullWidth != 0)) count++;
        }
        return count;
    }

    /// <summary>
    /// 2.0 mini mode (2.0 design §5.3): the only place a card's mode changes. Order, with _applyingLayout set for
    /// the whole method: CardModeApplier.Apply for the changed cards (mode, then scale, then minimum - the one
    /// safe order, 2.0 design §5.4), then the caller's geometry (ApplyCardGeometry), then the layout, then this
    /// coordinator's own mode bookkeeping (_modes.Set). Callers are the toggle (§5.5, every member of the
    /// switched group maps to the clicked mode) and a dock that changes mode (§5.6, DockModeChanges' result).
    /// </summary>
    private void ApplyModesAndGeometry(IReadOnlyDictionary<string, CardMode> changes, LayoutPolicyResult result,
        CardLayoutGeometrySnapshot geometry, string reason)
    {
        var fromByCard = changes.Keys.ToDictionary(id => id, id => _modes.Of(id), StringComparer.Ordinal);
        var edgesBefore = _settings.Layout.Edges.Count;
        _applyingLayout = true;
        try
        {
            var targets = changes
                .Where(pair => _windows.ContainsKey(pair.Key))
                .Select(pair => ((ICardModeWindow)_windows[pair.Key], pair.Value, AppliedScale(pair.Value)))
                .ToArray();
            // Apply the final minimum after geometry to avoid an intermediate resize when a group grows.
            CardModeApplier.ApplyBeforeGeometry(targets);
            ApplyCardGeometry(result.ProposedSettings!, result.ProposedRects, geometry);
            CardModeApplier.ApplyMinimum(targets);
            _settings.Layout = result.ProposedSettings!;
            foreach (var group in changes.GroupBy(pair => pair.Value, pair => pair.Key))
                _modes.Set(group, group.Key);
        }
        finally { _applyingLayout = false; }

        RefreshCardGroupState(geometry with { Rects = result.ProposedRects });
        UpdatePeekProbeTimer();

        var sample = changes.Keys.First();
        var sampleCard = result.ProposedSettings!.Cards.First(card => card.ProviderId == sample);
        var split = result.ProposedSettings.Edges.Count < edgesBefore;
        _diagnostics.Record(new DiagnosticEvent("card_mode_switched", Status:
            $"reason={reason},cards={string.Join('|', changes.Keys.Order(StringComparer.Ordinal))},from={fromByCard[sample]}," +
            $"to={changes[sample]},groupSize={changes.Count},newSize={sampleCard.Width:0}x{sampleCard.Height:0}," +
            $"returnSize={sampleCard.FullWidth:0}x{sampleCard.FullHeight:0},split={split}"));
    }

    /// <summary>2.0 mini mode (2.0 design §5.6): the incoming ids (a dock candidate's IncomingProviderIds) whose
    /// mode differs from <paramref name="targetMode"/> - the destination's mode - each mapped to that target
    /// mode. Empty when the incoming group is already the target mode, in which case the commit needs no mode
    /// change at all.</summary>
    internal static IReadOnlyDictionary<string, CardMode> DockModeChanges(
        IEnumerable<string> incomingProviderIds, CardMode targetMode, Func<string, CardMode> modeOf)
    {
        ArgumentNullException.ThrowIfNull(incomingProviderIds);
        ArgumentNullException.ThrowIfNull(modeOf);
        var changes = new Dictionary<string, CardMode>(StringComparer.Ordinal);
        foreach (var providerId in incomingProviderIds)
            if (modeOf(providerId) != targetMode) changes[providerId] = targetMode;
        return changes;
    }

    /// <summary>2.0 mini mode (2.0 design §5.7): the components (LayoutPolicy.GetComponentProviderIds) whose
    /// members disagree on mode - the invariant §5.6 relies on ("a docked group is always all full or all
    /// mini") checked directly against the live layout and the live mode map.</summary>
    internal static IReadOnlyList<ImmutableArray<string>> MixedModeGroups(
        LayoutSettings layout, IEnumerable<string> enabledProviderIds, Func<string, CardMode> modeOf)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(modeOf);
        var enabled = enabledProviderIds.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ImmutableArray<string>>();
        foreach (var providerId in enabled)
        {
            if (!seen.Add(providerId)) continue;
            var group = LayoutPolicy.GetComponentProviderIds(layout, enabled, providerId);
            foreach (var member in group) seen.Add(member);
            if (group.Select(modeOf).Distinct().Count() > 1) result.Add(group);
        }
        return result;
    }

    /// <summary>
    /// 2.0 mini mode (2.0 design §5.5): the toggle button in the title row switches the clicked card's whole
    /// group between full and mini. Order: finish any active gesture (its button is up, since the toggle is a
    /// plain click), flush a pending card-size settle first so its save captures the pre-toggle layout instead
    /// of racing it, then clear the scale session; capture geometry; ask LayoutPolicy.SwitchGroupMode; on
    /// accept, switch every member's mode and geometry together (ApplyModesAndGeometry) and persist, logging the
    /// split separately when the fit had to split the group (matching every other restore-like operation).
    /// </summary>
    private void OnModeToggleRequested(object? sender, EventArgs e)
    {
        if (_closing || sender is not ProviderUsageCardWindow window) return;
        var from = _modes.Of(window.ProviderId);
        _diagnostics.Record(new DiagnosticEvent("card_mode_toggle_requested", window.ProviderId, $"from={from}"));
        InterruptActiveGesture("mode_toggle");
        // 2.0 design §5.5 step 2: a pending card-size settle is flushed first, so its save captures the
        // pre-toggle layout rather than racing the toggle's own save.
        _scaleSettle.Flush();
        _scaleSession = null;
        // The interrupt can commit a dock that already switched this card. The
        // click asked for the mode opposite the one it saw; when the dock already gave the card that mode, the
        // request is met and switching again would undo it (or, going to Full, resize the group to the fallback).
        if (_modes.Of(window.ProviderId) != from)
        {
            _diagnostics.Record(new DiagnosticEvent("card_mode_switch_rejected", window.ProviderId,
                $"mode_changed_by_interrupt,now={_modes.Of(window.ProviderId)}"));
            return;
        }
        if (!TryCaptureGeometry(out var geometry)) return;
        var to = from == CardMode.Full ? CardMode.Mini : CardMode.Full;
        var miniSize = CardScale.DefaultSize(CardMode.Mini, AppliedScale(CardMode.Mini));
        var fullFallbackSize = CardScale.DefaultSize(CardMode.Full, AppliedScale(CardMode.Full));
        var result = LayoutPolicy.SwitchGroupMode(_settings.Layout, _settings.EnabledProviderIds, geometry.Rects,
            window.ProviderId, to, miniSize, fullFallbackSize, WorkAreaFor(geometry, window.ProviderId),
            CardScale.UsableMinimum(to, AppliedScale(to)));
        if (!result.Succeeded || result.ProposedSettings is null)
        {
            _diagnostics.Record(new DiagnosticEvent("card_mode_switch_rejected", window.ProviderId, result.SafeErrorCode));
            return;
        }
        var members = LayoutPolicy.GetComponentProviderIds(_settings.Layout, _settings.EnabledProviderIds, window.ProviderId);
        var changes = members.ToDictionary(id => id, _ => to, StringComparer.Ordinal);
        var edgesBefore = _settings.Layout.Edges.Count;
        ApplyModesAndGeometry(changes, result, geometry, "toggle");
        if (TryCaptureGeometry(out var applied))
        {
            _layoutGeometry = applied;
            PersistCapturedLayout(applied);
        }
        if (result.ProposedSettings.Edges.Count < edgesBefore)
            _diagnostics.Record(new DiagnosticEvent("layout_group_split_to_fit", window.ProviderId, Status:
                $"mode_toggle,edgesBefore={edgesBefore},edgesAfter={result.ProposedSettings.Edges.Count}"));
    }
}

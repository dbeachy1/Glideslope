namespace Glideslope.Core;

// LayoutPolicy, the 2.0 mini mode operations and card size: switching a group's mode, preparing the
// startup layout for a mode, filling mini cards' return sizes, scaling groups and return sizes, and
// raising saved sizes to the current minimums. Governed by the 2.0 design §3 and card size 1.1.
public static partial class LayoutPolicy
{
    /// <summary>
    /// 2.0 mini mode (2.0 design §3.3): switches <paramref name="providerId"/>'s whole group to
    /// <paramref name="toMode"/>. <paramref name="usableMinimum"/> is <paramref name="toMode"/>'s usable
    /// minimum. Placement keeps the clicked card's top-right corner (so a second click undoes the first
    /// exactly when nothing had to move to stay reachable), then the group is fitted into
    /// <paramref name="workArea"/> by the same rule every restore uses (<see cref="RecoverMissingMonitor"/>'s
    /// per-group step, shared through <see cref="RecoverGroup"/>): a group that fits stays; one too big is
    /// shrunk toward the minimum; one still too big is split into single cards, each fitted on its own; a
    /// single card still too big is pinned to the work area's top-left.
    /// §3.7: every card remembers its own size in each mode. Going to Mini uses the clicked card's own
    /// remembered mini size when it has one (else <paramref name="miniSize"/>, the caller's mini default), the
    /// mirror of how going to Full already uses the clicked card's remembered full size (else
    /// <paramref name="fullFallbackSize"/>). Either switch stamps the group's pre-switch size as the OTHER
    /// mode's remembered size for every member, so a card can return to exactly where it was left in either
    /// mode, indefinitely.
    /// </summary>
    public static LayoutPolicyResult SwitchGroupMode(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        string providerId,
        CardMode toMode,
        LogicalSize miniSize,
        LogicalSize fullFallbackSize,
        LogicalWorkArea workArea,
        LogicalSize usableMinimum)
    {
        var graph = ValidateInput(layout, enabledProviderIds, currentRects);
        if (graph.Error is not null) return Reject(graph.Error);
        if (!graph.Nodes.Contains(providerId)) return Reject("layout_unknown_card");
        var group = Component(layout, graph.Nodes, providerId);
        var groupSizes = group.Select(id => currentRects[id].Size).Distinct().ToArray();
        if (groupSizes.Length != 1) return Reject("layout_group_size_mismatch");
        var currentSize = groupSizes[0];

        LogicalSize newSize;
        // Not a LogicalSize: it is the sentinel stored in CardLayoutSettings.FullWidth/FullHeight, which (unlike
        // a window size) can legitimately be (0, 0) meaning "no remembered full size" - see EffectiveFullSize.
        (double Width, double Height) newFullSize;
        // 2.0 mini mode (2.0 design §3.7): the mirror sentinel for CardLayoutSettings.MiniWidth/MiniHeight - see
        // EffectiveMiniSize.
        (double Width, double Height) newMiniSize;
        if (toMode == CardMode.Mini)
        {
            // A coordinator defect guard (2.0 design §3.3 step 2): the coordinator always switches to the
            // OTHER mode, so a group whose members already carry a remembered full size (meaning they are
            // already mini) reaching here is a bug upstream, not a user-reachable state.
            if (group.Any(id => EffectiveFullSize(layout, id).Width != 0))
                return Reject("layout_mode_already_mini");
            // §3.7: the clicked card's own remembered mini size wins over the caller's mini default, so a card
            // that has been mini before returns to the size it was left at instead of snapping back to the
            // default every time it goes mini; raised to usableMinimum the same way the Full branch below
            // raises its restored size.
            var (clickedMiniWidth, clickedMiniHeight) = EffectiveMiniSize(layout, providerId);
            var restoredMini = clickedMiniWidth != 0 ? new LogicalSize(clickedMiniWidth, clickedMiniHeight) : miniSize;
            newSize = new LogicalSize(Math.Max(restoredMini.Width, usableMinimum.Width), Math.Max(restoredMini.Height, usableMinimum.Height));
            newFullSize = (currentSize.Width, currentSize.Height);
            newMiniSize = (0, 0);
        }
        else
        {
            var (clickedFullWidth, clickedFullHeight) = EffectiveFullSize(layout, providerId);
            var restored = clickedFullWidth != 0 ? new LogicalSize(clickedFullWidth, clickedFullHeight) : fullFallbackSize;
            newSize = new LogicalSize(Math.Max(restored.Width, usableMinimum.Width), Math.Max(restored.Height, usableMinimum.Height));
            newFullSize = (0, 0);
            // §3.7: every member's remembered mini size becomes the group's current (mini) size, the mirror of
            // newFullSize above in the To Mini branch.
            newMiniSize = (currentSize.Width, currentSize.Height);
        }

        // Step 4: reflow from the clicked card, keeping its top-right corner (the toggle sits at the right
        // end of the title row, so it stays under the pointer across the switch).
        var clicked = currentRects[providerId];
        var anchorRect = new LogicalRect(clicked.Right - newSize.Width, clicked.Y, newSize.Width, newSize.Height);
        ReflowResult reflowed;
        try
        {
            reflowed = ReflowComponent(layout, group, providerId, anchorRect, newSize);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Reject("layout_invalid_position");
        }
        if (reflowed.Error is not null) return Reject(reflowed.Error);

        var proposedSettings = layout.Clone();
        // Every member takes the new size and the new remembered full size before the anchors are captured
        // below, so EffectiveSize (read inside RecoverGroup) sees the post-switch size for this whole group.
        // §3.7: the new remembered mini size is stamped alongside it.
        foreach (var member in group)
        {
            var card = GetOrCreateCard(proposedSettings, member);
            card.FullWidth = newFullSize.Width;
            card.FullHeight = newFullSize.Height;
            card.MiniWidth = newMiniSize.Width;
            card.MiniHeight = newMiniSize.Height;
        }
        // Step 5: capture the group's anchors from the reflowed rects against workArea (SetPlacement, which
        // clamps to [0, 1]) before recovery, so the recovery below starts from where the toggle just placed
        // the group rather than from its pre-toggle position.
        CaptureComponent(proposedSettings, reflowed.Rects, group, workArea);

        var usableMinimumByProvider = group.ToDictionary(id => id, _ => usableMinimum, StringComparer.Ordinal);
        var groupRects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal);
        var groupMonitorByProvider = new Dictionary<string, string>(StringComparer.Ordinal);
        var issue = RecoverGroup(proposedSettings, proposedSettings, group, workArea, workArea.MonitorId, usableMinimum,
            usableMinimumByProvider, groupRects, groupMonitorByProvider);
        if (issue is not null) return Reject(issue);

        // Point 6: every other card keeps its rect and settings entry; only the switched group's rects change.
        var proposedRects = currentRects.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (member, rect) in groupRects)
        {
            proposedRects[member] = rect;
            var card = GetOrCreateCard(proposedSettings, member);
            SetPlacement(card, rect, workArea);
        }
        return Accept(proposedSettings, proposedRects);
    }

    /// <summary>
    /// 2.0 mini mode (2.0 design §3.4): applied at startup, after the display-recovery normalization and
    /// before any card is created, so every card is created directly in <paramref name="startMode"/>. Only
    /// components with an existing saved entry are touched; a card the window creates later (first launch, a
    /// provider just enabled) is not a component here and gets its mode from the window, as today.
    /// </summary>
    public static LayoutSettings PrepareForMode(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        CardMode startMode,
        LogicalSize miniSize)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(enabledProviderIds);
        var enabled = enabledProviderIds.ToHashSet(StringComparer.Ordinal);
        var result = layout.Clone();
        var cardsById = result.Cards.ToDictionary(card => card.ProviderId, StringComparer.Ordinal);
        var present = cardsById.Keys.Where(enabled.Contains).ToHashSet(StringComparer.Ordinal);
        foreach (var group in Components(result, present))
        {
            var root = ChooseGroupRoot(result, group);
            var rootCard = cardsById[root];
            if (startMode == CardMode.Full)
            {
                // "when the group root has a non-zero FullWidth": otherwise the group is left as saved (it
                // was already full, or has no remembered full size to restore).
                if (rootCard.FullWidth == 0) continue;
                var fullWidth = rootCard.FullWidth;
                var fullHeight = rootCard.FullHeight;
                // 2.0 mini mode (2.0 design §3.7): the group was saved mini, so its saved live size (the
                // root's Width/Height) is what it remembers to return to next time it goes mini. Read before
                // any member (including the root itself) is overwritten below.
                var miniWidth = rootCard.Width;
                var miniHeight = rootCard.Height;
                foreach (var member in group)
                {
                    var card = cardsById[member];
                    card.Width = fullWidth;
                    card.Height = fullHeight;
                    card.FullWidth = 0;
                    card.FullHeight = 0;
                    card.MiniWidth = miniWidth;
                    card.MiniHeight = miniHeight;
                }
            }
            else
            {
                // Preserve saved mini dimensions for groups already in mini mode. Only groups saved in full
                // mode receive a mini default here.
                if (rootCard.FullWidth != 0) continue;

                // Taking the root's values (rather than each member's own) makes a hand-edited group with
                // mismatched sizes restore as one group instead of failing the restore.
                var fullWidth = rootCard.Width;
                var fullHeight = rootCard.Height;
                // §3.7: a group saved full takes its own remembered mini size (the root's MiniWidth/MiniHeight)
                // when it has one, else the caller's mini default - the mirror of the Full branch above using
                // the root's remembered full size, else fullFallbackSize.
                var width = rootCard.MiniWidth != 0 ? rootCard.MiniWidth : miniSize.Width;
                var height = rootCard.MiniHeight != 0 ? rootCard.MiniHeight : miniSize.Height;
                foreach (var member in group)
                {
                    var card = cardsById[member];
                    card.FullWidth = fullWidth;
                    card.FullHeight = fullHeight;
                    card.Width = width;
                    card.Height = height;
                    // §3.7: MiniWidth/MiniHeight is cleared to 0, matching the mini-card invariant (a mini
                    // card's own MiniWidth/MiniHeight is always 0; its live Width/Height IS its mini size).
                    card.MiniWidth = 0;
                    card.MiniHeight = 0;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Every mini card's layout entry carries a non-zero return size. A newly created mini card has no saved
    /// full dimensions, so its default return size must be recorded for restoring Full mode and docking.
    /// Only cards that have a saved entry, are enabled and are in <paramref name="miniProviderIds"/> are
    /// touched, and only when their FullWidth is still 0; a card that already carries a return size, a full
    /// card, and a card outside the set are all left exactly as they are. Per component (the same grouping
    /// every other operation uses, <see cref="Components"/>), one value fills every zero member: the group
    /// root's (<see cref="ChooseGroupRoot"/>) return size when it is non-zero, else the first non-zero
    /// member's in ordinal id order, else <paramref name="fullDefault"/>. Pure and idempotent: a second call
    /// finds no zeros left to fill.
    /// </summary>
    public static LayoutSettings EnsureReturnSizes(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlySet<string> miniProviderIds,
        LogicalSize fullDefault)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(enabledProviderIds);
        ArgumentNullException.ThrowIfNull(miniProviderIds);
        var enabled = enabledProviderIds.ToHashSet(StringComparer.Ordinal);
        var result = layout.Clone();
        var cardsById = result.Cards.ToDictionary(card => card.ProviderId, StringComparer.Ordinal);
        var present = cardsById.Keys.Where(enabled.Contains).ToHashSet(StringComparer.Ordinal);
        foreach (var group in Components(result, present))
        {
            var zeroMiniMembers = group.Where(id => miniProviderIds.Contains(id) && cardsById[id].FullWidth == 0).ToList();
            if (zeroMiniMembers.Count == 0) continue;

            // One value fills every zero member of this group: the root's return size when it has one,
            // else the first non-zero member's (any member, not only a mini one - a mismatched or
            // partly-repaired group still follows one donor), else the full default.
            var root = ChooseGroupRoot(result, group);
            var rootCard = cardsById[root];
            double fillWidth;
            double fillHeight;
            if (rootCard.FullWidth != 0)
            {
                fillWidth = rootCard.FullWidth;
                fillHeight = rootCard.FullHeight;
            }
            else
            {
                var donor = group.Where(id => cardsById[id].FullWidth != 0).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
                if (donor is not null)
                {
                    fillWidth = cardsById[donor].FullWidth;
                    fillHeight = cardsById[donor].FullHeight;
                }
                else
                {
                    fillWidth = fullDefault.Width;
                    fillHeight = fullDefault.Height;
                }
            }

            foreach (var id in zeroMiniMembers)
            {
                cardsById[id].FullWidth = fillWidth;
                cardsById[id].FullHeight = fillHeight;
            }
        }
        return result;
    }

    /// <summary>
    /// Scales every card's size by <paramref name="ratio"/>, raised to
    /// at least <paramref name="minimum"/>. A connected group is resized as a unit: it is reflowed from its root with
    /// the new size and its saved gaps (the gap is a spacing between windows, not card content, so it does not
    /// scale), then placed so its bounding box keeps its top-left corner. A detached card keeps its own top-left.
    /// Placement inside a work area is not checked here: the coordinator captures the result and runs
    /// <see cref="RecoverMissingMonitor"/> on it, which applies the reachability rule.
    /// </summary>
    public static LayoutPolicyResult ScaleGroups(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        double ratio,
        LogicalSize minimum,
        IReadOnlySet<string>? onlyProviders = null)
    {
        var graph = ValidateInput(layout, enabledProviderIds, currentRects);
        if (graph.Error is not null) return Reject(graph.Error);
        if (!double.IsFinite(ratio) || ratio <= 0) return Reject("layout_invalid_scale");
        var proposedRects = currentRects.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var group in Components(layout, graph.Nodes))
        {
            // 2.0 mini mode (2.0 design §3.5): a scale applies to one mode's cards only. A group entirely
            // outside onlyProviders (the other mode) keeps its rect and size untouched; a group entirely
            // inside is scaled as before; a group straddling both is a mixed-mode group, which must never
            // exist, so it is rejected rather than silently scaling only part of it.
            if (onlyProviders is not null)
            {
                var memberCount = group.Count(onlyProviders.Contains);
                if (memberCount == 0) continue;
                if (memberCount != group.Count) return Reject("layout_group_partially_scaled");
            }
            var sizes = group.Select(providerId => currentRects[providerId].Size).Distinct().ToArray();
            if (sizes.Length != 1) return Reject("layout_group_size_mismatch");
            var root = ChooseGroupRoot(layout, group);
            ReflowResult reflowed;
            try
            {
                var size = new LogicalSize(Math.Max(sizes[0].Width * ratio, minimum.Width),
                    Math.Max(sizes[0].Height * ratio, minimum.Height));
                reflowed = ReflowComponent(layout, group, root, new LogicalRect(0, 0, size.Width, size.Height), size);
            }
            catch (ArgumentOutOfRangeException) { return Reject("layout_invalid_position"); }
            if (reflowed.Error is not null) return Reject(reflowed.Error);
            var left = group.Min(providerId => currentRects[providerId].X);
            var top = group.Min(providerId => currentRects[providerId].Y);
            var bounds = Bounds(reflowed.Rects);
            try
            {
                foreach (var (member, rect) in reflowed.Rects)
                    proposedRects[member] = rect.Translate(left - bounds.MinX, top - bounds.MinY);
            }
            catch (ArgumentOutOfRangeException) { return Reject("layout_invalid_position"); }
        }

        var proposedSettings = layout.Clone();
        foreach (var providerId in graph.Nodes)
        {
            var card = GetOrCreateCard(proposedSettings, providerId);
            card.Width = proposedRects[providerId].Width;
            card.Height = proposedRects[providerId].Height;
        }
        return Accept(proposedSettings, proposedRects);
    }

    /// <summary>
    /// 2.0 mini mode (2.0 design §3.5, §3.7): a copy of <paramref name="layout"/> in which each listed card's
    /// non-zero remembered return size for <paramref name="mode"/> is multiplied by <paramref name="ratio"/>
    /// and raised to <paramref name="minimum"/>. Used when one mode's scale changes while some cards are the
    /// other mode: their return size zooms with the scale change, so a card that changes mode later lands at
    /// the scale that was current when it changes, not the stale one from when it last changed away.
    /// <paramref name="mode"/> is the mode WHOSE scale changed: <see cref="CardMode.Full"/> (the existing,
    /// default meaning) rescales <paramref name="providerIds"/>' FullWidth/FullHeight - the remembered full
    /// size of cards that are currently mini; <see cref="CardMode.Mini"/> rescales their MiniWidth/MiniHeight -
    /// the remembered mini size of cards that are currently full.
    /// </summary>
    public static LayoutSettings ScaleReturnSizes(LayoutSettings layout, IEnumerable<string> providerIds, double ratio,
        LogicalSize minimum, CardMode mode = CardMode.Full)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(providerIds);
        var ids = providerIds.ToHashSet(StringComparer.Ordinal);
        var result = layout.Clone();
        foreach (var card in result.Cards)
        {
            if (!ids.Contains(card.ProviderId)) continue;
            if (mode == CardMode.Full)
            {
                if (card.FullWidth == 0) continue;
                card.FullWidth = Math.Max(card.FullWidth * ratio, minimum.Width);
                card.FullHeight = Math.Max(card.FullHeight * ratio, minimum.Height);
            }
            else
            {
                if (card.MiniWidth == 0) continue;
                card.MiniWidth = Math.Max(card.MiniWidth * ratio, minimum.Width);
                card.MiniHeight = Math.Max(card.MiniHeight * ratio, minimum.Height);
            }
        }
        return result;
    }

    /// <summary>A copy of <paramref name="layout"/> whose saved card
    /// sizes, and the legacy default size, are at least <paramref name="minimum"/>. Saved sizes are the real
    /// (scaled) window sizes and are otherwise taken as they are; only a size below the current scaled minimum is
    /// raised, so a restore after a scale change never fails as layout_too_small. Group members share one size,
    /// so they are raised alike and stay equal.</summary>
    public static LayoutSettings EnsureMinimumSizes(LayoutSettings layout, LogicalSize minimum)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var result = layout.Clone();
        result.CardWidth = Math.Max(result.CardWidth, minimum.Width);
        result.CardHeight = Math.Max(result.CardHeight, minimum.Height);
        foreach (var card in result.Cards)
        {
            card.Width = Math.Max(card.Width, minimum.Width);
            card.Height = Math.Max(card.Height, minimum.Height);
        }
        return result;
    }

    /// <summary>2.0 mini mode (2.0 design §3.1, §3.7): the per-card overload, for a layout that mixes full and
    /// mini groups with different minimums side by side. Each card's live Width/Height is raised to its own
    /// entry in <paramref name="minimumByProvider"/>; a non-zero FullWidth/FullHeight (a mini card's
    /// remembered full size) is raised to <paramref name="fullMinimum"/> instead, since that size is always a
    /// full size; a non-zero MiniWidth/MiniHeight (a full card's remembered mini size) is raised to
    /// <paramref name="miniMinimum"/>, the mirror rule; and the legacy CardWidth/CardHeight default is raised
    /// to <paramref name="fullMinimum"/> too, exactly as the single-minimum overload raises it.</summary>
    public static LayoutSettings EnsureMinimumSizes(LayoutSettings layout,
        IReadOnlyDictionary<string, LogicalSize> minimumByProvider, LogicalSize fullMinimum, LogicalSize miniMinimum)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(minimumByProvider);
        var result = layout.Clone();
        result.CardWidth = Math.Max(result.CardWidth, fullMinimum.Width);
        result.CardHeight = Math.Max(result.CardHeight, fullMinimum.Height);
        foreach (var card in result.Cards)
        {
            var minimum = minimumByProvider[card.ProviderId];
            card.Width = Math.Max(card.Width, minimum.Width);
            card.Height = Math.Max(card.Height, minimum.Height);
            if (card.FullWidth != 0) card.FullWidth = Math.Max(card.FullWidth, fullMinimum.Width);
            if (card.FullHeight != 0) card.FullHeight = Math.Max(card.FullHeight, fullMinimum.Height);
            // §3.7: a non-zero MiniWidth/MiniHeight (a full card's remembered mini size) is raised to the mini
            // minimum, mirroring FullWidth/FullHeight above.
            if (card.MiniWidth != 0) card.MiniWidth = Math.Max(card.MiniWidth, miniMinimum.Width);
            if (card.MiniHeight != 0) card.MiniHeight = Math.Max(card.MiniHeight, miniMinimum.Height);
        }
        return result;
    }
}

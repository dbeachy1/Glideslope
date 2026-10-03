using Avalonia.Controls;
using Avalonia.Threading;
using Glideslope.Core;

namespace Glideslope.App;

// Tracks, applies, commits, and cancels card move and resize gestures.
internal sealed partial class WindowCoordinator
{
    // The active gesture's lead window, kind and edge. LayoutGestureTracker (_gestures below) times
    // the gesture and decides when it ends; this coordinator keeps its own copy of "who and what"
    // because the tracker's contract (design doc §5.1) exposes only IsActive, not getters for the
    // in-progress lead/kind/edge - the same information Begin() was given, so the coordinator is the
    // one place that already has it. See BeginLayoutGesture for why these are only updated after
    // _gestures.Begin(...) returns, not before.
    private ProviderUsageCardWindow? _leadWindow;
    private LayoutGestureKind _leadKind;
    private WindowEdge? _leadEdge;
    // The tracker's id for the active gesture (0 when none). CommitGesture ignores a
    // settle whose id differs, since two gestures in a row on the same card share a lead.
    private long _leadGestureId;
    // A settle posted from the quiet-period timer whose commit has not run yet. An
    // interrupt or a new gesture commits it synchronously first; the posted callback then finds nothing to do.
    private GestureSettled? _settledAwaitingCommit;
    // Window design Appendix C.4/C.5: the docking graph as it was when the active gesture began. A detach
    // changes _settings.Layout at the press, so a cancel must put the graph back as well as the rects.
    private LayoutSettings? _layoutBeforeGesture;
    // Window design Appendix H.5: the geometry as it was when the active gesture began. _layoutGeometry is
    // refreshed by every accepted mid-gesture change, so a cancel restores from this instead.
    private CardLayoutGeometrySnapshot? _gestureStartGeometry;
    // Window design Appendix H.1: layout_gesture_outside_work_area is logged once per gesture, not per move.
    private bool _outsideWorkAreaLogged;
    private DateTimeOffset _gestureStartedAt;
    private readonly LayoutGestureTracker _gestures;
    // The same probe the tracker uses, kept so an interrupt can tell a drag still in
    // progress (cancel, as before) from one whose button is already up (commit).
    private readonly Func<bool> _primaryButtonHeld;
    // Design doc §5.1: 300 ms of stillness with the primary button released ends a gesture; while
    // the button is still held, the tracker re-arms instead of settling, so pausing mid-drag never
    // commits a snap or dock. This is only the fallback used when no OS-level end signal (design doc
    // §5.2, GestureEndSources.cs) arrives at all.
    private static readonly TimeSpan GestureQuietPeriod = TimeSpan.FromMilliseconds(300);

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        // Native move loops report progress through PositionChanged, not OnWindowPropertyChanged. Handle those
        // updates through the same geometry path as Width and Height changes.
        if (sender is not ProviderUsageCardWindow changed) return;
        // On Linux, the first reported position after a move request is the window manager's answer. Confirm it
        // before returning for an apply or shutdown so the platform records the actual position.
        var confirmation = _layoutPlatform.ConfirmPosition(changed, e.Point, out var requested);
        if (confirmation == PlacementConfirmation.Adjusted)
            _diagnostics.Record(new DiagnosticEvent("layout_wm_adjusted", changed.ProviderId,
                $"requested={requested.X},{requested.Y},reported={e.Point.X},{e.Point.Y},gestureActive={_leadWindow is not null}"));
        if (_closing || _applyingLayout) return;
        // A matching position is a delayed X11 echo. Consume the entry on any change so a later real move is not
        // mistaken for an echo; confirmed window-manager adjustments are also not user gestures.
        var echo = _appliedPositions.Remove(changed.ProviderId, out var applied) && applied == e.Point;
        OnCardGeometryChanged(changed, isSizeChange: false,
            isApplyEcho: echo || confirmation != PlacementConfirmation.None);
    }

    private void OnMoveDragStarted(object? sender, EventArgs e)
    {
        if (sender is not ProviderUsageCardWindow window || _closing) return;
        BeginLayoutGesture(window, LayoutGestureKind.Move, null);
    }

    /// <summary>
    /// A straight-edge drag resizes the whole group along that one dimension rather than detaching the card first.
    /// LayoutPolicy
    /// .ResizeGroup already handles an anisotropic resize correctly, since the requested size mid-
    /// gesture is read straight from the native window (an edge the user isn't dragging keeps its
    /// current value there), so no special-casing is needed here beyond starting the gesture.
    /// </summary>
    private void OnResizeDragStarted(ProviderUsageCardWindow window, WindowEdge edge)
    {
        if (_closing) return;
        BeginLayoutGesture(window, LayoutGestureKind.Resize, edge);
    }

    private void OnDetachDragStarted(object? sender, EventArgs e)
    {
        if (sender is not ProviderUsageCardWindow window || _closing) return;
        BeginLayoutGesture(window, LayoutGestureKind.Detach, null);
    }

    /// <summary>Begins a gesture. <paramref name="origin"/> says what started it: our pointer
    /// press ("pointer"), the OS move loop with no press of ours ("sizemove"), or a card change with no gesture at all
    /// ("untracked"), which passes the geometry from before that change as <paramref name="startGeometry"/>.</summary>
    private void BeginLayoutGesture(ProviderUsageCardWindow window, LayoutGestureKind kind, WindowEdge? edge,
        string origin = "pointer", CardLayoutGeometrySnapshot? startGeometry = null)
    {
        if (_closing) return;
        // Finish the previous gesture before starting another, so a completed drag is committed.
        InterruptActiveGesture("superseded");
        CardLayoutGeometrySnapshot geometry;
        if (startGeometry is not null) geometry = startGeometry;
        else if (!TryCaptureGeometry(out geometry)) return;
        // Design doc §5.2 change 2: Ctrl + body-drag pulls one card out of its snapped group. The graph
        // edges come off first (geometry unchanged); from then on the gesture is a plain Move of a one-card
        // group, which MoveGroup and LayoutCommitDecision already handle. A rejected detach degrades to Move.
        LayoutSettings? detachedLayout = null;
        if (kind == LayoutGestureKind.Detach)
        {
            var detached = LayoutPolicy.DetachCard(_settings.Layout, _settings.EnabledProviderIds, geometry.Rects,
                window.ProviderId, WorkAreaFor(geometry, window.ProviderId), UsableMinimumFor(window.ProviderId));
            if (detached.Succeeded && detached.ProposedSettings is not null) detachedLayout = detached.ProposedSettings;
            else
            {
                _diagnostics.Record(new DiagnosticEvent("layout_detach_rejected", window.ProviderId, detached.SafeErrorCode));
                kind = LayoutGestureKind.Move;
            }
        }
        // Begin can synchronously cancel an active gesture; keep its state intact until that handler returns.
        // Realign a grabbed group around the lead before computing its move; detach leaves the group unchanged.
        if (kind != LayoutGestureKind.Detach) geometry = RealignLeadGroup(window.ProviderId, geometry);
        _leadGestureId = _gestures.Begin(window.ProviderId, kind, edge, geometry.Rects);
        _leadWindow = window;
        _leadKind = kind;
        _leadEdge = edge;
        _layoutGeometry = geometry;
        _gestureStartGeometry = geometry;
        _layoutBeforeGesture = _settings.Layout.Clone();
        if (detachedLayout is not null)
        {
            _settings.Layout = detachedLayout;
            RefreshCardGroupState(geometry);
            _diagnostics.Record(new DiagnosticEvent("layout_detach_started", window.ProviderId));
        }
        _gestureStartedAt = _time.GetUtcNow();
        _outsideWorkAreaLogged = false;
        var groupSize = LayoutPolicy.GetComponentProviderIds(_settings.Layout, _settings.EnabledProviderIds, window.ProviderId).Length;
        _diagnostics.Record(new DiagnosticEvent("layout_gesture_started", window.ProviderId,
            Status: $"kind={kind},zone={(edge is null ? "body" : edge.ToString())},groupSize={groupSize},origin={origin},gesture={_leadGestureId}"));
    }

    /// <summary>Window design doc §20 R3: more than this (logical px, on any side) off the graph's shape is out of line.
    /// Above a whole pixel so DPI rounding on Windows (a card at 125 % reads back a fraction off) never triggers it.</summary>
    private const double RealignTolerance = 1.5;

    /// <summary>
    /// Rebuilds the lead's group from the lead's
    /// rectangle through the dock graph and moves any follower that is out of line back into place. On GNOME the
    /// window manager moves app-placed windows itself (§20.1), and once a group's windows overlapped, every MoveGroup
    /// of it was rejected as layout_overlap for good: the other cards never followed a drag again. Returns the
    /// geometry the gesture starts from (the realigned rects when anything moved).
    /// </summary>
    private CardLayoutGeometrySnapshot RealignLeadGroup(string leadId, CardLayoutGeometrySnapshot geometry)
    {
        var members = LayoutPolicy.GetComponentProviderIds(_settings.Layout, _settings.EnabledProviderIds, leadId);
        if (members.Length < 2) return geometry;
        var realigned = LayoutPolicy.RealignGroup(_settings.Layout, _settings.EnabledProviderIds, geometry.Rects, leadId);
        if (!realigned.Succeeded)
        {
            _diagnostics.Record(new DiagnosticEvent("layout_group_realign_rejected", leadId, realigned.SafeErrorCode));
            return geometry;
        }
        static double Offset(LogicalRect a, LogicalRect b) => new[]
        {
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y), Math.Abs(a.Width - b.Width), Math.Abs(a.Height - b.Height)
        }.Max();
        var moved = members.Where(id => id != leadId && Offset(geometry.Rects[id], realigned.ProposedRects[id]) > RealignTolerance)
            .ToDictionary(id => id, id => realigned.ProposedRects[id], StringComparer.Ordinal);
        if (moved.Count == 0) return geometry;
        var maxOffset = moved.Max(pair => Offset(geometry.Rects[pair.Key], pair.Value));
        _applyingLayout = true;
        try { ApplyCardGeometry(_settings.Layout, moved, geometry); }
        finally { _applyingLayout = false; }
        _diagnostics.Record(new DiagnosticEvent("layout_group_realigned", leadId,
            $"members={members.Length},moved={moved.Count},maxOffset={maxOffset:0.#}"));
        return geometry with { Rects = geometry.Rects.SetItems(moved) };
    }

    /// <summary>
    /// On platforms whose window manager confines app-placed windows, contain a multi-card group only after pointer
    /// release and only when a member is outside every monitor's work area. This avoids moving a lead still held by
    /// the user and leaves groups spanning monitors unchanged. Returns the geometry after any move.
    /// </summary>
    private CardLayoutGeometrySnapshot ContainLeadGroupIfConfined(string leadId, GestureEndSource source,
        CardLayoutGeometrySnapshot geometry)
    {
        if (!_layoutPlatform.WindowManagerConfinesPlacedWindows) return geometry;
        var members = LayoutPolicy.GetComponentProviderIds(_settings.Layout, _settings.EnabledProviderIds, leadId);
        if (members.Length < 2) return geometry;
        if (!members.Any(id => !OnSomeWorkArea(geometry.Rects[id], geometry))) return geometry;
        if (source != GestureEndSource.PointerRelease)
        {
            _diagnostics.Record(new DiagnosticEvent("layout_group_contain_deferred", leadId, $"source={source}"));
            return geometry;
        }
        var contained = LayoutPolicy.ContainGroup(_settings.Layout, _settings.EnabledProviderIds, geometry.Rects, leadId,
            WorkAreaFor(geometry, leadId));
        if (!contained.Succeeded)
        {
            _diagnostics.Record(new DiagnosticEvent("layout_group_contain_rejected", leadId, contained.SafeErrorCode));
            return geometry;
        }
        var before = geometry.Rects[leadId];
        var after = contained.ProposedRects[leadId];
        if (before == after) return geometry;
        ApplyLayoutResult(contained, geometry);
        _diagnostics.Record(new DiagnosticEvent("layout_group_contained", leadId,
            $"dx={after.X - before.X:0.#},dy={after.Y - before.Y:0.#}"));
        return CaptureCardGeometry();
    }

    /// <summary>§20.2a: true when each corner of <paramref name="rect"/> lies on some monitor's work area, so a card
    /// spanning two monitors counts as on screen.</summary>
    private static bool OnSomeWorkArea(LogicalRect rect, CardLayoutGeometrySnapshot geometry)
    {
        bool Covered(double x, double y) => geometry.Monitors.Values.Any(monitor =>
        {
            var area = monitor.WorkArea.Bounds;
            return x >= area.X && x <= area.Right && y >= area.Y && y <= area.Bottom;
        });
        return Covered(rect.X, rect.Y) && Covered(rect.Right, rect.Y) &&
               Covered(rect.X, rect.Bottom) && Covered(rect.Right, rect.Bottom);
    }

    /// <summary>
    /// For the active lead, restart the quiet period and propose the group move or resize from the gesture's start
    /// geometry. Apply successful proposals to followers only because the OS controls the lead during the gesture.
    /// Rejected proposals leave geometry unchanged. Untracked real moves and resizes begin a Pending gesture.
    /// </summary>
    private void OnCardGeometryChanged(ProviderUsageCardWindow window, bool isSizeChange, bool isApplyEcho = false)
    {
        if (!TryCaptureGeometry(out var current)) return;
        if (_leadWindow is null)
        {
            // 2.1 Ctrl peek (2.1 design §5.4): a change on the peeking card outside our own apply (a resize grip
            // dragged while it shows full) is the peek's own resize, not a gesture. Nothing repairs it: End
            // (CardPeekController) puts the exact pre-peek rect back regardless, and CaptureCardGeometry
            // (WindowCoordinator.cs) already reports that pre-peek rect to every other caller, so the live size here
            // never reaches the saved layout either way. Checked first so it can never begin a Pending gesture on a
            // card whose own mode never left Mini.
            if (window.ProviderId == _peekController.PeekingCard)
            {
                _diagnostics.Record(new DiagnosticEvent("layout_untracked_change", window.ProviderId,
                    $"decision=peek,sizeChange={isSizeChange}"));
                _layoutGeometry = current;
                return;
            }
            // Detect real external moves and resizes from the prior geometry while ignoring applies, display changes,
            // minimize and restore operations, and simultaneous movement of multiple cards.
            var before = _layoutGeometry;
            var decision = DecideUntrackedChange(window.ProviderId,
                window.IsVisible && window.WindowState == WindowState.Normal, isApplyEcho, before, current);
            if (decision != UntrackedChangeDecision.BeginPending)
            {
                _diagnostics.Record(new DiagnosticEvent("layout_untracked_change", window.ProviderId,
                    $"decision={decision},sizeChange={isSizeChange}"));
                if (decision == UntrackedChangeDecision.IgnoreDisplayChanged) RequestDisplayRevalidation("geometry_monitors_changed");
                _layoutGeometry = current;
                return;
            }
            BeginLayoutGesture(window, LayoutGestureKind.Pending, null, origin: "untracked", startGeometry: before);
        }
        if (!ReferenceEquals(_leadWindow, window) || _layoutGeometry is not { } baseline)
        {
            _layoutGeometry = current;
            return;
        }

        // A gesture started by WM_ENTERSIZEMOVE (with no pointer press of ours)
        // begins Pending, since Windows does not say up front whether it is a move or a resize. The
        // first geometry change resolves it - a size change means Resize, a position-only change
        // means Move - and only the first one: later changes leave an already-resolved kind alone.
        if (_leadKind == LayoutGestureKind.Pending)
        {
            _leadKind = isSizeChange ? LayoutGestureKind.Resize : LayoutGestureKind.Move;
            _gestures.ResolveKind(_leadKind);
            _diagnostics.Record(new DiagnosticEvent("layout_gesture_kind_resolved", window.ProviderId, Status: _leadKind.ToString()));
        }

        _gestures.NoteGeometry(window.ProviderId);
        var newRect = current.Rects[window.ProviderId];
        // Place followers at their start rectangles translated by the lead's total travel. This avoids accumulating
        // errors from delayed readback or window-manager adjustments between move events.
        var start = _gestureStartGeometry ?? baseline;
        var startLeadRect = start.Rects[window.ProviderId];
        var moveDelta = new LogicalPoint(newRect.X - startLeadRect.X, newRect.Y - startLeadRect.Y);
        // Resize from current geometry because the lead is already at its updated position. Allow a card to remain
        // partly off screen while resizing; commit applies the same rule.
        var proposal = _leadKind == LayoutGestureKind.Resize
            ? LayoutPolicy.ResizeGroup(_settings.Layout, _settings.EnabledProviderIds, current.Rects,
                window.ProviderId, newRect.Size, WorkAreaFor(current, window.ProviderId), UsableMinimumFor(window.ProviderId),
                requireInsideWorkArea: false)
            : LayoutPolicy.MoveGroup(_settings.Layout, _settings.EnabledProviderIds, start.Rects,
                window.ProviderId, moveDelta,
                WorkAreaFor(current, window.ProviderId), UsableMinimumFor(window.ProviderId));

        if (proposal.Succeeded)
        {
            ApplyLayoutResultToFollowers(proposal, current, window.ProviderId);
            if (TryCaptureGeometry(out var applied))
            {
                _layoutGeometry = applied;
                current = applied;
            }
        }
        else if (proposal.SafeErrorCode == "layout_outside_work_area" && _leadKind != LayoutGestureKind.Resize)
        {
            // Keep followers with the OS-controlled lead when the group crosses the work-area boundary. Commit-time
            // snapping can bring the group back inside; use the same start geometry and total lead travel.
            var members = LayoutPolicy.GetComponentProviderIds(_settings.Layout, _settings.EnabledProviderIds, window.ProviderId);
            var followerRects = members.Where(id => id != window.ProviderId && start.Rects.ContainsKey(id))
                .ToDictionary(id => id, id => start.Rects[id].Translate(moveDelta.X, moveDelta.Y), StringComparer.Ordinal);
            if (followerRects.Count > 0)
            {
                _applyingLayout = true;
                try { ApplyCardGeometry(_settings.Layout, followerRects, current); }
                finally { _applyingLayout = false; }
            }
            if (!_outsideWorkAreaLogged)
            {
                _outsideWorkAreaLogged = true;
                _diagnostics.Record(new DiagnosticEvent("layout_gesture_outside_work_area", window.ProviderId, Status: $"followers={followerRects.Count}"));
            }
            if (TryCaptureGeometry(out var carried))
            {
                _layoutGeometry = carried;
                current = carried;
            }
        }
        else
        {
            _diagnostics.Record(new DiagnosticEvent("layout_gesture_rejected", window.ProviderId, proposal.SafeErrorCode));
        }

        if (_leadKind == LayoutGestureKind.Resize) { ClearMagnetAndCandidate(); return; }
        if (_settings.SnapToScreenEdge) UpdateMagnet(current, window.ProviderId);
        else ClearMagnetAndCandidate();
    }

    internal enum UntrackedChangeDecision
    {
        BeginPending,
        IgnoreApplyEcho,
        IgnoreNotNormal,
        IgnoreNoBaseline,
        IgnoreDisplayChanged,
        IgnoreWindowSetChanged,
        IgnoreOthersMoved,
        IgnoreUnchanged,
        IgnoreOffScreen,
    }

    /// <summary>
    /// Whether a geometry change on <paramref name="providerId"/> that arrives with no
    /// gesture is a real move or resize of that one card (BeginPending), judged against <paramref name="baseline"/>,
    /// the geometry last seen before it. Not a gesture: the late echo of our own apply; a card that is minimized,
    /// maximized or hidden; a different monitor set (a display change, handled by the display handler); a different
    /// set of cards; any other card moved too (the OS relocating windows); no change; or a card that is, or was,
    /// off every work area (the minimize position -32000 and the restore from it).
    /// </summary>
    internal static UntrackedChangeDecision DecideUntrackedChange(string providerId, bool windowNormalAndVisible,
        bool isApplyEcho, CardLayoutGeometrySnapshot? baseline, CardLayoutGeometrySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (isApplyEcho) return UntrackedChangeDecision.IgnoreApplyEcho;
        if (!windowNormalAndVisible) return UntrackedChangeDecision.IgnoreNotNormal;
        if (baseline is null) return UntrackedChangeDecision.IgnoreNoBaseline;
        if (!CardLayoutGeometryComparison.MonitorsEqual(baseline.Monitors, current.Monitors))
            return UntrackedChangeDecision.IgnoreDisplayChanged;
        if (baseline.Rects.Count != current.Rects.Count || !current.Rects.ContainsKey(providerId) ||
            baseline.Rects.Keys.Any(id => !current.Rects.ContainsKey(id)))
            return UntrackedChangeDecision.IgnoreWindowSetChanged;
        foreach (var (id, rect) in current.Rects)
            if (!string.Equals(id, providerId, StringComparison.Ordinal) && baseline.Rects[id] != rect)
                return UntrackedChangeDecision.IgnoreOthersMoved;
        var before = baseline.Rects[providerId];
        var after = current.Rects[providerId];
        if (before == after) return UntrackedChangeDecision.IgnoreUnchanged;
        if (!CardLayoutGeometryComparison.IntersectsAnyWorkArea(before, current) ||
            !CardLayoutGeometryComparison.IntersectsAnyWorkArea(after, current))
            return UntrackedChangeDecision.IgnoreOffScreen;
        return UntrackedChangeDecision.BeginPending;
    }

    /// <summary>
    /// Applies a group proposal to every provider it touches except <paramref name="leadProviderId"/>
    /// (design doc §5.3): the OS already owns the lead's real position/size mid-gesture.
    /// </summary>
    private void ApplyLayoutResultToFollowers(LayoutPolicyResult result, CardLayoutGeometrySnapshot geometry, string leadProviderId)
    {
        if (!result.Succeeded || result.ProposedSettings is null) return;
        // Apply only the lead's group. The move proposal uses start geometry, so applying unrelated cards would
        // undo any movement they made during the gesture.
        var members = LayoutPolicy.GetComponentProviderIds(result.ProposedSettings, _settings.EnabledProviderIds, leadProviderId);
        var followerRects = result.ProposedRects.Where(pair => pair.Key != leadProviderId && members.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _applyingLayout = true;
        try
        {
            ApplyCardGeometry(result.ProposedSettings, followerRects, geometry);
            _settings.Layout = result.ProposedSettings;
            RefreshCardGroupState(geometry with { Rects = result.ProposedRects });
        }
        finally { _applyingLayout = false; }
    }

    /// <summary>The tracker's Settled handler. It can run on the quiet-period timer's
    /// thread; the settle is remembered before the commit is posted, so an interrupt or a new gesture that runs
    /// before the post can commit it synchronously (CommitAwaitingSettled) instead of the post committing it later
    /// against the new gesture's state.</summary>
    private void OnGestureSettled(GestureSettled settled)
    {
        Interlocked.Exchange(ref _settledAwaitingCommit, settled);
        Dispatcher.UIThread.Post(() => CommitAwaitingSettled(settled, "posted"));
    }

    /// <summary>Commits the remembered settle, if it is still waiting. With
    /// <paramref name="expected"/> null it commits whatever waits (an interrupt); otherwise only that settle.</summary>
    private void CommitAwaitingSettled(GestureSettled? expected, string source)
    {
        var waiting = expected is null
            ? Interlocked.Exchange(ref _settledAwaitingCommit, null)
            : Interlocked.CompareExchange(ref _settledAwaitingCommit, null, expected);
        if (expected is not null && !ReferenceEquals(waiting, expected))
        {
            _diagnostics.Record(new DiagnosticEvent("layout_gesture_commit_already_run", expected.Lead,
                $"gesture={expected.GestureId},source={source}"));
            return;
        }
        if (waiting is not null) CommitGesture(waiting);
    }

    /// <summary>
    /// Design doc §5.4: carries out the pure LayoutCommitDecision.Decide against live geometry and
    /// settings, then clears the magnet, captures anchors, saves, and logs layout_gesture_committed.
    /// Runs on the UI thread (see the constructor's Dispatcher.Post around this subscription), since
    /// the quiet period that can trigger this fires on a thread-pool timer.
    /// </summary>
    private void CommitGesture(GestureSettled settled)
    {
        // A gesture commit changes the layout (a dock, a
        // screen snap, a resize, or just the settled position), so a peek ends first here too, not only through
        // InterruptActiveGesture (which a commit reached via the quiet-period timer, rather than a new gesture
        // or an explicit interrupt, never goes through).
        EndPeekForLayoutBusy();
        // Compare the gesture id as well as the lead. Two gestures in a row on the same
        // card (release, press it again before the queued settle ran) share a lead, and the provider-only check let
        // the first settle commit the second gesture's state.
        if (_leadWindow is null || settled.GestureId != _leadGestureId ||
            !string.Equals(_leadWindow.ProviderId, settled.Lead, StringComparison.Ordinal))
        {
            // Settled is posted from the timer thread; a new gesture may have begun before this ran. Its state
            // belongs to the new gesture, so this settle must not touch it.
            _diagnostics.Record(new DiagnosticEvent("layout_gesture_commit_stale", settled.Lead,
                Status: $"current={_leadWindow?.ProviderId ?? "none"},gesture={settled.GestureId},currentGesture={_leadGestureId}"));
            return;
        }
        var durationMs = (long)(_time.GetUtcNow() - _gestureStartedAt).TotalMilliseconds;
        _diagnostics.Record(new DiagnosticEvent("layout_gesture_end_signal", settled.Lead, Status: settled.Source.ToString()));
        var action = LayoutCommitAction.Place;
        if (!_closing && _windows.ContainsKey(settled.Lead) && TryCaptureGeometry(out var geometry))
        {
            var candidate = _magnetCandidate;
            if (candidate is not null && !candidate.IsCurrent(_settings.Layout, geometry))
                candidate = FindMagnetCandidate(geometry, settled.Lead);
            // Window design Appendix H.7: the "magnet showed but it didn't snap" case.
            if (_magnetCandidate is not null && candidate is null) _diagnostics.Record(new DiagnosticEvent("layout_magnet_stale_no_candidate", settled.Lead));

            LayoutPolicyResult? resizeProposal = null;
            var resizeFits = false;
            if (settled.Kind == LayoutGestureKind.Resize)
            {
                // Preserve a resize that leaves the card partly off screen when Snap is off.
                resizeProposal = LayoutPolicy.ResizeGroup(_settings.Layout, _settings.EnabledProviderIds, geometry.Rects,
                    settled.Lead, geometry.Rects[settled.Lead].Size, WorkAreaFor(geometry, settled.Lead), UsableMinimumFor(settled.Lead),
                    requireInsideWorkArea: false);
                resizeFits = resizeProposal.Succeeded;
            }

            action = LayoutCommitDecision.Decide(settled.Kind, _settings.SnapToScreenEdge, candidate is not null, resizeFits);
            switch (action)
            {
                case LayoutCommitAction.Snap when candidate is not null:
                    // 2.0 mini mode (2.0 design §5.6): mode changes happen only inside this dock commit. The
                    // incoming group takes the destination's mode, the same way PreviewDock already made it
                    // take the destination's size; DockModeChanges is the pure static that decides which
                    // incoming cards actually change (none, when the incoming group is already the target mode).
                    var targetMode = _modes.Of(candidate.DestinationProviderId);
                    var dockModeChanges = DockModeChanges(candidate.IncomingProviderIds, targetMode, _modes.Of);
                    if (dockModeChanges.Count == 0)
                        ApplyLayoutResult(candidate.Proposal, geometry);
                    else
                        ApplyModesAndGeometry(dockModeChanges, candidate.Proposal, geometry, "dock");
                    geometry = CaptureCardGeometry();
                    _diagnostics.Record(new DiagnosticEvent("layout_dock_committed", settled.Lead,
                        Status: $"{candidate.DestinationProviderId},targetMode={targetMode},modeChanged={dockModeChanges.Count}"));
                    break;
                case LayoutCommitAction.ScreenSnap:
                    var outcome = EvaluateScreenEdgeSnap(_settings.Layout, _settings.EnabledProviderIds, geometry.Rects,
                        settled.Lead, WorkAreaFor(geometry, settled.Lead), UsableMinimumFor(settled.Lead),
                        hasDockCandidate: false, snapToScreenEdgeEnabled: true);
                    if (outcome is { Evaluated: true, Result.Succeeded: true }) ApplyLayoutResult(outcome.Result!, geometry);
                    geometry = CaptureCardGeometry();
                    _diagnostics.Record(new DiagnosticEvent("layout_screen_snap_evaluated", settled.Lead,
                        Status: $"succeeded={outcome.Succeeded},moved={outcome.Moved}"));
                    break;
                case LayoutCommitAction.ResizeApply when resizeProposal is { Succeeded: true }:
                    ApplyLayoutResult(resizeProposal, geometry);
                    geometry = CaptureCardGeometry();
                    break;
                case LayoutCommitAction.ResizeRevert:
                    _applyingLayout = true;
                    try { ApplyCardGeometry(_settings.Layout, settled.StartRects, geometry); }
                    finally { _applyingLayout = false; }
                    geometry = CaptureCardGeometry();
                    _diagnostics.Record(new DiagnosticEvent("layout_resize_reverted", settled.Lead, resizeProposal?.SafeErrorCode));
                    break;
                case LayoutCommitAction.Place:
                    break;
            }

            // Contain multi-card groups after pointer release on platforms whose window manager confines placement.
            geometry = ContainLeadGroupIfConfined(settled.Lead, settled.Source, geometry);

            // Refresh each card's docked sides before saving, regardless of which commit path ran.
            RefreshCardGroupState(geometry);
            _layoutGeometry = geometry;
            PersistCapturedLayout(geometry);
        }

        ClearMagnetAndCandidate();
        _leadWindow = null;
        _layoutBeforeGesture = null;
        _gestureStartGeometry = null;
        _leadKind = default;
        _leadEdge = null;
        _leadGestureId = 0;
        _diagnostics.Record(new DiagnosticEvent("layout_gesture_committed", settled.Lead,
            Status: $"kind={settled.Kind},decision={action},source={settled.Source},geometryChanges={settled.GeometryChanges}",
            DurationMilliseconds: durationMs));
    }

    /// <summary>Outcome of evaluating the screen-edge snap seam in <see cref="CommitGesture"/>.</summary>
    internal readonly record struct ScreenEdgeSnapOutcome(bool Evaluated, bool Succeeded, bool Moved, LayoutPolicyResult? Result);

    /// <summary>
    /// Extracted so a spec can drive exactly the decision
    /// CommitGesture makes - given the live SnapToScreenEdge setting value and whether a
    /// dock candidate currently wins instead - without instantiating a WindowCoordinator or a real
    /// window. Docking always takes priority over snapping (a dock candidate skips evaluation
    /// entirely, matching the pre-existing behavior), and a disabled setting is also a no-op.
    /// </summary>
    internal static ScreenEdgeSnapOutcome EvaluateScreenEdgeSnap(
        LayoutSettings layout,
        IEnumerable<string> enabledProviderIds,
        IReadOnlyDictionary<string, LogicalRect> currentRects,
        string providerId,
        LogicalWorkArea workArea,
        LogicalSize usableMinimum,
        bool hasDockCandidate,
        bool snapToScreenEdgeEnabled)
    {
        if (hasDockCandidate || !snapToScreenEdgeEnabled)
            return new ScreenEdgeSnapOutcome(false, false, false, null);

        var beforeRect = currentRects[providerId];
        var snap = LayoutPolicy.SnapGroupToScreenEdges(layout, enabledProviderIds, currentRects, providerId,
            workArea, usableMinimum, enabled: true);
        var moved = snap.Succeeded && snap.ProposedRects.TryGetValue(providerId, out var afterSnap) && afterSnap != beforeRect;
        return new ScreenEdgeSnapOutcome(true, snap.Succeeded, moved, snap);
    }

    private void OnLayoutGestureCancelled(object? sender, EventArgs e)
    {
        if (sender is not ProviderUsageCardWindow window || !ReferenceEquals(window, _leadWindow)) return;
        CancelActiveGesture("user_cancelled");
    }

    /// <summary>Cancels the active gesture, if any; a no-op otherwise. The actual restore-and-log
    /// work happens in OnGestureCancelled, which _gestures.Cancelled is wired to.
    /// Used where the gesture must be moved back regardless of button state: Esc,
    /// the lead card closed, app exit. Every other caller uses InterruptActiveGesture.)</summary>
    private void CancelActiveGesture(string reason)
    {
        if (_gestures.IsActive) _gestures.Cancel(reason);
    }

    internal enum GestureInterruptAction { Commit, Cancel }

    /// <summary>
    /// Finishes the active gesture before another operation changes the layout. A queued settle is committed first;
    /// an active gesture is committed when its button is up and cancelled while the button remains held.
    /// </summary>
    private void InterruptActiveGesture(string reason)
    {
        // End an active peek before any caller changes the layout.
        EndPeekForLayoutBusy();
        CommitAwaitingSettled(null, reason);
        if (!_gestures.IsActive) return;
        var buttonHeld = _primaryButtonHeld();
        var action = DecideGestureInterrupt(reason, buttonHeld);
        _diagnostics.Record(new DiagnosticEvent("layout_gesture_interrupted", _leadWindow?.ProviderId,
            $"reason={reason},buttonHeld={buttonHeld},action={action},gesture={_leadGestureId}"));
        if (action == GestureInterruptAction.Cancel)
        {
            _gestures.Cancel(reason);
            return;
        }
        if (_gestures.EndForCommit(GestureEndSource.Interrupted) is { } settled) CommitGesture(settled);
    }

    /// <summary>Esc, lead-card close, and app exit cancel. A superseding gesture commits. Other interruptions commit
    /// when the primary button is up and cancel while it remains held.</summary>
    internal static GestureInterruptAction DecideGestureInterrupt(string reason, bool primaryButtonHeld) => reason switch
    {
        "user_cancelled" or "card_closed" or "app_exit" => GestureInterruptAction.Cancel,
        "superseded" => GestureInterruptAction.Commit,
        _ => primaryButtonHeld ? GestureInterruptAction.Cancel : GestureInterruptAction.Commit,
    };

    /// <summary>
    /// Design doc §5.4: on Cancelled, restore the start rects, clear the magnet and log it. Runs
    /// synchronously (no Dispatcher.Post) because Cancel/Begin's supersede are always called by this
    /// coordinator itself on the UI thread, and callers such as DisableProviderAsync depend on the
    /// restore completing before they continue.
    /// </summary>
    private void OnGestureCancelled(string lead, string reason)
    {
        // Window design Appendix C.5: a detach removed the graph edges at the press; cancel puts them back.
        // Normalize for current providers and Snap setting; settings may have changed during the gesture.
        if (_layoutBeforeGesture is not null)
        {
            _settings.Layout = LayoutForSettings(_layoutBeforeGesture, _settings.EnabledProviderIds,
                _settings.SnapToScreenEdge, out var issue);
            if (issue is not null)
                _diagnostics.Record(new DiagnosticEvent("layout_reconcile_failed", lead, $"source=gesture_cancel,{issue}"));
        }
        ClearMagnetAndCandidate();
        // Window design Appendix H.5: restore the geometry the gesture started from; _layoutGeometry has been
        // moved along by every accepted mid-gesture change, so it is only the fallback.
        if ((_gestureStartGeometry ?? _layoutGeometry) is { } lastAccepted && TryCaptureGeometry(out var current))
        {
            _applyingLayout = true;
            try { ApplyCardGeometry(_settings.Layout, lastAccepted.Rects, current); }
            finally { _applyingLayout = false; }
            _layoutGeometry = CaptureCardGeometry();
            // Refresh docked sides to match the restored graph.
            RefreshCardGroupState(_layoutGeometry);
        }
        _leadWindow = null;
        _leadKind = default;
        _leadEdge = null;
        _leadGestureId = 0;
        _layoutBeforeGesture = null;
        _gestureStartGeometry = null;
        _diagnostics.Record(new DiagnosticEvent("layout_gesture_cancelled", lead, reason));
    }
}

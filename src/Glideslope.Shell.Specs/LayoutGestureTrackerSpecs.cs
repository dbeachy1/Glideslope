using System.Collections.Immutable;
using Avalonia.Controls;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Level 1 specs for design doc S5.1/S10: pure LayoutGestureTracker behavior against a fake clock
/// and a fake button state - no Avalonia window, dispatcher, or thread pool involved.
/// </summary>
internal static class LayoutGestureTrackerSpecs
{
    private static readonly ImmutableDictionary<string, LogicalRect> StartRects =
        ImmutableDictionary<string, LogicalRect>.Empty.Add("codex", new LogicalRect(0, 0, 940, 663));

    public static void Run()
    {
        PositionOnlyChangesThenSilenceSettleOnceAsQuietPeriod();
        ExitSizeMoveSettlesOnceAndTheLaterTimerDoesNothing();
        SilenceWhileButtonHeldDoesNotSettleUntilReleased();
        BeginWhileActiveSupersedesTheFirstGesture();
        CancelFiresCancelledOnceAndNeverSettled();
        September26ReplayReachesSettled();
        PendingBeginResolvesToMoveOnAPositionOnlyChange();
        BeginReturnsANewGestureIdThatSettledCarries();
        EndForCommitReturnsTheGestureWithoutRaisingEvents();
        QuietPeriodDebouncerFiresOnceAfterTheLastRequest();
    }

    /// <summary>Each Begin returns an id that its settle carries, allowing the coordinator to distinguish
    /// gestures with the same lead card.</summary>
    private static void BeginReturnsANewGestureIdThatSettledCarries()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new LayoutGestureTracker(clock, TimeSpan.FromMilliseconds(300), () => false);
        var settled = new List<GestureSettled>();
        tracker.Settled += settled.Add;

        var first = tracker.Begin("codex", LayoutGestureKind.Move, null, StartRects);
        tracker.End(GestureEndSource.ExitSizeMove);
        var second = tracker.Begin("codex", LayoutGestureKind.Move, null, StartRects);
        tracker.End(GestureEndSource.ExitSizeMove);

        Assert(first > 0 && second != first, $"each Begin returns a new gesture id (were {first} and {second})");
        Assert(settled.Count == 2 && settled[0].GestureId == first && settled[1].GestureId == second,
            "each settle carries the id its Begin returned, although both gestures have the same lead");
    }

    /// <summary>EndForCommit hands the gesture back for a synchronous commit, raising neither
    /// Settled nor Cancelled, and its timer can no longer settle it.</summary>
    private static void EndForCommitReturnsTheGestureWithoutRaisingEvents()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new LayoutGestureTracker(clock, TimeSpan.FromMilliseconds(300), () => false);
        var events = 0;
        tracker.Settled += _ => events++;
        tracker.Cancelled += (_, _) => events++;

        var id = tracker.Begin("codex", LayoutGestureKind.Move, null, StartRects);
        tracker.NoteGeometry("codex");
        var ended = tracker.EndForCommit(GestureEndSource.Interrupted);
        Assert(ended is { Lead: "codex", Kind: LayoutGestureKind.Move, Source: GestureEndSource.Interrupted, GeometryChanges: 1 } &&
               ended.GestureId == id, $"EndForCommit returns the active gesture as Interrupted (was {ended})");
        Assert(events == 0 && !tracker.IsActive, "neither Settled nor Cancelled fires, and the tracker is idle");
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert(events == 0, "the quiet-period timer armed before no longer settles it");
        Assert(tracker.EndForCommit(GestureEndSource.Interrupted) is null, "with no active gesture EndForCommit returns null");
    }

    /// <summary>Coalesces requests until a quiet period, reports the last source and count, and supports flush or cancellation.</summary>
    private static void QuietPeriodDebouncerFiresOnceAfterTheLastRequest()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var fired = new List<(string Source, int Count)>();
        var debouncer = new QuietPeriodDebouncer(clock, TimeSpan.FromMilliseconds(750), (source, count) => fired.Add((source, count)));

        debouncer.Request("screens_changed");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        debouncer.Request("scaling_changed");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert(fired.Count == 0, "a request inside the quiet period restarts it");
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert(fired.SequenceEqual([("scaling_changed", 2)]), "the burst runs once, with the last source and the request count");
        clock.Advance(TimeSpan.FromMilliseconds(750));
        Assert(fired.Count == 1, "nothing runs again without a new request");

        debouncer.Request("card_button");
        Assert(debouncer.HasPending, "a request is pending until it runs");
        debouncer.Flush();
        Assert(fired.Count == 2 && fired[1] == ("card_button", 1) && !debouncer.HasPending, "Flush runs the pending request at once");
        clock.Advance(TimeSpan.FromMilliseconds(750));
        Assert(fired.Count == 2, "the flushed request's timer does not run it a second time");

        debouncer.Request("card_button");
        Assert(debouncer.CancelPending() && !debouncer.CancelPending(), "CancelPending drops a pending request once");
        clock.Advance(TimeSpan.FromMilliseconds(750));
        debouncer.Dispose();
        debouncer.Request("after_dispose");
        clock.Advance(TimeSpan.FromMilliseconds(750));
        Assert(fired.Count == 2, "a cancelled request and a request after Dispose never run");
    }

    private static void PositionOnlyChangesThenSilenceSettleOnceAsQuietPeriod()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var buttonHeld = false;
        var tracker = new LayoutGestureTracker(clock, TimeSpan.FromMilliseconds(300), () => buttonHeld);
        GestureSettled? settled = null;
        var settledCount = 0;
        tracker.Settled += s => { settled = s; settledCount++; };
        tracker.Cancelled += (_, _) => throw new InvalidOperationException("FAIL: should not cancel");

        tracker.Begin("codex", LayoutGestureKind.Move, null, StartRects);
        tracker.NoteGeometry("codex");
        clock.Advance(TimeSpan.FromMilliseconds(150));
        tracker.NoteGeometry("codex");
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert(settledCount == 0, "still moving inside the quiet period should not settle yet");
        clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert(settledCount == 1, "silence for the whole quiet period should settle exactly once");
        Assert(settled is { Lead: "codex", Kind: LayoutGestureKind.Move, Source: GestureEndSource.QuietPeriod, GeometryChanges: 2 },
            $"the settled gesture should report lead codex, kind Move, source QuietPeriod, 2 geometry changes (was {settled})");
        Assert(!tracker.IsActive, "the tracker is idle once settled");
    }

    private static void ExitSizeMoveSettlesOnceAndTheLaterTimerDoesNothing()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new LayoutGestureTracker(clock, TimeSpan.FromMilliseconds(300), () => false);
        var settledSources = new List<GestureEndSource>();
        tracker.Settled += s => settledSources.Add(s.Source);

        tracker.Begin("codex", LayoutGestureKind.Resize, WindowEdge.East, StartRects);
        tracker.NoteGeometry("codex");
        tracker.End(GestureEndSource.ExitSizeMove);
        Assert(settledSources.SequenceEqual([GestureEndSource.ExitSizeMove]), "End should settle immediately with its own source");

        // The quiet-period timer NoteGeometry armed above is still pending; advancing past its
        // deadline must not settle a second time now that the gesture already ended via End().
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert(settledSources.Count == 1, "the superseded quiet-period timer must not settle again after End");
    }

    private static void SilenceWhileButtonHeldDoesNotSettleUntilReleased()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var buttonHeld = true;
        var tracker = new LayoutGestureTracker(clock, TimeSpan.FromMilliseconds(300), () => buttonHeld);
        var settledCount = 0;
        tracker.Settled += _ => settledCount++;

        tracker.Begin("codex", LayoutGestureKind.Move, null, StartRects);
        tracker.NoteGeometry("codex");
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert(settledCount == 0, "silence while the button is still held should re-arm, not settle");
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert(settledCount == 0, "a held button keeps deferring indefinitely");

        buttonHeld = false;
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert(settledCount == 1, "releasing the button and then going quiet should settle");
    }

    private static void BeginWhileActiveSupersedesTheFirstGesture()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new LayoutGestureTracker(clock, TimeSpan.FromMilliseconds(300), () => false);
        var cancelled = new List<(string Lead, string Reason)>();
        var settledLeads = new List<string>();
        tracker.Cancelled += (lead, reason) => cancelled.Add((lead, reason));
        tracker.Settled += s => settledLeads.Add(s.Lead);

        tracker.Begin("codex", LayoutGestureKind.Move, null, StartRects);
        tracker.Begin("claude", LayoutGestureKind.Move, null, StartRects);

        Assert(cancelled.SequenceEqual([("codex", "superseded")]), "the first gesture should be cancelled as superseded");
        Assert(tracker.IsActive, "the second gesture should now be active");
        tracker.End(GestureEndSource.ExitSizeMove);
        Assert(settledLeads.SequenceEqual(["claude"]), "only the second (surviving) gesture should ever settle");
    }

    private static void CancelFiresCancelledOnceAndNeverSettled()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new LayoutGestureTracker(clock, TimeSpan.FromMilliseconds(300), () => false);
        var cancelledCount = 0;
        tracker.Cancelled += (_, _) => cancelledCount++;
        tracker.Settled += _ => throw new InvalidOperationException("FAIL: a cancelled gesture must never settle");

        tracker.Begin("codex", LayoutGestureKind.Move, null, StartRects);
        tracker.NoteGeometry("codex");
        tracker.Cancel("user_cancelled");
        Assert(cancelledCount == 1, "Cancel should fire Cancelled exactly once");
        Assert(!tracker.IsActive, "the tracker is idle once cancelled");

        // The quiet-period timer NoteGeometry armed must not fire Settled after the cancel.
        clock.Advance(TimeSpan.FromMilliseconds(300));
        tracker.Cancel("again"); // Cancel with nothing active is a documented no-op.
        Assert(cancelledCount == 1, "cancelling an already-idle tracker is a no-op");
    }

    /// <summary>Design docs S1 and S10: position-only move events restart the quiet-period timer, allowing
    /// the tracker to settle a body drag that reports no size changes. GestureWiringProof checks that the
    /// coordinator forwards these events to the tracker.</summary>
    private static void September26ReplayReachesSettled()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new LayoutGestureTracker(clock, TimeSpan.FromMilliseconds(300), () => false);
        GestureSettled? settled = null;
        tracker.Settled += s => settled = s;

        tracker.Begin("codex", LayoutGestureKind.Move, null, StartRects);
        // A body drag delivers many small position-only ticks; NoteGeometry is all a pure move ever
        // reports (Width/Height never change), which is exactly the case the live bug dropped.
        for (var tick = 0; tick < 12; tick++)
        {
            tracker.NoteGeometry("codex");
            clock.Advance(TimeSpan.FromMilliseconds(20));
        }
        Assert(settled is null, "still receiving ticks inside the quiet period should not settle yet");
        clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert(settled is { Lead: "codex", Kind: LayoutGestureKind.Move, Source: GestureEndSource.QuietPeriod, GeometryChanges: 12 },
            $"Doug's logged move sequence should reach Settled once it goes quiet (was {settled})");
    }

    /// <summary>Design doc S5.2: WM_ENTERSIZEMOVE with no pointer press of ours begins
    /// Pending; the first geometry change resolves it - here a position-only one, to Move.</summary>
    private static void PendingBeginResolvesToMoveOnAPositionOnlyChange()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new LayoutGestureTracker(clock, TimeSpan.FromMilliseconds(300), () => false);
        GestureSettled? settled = null;
        tracker.Settled += s => settled = s;

        tracker.Begin("codex", LayoutGestureKind.Pending, null, StartRects);
        tracker.NoteGeometry("codex");
        tracker.ResolveKind(LayoutGestureKind.Move);
        tracker.End(GestureEndSource.ExitSizeMove);

        Assert(settled is { Lead: "codex", Kind: LayoutGestureKind.Move, Source: GestureEndSource.ExitSizeMove },
            $"a Pending gesture resolved by its first geometry change should settle with the resolved kind (was {settled})");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    /// <summary>
    /// A synchronous fake TimeProvider whose timers fire on Advance, on the calling thread - the
    /// same pattern Glideslope.Monitoring.Specs uses for ProviderScheduler, minus the async wrapper
    /// that tracker never needs (LayoutGestureTracker's callers marshal to the UI thread themselves;
    /// see WindowCoordinator's constructor). Reused by GestureWiringProof.
    /// </summary>
    internal sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow;
        private long _timestamp;

        public ManualTimeProvider(DateTimeOffset start) => _utcNow = start.ToUniversalTime();
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override long GetTimestamp() { lock (_gate) return _timestamp; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        public void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                _utcNow += amount;
                _timestamp += amount.Ticks;
                foreach (var timer in _timers.ToArray())
                {
                    if (!timer.TryFire(_timestamp, out var callback, out var state)) continue;
                    callbacks.Add((callback, state));
                }
            }

            foreach (var (callback, state) in callbacks) callback(state);
        }

        private void Reschedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(dueTime));
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(period));
            lock (_gate) timer.Reschedule(_timestamp, dueTime, period);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long? _dueTimestamp;
            private long _periodTicks;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                owner.Reschedule(this, dueTime, period);
                return true;
            }

            public void Dispose() { lock (owner._gate) _disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

            public bool TryFire(long now, out TimerCallback fireCallback, out object? fireState)
            {
                fireCallback = callback;
                fireState = state;
                if (_disposed || _dueTimestamp is not { } due || due > now) return false;
                if (_periodTicks > 0) _dueTimestamp = checked(due + _periodTicks);
                else _dueTimestamp = null;
                return true;
            }

            public void Reschedule(long now, TimeSpan dueTime, TimeSpan period)
            {
                _periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? null : checked(now + dueTime.Ticks);
            }
        }
    }
}

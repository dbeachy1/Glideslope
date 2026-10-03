using System.Collections.Immutable;
using Avalonia.Controls;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Gesture type selected from card input. Pending represents an OS move/resize loop that started without a card
/// pointer press; the first geometry change resolves it to Move or Resize.
/// </summary>
internal enum LayoutGestureKind { Move, Resize, Detach, Pending }

/// <summary>
/// Signal that ended a gesture: native move-loop exit, pointer release, quiet-period timeout, or coordinator
/// interruption before another operation changes the layout.
/// </summary>
internal enum GestureEndSource { ExitSizeMove, QuietPeriod, PointerRelease, Interrupted }

/// <summary>
/// One completed gesture. StartRects is the geometry passed to Begin; GestureId distinguishes consecutive
/// gestures on the same card.
/// </summary>
internal sealed record GestureSettled(
    string Lead,
    LayoutGestureKind Kind,
    WindowEdge? Edge,
    ImmutableDictionary<string, LogicalRect> StartRects,
    GestureEndSource Source,
    int GeometryChanges,
    long GestureId);

/// <summary>
/// Owns the lifecycle of one card move/resize/detach gesture: which card is being dragged, how, and
/// when the drag is over.
///
/// Tracks move, resize, and detach lifecycles. Native signals end gestures where available; a quiet-period
/// debounce is the fallback. The fallback checks whether the primary button remains held before settling.
///
/// This is a plain class: its only external dependencies are a TimeProvider (for the debounce) and
/// a "is the primary pointer button still down" probe, so LayoutGestureTrackerSpecs can drive every
/// rule with a fake clock and a fake button, with no Avalonia window, dispatcher, or thread pool
/// involved. WindowEdge and LogicalRect are plain value types carried through for the caller's
/// benefit (which edge is being resized, what geometry the gesture started from); this tracker
/// never touches a live window, control, or dispatcher.
/// </summary>
internal sealed class LayoutGestureTracker
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _quietPeriod;
    private readonly Func<bool> _primaryButtonHeld;
    private readonly object _gate = new();
    private string? _lead;
    private LayoutGestureKind _kind;
    private WindowEdge? _edge;
    private ImmutableDictionary<string, LogicalRect> _startRects = ImmutableDictionary<string, LogicalRect>.Empty;
    private int _geometryChanges;
    private long _generation;
    // Timer generations change with geometry updates; gesture IDs remain stable for each gesture.
    private long _lastGestureId;
    private long _activeGestureId;
    private ITimer? _quietTimer;

    public LayoutGestureTracker(TimeProvider time, TimeSpan quietPeriod, Func<bool> primaryButtonHeld)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _quietPeriod = quietPeriod > TimeSpan.Zero ? quietPeriod : throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        _primaryButtonHeld = primaryButtonHeld ?? throw new ArgumentNullException(nameof(primaryButtonHeld));
    }

    /// <summary>Fires exactly once per gesture, when it ends normally (native signal or quiet period).</summary>
    public event Action<GestureSettled>? Settled;

    /// <summary>Fires once for a gesture cancelled before settling, with the caller-supplied reason.</summary>
    public event Action<string, string>? Cancelled;

    public bool IsActive { get { lock (_gate) return _lead is not null; } }

    /// <summary>
    /// Starts a gesture and returns its unique id. Any existing gesture is cancelled before the new one begins.
    /// </summary>
    public long Begin(string lead, LayoutGestureKind kind, WindowEdge? edge, ImmutableDictionary<string, LogicalRect> startRects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lead);
        ArgumentNullException.ThrowIfNull(startRects);
        string? supersededLead;
        long gestureId;
        lock (_gate)
        {
            supersededLead = _lead;
            _lead = lead;
            _kind = kind;
            _edge = edge;
            _startRects = startRects;
            _geometryChanges = 0;
            gestureId = ++_lastGestureId;
            _activeGestureId = gestureId;
            ArmLocked();
        }
        if (supersededLead is not null) Cancelled?.Invoke(supersededLead, "superseded");
        return gestureId;
    }

    /// <summary>
    /// Records that the active gesture's geometry changed (any provider ID; the caller decides
    /// whether it belongs to the lead before calling this) and restarts the quiet-period deadline.
    /// A call with no active gesture is a no-op, since a geometry change can arrive for a card that
    /// is not being dragged.
    /// </summary>
    public void NoteGeometry(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (_gate)
        {
            if (_lead is null) return;
            _geometryChanges++;
            ArmLocked();
        }
    }

    /// <summary>
    /// Resolves a gesture that began as Pending (design doc §5.2: WM_ENTERSIZEMOVE with no pointer
    /// press) once its first geometry change arrives - the caller decides Resize (a size changed) or
    /// Move (position only) and passes that here. A no-op once the gesture is no longer Pending
    /// (only the first geometry change decides) and a no-op with no active gesture.
    /// </summary>
    public void ResolveKind(LayoutGestureKind kind)
    {
        lock (_gate)
        {
            if (_lead is null || _kind != LayoutGestureKind.Pending) return;
            _kind = kind;
        }
    }

    /// <summary>Ends the active gesture immediately, regardless of whether the primary button is
    /// still held - an explicit OS-level end signal is authoritative. A call with no active gesture
    /// is a no-op.</summary>
    public void End(GestureEndSource source)
    {
        GestureSettled? settled;
        lock (_gate)
        {
            if (_lead is null) return;
            settled = Snapshot(source);
            ClearLocked();
        }
        Settled?.Invoke(settled);
    }

    /// <summary>
    /// Ends the active gesture and returns its settled record without raising events, allowing the coordinator to
    /// commit synchronously before another operation proceeds. Returns null when no gesture is active.
    /// </summary>
    public GestureSettled? EndForCommit(GestureEndSource source)
    {
        lock (_gate)
        {
            if (_lead is null) return null;
            var settled = Snapshot(source);
            ClearLocked();
            return settled;
        }
    }

    /// <summary>Ends the active gesture without settling it: Cancelled fires, Settled never does.
    /// A call with no active gesture is a no-op.</summary>
    public void Cancel(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string? lead;
        lock (_gate)
        {
            lead = _lead;
            if (lead is null) return;
            ClearLocked();
        }
        Cancelled?.Invoke(lead, reason);
    }

    private void ArmLocked()
    {
        _quietTimer?.Dispose();
        var generation = ++_generation;
        _quietTimer = _time.CreateTimer(_ => OnQuietPeriodElapsed(generation), null, _quietPeriod, Timeout.InfiniteTimeSpan);
    }

    private void OnQuietPeriodElapsed(long generation)
    {
        GestureSettled? settled;
        lock (_gate)
        {
            // A deadline that was superseded or cleared after its callback was already queued
            // (End/Cancel/a new Begin all bump the generation) does nothing: the gesture it was
            // timing no longer exists, and Settled/Cancelled has already fired once for it.
            if (_lead is null || generation != _generation) return;
            if (_primaryButtonHeld())
            {
                // Still dragging, just paused: re-arm instead of settling so a mid-drag pause never
                // commits a snap or dock while the user keeps the button down (design doc §5.1).
                ArmLocked();
                return;
            }
            settled = Snapshot(GestureEndSource.QuietPeriod);
            ClearLocked();
        }
        Settled?.Invoke(settled);
    }

    private GestureSettled Snapshot(GestureEndSource source) =>
        new(_lead!, _kind, _edge, _startRects, source, _geometryChanges, _activeGestureId);

    private void ClearLocked()
    {
        _generation++;
        _quietTimer?.Dispose();
        _quietTimer = null;
        _lead = null;
        _kind = default;
        _edge = null;
        _startRects = ImmutableDictionary<string, LogicalRect>.Empty;
        _geometryChanges = 0;
        _activeGestureId = 0;
    }
}

/// <summary>
/// Runs one callback after a burst of requests has been quiet for the configured period. The callback receives the
/// last request's source and request count; callers marshal it to their required thread.
/// </summary>
internal sealed class QuietPeriodDebouncer : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _quietPeriod;
    private readonly Action<string, int> _elapsed;
    private readonly object _gate = new();
    private ITimer? _timer;
    private long _generation;
    private string? _lastSource;
    private int _requests;
    private bool _disposed;

    public QuietPeriodDebouncer(TimeProvider time, TimeSpan quietPeriod, Action<string, int> elapsed)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _quietPeriod = quietPeriod > TimeSpan.Zero ? quietPeriod : throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        _elapsed = elapsed ?? throw new ArgumentNullException(nameof(elapsed));
    }

    /// <summary>True while requests are waiting for their quiet period.</summary>
    public bool HasPending { get { lock (_gate) return _requests > 0; } }

    /// <summary>Records one request and restarts the quiet period. Ignored after Dispose.</summary>
    public void Request(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        lock (_gate)
        {
            if (_disposed) return;
            _requests++;
            _lastSource = source;
            _timer?.Dispose();
            var generation = ++_generation;
            _timer = _time.CreateTimer(_ => Fire(generation), null, _quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs the callback now for the waiting requests, if any, instead of at the end of the quiet period.</summary>
    public void Flush() => Fire(null);

    /// <summary>Drops the waiting requests without running the callback; true when there were any.</summary>
    public bool CancelPending()
    {
        lock (_gate)
        {
            if (_requests == 0) return false;
            ClearLocked();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ClearLocked();
        }
    }

    private void Fire(long? expectedGeneration)
    {
        string source;
        int requests;
        lock (_gate)
        {
            // A timer that was re-armed, flushed or cancelled after its callback was queued does nothing.
            if (_disposed || _requests == 0) return;
            if (expectedGeneration is { } generation && generation != _generation) return;
            source = _lastSource!;
            requests = _requests;
            ClearLocked();
        }
        _elapsed(source, requests);
    }

    private void ClearLocked()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
        _requests = 0;
        _lastSource = null;
    }
}

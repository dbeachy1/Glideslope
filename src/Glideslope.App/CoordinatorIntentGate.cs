namespace Glideslope.App;

/// <summary>
/// Serializes settings-store commits and shutdown, draining accepted work before shutdown completes. UI-thread
/// layout operations are not gated, so callers must account for live layout changes across asynchronous waits.
/// </summary>
internal sealed class CoordinatorIntentGate : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource<GateLease>> _waiters = [];
    private bool _occupied;
    private bool _disposeRequested;
    private Task? _disposeTask;

    public Task<GateLease> EnterAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (!_occupied)
            {
                _occupied = true;
                return Task.FromResult(new GateLease(this));
            }

            var waiter = new TaskCompletionSource<GateLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
            return waiter.Task;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposeRequested = true;
            if (!_occupied)
            {
                return ValueTask.CompletedTask;
            }

            var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = drained.Task;
            _drained = drained;
            return new ValueTask(drained.Task);
        }
    }

    private TaskCompletionSource<bool>? _drained;

    private void Release()
    {
        TaskCompletionSource<GateLease>? next = null;
        TaskCompletionSource<bool>? drained = null;
        lock (_sync)
        {
            if (_waiters.Count > 0)
            {
                next = _waiters.Dequeue();
            }
            else
            {
                _occupied = false;
                if (_disposeRequested)
                {
                    drained = _drained;
                    _drained = null;
                }
            }
        }

        if (next is not null)
        {
            next.TrySetResult(new GateLease(this));
        }
        else
        {
            drained?.TrySetResult(true);
        }
    }

    internal sealed class GateLease : IAsyncDisposable
    {
        private readonly CoordinatorIntentGate _owner;
        private int _released;

        internal GateLease(CoordinatorIntentGate owner)
        {
            _owner = owner;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}

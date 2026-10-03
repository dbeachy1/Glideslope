namespace Glideslope.Core;

/// <summary>
/// Keeps Windows mutex ownership on one dedicated thread so async broker teardown
/// can always release the mutex from the thread that acquired it.
/// </summary>
internal sealed class WindowsMutexLease : IAsyncDisposable
{
    private readonly Mutex _mutex;
    private readonly TimeSpan _wait;
    private readonly IDiagnosticSink _diagnostics;
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource<bool> _acquisition = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _ownerThread;
    private int _disposeRequested;

    private WindowsMutexLease(Mutex mutex, TimeSpan wait, IDiagnosticSink diagnostics)
    {
        _mutex = mutex;
        _wait = wait;
        _diagnostics = diagnostics;
        _ownerThread = new Thread(AcquireAndHold)
        {
            IsBackground = true,
            Name = "Glideslope instance mutex"
        };
    }

    public bool Acquired { get; private set; }

    public bool RecoveredAbandoned { get; private set; }

    public bool Stopped => _stopped.Task.IsCompleted;

    /// <summary>Tries to take the mutex on the lease's own thread. <paramref name="wait"/> is zero for the
    /// normal single-instance check; a launch that found the owner closing passes a
    /// bounded wait, so it takes over as soon as the old owner releases the mutex at the end of its exit.</summary>
    public static async Task<WindowsMutexLease> TryAcquireAsync(Mutex mutex, TimeSpan wait, IDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var lease = new WindowsMutexLease(mutex, wait, diagnostics);
        lease._ownerThread.Start();
        await lease._acquisition.Task.ConfigureAwait(false);
        return lease;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
        {
            _release.Set();
        }

        try
        {
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The thread remains alive rather than disposing a mutex it may still own.
            _diagnostics.Record(new DiagnosticEvent("instance_mutex_release_timeout", Status: "owner_thread_still_alive"));
        }

        if (Stopped)
        {
            _release.Dispose();
        }

    }

    private void AcquireAndHold()
    {
        try
        {
            try
            {
                Acquired = _mutex.WaitOne(_wait);
            }
            catch (AbandonedMutexException)
            {
                Acquired = true;
                RecoveredAbandoned = true;
            }

            _acquisition.TrySetResult(Acquired);
            if (!Acquired)
            {
                return;
            }

            _release.Wait();
            _mutex.ReleaseMutex();
        }
        catch (Exception exception)
        {
            // The caller receives the exception. Log it here as well because no caller is waiting for a
            // release failure after acquisition.
            _diagnostics.Record(new DiagnosticEvent("instance_mutex_failed", Status: exception.GetType().Name));
            _acquisition.TrySetException(exception);
        }
        finally
        {
            _stopped.TrySetResult(true);
        }
    }
}

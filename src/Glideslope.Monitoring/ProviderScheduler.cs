using System.Collections.Immutable;
using Glideslope.Domain;

namespace Glideslope.Monitoring;

/// <summary>Coordinates independent provider polls, freshness and per-provider retry policy.</summary>
public sealed partial class ProviderScheduler : IAsyncDisposable
{
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumRefreshInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(60);
    /// <summary>Overall read budget. It exceeds provider-specific timeout and cleanup budgets so each provider
    /// can report its own failure before the scheduler times out the read.</summary>
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(75);

    /// <summary>How long DisposeAsync waits, on the injected clock, for reads
    /// and provider tasks still running after cancellation (a CLI transport's kill wait and pipe drain take up to
    /// 8 s). After that it logs what it gave up on and disposes anyway.</summary>
    public static readonly TimeSpan DisposeReadDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderSlot> _slots;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _jitterSample;
    private readonly Func<UsageObservation, bool>? _historySink;
    private readonly TimeSpan _readTimeout;
    // Window-interaction design §17 rule 2: the reset last published per "{provider}|{scope}|{bucket}",
    // so a poll whose rounded reset lands within the jitter tolerance keeps the same window. Guarded by _gate.
    private readonly Dictionary<string, DateTimeOffset> _lastResets = new(StringComparer.Ordinal);

    /// <summary>Reports scheduler decisions and failures to the app log, including stored-window reset
    /// adoption, read timeouts or abandonment, and observer failures. Event codes identify each case.</summary>
    public event EventHandler<SchedulerDiagnosticEventArgs>? DiagnosticRaised;
    private TimeSpan _refreshInterval = DefaultRefreshInterval;
    private bool _started;
    private bool _disposed;

    public ProviderScheduler(
        IReadOnlyDictionary<string, IUsageProvider> providers,
        TimeProvider? timeProvider = null,
        Func<double>? jitterSample = null,
        TimeSpan? readTimeout = null,
        Func<UsageObservation, bool>? historySink = null,
        StoredWindowLookup? storedWindowLookup = null)
    {
        _storedWindowLookup = storedWindowLookup;
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count == 0) throw new ArgumentException("At least one provider is required.", nameof(providers));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jitterSample = jitterSample ?? Random.Shared.NextDouble;
        _historySink = historySink;
        _readTimeout = readTimeout ?? DefaultReadTimeout;
        if (_readTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(readTimeout));

        _slots = new Dictionary<string, ProviderSlot>(StringComparer.Ordinal);
        foreach (var (providerId, provider) in providers)
        {
            if (!ProviderIds.IsKnown(providerId)) throw new ArgumentException("Provider map contains an unknown provider ID.", nameof(providers));
            ArgumentNullException.ThrowIfNull(provider);
            if (!_slots.TryAdd(providerId, new ProviderSlot(providerId, provider, _timeProvider.GetUtcNow())))
                throw new ArgumentException("Provider IDs must be unique.", nameof(providers));
        }
    }

    /// <summary>Raised on the polling thread; UI consumers must marshal state to their dispatcher.</summary>
    public event EventHandler<ProviderStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when a fresh observation could not be handed to the bounded history writer.</summary>
    public event EventHandler<HistoryObservationWriteFailedEventArgs>? HistoryObservationWriteFailed;

    public Task StartAsync(IEnumerable<string> enabledProviderIds, TimeSpan refreshInterval, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_started) throw new InvalidOperationException("The provider scheduler has already started.");
            _started = true;
        }

        return ApplyConfigurationAsync(enabledProviderIds, refreshInterval, cancellationToken);
    }

    /// <summary>Applies enabled selection and interval. Window visibility is deliberately irrelevant.</summary>
    public Task ApplyConfigurationAsync(
        IEnumerable<string> enabledProviderIds,
        TimeSpan refreshInterval,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(enabledProviderIds);
        if (refreshInterval < MinimumRefreshInterval || refreshInterval > MaximumRefreshInterval)
            throw new ArgumentOutOfRangeException(nameof(refreshInterval), "Refresh interval must be between five and thirty minutes.");

        var enabled = enabledProviderIds.ToHashSet(StringComparer.Ordinal);
        if (enabled.Count == 0) throw new ArgumentException("At least one provider must remain enabled.", nameof(enabledProviderIds));
        if (enabled.Any(providerId => !ProviderIds.IsKnown(providerId) || !_slots.ContainsKey(providerId)))
            throw new ArgumentException("Every enabled provider must have a registered adapter.", nameof(enabledProviderIds));

        var cancel = new List<CancellationTokenSource>();
        var start = new List<(ProviderSlot Slot, long Generation, CancellationToken Token)>();
        var changed = new List<ProviderDisplayState>();
        var evicted = new List<(string ProviderId, long StaleGeneration, int Count)>();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_started) _started = true;
            var intervalChanged = _refreshInterval != refreshInterval;
            _refreshInterval = refreshInterval;

            foreach (var slot in _slots.Values)
            {
                var shouldEnable = enabled.Contains(slot.ProviderId);
                if (slot.Enabled != shouldEnable)
                {
                    // Release this generation's seed claims before the next generation can read.
                    var staleGeneration = slot.Generation;
                    slot.Generation++;
                    var evictedCount = EvictSeedClaimsForStaleGenerationLocked(slot.ProviderId, staleGeneration);
                    if (evictedCount > 0) evicted.Add((slot.ProviderId, staleGeneration, evictedCount));
                    slot.Enabled = shouldEnable;
                    slot.IsReading = false;
                    slot.InFlight = null;
                    slot.UpdatedAtUtc = _timeProvider.GetUtcNow();
                    if (slot.GenerationCancellation is { } oldCancellation)
                    {
                        cancel.Add(oldCancellation);
                        slot.GenerationCancellation = null;
                    }

                    if (shouldEnable)
                    {
                        slot.GenerationCancellation = new CancellationTokenSource();
                        start.Add((slot, slot.Generation, slot.GenerationCancellation.Token));
                    }
                    else
                    {
                        slot.RetryAtUtc = null;
                        slot.ProviderRetryAfterUtc = null;
                        slot.ForcedFreshness = slot.Snapshot is null ? slot.ForcedFreshness : null;
                    }

                    slot.PulseWaiter();
                    changed.Add(CreateDisplayStateLocked(slot));
                }
                else if (intervalChanged && slot.Enabled)
                {
                    if (slot.ProviderRetryAfterUtc is null)
                    {
                        slot.NextDelay = slot.FailureCount == 0 ? _refreshInterval : ComputeBackoffLocked(slot);
                        slot.RetryAtUtc = _timeProvider.GetUtcNow() + slot.NextDelay;
                    }
                    slot.PulseWaiter();
                }
            }
        }

        foreach (var cancellation in cancel)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        foreach (var (providerId, staleGeneration, count) in evicted)
            PublishDiagnostic("seed_claims_evicted_for_new_generation", providerId,
                $"count={count},stale_generation={staleGeneration}");
        foreach (var state in changed) Publish(state);
        foreach (var item in start) StartLoop(item.Slot, item.Generation, item.Token);
        return Task.CompletedTask;
    }

    /// <summary>Joins an active read. A source Retry-After deadline is never bypassed by a manual retry.</summary>
    public async Task<RefreshRequestResult> RefreshAsync(string providerId, CancellationToken cancellationToken = default)
    {
        Task<ProviderDisplayState>? readTask;
        ProviderDisplayState? deferred = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            var slot = GetSlotLocked(providerId);
            if (!slot.Enabled || slot.GenerationCancellation is null)
                return new RefreshRequestResult(CreateDisplayStateLocked(slot), WasDeferredByProviderDeadline: false);

            if (slot.InFlight is not null)
            {
                readTask = slot.InFlight;
            }
            else if (slot.ProviderRetryAfterUtc is { } retryAt && retryAt > _timeProvider.GetUtcNow())
            {
                deferred = CreateDisplayStateLocked(slot);
                readTask = null;
            }
            else
            {
                readTask = null;
            }
        }

        if (deferred is not null) return new RefreshRequestResult(deferred, WasDeferredByProviderDeadline: true);
        if (readTask is null)
        {
            lock (_gate)
            {
                var slot = GetSlotLocked(providerId);
                if (!slot.Enabled || slot.GenerationCancellation is null)
                    return new RefreshRequestResult(CreateDisplayStateLocked(slot), WasDeferredByProviderDeadline: false);
                // Join an in-flight read. A later click can start a Manual read after a provider hold ends.
                if (slot.InFlight is not null)
                    readTask = slot.InFlight;
                else if (slot.ProviderRetryAfterUtc is { } retryAt && retryAt > _timeProvider.GetUtcNow())
                    return new RefreshRequestResult(CreateDisplayStateLocked(slot), WasDeferredByProviderDeadline: true);
                else
                    readTask = StartReadLocked(slot, slot.Generation, slot.GenerationCancellation.Token, manual: true);
            }
        }

        var state = await readTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RefreshRequestResult(state, WasDeferredByProviderDeadline: false);
    }

    /// <summary>Invalidates the current provider generation and removes data from the prior auth scope.</summary>
    public Task InvalidateAuthenticationAsync(string providerId)
    {
        ProviderSlot slot;
        CancellationTokenSource? oldCancellation;
        long generation;
        long staleGeneration;
        CancellationToken token;
        ProviderDisplayState state;
        int evictedCount;
        lock (_gate)
        {
            ThrowIfDisposed();
            slot = GetSlotLocked(providerId);
            staleGeneration = slot.Generation;
            slot.Generation++;
            generation = slot.Generation;
            // Release stale claims under the same lock as the generation change, before the next read starts.
            evictedCount = EvictSeedClaimsForStaleGenerationLocked(providerId, staleGeneration);
            slot.InFlight = null;
            oldCancellation = slot.GenerationCancellation;
            slot.GenerationCancellation = slot.Enabled ? new CancellationTokenSource() : null;
            token = slot.GenerationCancellation?.Token ?? CancellationToken.None;
            slot.Snapshot = null;
            slot.LastSuccessTimestamp = null;
            slot.ForcedFreshness = SnapshotFreshness.AccountChanged;
            slot.RetryAtUtc = null;
            slot.ProviderRetryAfterUtc = null;
            slot.FailureCount = 0;
            slot.PulseWaiter();
            state = CreateDisplayStateLocked(slot);
        }

        oldCancellation?.Cancel();
        oldCancellation?.Dispose();
        if (evictedCount > 0)
            PublishDiagnostic("seed_claims_evicted_for_new_generation", providerId,
                $"count={evictedCount},stale_generation={staleGeneration}");
        Publish(state);
        if (slot.Enabled) StartLoop(slot, generation, token);
        return Task.CompletedTask;
    }

    /// <summary>Returns a point-in-time copy with freshness recalculated from monotonic elapsed time.</summary>
    public ImmutableDictionary<string, ProviderDisplayState> GetStates()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _slots.Values.ToImmutableDictionary(slot => slot.ProviderId, CreateDisplayStateLocked, StringComparer.Ordinal);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var disposeStartedAt = _timeProvider.GetTimestamp();
        Task[] loops;
        CancellationTokenSource[] cancellations;
        IAsyncDisposable[] asyncProviders;
        IDisposable[] syncProviders;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var slot in _slots.Values)
            {
                // No read can apply after disposal, so release this generation's claims with the generation bump.
                var staleGeneration = slot.Generation;
                slot.Enabled = false;
                slot.Generation++;
                EvictSeedClaimsForStaleGenerationLocked(slot.ProviderId, staleGeneration);
                slot.IsReading = false;
                slot.PulseWaiter();
            }
            cancellations = _slots.Values.Select(slot => slot.GenerationCancellation).OfType<CancellationTokenSource>().ToArray();
            foreach (var slot in _slots.Values) slot.GenerationCancellation = null;
            loops = _slots.Values.Select(slot => slot.LoopTask).OfType<Task>().ToArray();
            asyncProviders = _slots.Values.Select(slot => slot.Provider).OfType<IAsyncDisposable>().Distinct().ToArray();
            syncProviders = _slots.Values.Select(slot => slot.Provider).OfType<IDisposable>()
                .Where(provider => provider is not IAsyncDisposable).Distinct().ToArray();
        }

        foreach (var cancellation in cancellations)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        // Report pending work so slow shutdowns can be diagnosed from the log.
        PublishDiagnostic("scheduler_dispose_awaiting_loops", "scheduler",
            $"count={loops.Length},pending={loops.Count(loop => !loop.IsCompleted)}");
        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
            // Manual reads do not belong to a polling loop. Track all scheduler reads and abandoned provider
            // tasks so dependent services are not disposed while they are still running.
            await WaitForPendingWorkAsync().ConfigureAwait(false);
        }
        finally
        {
            foreach (var provider in asyncProviders) await provider.DisposeAsync().ConfigureAwait(false);
            foreach (var provider in syncProviders) provider.Dispose();
            var elapsedMs = (long)_timeProvider.GetElapsedTime(disposeStartedAt, _timeProvider.GetTimestamp()).TotalMilliseconds;
            PublishDiagnostic("scheduler_dispose_providers_disposed", "scheduler",
                $"async_count={asyncProviders.Length},sync_count={syncProviders.Length},elapsed_ms={elapsedMs}");
        }
    }

    /// <summary>Waits at most <see cref="DisposeReadDrainTimeout"/> on the
    /// injected clock for every read and provider task still running. Their outcomes were already handled (or
    /// are logged by the abandoned-read continuation); only completion matters here.</summary>
    private async Task WaitForPendingWorkAsync()
    {
        (string ProviderId, Task[] Work)[] pending;
        lock (_gate)
        {
            pending = _slots.Values.Where(slot => slot.PendingWork.Count > 0)
                .Select(slot => (slot.ProviderId, slot.PendingWork.ToArray()))
                .ToArray();
        }
        if (pending.Length == 0) return;
        foreach (var (providerId, work) in pending)
            PublishDiagnostic("scheduler_dispose_waiting", providerId, $"pending={work.Length}");

        var settled = Task.WhenAll(pending.SelectMany(item => item.Work).Select(IgnoreOutcome));
        try
        {
            await settled.WaitAsync(DisposeReadDrainTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            foreach (var (providerId, _) in pending)
            {
                int left;
                lock (_gate) left = _slots[providerId].PendingWork.Count;
                if (left > 0)
                    PublishDiagnostic("scheduler_dispose_read_abandoned", providerId,
                        $"pending={left},waited_ms={(long)DisposeReadDrainTimeout.TotalMilliseconds}");
            }
        }
    }

    private static Task IgnoreOutcome(Task task) =>
        task.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Records <paramref name="task"/> as running work of
    /// <paramref name="slot"/> until it completes. A fault is observed here; it is logged here only when
    /// <paramref name="logFault"/> (a provider task's fault is already logged by the read that awaited it).</summary>
    private void TrackPendingWork(ProviderSlot slot, Task task, bool logFault)
    {
        lock (_gate) slot.PendingWork.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_gate) slot.PendingWork.Remove(completed);
            if (completed.IsFaulted && completed.Exception?.InnerException is { } fault && logFault)
                PublishDiagnostic("scheduler_read_task_faulted", slot.ProviderId, fault.GetType().Name);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void StartLoop(ProviderSlot slot, long generation, CancellationToken cancellationToken)
    {
        var loop = RunLoopAsync(slot, generation, cancellationToken);
        lock (_gate)
        {
            if (slot.Generation == generation && slot.Enabled && !_disposed) slot.LoopTask = loop;
        }
    }

    private async Task RunLoopAsync(ProviderSlot slot, long generation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task<ProviderDisplayState> readTask;
                lock (_gate)
                {
                    if (_disposed || !slot.Enabled || slot.Generation != generation) return;
                    readTask = slot.InFlight ?? StartReadLocked(slot, generation, cancellationToken, manual: false);
                }

                await readTask.ConfigureAwait(false);
                await WaitUntilNextAttemptAsync(slot, generation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <param name="manual">True only when RefreshAsync starts the read (the user
    /// pressed Refresh/Retry); every loop-started read passes false.</param>
    private Task<ProviderDisplayState> StartReadLocked(ProviderSlot slot, long generation, CancellationToken generationToken, bool manual)
    {
        var completion = new TaskCompletionSource<ProviderDisplayState>(TaskCreationOptions.RunContinuationsAsynchronously);
        slot.InFlight = completion.Task;
        slot.IsReading = true;
        slot.UpdatedAtUtc = _timeProvider.GetUtcNow();
        var readingState = CreateDisplayStateLocked(slot);
        // Track manual reads because they do not belong to a polling loop.
        TrackPendingWork(slot, Task.Run(() => ExecuteReadAsync(slot, generation, generationToken, completion, manual)), logFault: true);
        Publish(readingState);
        return completion.Task;
    }

    private async Task ExecuteReadAsync(
        ProviderSlot slot,
        long generation,
        CancellationToken generationToken,
        TaskCompletionSource<ProviderDisplayState> completion,
        bool manual)
    {
        var startedAt = _timeProvider.GetTimestamp();
        ProviderReadResult result;
        bool timedOut;
        try
        {
            (result, timedOut) = await RunProviderAttemptAsync(slot, generationToken,
                manual ? ProviderReadIntent.Manual : ProviderReadIntent.Scheduled).ConfigureAwait(false);
            if (ShouldRetryAutomatically(slot, generation, manual, timedOut, result))
            {
                // Retry one scheduled no-answer failure before publishing it. Keep the last data visible while
                // retrying; only a hang latch from this launch may be cleared, never a sign-in or spend hold.
                PublishDiagnostic("provider_read_auto_retry", slot.ProviderId,
                    $"first={result.Status},code={result.SafeErrorCode ?? "none"}");
                (result, timedOut) = await RunProviderAttemptAsync(slot, generationToken, ProviderReadIntent.AutomaticRetry).ConfigureAwait(false);
                PublishDiagnostic("provider_read_auto_retry_finished", slot.ProviderId,
                    $"status={result.Status},code={result.SafeErrorCode ?? "none"}");
            }
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            CompleteCancelledRead(slot, generation, completion);
            return;
        }

        // Off the lock: the store is asked once per bucket per launch for a window to continue.
        SeedOutcome? seed = null;
        if (result.Status == ProviderStatus.Ready && result.Snapshot is { } toSeed)
        {
            try
            {
                seed = await SeedResetsFromStoreAsync(toSeed, generation, generationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
            {
                // Complete the read task on cancellation so disposal can finish. Store-seed claims are
                // released by SeedResetsFromStoreAsync.
                CompleteCancelledRead(slot, generation, completion);
                return;
            }
        }
        var seeded = seed?.Seeded;
        var seedApplied = false;

        ProviderDisplayState? changed = null;
        ImmutableArray<UsageObservation> observations = [];
        lock (_gate)
        {
            if (!_disposed && slot.Enabled && slot.Generation == generation)
            {
                var duration = _timeProvider.GetElapsedTime(startedAt, _timeProvider.GetTimestamp());
                if (result.Snapshot is { } resultSnapshot && resultSnapshot.ProviderId != slot.ProviderId)
                    result = new ProviderReadResult(ProviderStatus.UnknownError, safeErrorCode: "provider_snapshot_mismatch");

                slot.IsReading = false;
                slot.Status = result.Status;
                slot.Actions = result.Actions;
                slot.SafeErrorCode = result.SafeErrorCode;
                slot.LastAttemptDuration = duration;
                slot.UpdatedAtUtc = _timeProvider.GetUtcNow();
                slot.ProviderRetryAfterUtc = result.RetryAfterUtc is { } providerDeadline && providerDeadline > slot.UpdatedAtUtc
                    ? providerDeadline
                    : null;

                if (result.Status == ProviderStatus.Ready && result.Snapshot is { } rawSnapshot)
                {
                    // Window-interaction design §17: stabilize every reset (minute granularity, jitter
                    // tolerance, not-started detection) before the snapshot is published or recorded, so
                    // the card, the coordinator and the store all see one identity per real window.
                    var freshSnapshot = WindowIdentityPolicy.Stabilize(rawSnapshot,
                        bucketId => PreviousReset(ResetKey(rawSnapshot, bucketId), seeded));
                    foreach (var bucket in freshSnapshot.Buckets)
                    {
                        if (bucket.WindowStarted && bucket.ResetAtUtc is { } reset)
                            _lastResets[ResetKey(freshSnapshot, bucket.Id)] = reset;
                    }
                    slot.Snapshot = freshSnapshot;
                    observations = CreateObservations(freshSnapshot);
                    slot.LastSuccessTimestamp = _timeProvider.GetTimestamp();
                    slot.ForcedFreshness = null;
                    slot.FailureCount = 0;
                    slot.NextDelay = _refreshInterval;
                    slot.ProviderRetryAfterUtc = null;
                    seedApplied = true;
                }
                else
                {
                    if (result.Snapshot is { } rawLastGood)
                    {
                        // Every published snapshot goes through the same stabilization, so a
                        // restored one cannot carry an unrounded identity that the next Ready poll would
                        // count as a rollover. The remembered resets are not updated from a restore.
                        var lastGood = WindowIdentityPolicy.Stabilize(rawLastGood,
                            bucketId => PreviousReset(ResetKey(rawLastGood, bucketId), seeded));
                        if (slot.Snapshot is null)
                        {
                            slot.Snapshot = lastGood;
                            slot.LastSuccessTimestamp = null;
                            slot.ForcedFreshness = SnapshotFreshness.RestoredHistorical;
                        }
                        else if (!StringComparer.Ordinal.Equals(slot.Snapshot.AccountScope, lastGood.AccountScope))
                        {
                            slot.Snapshot = null;
                            slot.LastSuccessTimestamp = null;
                            slot.ForcedFreshness = SnapshotFreshness.AccountChanged;
                        }
                    }

                    if (result.Status is ProviderStatus.Offline or ProviderStatus.RateLimited or ProviderStatus.UnknownError)
                    {
                        slot.FailureCount++;
                        slot.NextDelay = slot.ProviderRetryAfterUtc is null ? ComputeBackoffLocked(slot) : slot.ProviderRetryAfterUtc.Value - slot.UpdatedAtUtc;
                    }
                    else
                    {
                        slot.FailureCount = 0;
                        slot.NextDelay = _refreshInterval;
                    }
                }

                if (slot.ProviderRetryAfterUtc is not null)
                    slot.RetryAtUtc = slot.ProviderRetryAfterUtc;
                else
                    slot.RetryAtUtc = slot.UpdatedAtUtc + slot.NextDelay;
                if (timedOut) slot.SafeErrorCode = "read_timeout";
                slot.InFlight = null;
                slot.PulseWaiter();
                changed = CreateDisplayStateLocked(slot);
            }
            // Count answered lookups as consulted only when this read was applied.
            SettleSeedClaimsLocked(seed, seedApplied, generation);
        }

        if (seed is not null) PublishSeedOutcome(slot.ProviderId, seed, seedApplied);
        if (changed is not null)
        {
            RecordObservations(observations);
            Publish(changed);
        }
        completion.TrySetResult(changed ?? GetStateIfAvailable(slot));
    }

    /// <summary>One call of the provider under the slot's read gate, with its own read timeout. Returns the
    /// provider's result, or a typed failure when the scheduler's timeout ends it or the provider throws; a
    /// generation cancellation propagates as <see cref="OperationCanceledException"/>. Retries use the same
    /// gate, budget and abandonment rules.</summary>
    private async Task<(ProviderReadResult Result, bool TimedOut)> RunProviderAttemptAsync(
        ProviderSlot slot, CancellationToken generationToken, ProviderReadIntent intent)
    {
        ProviderReadResult result;
        var timedOut = false;
        using var timeoutSource = new CancellationTokenSource(_readTimeout, _timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(generationToken, timeoutSource.Token);
        var acquiredReadGate = false;
        Task<ProviderReadResult>? providerTask = null;
        try
        {
            await slot.ReadGate.WaitAsync(linkedSource.Token).ConfigureAwait(false);
            acquiredReadGate = true;
            // Register pending work before calling the provider so concurrent disposal cannot miss a read
            // that has started but has not yet been tracked.
            var providerCallTracked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TrackPendingWork(slot, providerCallTracked.Task, logFault: false);
            // A provider that distinguishes user-initiated reads gets the intent; any
            // other provider keeps the plain ReadAsync it always had.
            providerTask = StartProviderRead(slot.Provider, intent, linkedSource.Token);
            _ = providerTask.ContinueWith(static (_, state) => ((TaskCompletionSource)state!).TrySetResult(),
                providerCallTracked, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // Bound the wait by the linked token. If a provider ignores cancellation, keep its read gate until
            // the task ends so a second process for that provider cannot start alongside it.
            result = await providerTask.WaitAsync(linkedSource.Token).ConfigureAwait(false);
            if (result is null) result = new ProviderReadResult(ProviderStatus.UnknownError, safeErrorCode: "provider_returned_no_result");
        }
        catch (OperationCanceledException) when (generationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            timedOut = true;
            result = new ProviderReadResult(ProviderStatus.UnknownError, safeErrorCode: "read_timeout");
            PublishDiagnostic("provider_read_timeout", slot.ProviderId,
                $"budget_ms={(long)_readTimeout.TotalMilliseconds},gate_acquired={acquiredReadGate}");
        }
        catch (Exception ex)
        {
            result = new ProviderReadResult(ProviderStatus.UnknownError, safeErrorCode: "provider_read_failed");
            PublishDiagnostic("provider_read_failed", slot.ProviderId, ex.GetType().Name);
        }
        finally
        {
            if (acquiredReadGate)
            {
                if (providerTask is null || providerTask.IsCompleted)
                    slot.ReadGate.Release();
                else
                    ReleaseGateAfterAbandonedRead(slot, providerTask, generationToken.IsCancellationRequested ? "cancelled" : "timeout");
            }
        }
        return (result, timedOut);
    }

    /// <summary>Whether a finished attempt is retried at once (AutomaticRetry) before anything
    /// is published. Only a scheduled read (a user's own click is shown as it answered); only "no answer"
    /// (Offline or UnknownError) with no provider Retry-After; never the scheduler's own read timeout, which
    /// fires only when a provider overran its own budget and would leave the retry waiting on the gate that
    /// provider still holds; and only when the card was showing data or nothing yet (last status Ready or none),
    /// so each failure episode gets one automatic retry and a provider that keeps failing waits for the
    /// user's Refresh, as its latch intends.</summary>
    private bool ShouldRetryAutomatically(ProviderSlot slot, long generation, bool manual, bool timedOut, ProviderReadResult result)
    {
        if (manual || timedOut) return false;
        if (result.Status is not (ProviderStatus.Offline or ProviderStatus.UnknownError)) return false;
        if (result.RetryAfterUtc is not null) return false;
        lock (_gate)
        {
            return !_disposed && slot.Enabled && slot.Generation == generation &&
                   slot.Status is null or ProviderStatus.Ready;
        }
    }

    private void CompleteCancelledRead(ProviderSlot slot, long generation, TaskCompletionSource<ProviderDisplayState> completion)
    {
        ProviderDisplayState state;
        lock (_gate)
        {
            if (slot.Generation == generation)
            {
                slot.IsReading = false;
                slot.InFlight = null;
                slot.PulseWaiter();
            }
            state = CreateDisplayStateLocked(slot);
        }
        completion.TrySetResult(state);
    }

    private async Task WaitUntilNextAttemptAsync(ProviderSlot slot, long generation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Task signal;
            TimeSpan delay;
            lock (_gate)
            {
                if (_disposed || !slot.Enabled || slot.Generation != generation) return;
                signal = slot.Waiter.Task;
                delay = slot.RetryAtUtc is { } retryAt
                    ? retryAt - _timeProvider.GetUtcNow()
                    : slot.NextDelay;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            }

            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(delay, _timeProvider, waitCancellation.Token);
            var signalTask = signal.WaitAsync(cancellationToken);
            var completed = await Task.WhenAny(delayTask, signalTask).ConfigureAwait(false);
            if (completed == signalTask)
            {
                waitCancellation.Cancel();
                try { await delayTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested) { }
                await signalTask.ConfigureAwait(false);
                continue;
            }

            await delayTask.ConfigureAwait(false);
            return;
        }
    }

    private TimeSpan ComputeBackoffLocked(ProviderSlot slot)
    {
        var exponent = Math.Clamp(slot.FailureCount - 1, 0, 30);
        var baseTicks = Math.Min(MaximumBackoff.Ticks, _refreshInterval.Ticks * Math.Pow(2, exponent));
        var sample = _jitterSample();
        if (!double.IsFinite(sample) || sample is < 0 or > 1) sample = 0.5;
        var jitteredTicks = baseTicks * (0.9 + sample * 0.2);
        return TimeSpan.FromTicks((long)Math.Min(MaximumBackoff.Ticks, Math.Max(1, jitteredTicks)));
    }

    private ProviderDisplayState GetStateIfAvailable(ProviderSlot slot)
    {
        lock (_gate) return CreateDisplayStateLocked(slot);
    }

    private ProviderDisplayState CreateDisplayStateLocked(ProviderSlot slot)
    {
        var freshness = GetFreshnessLocked(slot);
        return new ProviderDisplayState(
            slot.ProviderId,
            slot.Generation,
            slot.Enabled,
            slot.IsReading,
            slot.Status,
            slot.Snapshot,
            freshness,
            slot.RetryAtUtc,
            slot.Actions,
            slot.SafeErrorCode,
            slot.LastAttemptDuration,
            slot.UpdatedAtUtc);
    }

    private SnapshotFreshness? GetFreshnessLocked(ProviderSlot slot)
    {
        if (slot.Status is ProviderStatus.AuthenticationExpired or ProviderStatus.NeedsSignIn)
            return SnapshotFreshness.AuthenticationFailed;
        if (slot.ForcedFreshness is { } forced) return forced;
        if (slot.Snapshot is null) return null;
        if (slot.LastSuccessTimestamp is not { } lastSuccess) return SnapshotFreshness.RestoredHistorical;
        var horizon = TimeSpan.FromTicks(_refreshInterval.Ticks * 2);
        return _timeProvider.GetElapsedTime(lastSuccess, _timeProvider.GetTimestamp()) >= horizon
            ? SnapshotFreshness.Stale
            : SnapshotFreshness.Fresh;
    }

    private ProviderSlot GetSlotLocked(string providerId)
    {
        if (!_slots.TryGetValue(providerId, out var slot))
            throw new ArgumentException("Provider is not registered.", nameof(providerId));
        return slot;
    }

    private void Publish(ProviderDisplayState state)
    {
        var handlers = StateChanged;
        if (handlers is null) return;
        var args = new ProviderStateChangedEventArgs(state);
        foreach (EventHandler<ProviderStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception ex)
            {
                /* A presentation callback must not stop an independent provider poll. */
                // Log observer failures with the state's
                // provider. PublishDiagnostic swallows its own handler's failure, so this cannot loop.
                PublishDiagnostic("scheduler_state_handler_failed", state.ProviderId, ex.GetType().Name);
            }
        }
    }

    private static string ResetKey(AccountSnapshot snapshot, string bucketId) =>
        $"{snapshot.ProviderId}|{snapshot.AccountScope}|{bucketId}";

    /// <summary>Under _gate, returns the reset published earlier this launch or the reset seeded from storage.
    /// A stored reset takes precedence because it continues the existing window.</summary>
    private DateTimeOffset? PreviousReset(string key, Dictionary<string, DateTimeOffset>? seeded)
    {
        if (seeded is not null && seeded.TryGetValue(key, out var stored)) return stored;
        return _lastResets.TryGetValue(key, out var published) ? published : null;
    }

    /// <summary>Starts the provider's read as a Task so it can be awaited with a bound
    /// and tracked after being abandoned. A provider that throws before returning its task is reported through a
    /// faulted task, exactly as if it had thrown inside it.</summary>
    private static Task<ProviderReadResult> StartProviderRead(IUsageProvider provider, ProviderReadIntent intent, CancellationToken cancellationToken)
    {
        try
        {
            return provider is IIntentAwareUsageProvider intentAware
                ? intentAware.ReadAsync(intent, cancellationToken).AsTask()
                : provider.ReadAsync(cancellationToken).AsTask();
        }
        catch (Exception ex)
        {
            return Task.FromException<ProviderReadResult>(ex);
        }
    }

    /// <summary>The read gate stays held by a provider task the scheduler stopped
    /// waiting for, and is released when that task really ends, so a provider never runs two reads at once.</summary>
    private void ReleaseGateAfterAbandonedRead(ProviderSlot slot, Task<ProviderReadResult> providerTask, string reason)
    {
        var abandonedAt = _timeProvider.GetTimestamp();
        PublishDiagnostic("provider_read_abandoned", slot.ProviderId, $"reason={reason}");
        _ = providerTask.ContinueWith(completed =>
        {
            slot.ReadGate.Release();
            var after = (long)_timeProvider.GetElapsedTime(abandonedAt, _timeProvider.GetTimestamp()).TotalMilliseconds;
            var outcome = completed.IsFaulted
                ? completed.Exception?.InnerException?.GetType().Name ?? "Faulted"
                : completed.Status.ToString();
            PublishDiagnostic("provider_read_abandoned_finished", slot.ProviderId, $"reason={reason},outcome={outcome},after_ms={after}");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void PublishDiagnostic(string code, string providerId, string status)
    {
        var handlers = DiagnosticRaised;
        if (handlers is null) return;
        try { handlers(this, new SchedulerDiagnosticEventArgs(code, providerId, status)); }
        catch (Exception) { /* A log sink must not stop a provider poll. */ }
    }

    private static ImmutableArray<UsageObservation> CreateObservations(AccountSnapshot snapshot)
    {
        var observations = ImmutableArray.CreateBuilder<UsageObservation>(snapshot.Buckets.Length);
        foreach (var bucket in snapshot.Buckets)
        {
            var window = UsageWindowIdentityFactory.From(snapshot, bucket);
            if (window is null) continue;
            try
            {
                observations.Add(new UsageObservation(window, snapshot.ObservedAtUtc, bucket.RemainingFraction));
            }
            catch (ArgumentOutOfRangeException)
            {
                // A source timestamp outside its reported window cannot form a real history sample.
            }
        }
        return observations.ToImmutable();
    }

    private void RecordObservations(ImmutableArray<UsageObservation> observations)
    {
        if (_historySink is null) return;
        foreach (var observation in observations)
        {
            try
            {
                if (!_historySink(observation))
                    PublishHistoryObservationWriteFailed("history_unavailable");
            }
            catch (Exception)
            {
                PublishHistoryObservationWriteFailed("history_sink_failed");
            }
        }
    }

    private void PublishHistoryObservationWriteFailed(string safeErrorCode)
    {
        var handlers = HistoryObservationWriteFailed;
        if (handlers is null) return;
        var args = new HistoryObservationWriteFailedEventArgs(safeErrorCode);
        foreach (EventHandler<HistoryObservationWriteFailedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception ex)
            {
                /* A history status observer must not interrupt provider polling. */
                // Log observer failures. The event carries
                // no provider, so the diagnostic names the scheduler.
                PublishDiagnostic("scheduler_history_handler_failed", "scheduler", ex.GetType().Name);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProviderScheduler));
    }

    private sealed class ProviderSlot(string providerId, IUsageProvider provider, DateTimeOffset nowUtc)
    {
        public string ProviderId { get; } = providerId;
        public IUsageProvider Provider { get; } = provider;
        public bool Enabled { get; set; }
        public bool IsReading { get; set; }
        public long Generation { get; set; }
        public ProviderStatus? Status { get; set; }
        public AccountSnapshot? Snapshot { get; set; }
        public SnapshotFreshness? ForcedFreshness { get; set; }
        public long? LastSuccessTimestamp { get; set; }
        public DateTimeOffset? RetryAtUtc { get; set; }
        public DateTimeOffset? ProviderRetryAfterUtc { get; set; }
        public TimeSpan NextDelay { get; set; } = DefaultRefreshInterval;
        public TimeSpan? LastAttemptDuration { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; } = nowUtc;
        public ImmutableArray<ProviderAction> Actions { get; set; } = [];
        public string? SafeErrorCode { get; set; }
        public int FailureCount { get; set; }
        public CancellationTokenSource? GenerationCancellation { get; set; }
        public Task<ProviderDisplayState>? InFlight { get; set; }
        public Task? LoopTask { get; set; }
        public SemaphoreSlim ReadGate { get; } = new(1, 1);
        /// <summary>This slot's running read tasks and provider tasks. Under _gate.</summary>
        public HashSet<Task> PendingWork { get; } = [];
        public TaskCompletionSource Waiter { get; private set; } = NewWaiter();

        public void PulseWaiter()
        {
            var prior = Waiter;
            Waiter = NewWaiter();
            prior.TrySetResult();
        }

        private static TaskCompletionSource NewWaiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class HistoryObservationWriteFailedEventArgs(string safeErrorCode) : EventArgs
{
    public string SafeErrorCode { get; } = safeErrorCode;
}

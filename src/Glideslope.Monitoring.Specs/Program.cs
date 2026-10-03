using System.Collections.Immutable;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.Monitoring.Specs;

internal static class Program
{
    private static async Task<int> Main()
    {
        await Run(ManualRefreshJoinsAndPollingDoesNotOverlap);
        await Run(SuccessfulReadsAloneCreateNormalizedHistoryObservations);
        await Run(DisabledGenerationCannotPublishLateResult);
        await Run(RetryAfterIsAuthoritativeAndManualRetryIsDeferred);
        await Run(LocalBackoffUsesRefreshIntervalAndCaps);
        await Run(ReadTimeoutUsesInjectedTimeProvider);
        await Run(FreshnessExpiresOnMonotonicHorizon);
        await Run(IntentAwareProviderReceivesReadIntent);
        await Run(JitteredResetsShareOneWindowAndUnstartedWindowsRecordNothing);
        await Run(FirstPollContinuesAStoredWindowWithinTolerance);
        await Run(DisposeCompletesWhileStoreLookupIsPending);
        await Run(ProviderIgnoringCancellationDoesNotPinTheSlotOrDispose);
        await Run(DisposeWaitsForAManualReadStartedByRefresh);
        await Run(FailedStoreLookupLeavesTheBucketEligible);
        await Run(CancelledStoreLookupLeavesTheBucketEligible);
        await Run(DiscardedStoreLookupLeavesTheBucketEligible);
        await Run(ThrowingHandlersAreLoggedAndPollingContinues);
        await Run(ScheduledFailureIsRetriedOnceBeforeItIsShown);
        await Run(AutomaticRetryIsSkippedForSignInManualReadsAndRepeatFailures);
        Console.WriteLine("Glideslope monitoring specs passed.");
        return 0;
    }

    /// <summary>Runs one spec with a start/finish log line naming it, so a hang or a failure names which
    /// spec it happened in instead of leaving the whole run silent until it exits.</summary>
    private static async Task Run(Func<Task> spec, [System.Runtime.CompilerServices.CallerArgumentExpression("spec")] string? name = null)
    {
        Console.WriteLine($"[spec] {name} starting");
        var startedAt = DateTimeOffset.UtcNow;
        await spec();
        Console.WriteLine($"[spec] {name} passed in {(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:F0} ms");
    }

    /// <summary>A provider may ignore cancellation. After its read times out, the slot is released, later reads
    /// remain serialized, and disposal is bounded by DisposeReadDrainTimeout.</summary>
    private static async Task ProviderIgnoringCancellationDoesNotPinTheSlotOrDispose()
    {
        var clock = new ManualTimeProvider(Epoch);
        var stuck = NewGate();
        var provider = new ScriptedProvider(ProviderIds.Codex, _ => stuck.Task);   // never looks at its token
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<SchedulerDiagnosticEventArgs>();
        var diagnosticsPulse = new Pulse();
        var scheduler = NewScheduler(provider, clock, timeout: TimeSpan.FromSeconds(45));
        scheduler.DiagnosticRaised += (_, args) => { diagnostics.Enqueue(args); diagnosticsPulse.Signal(); };
        await scheduler.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => provider.CallCount == 1, "the stuck read did not start", provider.Pulse);
        // The read's own read-timeout CancellationTokenSource is created before the provider is ever invoked
        // (ExecuteReadAsync's synchronous prefix, ahead of StartProviderRead), so by the time CallCount == 1
        // that timer is already armed: one AdvanceAsync call for the exact 45 s read budget is all this needs.
        await clock.AdvanceAsync(TimeSpan.FromSeconds(45));
        await WaitForStatesAsync(scheduler,
            states => states[ProviderIds.Codex].SafeErrorCode == "read_timeout" && !states[ProviderIds.Codex].IsReading,
            "a provider that ignores cancellation pinned its read past the timeout");
        await WaitForAsync(() => diagnostics.Any(d => d.Code == "provider_read_abandoned" && d.Status == "reason=timeout"),
            "the abandoned read was not logged", diagnosticsPulse);

        var refresh = scheduler.RefreshAsync(ProviderIds.Codex);
        // Unlike the read above, this manual read never reaches the provider (it blocks forever on the read
        // gate the abandoned read still holds), so there is no CallCount signal for "its own read-timeout
        // timer is armed". That timer is created inside a Task.Run body, so the spec waits for the clock to
        // hold an active timer due exactly 45 s from now (the polling loop's own timers are minutes away),
        // then advances once by exactly the read budget.
        // The timeout is armed inside Task.Run; wait for that timer before advancing the injected clock.
        await WaitForAsync(() => clock.HasActiveTimerDueIn(TimeSpan.FromSeconds(45)),
            "the manual read's timeout was not armed", clock.TimerChanged);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(45));
        Assert((await refresh).State.SafeErrorCode == "read_timeout", "a read that cannot get the gate ends as a read timeout");
        Assert(provider.CallCount == 1, "no second provider read ran beside the stuck one");

        var disposing = scheduler.DisposeAsync().AsTask();
        // Unlike the read timeouts above, DisposeAsync's own unwind (Pulse the polling loop's waiter, cancel
        // its generation token, await the loop task) needs no clock advance at all: it is a pure continuation
        // chain. Only once that unwind reaches WaitForPendingWorkAsync does it arm the 10 s drain timer this
        // spec is proving. Waiting for it first, then advancing by exactly the drain timeout once, replaces
        // guessing how many clock advances the unwind needs.
        // The waiting diagnostic is logged before the drain timer is created, so wait for the timer itself
        // before advancing the clock.
        await WaitForAsync(() => diagnostics.Any(d => d.Code == "scheduler_dispose_waiting"),
            "dispose did not reach its read-drain wait", diagnosticsPulse);
        await WaitForAsync(() => clock.HasActiveTimerDueIn(TimeSpan.FromSeconds(10)),
            "dispose's read-drain timer was not armed", clock.TimerChanged);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(10));
        await disposing;
        Assert(diagnostics.Any(d => d.Code == "scheduler_dispose_read_abandoned" && d.ProviderId == ProviderIds.Codex),
            "dispose logs the read it gave up on");

        stuck.SetResult(Ready(provider, clock, "late-scope"));
        await WaitForAsync(() => diagnostics.Any(d => d.Code == "provider_read_abandoned_finished"),
            "the abandoned task's end was not logged", diagnosticsPulse);
    }

    /// <summary>A read started by RefreshAsync is not owned by a polling loop, so DisposeAsync must wait
    /// for it before disposing providers and the history store.</summary>
    private static async Task DisposeWaitsForAManualReadStartedByRefresh()
    {
        var clock = new ManualTimeProvider(Epoch);
        var sawCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishAfterCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DisposalRecordingProvider? provider = null;
        provider = new DisposalRecordingProvider(ProviderIds.Codex, async (call, token) =>
        {
            if (call == 1) return Ready(ProviderIds.Codex, clock, "scope-e10");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                sawCancellation.TrySetResult();
                await finishAfterCancel.Task;   // the kill and drain a CLI transport does after a cancel
                throw;
            }
            throw new InvalidOperationException("Unreachable.");
        });
        var scheduler = NewScheduler(provider, clock);
        await scheduler.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(5));
        await WaitForStatesAsync(scheduler, states => states[ProviderIds.Codex].Status == ProviderStatus.Ready,
            "the first poll did not complete");
        var refresh = scheduler.RefreshAsync(ProviderIds.Codex);
        await WaitForAsync(() => provider.CallCount == 2, "the manual read did not start", provider.Pulse);

        var disposing = scheduler.DisposeAsync().AsTask();
        await sawCancellation.Task;
        finishAfterCancel.SetResult();
        await disposing;
        Assert(provider.Disposed, "the provider is disposed");
        Assert(!provider.ReadActiveWhenDisposed, "the provider was disposed while the manual read was still running");
        await refresh;
    }

    /// <summary>A failed history lookup leaves the bucket eligible for another lookup. Once a lookup returns
    /// a stored window, later polls reuse that result.</summary>
    private static async Task FailedStoreLookupLeavesTheBucketEligible()
    {
        var clock = new ManualTimeProvider(Epoch);
        var (stored, reportedReset, provider) = StoredWindowFixture(clock);
        var lookups = 0;
        var observations = new System.Collections.Concurrent.ConcurrentQueue<UsageObservation>();
        var observationsPulse = new Pulse();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<SchedulerDiagnosticEventArgs>();
        await using var scheduler = new ProviderScheduler(new Dictionary<string, IUsageProvider> { [ProviderIds.Claude] = provider },
            clock, () => 0.5, historySink: observation => { observations.Enqueue(observation); observationsPulse.Signal(); return true; },
            storedWindowLookup: (_, _, _, _) => Interlocked.Increment(ref lookups) == 1
                ? Task.FromException<ImmutableArray<UsageWindowIdentity>>(new InvalidOperationException("synthetic store unavailable"))
                : Task.FromResult(ImmutableArray.Create(stored)));
        scheduler.DiagnosticRaised += (_, args) => diagnostics.Enqueue(args);

        await scheduler.StartAsync([ProviderIds.Claude], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => observations.Count == 1, "the first poll did not record an observation", observationsPulse);
        Assert(observations.ToArray()[0].Window.ResetAtUtc == reportedReset, "with the store unavailable the reported reset stands");
        Assert(diagnostics.Any(d => d.Code == "window_reset_store_lookup_failed" && d.Status.Contains("InvalidOperationException") &&
                                    d.Status.Contains("retry=next_poll")), "the failed lookup is logged with its type");

        await scheduler.RefreshAsync(ProviderIds.Claude);
        await WaitForAsync(() => observations.Count >= 2, "the second poll did not record an observation", observationsPulse);
        Assert(observations.ToArray()[1].Window == stored, "the next poll asks again and continues the stored window");
        await scheduler.RefreshAsync(ProviderIds.Claude);
        await WaitForAsync(() => observations.Count >= 3, "the third poll did not record an observation", observationsPulse);
        Assert(observations.ToArray()[2].Window == stored, "the adopted window is kept");
        Assert(Volatile.Read(ref lookups) == 2, $"one failed and one answered lookup, then no more (got {lookups})");
    }

    /// <summary>A lookup cancelled with its read leaves the bucket eligible; a later poll can retrieve the
    /// stored window.</summary>
    private static async Task CancelledStoreLookupLeavesTheBucketEligible()
    {
        var clock = new ManualTimeProvider(Epoch);
        var (stored, _, provider) = StoredWindowFixture(clock);
        var lookups = 0;
        var firstLookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = new System.Collections.Concurrent.ConcurrentQueue<UsageObservation>();
        await using var scheduler = new ProviderScheduler(new Dictionary<string, IUsageProvider> { [ProviderIds.Claude] = provider },
            clock, () => 0.5, historySink: observation => { observations.Enqueue(observation); return true; },
            storedWindowLookup: async (_, _, _, token) =>
            {
                if (Interlocked.Increment(ref lookups) == 1)
                {
                    firstLookupStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return ImmutableArray.Create(stored);
            });

        await scheduler.StartAsync([ProviderIds.Claude], TimeSpan.FromMinutes(5));
        await firstLookupStarted.Task;
        await scheduler.InvalidateAuthenticationAsync(ProviderIds.Claude);
        // Invalidating authentication releases this key's seed claim under the scheduler lock before
        // the next generation reads, so the next poll consults the store again.
        await RefreshUntilAsync(scheduler, () => observations.Any(o => o.Window == stored),
            "a later poll did not continue the stored window after a cancelled lookup");
        Assert(Volatile.Read(ref lookups) == 2, $"one cancelled and one answered lookup (got {lookups})");
    }

    /// <summary>A lookup result from a discarded generation is ignored, leaves the bucket eligible, and logs
    /// window_reset_seed_discarded.</summary>
    private static async Task DiscardedStoreLookupLeavesTheBucketEligible()
    {
        var clock = new ManualTimeProvider(Epoch);
        var (stored, _, provider) = StoredWindowFixture(clock);
        var lookups = 0;
        ProviderScheduler? schedulerReference = null;
        var observations = new System.Collections.Concurrent.ConcurrentQueue<UsageObservation>();
        var observationsPulse = new Pulse();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<SchedulerDiagnosticEventArgs>();
        var diagnosticsPulse = new Pulse();
        await using var scheduler = new ProviderScheduler(new Dictionary<string, IUsageProvider> { [ProviderIds.Claude] = provider },
            clock, () => 0.5, historySink: observation => { observations.Enqueue(observation); observationsPulse.Signal(); return true; },
            storedWindowLookup: async (_, _, _, _) =>
            {
                // Authentication changes while the store answers: the answer arrives, the read it belongs to does not count.
                if (Interlocked.Increment(ref lookups) == 1)
                    await schedulerReference!.InvalidateAuthenticationAsync(ProviderIds.Claude);
                return ImmutableArray.Create(stored);
            });
        schedulerReference = scheduler;
        scheduler.DiagnosticRaised += (_, args) => { diagnostics.Enqueue(args); diagnosticsPulse.Signal(); };

        await scheduler.StartAsync([ProviderIds.Claude], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => diagnostics.Any(d => d.Code == "window_reset_seed_discarded"),
            "the discarded lookup was not logged", diagnosticsPulse);
        // The authentication change occurs inside the first lookup. Its generation change releases the seed claim
        // under the scheduler lock, allowing the next read to consult the store again.
        await RefreshUntilAsync(scheduler, () => observations.Any(o => o.Window == stored),
            "a later poll did not continue the stored window after a discarded lookup");
        Assert(Volatile.Read(ref lookups) == 2, $"one discarded and one applied lookup (got {lookups})");
        var count = observations.Count;
        await scheduler.RefreshAsync(ProviderIds.Claude);
        await WaitForAsync(() => observations.Count > count, "the follow-up poll did not record", observationsPulse);
        Assert(Volatile.Read(ref lookups) == 2, "no lookup after the applied one");
    }

    /// <summary>Throwing event handlers are logged with provider and exception type, while later handlers and
    /// scheduled polls continue. The test checks diagnostics synchronously during the first state notification.</summary>
    private static async Task ThrowingHandlersAreLoggedAndPollingContinues()
    {
        var clock = new ManualTimeProvider(Epoch);
        ScriptedProvider? provider = null;
        provider = new ScriptedProvider(ProviderIds.Codex, _ => Task.FromResult(Ready(provider!, clock, "scope-d2")));
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<SchedulerDiagnosticEventArgs>();
        var stateHandlerFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyHandlerFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The history sink refuses every observation, so each Ready read raises HistoryObservationWriteFailed.
        await using var scheduler = new ProviderScheduler(new Dictionary<string, IUsageProvider> { [ProviderIds.Codex] = provider },
            clock, () => 0.5, historySink: _ => false);
        scheduler.DiagnosticRaised += (_, args) =>
        {
            diagnostics.Enqueue(args);
            if (args.Code == "scheduler_state_handler_failed") stateHandlerFailed.TrySetResult();
            if (args.Code == "scheduler_history_handler_failed") historyHandlerFailed.TrySetResult();
        };
        scheduler.StateChanged += (_, _) => throw new InvalidOperationException("synthetic presentation failure");
        scheduler.HistoryObservationWriteFailed += (_, _) => throw new InvalidOperationException("synthetic history observer failure");
        var readyStates = new System.Collections.Concurrent.ConcurrentQueue<(ProviderDisplayState State, bool StateFailureLogged, bool HistoryFailureLogged)>();
        var readyPulse = new Pulse();
        scheduler.StateChanged += (_, args) =>
        {
            if (args.State.Status != ProviderStatus.Ready || args.State.IsReading) return;
            readyStates.Enqueue((args.State, stateHandlerFailed.Task.IsCompleted, historyHandlerFailed.Task.IsCompleted));
            readyPulse.Signal();
        };

        await scheduler.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => readyStates.Count == 1, "the first Ready state was not published past the throwing handler", readyPulse);
        var first = readyStates.ToArray()[0];
        Assert(first.StateFailureLogged, "a throwing StateChanged handler is logged as scheduler_state_handler_failed");
        Assert(first.HistoryFailureLogged, "a throwing HistoryObservationWriteFailed handler is logged as scheduler_history_handler_failed");
        Assert(diagnostics.Any(d => d.Code == "scheduler_state_handler_failed" && d.ProviderId == ProviderIds.Codex &&
                                    d.Status == nameof(InvalidOperationException)),
            "the state handler failure names the state's provider and the exception type");
        Assert(diagnostics.Any(d => d.Code == "scheduler_history_handler_failed" && d.ProviderId == "scheduler" &&
                                    d.Status == nameof(InvalidOperationException)),
            "the history handler failure names the scheduler and the exception type");

        // The poll after the failures still runs on schedule and publishes: exactly one refresh interval later.
        await WaitForAsync(() => clock.HasActiveTimerDueIn(TimeSpan.FromMinutes(5)), "the interval poll timer was not armed", clock.TimerChanged);
        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => readyStates.Count == 2, "the interval poll's Ready state was not published", readyPulse);
        Assert(provider.CallCount == 2, $"the interval poll read the provider once more (got {provider.CallCount})");
        Assert(readyStates.ToArray()[1].State.Snapshot?.AccountScope == "scope-d2", "the next state carries the new read's snapshot");
    }

    /// <summary>Runs one refresh and asserts its result. A retry loop would hide failures in the scheduler's
    /// generation-claim ordering.</summary>
    private static async Task RefreshUntilAsync(ProviderScheduler scheduler, Func<bool> done, string error)
    {
        await scheduler.RefreshAsync(ProviderIds.Claude);
        Assert(done(), error);
    }

    private static (UsageWindowIdentity Stored, DateTimeOffset ReportedReset, ScriptedProvider Provider) StoredWindowFixture(ManualTimeProvider clock)
    {
        var storedReset = Epoch + TimeSpan.FromDays(5);
        var reportedReset = storedReset - TimeSpan.FromMinutes(1);
        var stored = new UsageWindowIdentity("opaque-scope", ProviderIds.Claude, "weekly", storedReset - TimeSpan.FromDays(7), storedReset, "test-weekly");
        var provider = new ScriptedProvider(ProviderIds.Claude, _ =>
        {
            var now = clock.GetUtcNow();
            return Task.FromResult(new ProviderReadResult(ProviderStatus.Ready, new AccountSnapshot(
                ProviderIds.Claude, "opaque-scope", null, now, now, "test.source",
                [new QuotaBucket("weekly", QuotaBucketRole.Weekly, 0.5, TimeSpan.FromDays(7), reportedReset, "test-weekly")])));
        });
        return (stored, reportedReset, provider);
    }

    /// <summary>Waits for <see cref="ProviderScheduler.StateChanged"/> until <paramref name="predicate"/>
    /// holds over the current states. Checks once immediately before awaiting the event.</summary>
    private static async Task WaitForStatesAsync(ProviderScheduler scheduler,
        Func<ImmutableDictionary<string, ProviderDisplayState>, bool> predicate, string error)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(object? _, ProviderStateChangedEventArgs __)
        {
            if (predicate(scheduler.GetStates())) signal.TrySetResult();
        }
        scheduler.StateChanged += OnStateChanged;
        try
        {
            if (!predicate(scheduler.GetStates()))
                await signal.Task.ConfigureAwait(false);
        }
        finally
        {
            scheduler.StateChanged -= OnStateChanged;
        }
        Assert(predicate(scheduler.GetStates()), error);
    }

    /// <summary>Disposal completes when cancellation reaches a pending first-poll store lookup.</summary>
    private static async Task DisposeCompletesWhileStoreLookupIsPending()
    {
        var clock = new ManualTimeProvider(Epoch);
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ProviderIds.Claude, _ => Task.FromResult(Ready(ProviderIds.Claude, clock, "opaque-scope")));
        var scheduler = new ProviderScheduler(new Dictionary<string, IUsageProvider> { [ProviderIds.Claude] = provider },
            clock, () => 0.5, historySink: _ => true,
            storedWindowLookup: async (_, _, _, token) =>
            {
                lookupStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);   // honours cancellation like the SQLite store does
                return ImmutableArray<UsageWindowIdentity>.Empty;
            });
        await scheduler.StartAsync([ProviderIds.Claude], TimeSpan.FromMinutes(5));
        await lookupStarted.Task;
        await scheduler.DisposeAsync().AsTask();
    }

    /// <summary>Design §17 restart continuity: on a bucket's first poll after launch the scheduler asks
    /// the store for that bucket's windows and adopts the newest one whose reset is within the jitter tolerance
    /// (represented here by a reported reset one minute before the stored reset).
    /// A stored window further away is not adopted, and the adoption is announced through DiagnosticRaised.</summary>
    private static async Task FirstPollContinuesAStoredWindowWithinTolerance()
    {
        var clock = new ManualTimeProvider(Epoch);
        var storedReset = Epoch + TimeSpan.FromDays(5);                       // synthetic stored reset
        var reportedReset = storedReset - TimeSpan.FromMinutes(1);            // synthetic one-minute rounding difference
        var stored = new UsageWindowIdentity("opaque-scope", ProviderIds.Claude, "weekly", storedReset - TimeSpan.FromDays(7), storedReset, "test-weekly");
        var farAway = new UsageWindowIdentity("opaque-scope", ProviderIds.Claude, "weekly", storedReset - TimeSpan.FromDays(14), storedReset - TimeSpan.FromDays(7), "test-weekly");
        var lookups = 0;
        var provider = new ScriptedProvider(ProviderIds.Claude, _ =>
        {
            var now = clock.GetUtcNow();
            return Task.FromResult(new ProviderReadResult(ProviderStatus.Ready, new AccountSnapshot(
                ProviderIds.Claude, "opaque-scope", null, now, now, "test.source",
                [new QuotaBucket("weekly", QuotaBucketRole.Weekly, 0.5, TimeSpan.FromDays(7), reportedReset, "test-weekly")])));
        });
        var observations = new System.Collections.Concurrent.ConcurrentQueue<UsageObservation>();
        var observationsPulse = new Pulse();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<SchedulerDiagnosticEventArgs>();
        await using var scheduler = new ProviderScheduler(new Dictionary<string, IUsageProvider> { [ProviderIds.Claude] = provider },
            clock, () => 0.5, historySink: observation => { observations.Enqueue(observation); observationsPulse.Signal(); return true; },
            storedWindowLookup: (_, _, _, _) => { lookups++; return Task.FromResult(ImmutableArray.Create(farAway, stored)); });
        scheduler.DiagnosticRaised += (_, args) => diagnostics.Enqueue(args);

        await scheduler.StartAsync([ProviderIds.Claude], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => observations.Count == 1, "the first poll did not record an observation", observationsPulse);
        await scheduler.RefreshAsync(ProviderIds.Claude);
        await WaitForAsync(() => observations.Count >= 2, "the second poll did not record an observation", observationsPulse);

        var samples = observations.ToArray();
        Assert(samples[0].Window == stored, "the first poll continues the stored window instead of starting one a minute earlier");
        Assert(samples[1].Window == stored, "later polls keep the adopted reset from memory");
        Assert(lookups == 1, $"the store is consulted once per bucket per launch (got {lookups})");
        Assert(diagnostics.Any(d => d.Code == "window_reset_adopted_from_store" && d.ProviderId == ProviderIds.Claude && d.Status.Contains("bucket=weekly")),
            "the adoption is announced for the log");

        // A stored window outside the tolerance is left alone: the reported reset stands.
        var clock2 = new ManualTimeProvider(Epoch);
        var provider2 = new ScriptedProvider(ProviderIds.Codex, _ =>
        {
            var now = clock2.GetUtcNow();
            return Task.FromResult(new ProviderReadResult(ProviderStatus.Ready, new AccountSnapshot(
                ProviderIds.Codex, "opaque-scope", null, now, now, "test.source",
                [new QuotaBucket("weekly", QuotaBucketRole.Weekly, 0.5, TimeSpan.FromDays(7), storedReset + TimeSpan.FromMinutes(5), "test-weekly")])));
        });
        var observations2 = new System.Collections.Concurrent.ConcurrentQueue<UsageObservation>();
        var observations2Pulse = new Pulse();
        await using var scheduler2 = new ProviderScheduler(new Dictionary<string, IUsageProvider> { [ProviderIds.Codex] = provider2 },
            clock2, () => 0.5, historySink: observation => { observations2.Enqueue(observation); observations2Pulse.Signal(); return true; },
            storedWindowLookup: (_, _, _, _) => Task.FromResult(ImmutableArray.Create(stored)));
        await scheduler2.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => observations2.Count == 1, "the out-of-tolerance poll did not record an observation", observations2Pulse);
        Assert(observations2.ToArray()[0].Window.ResetAtUtc == storedReset + TimeSpan.FromMinutes(5),
            "a stored window more than two minutes away is not adopted");
    }

    /// <summary>Window-interaction design §17: the scheduler stabilizes every Ready snapshot.
    /// Two polls whose resets differ by sub-second jitter record into one window with one identity, the
    /// published snapshot carries the stabilized reset, and a bucket whose window has not started (100 %,
    /// "resets one duration from now") records nothing.</summary>
    private static async Task JitteredResetsShareOneWindowAndUnstartedWindowsRecordNothing()
    {
        var clock = new ManualTimeProvider(Epoch);
        var trueReset = Epoch + TimeSpan.FromDays(5);
        ScriptedProvider? provider = null;
        provider = new ScriptedProvider(ProviderIds.Claude, _ =>
        {
            // Poll 1 rounds to the true minute; poll 2 (+31 s) would round to the next minute on its own, so
            // only the scheduler's memory of the published reset (rule 2) keeps the two in one window.
            var jitter = provider!.CallCount == 1 ? TimeSpan.FromMilliseconds(-450) : TimeSpan.FromSeconds(31);
            var now = clock.GetUtcNow();
            return Task.FromResult(new ProviderReadResult(ProviderStatus.Ready, new AccountSnapshot(
                ProviderIds.Claude, "opaque-scope", null, now, now, "test.source",
                [
                    new QuotaBucket("weekly", QuotaBucketRole.Weekly, 0.75, TimeSpan.FromDays(7), trueReset + jitter, "test-weekly"),
                    new QuotaBucket("short", QuotaBucketRole.Short, 1.0, TimeSpan.FromHours(5), now + TimeSpan.FromHours(5) + TimeSpan.FromSeconds(1), "test-short")
                ])));
        });
        var observations = new System.Collections.Concurrent.ConcurrentQueue<UsageObservation>();
        var observationsPulse = new Pulse();
        var published = new System.Collections.Concurrent.ConcurrentQueue<ProviderDisplayState>();
        await using var scheduler = new ProviderScheduler(new Dictionary<string, IUsageProvider>
        {
            [ProviderIds.Claude] = provider
        }, clock, () => 0.5, historySink: observation =>
        {
            observations.Enqueue(observation);
            observationsPulse.Signal();
            return true;
        });
        scheduler.StateChanged += (_, args) => published.Enqueue(args.State);

        await scheduler.StartAsync([ProviderIds.Claude], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => observations.Count == 1, "the first jittered poll did not record one observation", observationsPulse);
        await scheduler.RefreshAsync(ProviderIds.Claude);
        await WaitForAsync(() => observations.Count >= 2, "the second jittered poll did not record one observation", observationsPulse);

        var samples = observations.ToArray();
        Assert(samples.All(sample => sample.Window.BucketId == "weekly"), "the not-started short window records nothing");
        Assert(samples[0].Window == samples[1].Window, "sub-second reset jitter does not change the window identity");
        Assert(samples[0].Window.ResetAtUtc == trueReset && samples[0].Window.NominalStartUtc == trueReset - TimeSpan.FromDays(7),
            "the shared identity uses the reset rounded to the minute");

        var ready = published.Where(state => state.Snapshot is not null).Select(state => state.Snapshot!).ToArray();
        Assert(ready.Length >= 2 && ready.All(snapshot => snapshot.Buckets.Single(b => b.Id == "weekly").ResetAtUtc == trueReset),
            "every published snapshot carries the stabilized weekly reset");
        Assert(ready.All(snapshot => !snapshot.Buckets.Single(b => b.Id == "short").WindowStarted &&
                                     snapshot.Buckets.Single(b => b.Id == "short").ResetAtUtc is not null),
            "the published not-started bucket keeps its reset for display and is flagged");
    }

    private static async Task ManualRefreshJoinsAndPollingDoesNotOverlap()
    {
        var clock = new ManualTimeProvider(Epoch);
        var providerResultGate = NewGate();
        var provider = new ScriptedProvider(ProviderIds.Codex, _ => providerResultGate.Task);
        await using var scheduler = NewScheduler(provider, clock);
        await scheduler.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => provider.CallCount == 1, "initial poll did not start", provider.Pulse);

        var manual = scheduler.RefreshAsync(ProviderIds.Codex);
        Assert(provider.CallCount == 1, "manual retry joins the active read instead of starting a second one");
        Assert(provider.MaximumConcurrentReads == 1, "only one provider read may be active at a time");
        providerResultGate.SetResult(Ready(provider, clock, "scope-a"));
        var outcome = await manual;
        Assert(outcome.State.Status == ProviderStatus.Ready && outcome.State.Snapshot is not null,
            "joined manual request receives the completed immutable snapshot");
        Assert(!outcome.WasDeferredByProviderDeadline, "joined read is not a rate-limit deferral");
        var published = scheduler.GetStates()[ProviderIds.Codex];
        Assert(published.Snapshot?.AccountScope == "scope-a" && published.Freshness == SnapshotFreshness.Fresh,
            "fresh successful result becomes visible state");
    }

    private static async Task SuccessfulReadsAloneCreateNormalizedHistoryObservations()
    {
        var clock = new ManualTimeProvider(Epoch);
        ScriptedProvider? provider = null;
        provider = new ScriptedProvider(ProviderIds.Claude, _ => Task.FromResult(provider!.CallCount == 1
            ? Ready(provider!, clock, "opaque-scope")
            : new ProviderReadResult(ProviderStatus.Offline, safeErrorCode: "network_unavailable")));
        var observations = new System.Collections.Concurrent.ConcurrentQueue<UsageObservation>();
        var observationsPulse = new Pulse();
        await using var scheduler = new ProviderScheduler(new Dictionary<string, IUsageProvider>
        {
            [ProviderIds.Claude] = provider
        }, clock, () => 0.5, historySink: observation =>
        {
            observations.Enqueue(observation);
            observationsPulse.Signal();
            return true;
        });

        await scheduler.StartAsync([ProviderIds.Claude], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => observations.Count == 1, "successful provider read did not create one history observation", observationsPulse);
        if (!observations.TryPeek(out var sample))
            throw new InvalidOperationException("Successful observation is not available to inspect.");
        Assert(sample.Window.AccountScope == "opaque-scope" && sample.Window.ProviderId == ProviderIds.Claude,
            "history keeps only the opaque account scope and stable provider ID");
        Assert(sample.ObservedAtUtc == Epoch && sample.Window.NominalStartUtc == Epoch &&
               sample.Window.ResetAtUtc == Epoch + TimeSpan.FromDays(7) && sample.RemainingFraction == 0.75,
            "history uses the successful source timestamp, normalized bucket fraction, and actual window");

        var failedRead = await scheduler.RefreshAsync(ProviderIds.Claude);
        Assert(failedRead.State.Status == ProviderStatus.Offline, "provider failure remains live monitoring state");
        Assert(observations.Count == 1, "failed reads and ordinary state updates do not create fake observations");
    }

    private static async Task DisabledGenerationCannotPublishLateResult()
    {
        var clock = new ManualTimeProvider(Epoch);
        var codexGate = NewGate();
        var codex = new ScriptedProvider(ProviderIds.Codex, _ => codexGate.Task);
        ScriptedProvider? claude = null;
        claude = new ScriptedProvider(ProviderIds.Claude, _ => Task.FromResult(Ready(claude!, clock, "scope-c")));
        await using var scheduler = new ProviderScheduler(new Dictionary<string, IUsageProvider>
        {
            [ProviderIds.Codex] = codex,
            [ProviderIds.Claude] = claude
        }, clock, () => 0.5);
        await scheduler.StartAsync([ProviderIds.Codex, ProviderIds.Claude], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => codex.CallCount == 1 && claude.CallCount == 1, "both enabled providers did not start",
            codex.Pulse, claude.Pulse);
        var generation = scheduler.GetStates()[ProviderIds.Codex].Generation;

        await scheduler.ApplyConfigurationAsync([ProviderIds.Claude], TimeSpan.FromMinutes(5));
        codexGate.SetResult(Ready(codex, clock, "scope-a"));
        await WaitForStatesAsync(scheduler, states => !states[ProviderIds.Codex].IsReading, "disabled provider did not settle");
        var disabled = scheduler.GetStates()[ProviderIds.Codex];
        Assert(!disabled.IsEnabled && disabled.Generation > generation, "disabling advances the provider generation");
        Assert(disabled.Snapshot is null, "a response from a disabled generation cannot publish");
        Assert(scheduler.GetStates()[ProviderIds.Claude].Snapshot?.AccountScope == "scope-c",
            "one provider's cancellation leaves another provider live");
    }

    private static async Task RetryAfterIsAuthoritativeAndManualRetryIsDeferred()
    {
        var clock = new ManualTimeProvider(Epoch);
        ScriptedProvider? provider = null;
        provider = new ScriptedProvider(ProviderIds.Codex, _ =>
        {
            var call = provider!.CallCount;
            return Task.FromResult(call == 1
                ? new ProviderReadResult(ProviderStatus.RateLimited, retryAfterUtc: clock.GetUtcNow() + TimeSpan.FromMinutes(10), safeErrorCode: "rate_limited")
                : Ready(provider!, clock, "scope-a"));
        });
        await using var scheduler = NewScheduler(provider!, clock);
        await scheduler.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(5));
        await WaitForStatesAsync(scheduler, states => states[ProviderIds.Codex].Status == ProviderStatus.RateLimited,
            "rate-limit response was not published");
        Assert(scheduler.GetStates()[ProviderIds.Codex].RetryAtUtc == Epoch + TimeSpan.FromMinutes(10),
            "source retry deadline is preserved exactly even when longer than local backoff");

        var manual = await scheduler.RefreshAsync(ProviderIds.Codex);
        Assert(manual.WasDeferredByProviderDeadline && provider.CallCount == 1,
            "manual Retry cannot bypass a future provider deadline");
        // Match the retry timer's exact deadline; another active timer may belong to the completed read timeout.
        await WaitForAsync(() => clock.HasActiveTimerDueIn(TimeSpan.FromMinutes(10)), "retry-after timer was not armed", clock.TimerChanged);
        await clock.AdvanceAsync(TimeSpan.FromMinutes(9));
        Assert(provider.CallCount == 1, "scheduler does not retry before provider deadline");
        await clock.AdvanceAsync(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => provider.CallCount == 2, "scheduler did not retry at provider deadline", provider.Pulse);
        await WaitForStatesAsync(scheduler, states => states[ProviderIds.Codex].Status == ProviderStatus.Ready,
            "provider did not recover after authoritative retry deadline");
    }

    private static async Task LocalBackoffUsesRefreshIntervalAndCaps()
    {
        var clock = new ManualTimeProvider(Epoch);
        ScriptedProvider? provider = null;
        provider = new ScriptedProvider(ProviderIds.Codex, _ =>
        {
            if (provider!.CallCount == 1)
                return Task.FromResult(new ProviderReadResult(ProviderStatus.Offline, safeErrorCode: "network_unavailable"));
            return Task.FromResult(new ProviderReadResult(ProviderStatus.Offline, safeErrorCode: "network_unavailable"));
        });
        await using var scheduler = NewScheduler(provider, clock, jitter: () => 0.5);
        await scheduler.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(5));
        await WaitForStatesAsync(scheduler, states => states[ProviderIds.Codex].Status == ProviderStatus.Offline,
            "offline result was not published");
        Assert(scheduler.GetStates()[ProviderIds.Codex].RetryAtUtc == Epoch + TimeSpan.FromMinutes(5),
            "first local retry uses the configured refresh interval");
        await WaitForAsync(() => clock.HasActiveTimerDueIn(TimeSpan.FromMinutes(5)), "local retry timer was not armed", clock.TimerChanged);
        // The first failure is retried once at once before it is published (see
        // ScheduledFailureIsRetriedOnceBeforeItIsShown), so the first episode made two calls; later failures
        // of the same episode make one each.
        Assert(provider.CallCount == 2, "the first failure was retried once before it was published");
        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => provider.CallCount == 3, "first exponential retry did not run", provider.Pulse);
        await WaitForStatesAsync(scheduler, states => states[ProviderIds.Codex].RetryAtUtc > clock.GetUtcNow(),
            "second offline result was not published");
        Assert(scheduler.GetStates()[ProviderIds.Codex].RetryAtUtc == clock.GetUtcNow() + TimeSpan.FromMinutes(10),
            "subsequent local retry doubles the interval");

        var capClock = new ManualTimeProvider(Epoch);
        var capProvider = new ScriptedProvider(ProviderIds.Codex,
            _ => Task.FromResult(new ProviderReadResult(ProviderStatus.Offline, safeErrorCode: "network_unavailable")));
        await using var capScheduler = NewScheduler(capProvider, capClock, jitter: () => 0.5);
        await capScheduler.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(30));
        await WaitForStatesAsync(capScheduler, states => states[ProviderIds.Codex].Status == ProviderStatus.Offline,
            "cap provider did not fail");
        for (var failure = 1; failure < 5; failure++)
        {
            var retryAt = capScheduler.GetStates()[ProviderIds.Codex].RetryAtUtc!.Value;
            await WaitForAsync(() => capClock.HasActiveTimerDueIn(retryAt - capClock.GetUtcNow()), "capped retry timer was not armed", capClock.TimerChanged);
            await capClock.AdvanceAsync(retryAt - capClock.GetUtcNow());
            await WaitForAsync(() => capProvider.CallCount == failure + 2, "backoff retry did not run", capProvider.Pulse);
            await WaitForStatesAsync(capScheduler, states => states[ProviderIds.Codex].RetryAtUtc > capClock.GetUtcNow(),
                "backoff failure was not published");
        }
        Assert(capScheduler.GetStates()[ProviderIds.Codex].RetryAtUtc - capClock.GetUtcNow() == ProviderScheduler.MaximumBackoff,
            "exponential retry delay is capped at 60 minutes");
    }

    private static async Task ReadTimeoutUsesInjectedTimeProvider()
    {
        var clock = new ManualTimeProvider(Epoch);
        var provider = new ScriptedProvider(ProviderIds.Codex, async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ProviderReadResult(ProviderStatus.Ready);
        });
        await using var scheduler = NewScheduler(provider, clock, timeout: TimeSpan.FromSeconds(45));
        await scheduler.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => provider.CallCount == 1, "timeout read did not start", provider.Pulse);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(45));
        await WaitForStatesAsync(scheduler, states => states[ProviderIds.Codex].SafeErrorCode == "read_timeout",
            "injected timeout clock did not classify the expired read");
        Assert(scheduler.GetStates()[ProviderIds.Codex].Status == ProviderStatus.UnknownError,
            "timeout is surfaced as a typed provider error");
    }

    private static async Task FreshnessExpiresOnMonotonicHorizon()
    {
        var clock = new ManualTimeProvider(Epoch);
        var secondReadGate = NewGate();
        ScriptedProvider? provider = null;
        provider = new ScriptedProvider(ProviderIds.Codex, _ => provider!.CallCount == 1
            ? Task.FromResult(Ready(provider!, clock, "scope-a"))
            : secondReadGate.Task);
        await using var scheduler = NewScheduler(provider, clock, timeout: TimeSpan.FromMinutes(10));
        await scheduler.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(5));
        await WaitForStatesAsync(scheduler, states => states[ProviderIds.Codex].Status == ProviderStatus.Ready,
            "freshness test initial snapshot did not arrive");
        await WaitForAsync(() => clock.HasActiveTimerDueIn(TimeSpan.FromMinutes(5)), "freshness test poll timer was not armed", clock.TimerChanged);
        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => provider.CallCount == 2, "next poll did not start at the configured interval", provider.Pulse);
        // Freshness uses elapsed monotonic time when states are read. Wait for the pending read's timeout timer
        // before advancing the clock.
        await WaitForAsync(() => clock.HasActiveTimerDueIn(TimeSpan.FromMinutes(10)), "the pending read's timeout was not armed", clock.TimerChanged);
        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));
        var stale = scheduler.GetStates()[ProviderIds.Codex];
        Assert(stale.Snapshot?.AccountScope == "scope-a" && stale.Freshness == SnapshotFreshness.Stale,
            "pending network read does not extend the two-interval freshness horizon");

        var generation = stale.Generation;
        await scheduler.InvalidateAuthenticationAsync(ProviderIds.Codex);
        var invalidated = scheduler.GetStates()[ProviderIds.Codex];
        Assert(invalidated.Generation > generation && invalidated.Snapshot is null,
            "new authentication generation clears old account values immediately");
        secondReadGate.TrySetResult(Ready(provider, clock, "scope-old"));
        await WaitForAsync(() => provider.CallCount >= 3, "fresh authentication generation did not start its own read", provider.Pulse);
    }

    /// <summary>
    /// the scheduler hands a provider that implements IIntentAwareUsageProvider the
    /// reason for each read. Loop-started reads (the initial poll and the interval poll) are Scheduled;
    /// a RefreshAsync that starts its own read is Manual. The plain ReadAsync is never used for such a
    /// provider.
    /// </summary>
    private static async Task IntentAwareProviderReceivesReadIntent()
    {
        var clock = new ManualTimeProvider(Epoch);
        IntentRecordingProvider? provider = null;
        provider = new IntentRecordingProvider(ProviderIds.Gemini, () => Task.FromResult(Ready(provider!.ProviderId, clock, "scope-intent")));
        await using var scheduler = new ProviderScheduler(
            new Dictionary<string, IUsageProvider> { [ProviderIds.Gemini] = provider }, clock, () => 0.5);
        var statePulse = new Pulse();
        scheduler.StateChanged += (_, __) => statePulse.Signal();
        await scheduler.StartAsync([ProviderIds.Gemini], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => provider.Intents.Count == 1 && !scheduler.GetStates()[ProviderIds.Gemini].IsReading,
            "intent spec initial poll did not complete", provider.Pulse, statePulse);
        Assert(provider.Intents.SequenceEqual([ProviderReadIntent.Scheduled]), "the initial poll is a Scheduled read");

        // The manual read's completion wakes the polling loop, which cancels its 5-minute
        // wait and arms a new one due at the same moment. Only a timer created after this point is that new wait.
        var timersBeforeManual = clock.TimerSequence;
        var manual = await scheduler.RefreshAsync(ProviderIds.Gemini);
        Assert(manual.State.Status == ProviderStatus.Ready, "the manual read completes");
        Assert(provider.Intents.SequenceEqual([ProviderReadIntent.Scheduled, ProviderReadIntent.Manual]),
            "RefreshAsync starts a Manual read");

        await WaitForAsync(() => clock.HasActiveTimerDueIn(TimeSpan.FromMinutes(5), timersBeforeManual), "intent spec poll timer was not armed", clock.TimerChanged);
        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => provider.Intents.Count == 3, "intent spec interval poll did not start", provider.Pulse);
        Assert(provider.Intents[2] == ProviderReadIntent.Scheduled, "the interval poll is a Scheduled read again");
        Assert(provider.PlainReadCount == 0, "an intent-aware provider is always called through the intent overload");
    }

    /// <summary>A scheduled read that gets no answer is run again at once as a Manual read
    /// (what the Refresh click does); while that retry runs, the card keeps its last good data and nothing about
    /// the failure is published.</summary>
    private static async Task ScheduledFailureIsRetriedOnceBeforeItIsShown()
    {
        var clock = new ManualTimeProvider(Epoch);
        var hungRead = NewGate();
        var retryRead = NewGate();
        IntentRecordingProvider? provider = null;
        provider = new IntentRecordingProvider(ProviderIds.Gemini, () => provider!.Intents.Count switch
        {
            1 => Task.FromResult(Ready(ProviderIds.Gemini, clock, "scope-a")),
            2 => hungRead.Task,
            _ => retryRead.Task
        });
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<SchedulerDiagnosticEventArgs>();
        var published = new System.Collections.Concurrent.ConcurrentQueue<ProviderDisplayState>();
        var statePulse = new Pulse();
        await using var scheduler = new ProviderScheduler(
            new Dictionary<string, IUsageProvider> { [ProviderIds.Gemini] = provider }, clock, () => 0.5);
        scheduler.DiagnosticRaised += (_, args) => diagnostics.Enqueue(args);
        scheduler.StateChanged += (_, args) => { published.Enqueue(args.State); statePulse.Signal(); };
        await scheduler.StartAsync([ProviderIds.Gemini], TimeSpan.FromMinutes(5));
        await WaitForAsync(() => scheduler.GetStates()[ProviderIds.Gemini] is { Status: ProviderStatus.Ready, IsReading: false },
            "the first poll did not publish its reading", statePulse);

        await WaitForAsync(() => clock.HasActiveTimerDueIn(TimeSpan.FromMinutes(5)), "the poll timer was not armed", clock.TimerChanged);
        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => provider.Intents.Count == 2, "the interval poll did not start", provider.Pulse);
        hungRead.SetResult(new ProviderReadResult(ProviderStatus.Offline, safeErrorCode: "antigravity_unresponsive"));
        await WaitForAsync(() => provider.Intents.Count == 3, "the failed poll was not retried", provider.Pulse);
        Assert(provider.Intents.SequenceEqual([ProviderReadIntent.Scheduled, ProviderReadIntent.Scheduled, ProviderReadIntent.AutomaticRetry]),
            "the automatic retry is an AutomaticRetry read, which clears a hang latch like the Refresh click");

        var duringRetry = scheduler.GetStates()[ProviderIds.Gemini];
        Assert(duringRetry is { Status: ProviderStatus.Ready, IsReading: true } && duringRetry.Snapshot?.AccountScope == "scope-a",
            "while the retry runs the card keeps its last data and shows a read in progress");
        Assert(diagnostics.Any(d => d.Code == "provider_read_auto_retry" && d.Status == "first=Offline,code=antigravity_unresponsive"),
            "the automatic retry is logged with the failure it retries");

        retryRead.SetResult(Ready(ProviderIds.Gemini, clock, "scope-b"));
        await WaitForAsync(() => scheduler.GetStates()[ProviderIds.Gemini] is { IsReading: false, Snapshot.AccountScope: "scope-b" },
            "the retry's reading was not published", statePulse);
        Assert(published.All(state => state.Status is null or ProviderStatus.Ready),
            "the failure the retry fixed was never published");
        Assert(diagnostics.Any(d => d.Code == "provider_read_auto_retry_finished" && d.Status == "status=Ready,code=none"),
            "the retry's outcome is logged");
    }

    /// <summary>The automatic retry is for "no answer" only, once per failure episode, and never
    /// for a read the user started. A sign-in failure is shown at once (a Manual read would bypass Antigravity's
    /// sign-in hold and could open a browser login); a failure that repeats while the card already shows it, and
    /// a failed Refresh click, each make one provider call.</summary>
    private static async Task AutomaticRetryIsSkippedForSignInManualReadsAndRepeatFailures()
    {
        var signInClock = new ManualTimeProvider(Epoch);
        var signIn = new IntentRecordingProvider(ProviderIds.Gemini,
            () => Task.FromResult(new ProviderReadResult(ProviderStatus.NeedsSignIn, safeErrorCode: "antigravity_sign_in_required")));
        await using (var signInScheduler = new ProviderScheduler(
            new Dictionary<string, IUsageProvider> { [ProviderIds.Gemini] = signIn }, signInClock, () => 0.5))
        {
            await signInScheduler.StartAsync([ProviderIds.Gemini], TimeSpan.FromMinutes(5));
            await WaitForStatesAsync(signInScheduler, states => states[ProviderIds.Gemini] is { Status: ProviderStatus.NeedsSignIn, IsReading: false },
                "the sign-in failure was not published");
            Assert(signIn.Intents.SequenceEqual([ProviderReadIntent.Scheduled]), "a sign-in failure is not retried automatically");
        }

        var clock = new ManualTimeProvider(Epoch);
        var offline = new IntentRecordingProvider(ProviderIds.Codex,
            () => Task.FromResult(new ProviderReadResult(ProviderStatus.Offline, safeErrorCode: "codex_app_server_timeout")));
        await using var scheduler = new ProviderScheduler(
            new Dictionary<string, IUsageProvider> { [ProviderIds.Codex] = offline }, clock, () => 0.5);
        await scheduler.StartAsync([ProviderIds.Codex], TimeSpan.FromMinutes(5));
        await WaitForStatesAsync(scheduler, states => states[ProviderIds.Codex] is { Status: ProviderStatus.Offline, IsReading: false },
            "the first failure was not published");
        Assert(offline.Intents.SequenceEqual([ProviderReadIntent.Scheduled, ProviderReadIntent.AutomaticRetry]),
            "a first poll with no answer is retried once, then shown");
        Assert(scheduler.GetStates()[ProviderIds.Codex].RetryAtUtc == Epoch + TimeSpan.FromMinutes(5),
            "the retried episode counts as one failure for backoff");

        await WaitForAsync(() => clock.HasActiveTimerDueIn(TimeSpan.FromMinutes(5)), "the backoff timer was not armed", clock.TimerChanged);
        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => offline.Intents.Count == 3, "the backoff poll did not run", offline.Pulse);
        await WaitForStatesAsync(scheduler, states => states[ProviderIds.Codex] is { IsReading: false } s && s.RetryAtUtc > clock.GetUtcNow(),
            "the repeat failure was not published");
        Assert(offline.Intents.Count == 3 && offline.Intents[2] == ProviderReadIntent.Scheduled,
            "a failure the card already shows is not retried again");

        var manual = await scheduler.RefreshAsync(ProviderIds.Codex);
        Assert(manual.State.Status == ProviderStatus.Offline && offline.Intents.Count == 4,
            "a failed Refresh click is shown as it answered, with no automatic retry");
    }

    private static ProviderScheduler NewScheduler(
        IUsageProvider provider,
        ManualTimeProvider clock,
        Func<double>? jitter = null,
        TimeSpan? timeout = null) => new(
            new Dictionary<string, IUsageProvider> { [ProviderIds.Codex] = provider },
            clock,
            jitter ?? (() => 0.5),
            timeout);

    private static ProviderReadResult Ready(ScriptedProvider provider, ManualTimeProvider clock, string scope) =>
        Ready(provider.ProviderId, clock, scope);

    private static ProviderReadResult Ready(string providerId, ManualTimeProvider clock, string scope) => new(
        ProviderStatus.Ready,
        new AccountSnapshot(
            providerId,
            scope,
            null,
            clock.GetUtcNow(),
            clock.GetUtcNow(),
            "test.source",
            [new QuotaBucket("weekly", QuotaBucketRole.Weekly, 0.75, TimeSpan.FromDays(7), clock.GetUtcNow() + TimeSpan.FromDays(7), "test-weekly")]));

    private static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TaskCompletionSource<ProviderReadResult> NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A repeatable signal. Each call to <see cref="Signal"/> completes the current <see cref="Task"/>
    /// and creates a new one for the next wait.</summary>
    private sealed class Pulse
    {
        private readonly object _gate = new();
        private TaskCompletionSource _current = New();
        public Task Task { get { lock (_gate) return _current.Task; } }
        public void Signal()
        {
            TaskCompletionSource prior;
            lock (_gate)
            {
                prior = _current;
                _current = New();
            }
            prior.TrySetResult();
        }
        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Waits for a pulse and rechecks <paramref name="predicate"/> until it holds. Captures each pulse's
    /// current task before checking the predicate so a signal between the check and await is not missed. The
    /// final assertion uses <paramref name="error"/> if the condition no longer holds.</summary>
    private static async Task WaitForAsync(Func<bool> predicate, string error, params Pulse[] pulses)
    {
        while (true)
        {
            var wakeups = new Task[pulses.Length];
            for (var i = 0; i < pulses.Length; i++) wakeups[i] = pulses[i].Task;
            if (predicate()) break;
            await Task.WhenAny(wakeups).ConfigureAwait(false);
        }
        Assert(predicate(), error);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    private sealed class ScriptedProvider(
        string providerId,
        Func<CancellationToken, Task<ProviderReadResult>> read) : IUsageProvider
    {
        private int _callCount;
        private int _concurrent;
        private int _maximumConcurrent;

        public string ProviderId { get; } = providerId;
        public int CallCount => Volatile.Read(ref _callCount);
        public int MaximumConcurrentReads => Volatile.Read(ref _maximumConcurrent);
        // signalled every time CallCount changes, so a spec awaits this
        // instead of polling CallCount against a real timer.
        public Pulse Pulse { get; } = new();

        public async ValueTask<ProviderReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Pulse.Signal();
            var concurrent = Interlocked.Increment(ref _concurrent);
            UpdateMaximum(concurrent);
            try { return await read(cancellationToken).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _concurrent); }
        }

        private void UpdateMaximum(int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _maximumConcurrent);
                if (value <= current) return;
            } while (Interlocked.CompareExchange(ref _maximumConcurrent, value, current) != current);
        }
    }

    /// <summary>Records whether a provider read is active when the scheduler disposes the provider.</summary>
    private sealed class DisposalRecordingProvider(
        string providerId,
        Func<int, CancellationToken, Task<ProviderReadResult>> read) : IUsageProvider, IAsyncDisposable
    {
        private int _callCount;
        private int _active;

        public string ProviderId { get; } = providerId;
        public int CallCount => Volatile.Read(ref _callCount);
        public bool Disposed { get; private set; }
        public bool ReadActiveWhenDisposed { get; private set; }
        // signalled every time CallCount changes.
        public Pulse Pulse { get; } = new();

        public async ValueTask<ProviderReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            Pulse.Signal();
            Interlocked.Increment(ref _active);
            try { return await read(call, cancellationToken).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _active); }
        }

        public ValueTask DisposeAsync()
        {
            ReadActiveWhenDisposed = Volatile.Read(ref _active) > 0;
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Records read intents and counts calls through the plain ReadAsync overload.</summary>
    private sealed class IntentRecordingProvider(
        string providerId,
        Func<Task<ProviderReadResult>> read) : IIntentAwareUsageProvider
    {
        private readonly object _gate = new();
        private readonly List<ProviderReadIntent> _intents = [];
        private int _plainReadCount;

        public string ProviderId { get; } = providerId;
        public IReadOnlyList<ProviderReadIntent> Intents { get { lock (_gate) return _intents.ToArray(); } }
        public int PlainReadCount => Volatile.Read(ref _plainReadCount);
        // signalled every time Intents changes.
        public Pulse Pulse { get; } = new();

        public async ValueTask<ProviderReadResult> ReadAsync(ProviderReadIntent intent, CancellationToken cancellationToken)
        {
            lock (_gate) _intents.Add(intent);
            Pulse.Signal();
            return await read().ConfigureAwait(false);
        }

        public async ValueTask<ProviderReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _plainReadCount);
            return await read().ConfigureAwait(false);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow;
        private long _timestamp;

        public ManualTimeProvider(DateTimeOffset start) => _utcNow = start.ToUniversalTime();
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
        public override long GetTimestamp() { lock (_gate) return _timestamp; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public int ActiveTimerCount { get { lock (_gate) return _timers.Count(timer => timer.IsActive); } }

        /// <summary>Returns true when an active timer is due exactly <paramref name="dueIn"/> from now.</summary>
        public bool HasActiveTimerDueIn(TimeSpan dueIn) => HasActiveTimerDueIn(dueIn, createdAfter: -1);

        /// <summary>The same, counting only timers created after <paramref
        /// name="createdAfter"/> (a <see cref="TimerSequence"/> value). When the scheduler replaces its wait timer with
        /// one due at the same moment, the old one would otherwise satisfy the wait before the new one is armed.</summary>
        public bool HasActiveTimerDueIn(TimeSpan dueIn, long createdAfter)
        {
            lock (_gate) return _timers.Any(timer => timer.Sequence > createdAfter && timer.IsDueAt(_timestamp + dueIn.Ticks));
        }

        /// <summary>The sequence number of the most recently created timer (0 before any).</summary>
        public long TimerSequence { get { lock (_gate) return _timerSequence; } }
        private long _timerSequence;
        // signalled every time a timer is created or rescheduled, so a spec
        // awaits this instead of polling ActiveTimerCount against a real timer.
        public Pulse TimerChanged { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state, ++_timerSequence);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        public async Task AdvanceAsync(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
            List<(ManualTimer Timer, TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                _utcNow += amount;
                _timestamp += amount.Ticks;
                foreach (var timer in _timers.ToArray())
                {
                    if (!timer.TryFire(_timestamp, out var callback, out var state)) continue;
                    callbacks.Add((timer, callback, state));
                }
            }

            foreach (var (timer, callback, state) in callbacks) callback(state);
            await Task.Yield();
        }

        private void Reschedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(dueTime));
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(period));
            lock (_gate) timer.Reschedule(_timestamp, dueTime, period);
            TimerChanged.Signal();
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, long sequence) : ITimer
        {
            public long Sequence { get; } = sequence;
            private long? _dueTimestamp;
            private long _periodTicks;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                owner.Reschedule(this, dueTime, period);
                return true;
            }

            public void Dispose()
            {
                lock (owner._gate) _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool TryFire(long now, out TimerCallback fireCallback, out object? fireState)
            {
                fireCallback = callback;
                fireState = state;
                if (_disposed || _dueTimestamp is not { } due || due > now) return false;
                if (_periodTicks > 0) _dueTimestamp = checked(due + _periodTicks);
                else _dueTimestamp = null;
                return true;
            }

            public bool IsActive => !_disposed && _dueTimestamp is not null;

            public bool IsDueAt(long timestamp) => !_disposed && _dueTimestamp == timestamp;

            public void Reschedule(long now, TimeSpan dueTime, TimeSpan period)
            {
                _periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? null : checked(now + dueTime.Ticks);
            }
        }
    }
}

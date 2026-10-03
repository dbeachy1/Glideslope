using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;
using Glideslope.Providers;
using Glideslope.Storage;

namespace Glideslope.App;

/// <summary>Owns the one app-wide scheduler and shared normalized history store.</summary>
internal sealed class ProviderMonitoringSession : IAsyncDisposable
{
    private readonly IDiagnosticSink _diagnostics;
    private readonly IDisposable[] _ownedTransports;
    private readonly object _stateGate = new();
    private readonly Dictionary<string, ProviderStatus?> _lastStatuses = new(StringComparer.Ordinal);
    private int _historyWriteFailureReported;
    private int _disposeStarted;

    private ProviderMonitoringSession(
        ProviderScheduler scheduler,
        IUsageHistoryStore? history,
        IDiagnosticSink diagnostics,
        IEnumerable<IDisposable> ownedTransports)
    {
        Scheduler = scheduler;
        History = history;
        _diagnostics = diagnostics;
        _ownedTransports = ownedTransports.ToArray();
        Scheduler.StateChanged += OnProviderStateChanged;
        Scheduler.HistoryObservationWriteFailed += OnHistoryObservationWriteFailed;
        Scheduler.DiagnosticRaised += OnSchedulerDiagnostic;
        if (History is not null) History.HealthChanged += OnHistoryHealthChanged;
    }

    private void OnSchedulerDiagnostic(object? sender, SchedulerDiagnosticEventArgs args) =>
        _diagnostics.Record(new DiagnosticEvent(args.Code, args.ProviderId, args.Status));

    public ProviderScheduler Scheduler { get; }
    public IUsageHistoryStore? History { get; }
    public bool HistoryAvailable => History?.Health.IsAvailable == true;

    public static async Task<ProviderMonitoringSession> CreateAsync(
        UserPaths paths,
        AppSettings settings,
        IDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Directory.CreateDirectory(paths.DataDirectory);
        // Forward storage diagnostics to the app log.
        Action<StorageDiagnostic> storageDiagnostics = d => diagnostics.Record(new DiagnosticEvent(d.Code, Status: d.Status));
        var scopeKeys = new AccountScopeKeyStore(paths.DataDirectory, storageDiagnostics);
        IUsageHistoryStore? history = null;
        try
        {
            // Share the migration database name so migration and open use the same file.
            history = await SqliteUsageHistoryStore.OpenAsync(
                Path.Combine(paths.DataDirectory, UserDataMigration.HistoryDatabaseFileName), settings.RetentionDays,
                timeProvider: null, diagnostics: storageDiagnostics).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            diagnostics.Record(new DiagnosticEvent("usage_history_open_failed", Status: $"history_unavailable_{ex.GetType().Name}"));
        }

        var providerDiagnostics = new ProviderDiagnosticBridge(diagnostics);
        var codexTransport = new CodexAppServerTransport(providerDiagnostics);
        var providers = new Dictionary<string, IUsageProvider>(StringComparer.Ordinal)
        {
            // Usage comes from the official CLI, which manages its own authentication.
            [ProviderIds.Claude] = new ClaudeUsageProvider(
                new ClaudeAuthContextReader(providerDiagnostics), new ClaudeCliUsageTransport(providerDiagnostics), scopeKeys.ResolveAsync,
                providerDiagnostics),
            [ProviderIds.Codex] = new CodexUsageProvider(codexTransport, scopeKeys.ResolveAsync, providerDiagnostics),
            // Read Antigravity's Gemini Models usage through its CLI.
            [ProviderIds.Gemini] = new AntigravityUsageProvider(
                new AntigravityHomeContextReader(), new AntigravityCliUsageTransport(providerDiagnostics),
                scopeKeys.ResolveAsync, providerDiagnostics)
        };
        Func<UsageObservation, bool>? historySink = history is null ? null : history.TryAppend;
        // Design §17 restart continuity: the scheduler continues a stored window whose reset is within the
        // jitter tolerance of the source's (the CLI prints resets truncated to the minute).
        // Propagate unreadable history as a failure; an empty result would be indistinguishable from no stored windows.
        StoredWindowLookup? storedWindows = history is null
            ? null
            : (scope, providerId, bucketId, token) => history.ListWindowsOrThrowAsync(scope, providerId, bucketId, token);
        var scheduler = new ProviderScheduler(providers, historySink: historySink, storedWindowLookup: storedWindows);
        return new ProviderMonitoringSession(scheduler, history, diagnostics, []);
    }

    public Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        Scheduler.StartAsync(settings.EnabledProviderIds,
            TimeSpan.FromMinutes(settings.RefreshMinutes), cancellationToken);

    public Task ApplyConfigurationAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        Scheduler.ApplyConfigurationAsync(settings.EnabledProviderIds,
            TimeSpan.FromMinutes(settings.RefreshMinutes), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        Scheduler.StateChanged -= OnProviderStateChanged;
        Scheduler.HistoryObservationWriteFailed -= OnHistoryObservationWriteFailed;
        if (History is not null) History.HealthChanged -= OnHistoryHealthChanged;
        try
        {
            await Scheduler.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Scheduler.DiagnosticRaised -= OnSchedulerDiagnostic;
            if (History is not null)
            {
                try { await History.FlushAsync().ConfigureAwait(false); }
                catch (Exception ex) { _diagnostics.Record(new DiagnosticEvent("usage_history_flush_failed", Status: $"history_not_flushed_{ex.GetType().Name}")); }
                try { await History.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _diagnostics.Record(new DiagnosticEvent("usage_history_dispose_failed", Status: $"history_not_closed_{ex.GetType().Name}")); }
            }
            foreach (var transport in _ownedTransports)
            {
                try { transport.Dispose(); }
                catch (Exception ex) { _diagnostics.Record(new DiagnosticEvent("provider_transport_dispose_failed", Status: $"transport_not_closed_{ex.GetType().Name}")); }
            }
        }
    }

    private void OnProviderStateChanged(object? sender, ProviderStateChangedEventArgs args)
    {
        var next = args.State.Status;
        bool changed;
        lock (_stateGate)
        {
            changed = !_lastStatuses.TryGetValue(args.State.ProviderId, out var previous) || previous != next;
            _lastStatuses[args.State.ProviderId] = next;
        }
        if (changed)
            _diagnostics.Record(new DiagnosticEvent("provider_state_changed", args.State.ProviderId,
                next?.ToString().ToLowerInvariant() ?? "connecting"));
    }

    private void OnHistoryObservationWriteFailed(object? sender, HistoryObservationWriteFailedEventArgs args)
    {
        if (Interlocked.Exchange(ref _historyWriteFailureReported, 1) == 0)
            _diagnostics.Record(new DiagnosticEvent("usage_history_write_failed", Status: args.SafeErrorCode));
    }

    private void OnHistoryHealthChanged(object? sender, UsageHistoryHealthChangedEventArgs args)
    {
        _diagnostics.Record(new DiagnosticEvent("usage_history_health_changed",
            Status: args.Health.IsAvailable ? "available" : args.Health.SafeErrorCode ?? "unavailable"));
    }

    /// <summary>Forwards a Glideslope.Providers diagnostic event onto the app's own readable log.</summary>
    private sealed class ProviderDiagnosticBridge(IDiagnosticSink target) : IProviderDiagnosticSink
    {
        public void Record(ProviderDiagnostic diagnostic) =>
            target.Record(new DiagnosticEvent(diagnostic.Code, diagnostic.ProviderId, diagnostic.Status, diagnostic.DurationMilliseconds));
    }
}

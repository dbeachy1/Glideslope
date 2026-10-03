using System.Collections.Immutable;
using System.Threading.Channels;
using Glideslope.Domain;
using Microsoft.Data.Sqlite;

namespace Glideslope.Storage;

/// <summary>
/// Stores only account-scoped window identity and normalized remaining-fraction observations.
/// A bounded channel keeps provider polling independent from database latency; a single task
/// owns all writes, and storage errors drop later samples instead of accumulating retries.
/// A write failure or full queue drops samples only for <see cref="WriteRetryCooldown"/>; writes
/// resume after the cooldown.
/// </summary>
public sealed class SqliteUsageHistoryStore : IUsageHistoryStore
{
    public const int MinimumRetentionDays = 7;
    public const int MaximumRetentionDays = 365;
    public const int DefaultRetentionDays = 60;
    public const int MaximumQuerySamples = 100_000;

    /// <summary>How long writes are refused after a write failure or a full queue
    /// before the store accepts them again. Measured on the injected <see cref="TimeProvider"/>.</summary>
    public static readonly TimeSpan WriteRetryCooldown = TimeSpan.FromSeconds(30);

    private const int WriterQueueCapacity = 512;
    private readonly SqliteConnection _writerConnection;
    private readonly Channel<WriterItem> _writerQueue;
    private readonly Task _writerTask;
    private readonly TimeProvider _timeProvider;
    private readonly Action<StorageDiagnostic> _diagnostics;
    private int _retentionDays;
    private int _writesSincePrune;
    private readonly object _healthGate = new();
    private HistoryStorageHealth _health = new(true, null);
    // Only a write failure or full queue sets this deadline. Read and prune failures leave a pending
    // write cooldown unchanged and do not prevent samples from being accepted.
    private DateTimeOffset? _writeRetryAtUtc;
    private int _disposed;

    // Read and prune failures are unavailable states that do not refuse writes.
    private const string ReadFailedCode = "history_read_failed";
    private const string PruneFailedCode = "history_prune_failed";

    private SqliteUsageHistoryStore(SqliteConnection writerConnection, int retentionDays, TimeProvider timeProvider,
        Action<StorageDiagnostic> diagnostics)
    {
        _writerConnection = writerConnection;
        _retentionDays = retentionDays;
        _timeProvider = timeProvider;
        _diagnostics = diagnostics;
        _writerQueue = Channel.CreateBounded<WriterItem>(new BoundedChannelOptions(WriterQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _writerTask = RunWriterAsync();
    }

    public HistoryStorageHealth Health
    {
        get { lock (_healthGate) return _health; }
    }

    public event EventHandler<UsageHistoryHealthChangedEventArgs>? HealthChanged;

    /// <summary>Opens the database, applies schema migrations, and performs one bounded prune batch.</summary>
    public static Task<SqliteUsageHistoryStore> OpenAsync(
        string databasePath,
        int retentionDays = DefaultRetentionDays,
        CancellationToken cancellationToken = default) =>
        OpenAsync(databasePath, retentionDays, timeProvider: null, diagnostics: null, cancellationToken);

    /// <summary>
    /// Opens with the clock that times the write-retry cooldown and a
    /// sink for the store's decisions (history_write_retry_scheduled, history_write_recovered). Null
    /// selects <see cref="TimeProvider.System"/> and drops diagnostics.
    /// </summary>
    public static async Task<SqliteUsageHistoryStore> OpenAsync(
        string databasePath,
        int retentionDays,
        TimeProvider? timeProvider,
        Action<StorageDiagnostic>? diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (retentionDays is < MinimumRetentionDays or > MaximumRetentionDays)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "History retention must be between 7 and 365 days.");

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Database path must include a directory.", nameof(databasePath));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Action<StorageDiagnostic> sink = diagnostics ?? (static _ => { });
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteHistorySchema.ConfigureAndMigrateAsync(connection, sink, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await SqliteHistorySchema.RepairWindowsAsync(connection, sink, cancellationToken).ConfigureAwait(false);
            // Pruning is upkeep: a failure is logged without failing the open, and the writer retries
            // during its periodic prune.
            try
            {
                await SqliteHistorySchema.PruneExpiredAsync(connection, retentionDays, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex)
            {
                sink(new StorageDiagnostic("history_prune_failed", $"open_{ex.GetType().Name}_code_{ex.SqliteErrorCode}"));
            }
            return new SqliteUsageHistoryStore(connection, retentionDays, timeProvider ?? TimeProvider.System, sink);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool TryAppend(UsageObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // Only a pending write-retry cooldown refuses a sample; read and prune failures do not.
        if (!AcceptsWrites()) return false;
        if (_writerQueue.Writer.TryWrite(WriterItem.ForObservation(observation))) return true;
        ScheduleWriteRetry("history_queue_full", "queue_full");
        return false;
    }

    public async Task<UsageHistoryQueryResult> QueryWindowAsync(
        UsageWindowIdentity window,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        int maxSamples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (maxSamples is < 1 or > MaximumQuerySamples)
            throw new ArgumentOutOfRangeException(nameof(maxSamples), $"Query limit must be between 1 and {MaximumQuerySamples} samples.");

        var from = Math.Max(ToUtcTicks(fromUtc), ToUtcTicks(window.NominalStartUtc));
        var through = Math.Min(ToUtcTicks(throughUtc), ToUtcTicks(window.ResetAtUtc));
        if (through < from)
            return new UsageHistoryQueryResult(Health.IsAvailable, [], Health.SafeErrorCode);

        try
        {
            await using var connection = CreateReaderConnection(_writerConnection.DataSource);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.observed_utc_ticks, s.remaining_fraction
                FROM usage_observations AS s
                INNER JOIN usage_windows AS w ON w.id = s.window_id
                WHERE w.account_scope = $scope
                  AND w.provider_id = $provider
                  AND w.bucket_id = $bucket
                  AND w.nominal_start_utc_ticks = $start
                  AND w.reset_utc_ticks = $reset
                  AND w.source_semantics = $semantics
                  AND s.observed_utc_ticks >= $from
                  AND s.observed_utc_ticks <= $through
                ORDER BY s.observed_utc_ticks ASC
                LIMIT $limit;
                """;
            AddWindowParameters(command, window);
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$through", through);
            command.Parameters.AddWithValue("$limit", maxSamples);

            var samples = ImmutableArray.CreateBuilder<UsageObservation>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var observed = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero);
                var remaining = reader.GetDouble(1);
                samples.Add(new UsageObservation(window, observed, remaining));
            }
            RecordReadSucceeded();
            return new UsageHistoryQueryResult(true, samples.ToImmutable(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Report the sanitized query failure; a read failure does not refuse writes.
        catch (SqliteException ex)
        {
            return QueryFailed($"{ex.GetType().Name}_code_{ex.SqliteErrorCode}");
        }
        catch (IOException ex)
        {
            return QueryFailed(ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            return QueryFailed(ex.GetType().Name);
        }
    }

    /// <summary>Records and logs a query read failure.</summary>
    private UsageHistoryQueryResult QueryFailed(string reason)
    {
        RecordReadFailed("history_query_failed", reason);
        return new UsageHistoryQueryResult(false, [], ReadFailedCode);
    }

    /// <summary>
    /// Lists every stored window of one account-scoped bucket that has at
    /// least one observation, ordered by nominal start. Runs on the reader path like
    /// <see cref="QueryWindowAsync"/>, with the same health handling: a read failure marks the store
    /// unavailable (history_read_failed), logs history_list_windows_failed with the exception type, and
    /// returns an empty array.
    /// </summary>
    public async Task<ImmutableArray<UsageWindowIdentity>> ListWindowsAsync(
        string accountScope,
        string providerId,
        string bucketId,
        CancellationToken cancellationToken = default) =>
        (await TryListWindowsAsync(accountScope, providerId, bucketId, cancellationToken).ConfigureAwait(false)).Windows;

    /// <summary>The listing with its availability; see
    /// <see cref="IUsageHistoryStore.TryListWindowsAsync"/>.</summary>
    public async Task<UsageWindowListResult> TryListWindowsAsync(
        string accountScope,
        string providerId,
        string bucketId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        try
        {
            await using var connection = CreateReaderConnection(_writerConnection.DataSource);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT w.nominal_start_utc_ticks, w.reset_utc_ticks, w.source_semantics
                FROM usage_windows AS w
                WHERE w.account_scope = $scope
                  AND w.provider_id = $provider
                  AND w.bucket_id = $bucket
                  AND EXISTS (SELECT 1 FROM usage_observations AS o WHERE o.window_id = w.id)
                ORDER BY w.nominal_start_utc_ticks ASC, w.reset_utc_ticks ASC, w.id ASC;
                """;
            command.Parameters.AddWithValue("$scope", accountScope);
            command.Parameters.AddWithValue("$provider", providerId);
            command.Parameters.AddWithValue("$bucket", bucketId);

            var windows = ImmutableArray.CreateBuilder<UsageWindowIdentity>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var start = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero);
                var reset = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero);
                windows.Add(new UsageWindowIdentity(accountScope, providerId, bucketId, start, reset, reader.GetString(2)));
            }
            RecordReadSucceeded();
            return new UsageWindowListResult(true, windows.ToImmutable(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException ex)
        {
            return ListWindowsFailed($"{ex.GetType().Name}_code_{ex.SqliteErrorCode}");
        }
        catch (IOException ex)
        {
            return ListWindowsFailed(ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ListWindowsFailed(ex.GetType().Name);
        }
    }

    /// <summary>Records and logs a listing read failure.</summary>
    private UsageWindowListResult ListWindowsFailed(string reason)
    {
        RecordReadFailed("history_list_windows_failed", reason);
        return new UsageWindowListResult(false, [], ReadFailedCode);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writerQueue.Writer.WriteAsync(WriterItem.ForFlush(completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetRetentionDaysAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (retentionDays is < MinimumRetentionDays or > MaximumRetentionDays)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "History retention must be between 7 and 365 days.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writerQueue.Writer.WriteAsync(WriterItem.ForRetention(retentionDays, completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writerQueue.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
        await _writerConnection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunWriterAsync()
    {
        await foreach (var item in _writerQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.FlushCompletion is { } completion)
            {
                completion.TrySetResult();
                continue;
            }

            if (item.RetentionDays is { } retentionDays)
            {
                // Keep the new retention value even when pruning fails so the periodic prune retries it.
                // A successful prune also clears a pending write cooldown because it commits a write transaction.
                Volatile.Write(ref _retentionDays, retentionDays);
                try
                {
                    await SqliteHistorySchema.PruneExpiredAsync(_writerConnection, retentionDays, CancellationToken.None).ConfigureAwait(false);
                    _writesSincePrune = 0;
                    RecordWriteSucceeded(recoveredBy: "prune");
                }
                catch (SqliteException ex)
                {
                    RecordPruneFailed($"{ex.GetType().Name}_code_{ex.SqliteErrorCode}");
                }
                catch (Exception ex)
                {
                    RecordPruneFailed(ex.GetType().Name);
                }
                item.Completion?.TrySetResult();
                continue;
            }

            // Drop samples only during the write-retry cooldown.
            if (item.Observation is not { } observation || !AcceptsWrites()) continue;
            var persisted = false;
            try
            {
                await PersistAsync(observation, CancellationToken.None).ConfigureAwait(false);
                persisted = true;
                RecordWriteSucceeded();
            }
            // Schedule a retry and log the exception type for each failed write.
            catch (SqliteException ex)
            {
                ScheduleWriteRetry("history_write_failed", $"{ex.GetType().Name}_code_{ex.SqliteErrorCode}");
            }
            catch (IOException ex)
            {
                ScheduleWriteRetry("history_write_failed", ex.GetType().Name);
            }
            catch (UnauthorizedAccessException ex)
            {
                ScheduleWriteRetry("history_write_failed", ex.GetType().Name);
            }
            catch (Exception ex)
            {
                ScheduleWriteRetry("history_write_failed", ex.GetType().Name);
            }

            if (persisted)
                await PruneIfDueAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Prunes after every 1,000 persisted samples. The counter resets after each attempt so repeated
    /// failures are retried every 1,000 writes instead of on every write.
    /// </summary>
    private async Task PruneIfDueAsync()
    {
        if (++_writesSincePrune < 1_000) return;
        _writesSincePrune = 0;
        try
        {
            await SqliteHistorySchema.PruneExpiredAsync(_writerConnection, Volatile.Read(ref _retentionDays), CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            RecordPruneFailed($"{ex.GetType().Name}_code_{ex.SqliteErrorCode}");
        }
        catch (Exception ex)
        {
            RecordPruneFailed(ex.GetType().Name);
        }
    }

    /// <summary>
    /// True when a sample may be queued or written. Only a pending write-retry cooldown refuses samples.
    /// </summary>
    private bool AcceptsWrites()
    {
        lock (_healthGate)
            return _writeRetryAtUtc is not { } retryAt || _timeProvider.GetUtcNow() >= retryAt;
    }

    /// <summary>
    /// Marks storage unavailable with <paramref name="safeErrorCode"/> and refuses
    /// writes until <see cref="WriteRetryCooldown"/> has passed, then lets the next sample try again.
    /// </summary>
    private void ScheduleWriteRetry(string safeErrorCode, string reason)
    {
        var retryAt = _timeProvider.GetUtcNow() + WriteRetryCooldown;
        SetHealth(new HistoryStorageHealth(false, safeErrorCode), retryAt);
        _diagnostics(new StorageDiagnostic("history_write_retry_scheduled",
            $"{safeErrorCode}_{reason}_retry_in_{(int)WriteRetryCooldown.TotalSeconds}s"));
    }

    /// <summary>A successful write makes storage available again; when it ends a
    /// write-retry cooldown, that recovery is logged.
    /// <paramref name="recoveredBy"/> names a write other than a sample (the
    /// retention prune); it is appended to the recovery status as "_by_&lt;recoveredBy&gt;".</summary>
    private void RecordWriteSucceeded(string? recoveredBy = null)
    {
        // Check the cooldown and update health under one lock so a concurrent retry cannot be erased.
        string? recoveredFrom;
        UsageHistoryHealthChangedEventArgs? args;
        lock (_healthGate)
        {
            recoveredFrom = _writeRetryAtUtc is not null ? _health.SafeErrorCode ?? "unavailable" : null;
            args = ApplyHealthLocked(new HistoryStorageHealth(true, null), writeRetryAtUtc: null);
        }
        RaiseHealthChanged(args);
        if (recoveredFrom is not null)
            _diagnostics(new StorageDiagnostic("history_write_recovered",
                recoveredBy is null ? $"after_{recoveredFrom}" : $"after_{recoveredFrom}_by_{recoveredBy}"));
    }

    /// <summary>
    /// A failed read (query or listing). Logs <paramref name="diagnosticCode"/>
    /// with the reason, and publishes history_read_failed unless a write cooldown is pending: that state
    /// already reports unavailable, and its retry time must survive. Writes are
    /// never refused because of a read failure.
    /// </summary>
    private void RecordReadFailed(string diagnosticCode, string reason)
    {
        _diagnostics(new StorageDiagnostic(diagnosticCode, reason));
        UsageHistoryHealthChangedEventArgs? args = null;
        lock (_healthGate)
        {
            if (_writeRetryAtUtc is null)
                args = ApplyHealthLocked(new HistoryStorageHealth(false, ReadFailedCode), writeRetryAtUtc: null);
        }
        RaiseHealthChanged(args);
    }

    /// <summary>A successful read ends a read failure, and only a read failure: a
    /// read proves nothing about writes or pruning. Logged as history_read_recovered.</summary>
    private void RecordReadSucceeded()
    {
        UsageHistoryHealthChangedEventArgs? args = null;
        lock (_healthGate)
        {
            if (_writeRetryAtUtc is null && !_health.IsAvailable && _health.SafeErrorCode == ReadFailedCode)
                args = ApplyHealthLocked(new HistoryStorageHealth(true, null), writeRetryAtUtc: null);
        }
        RaiseHealthChanged(args);
        if (args is not null)
            _diagnostics(new StorageDiagnostic("history_read_recovered", $"after_{ReadFailedCode}"));
    }

    /// <summary>A failed prune (retention change or periodic). Logged as
    /// history_prune_failed with the reason; publishes history_prune_failed unless a write cooldown is
    /// pending. Writes are not refused; the next successful write or prune makes storage available again.</summary>
    private void RecordPruneFailed(string reason)
    {
        _diagnostics(new StorageDiagnostic("history_prune_failed", reason));
        UsageHistoryHealthChangedEventArgs? args = null;
        lock (_healthGate)
        {
            if (_writeRetryAtUtc is null)
                args = ApplyHealthLocked(new HistoryStorageHealth(false, PruneFailedCode), writeRetryAtUtc: null);
        }
        RaiseHealthChanged(args);
    }

    private async Task PersistAsync(UsageObservation observation, CancellationToken cancellationToken)
    {
        var window = observation.Window;
        await using var transaction = await _writerConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long windowId;
        await using (var find = _writerConnection.CreateCommand())
        {
            find.Transaction = (SqliteTransaction)transaction;
            find.CommandText = """
                INSERT INTO usage_windows
                    (account_scope, provider_id, bucket_id, nominal_start_utc_ticks, reset_utc_ticks, source_semantics)
                VALUES ($scope, $provider, $bucket, $start, $reset, $semantics)
                ON CONFLICT(account_scope, provider_id, bucket_id, nominal_start_utc_ticks, reset_utc_ticks, source_semantics)
                DO NOTHING;
                """;
            AddWindowParameters(find, window);
            await find.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var find = _writerConnection.CreateCommand())
        {
            find.Transaction = (SqliteTransaction)transaction;
            find.CommandText = """
                SELECT id FROM usage_windows
                WHERE account_scope = $scope AND provider_id = $provider AND bucket_id = $bucket
                  AND nominal_start_utc_ticks = $start AND reset_utc_ticks = $reset AND source_semantics = $semantics;
                """;
            AddWindowParameters(find, window);
            windowId = (long)(await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Window identity insert did not return a row."));
        }

        await using (var insert = _writerConnection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO usage_observations(window_id, observed_utc_ticks, remaining_fraction)
                VALUES ($window, $observed, $remaining)
                ON CONFLICT(window_id, observed_utc_ticks) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$window", windowId);
            insert.Parameters.AddWithValue("$observed", ToUtcTicks(observation.ObservedAtUtc));
            insert.Parameters.AddWithValue("$remaining", observation.RemainingFraction);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteConnection CreateReaderConnection(string dataSource) => new(new SqliteConnectionStringBuilder
    {
        DataSource = dataSource,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Shared,
        Pooling = false,
        DefaultTimeout = 5
    }.ToString());

    private static void AddWindowParameters(SqliteCommand command, UsageWindowIdentity window)
    {
        command.Parameters.AddWithValue("$scope", window.AccountScope);
        command.Parameters.AddWithValue("$provider", window.ProviderId);
        command.Parameters.AddWithValue("$bucket", window.BucketId);
        command.Parameters.AddWithValue("$start", ToUtcTicks(window.NominalStartUtc));
        command.Parameters.AddWithValue("$reset", ToUtcTicks(window.ResetAtUtc));
        command.Parameters.AddWithValue("$semantics", window.SourceSemantics);
    }

    // Store the retry deadline with health under the same lock. Read and prune failures preserve it.
    private void SetHealth(HistoryStorageHealth health, DateTimeOffset? writeRetryAtUtc = null)
    {
        UsageHistoryHealthChangedEventArgs? args;
        lock (_healthGate)
            args = ApplyHealthLocked(health, writeRetryAtUtc);
        RaiseHealthChanged(args);
    }

    // Applies a health change while the caller holds _healthGate.
    // Returns the event to raise after the lock is released, or null when health did not change.
    private UsageHistoryHealthChangedEventArgs? ApplyHealthLocked(HistoryStorageHealth health, DateTimeOffset? writeRetryAtUtc)
    {
        _writeRetryAtUtc = writeRetryAtUtc;
        if (_health == health) return null;
        _health = health;
        return new UsageHistoryHealthChangedEventArgs(health);
    }

    private void RaiseHealthChanged(UsageHistoryHealthChangedEventArgs? args)
    {
        if (args is null || HealthChanged is not { } handlers) return;
        foreach (EventHandler<UsageHistoryHealthChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception ex)
            {
                /* A health observer must not stop the serialized writer. */
                // Observer failures must not prevent storage work; report them through diagnostics.
                _diagnostics(new StorageDiagnostic("history_health_observer_failed", ex.GetType().Name));
            }
        }
    }

    private static long ToUtcTicks(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime.Ticks;

    private sealed record WriterItem(UsageObservation? Observation, TaskCompletionSource? FlushCompletion, int? RetentionDays = null, TaskCompletionSource? Completion = null)
    {
        public static WriterItem ForObservation(UsageObservation observation) => new(observation, null);
        public static WriterItem ForFlush(TaskCompletionSource completion) => new(null, completion);
        public static WriterItem ForRetention(int days, TaskCompletionSource completion) => new(null, null, days, completion);
    }
}

/// <summary>
/// One sanitized decision from Glideslope.Storage (a code plus a status
/// made of codes, counts and exception type names; never paths, keys or account data). Storage does not
/// reference Glideslope.Core, so, as with Glideslope.Providers' ProviderDiagnostic, the app forwards
/// these onto its own diagnostic sink. Used by <see cref="SqliteUsageHistoryStore"/> and
/// <see cref="AccountScopeKeyStore"/>.
/// </summary>
public sealed record StorageDiagnostic(string Code, string Status);

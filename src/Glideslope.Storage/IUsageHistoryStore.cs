using System.Collections.Immutable;
using Glideslope.Domain;

namespace Glideslope.Storage;

/// <summary>Bounded history access for normalized, real provider observations.</summary>
public interface IUsageHistoryStore : IAsyncDisposable
{
    /// <summary>Queues one observation without waiting for disk I/O. False means this sample was not retained.</summary>
    bool TryAppend(UsageObservation observation);

    /// <summary>Reads a bounded, ordered slice of exactly one account-scoped quota window.</summary>
    Task<UsageHistoryQueryResult> QueryWindowAsync(
        UsageWindowIdentity window,
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        int maxSamples,
        CancellationToken cancellationToken = default);

    /// <summary>Every stored window for one account-scoped bucket that has at least one observation, oldest first.</summary>
    /// <remarks>Feeds the card's previous-week and next-week buttons. An
    /// unavailable store returns an empty array.</remarks>
    Task<ImmutableArray<UsageWindowIdentity>> ListWindowsAsync(string accountScope, string providerId, string bucketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same listing as <see cref="ListWindowsAsync"/>, with store availability so a caller can distinguish
    /// an unreadable store from an empty result.
    /// SqliteUsageHistoryStore implements the listing here, and its ListWindowsAsync returns this result's
    /// Windows.
    /// </summary>
    /// <remarks>The default body is suitable only for stores whose listing cannot fail. Stores that can fail
    /// must implement this member so they can report unavailability.</remarks>
    async Task<UsageWindowListResult> TryListWindowsAsync(string accountScope, string providerId, string bucketId, CancellationToken cancellationToken = default) =>
        new(true, await ListWindowsAsync(accountScope, providerId, bucketId, cancellationToken).ConfigureAwait(false), null);

    /// <summary>Waits until all observations accepted before this call have completed.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates bounded retention and serializes its prune transaction with queued writes.</summary>
    Task SetRetentionDaysAsync(int retentionDays, CancellationToken cancellationToken = default);

    HistoryStorageHealth Health { get; }
    event EventHandler<UsageHistoryHealthChangedEventArgs>? HealthChanged;
}

public sealed record UsageHistoryQueryResult(
    bool IsAvailable,
    ImmutableArray<UsageObservation> Samples,
    string? SafeErrorCode);

/// <summary>The outcome of <see cref="IUsageHistoryStore.TryListWindowsAsync"/>.
/// IsAvailable false means the store could not be read (Windows is then empty and SafeErrorCode says why);
/// IsAvailable true with an empty Windows means nothing is stored for that bucket.</summary>
public sealed record UsageWindowListResult(bool IsAvailable, ImmutableArray<UsageWindowIdentity> Windows, string? SafeErrorCode);

public sealed record HistoryStorageHealth(bool IsAvailable, string? SafeErrorCode);

public sealed class UsageHistoryHealthChangedEventArgs(HistoryStorageHealth health) : EventArgs
{
    public HistoryStorageHealth Health { get; } = health;
}

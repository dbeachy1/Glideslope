using System.Collections.Immutable;
using Glideslope.Domain;

namespace Glideslope.Storage;

/// <summary>
/// Thrown by <see cref="UsageHistoryStoreExtensions.ListWindowsOrThrowAsync"/>
/// when the history store could not be read. The scheduler's first-poll seeding lookup is bound to that
/// method, so an unreadable store reaches the scheduler's existing catch (logged there as
/// window_reset_store_lookup_failed with this type's name) instead of looking like "no stored windows".
/// <see cref="SafeErrorCode"/> is the store's sanitized code (for example history_read_failed); the message
/// carries nothing else.
/// </summary>
public sealed class UsageHistoryUnavailableException : Exception
{
    public UsageHistoryUnavailableException(string safeErrorCode)
        : base($"Usage history is unavailable ({safeErrorCode}).")
    {
        SafeErrorCode = safeErrorCode;
    }

    public string SafeErrorCode { get; }
}

/// <summary>Listing helpers over <see cref="IUsageHistoryStore"/>.</summary>
public static class UsageHistoryStoreExtensions
{
    /// <summary>
    /// The stored windows of one bucket, oldest first, or <see cref="UsageHistoryUnavailableException"/> when
    /// the store could not be read. The app binds the scheduler's StoredWindowLookup to this method.
    /// </summary>
    public static async Task<ImmutableArray<UsageWindowIdentity>> ListWindowsOrThrowAsync(
        this IUsageHistoryStore store,
        string accountScope,
        string providerId,
        string bucketId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var listed = await store.TryListWindowsAsync(accountScope, providerId, bucketId, cancellationToken).ConfigureAwait(false);
        if (!listed.IsAvailable)
            throw new UsageHistoryUnavailableException(listed.SafeErrorCode ?? "history_unavailable");
        return listed.Windows;
    }
}

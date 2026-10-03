using Glideslope.Domain;
using System.Collections.Immutable;

namespace Glideslope.Monitoring;

/// <summary>Immutable UI-facing state for one provider. No timer tick creates an observation.</summary>
public sealed record ProviderDisplayState(
    string ProviderId,
    long Generation,
    bool IsEnabled,
    bool IsReading,
    ProviderStatus? Status,
    AccountSnapshot? Snapshot,
    SnapshotFreshness? Freshness,
    DateTimeOffset? RetryAtUtc,
    ImmutableArray<ProviderAction> Actions,
    string? SafeErrorCode,
    TimeSpan? LastAttemptDuration,
    DateTimeOffset UpdatedAtUtc);

public sealed class ProviderStateChangedEventArgs(ProviderDisplayState state) : EventArgs
{
    public ProviderDisplayState State { get; } = state;
}

public sealed record RefreshRequestResult(ProviderDisplayState State, bool WasDeferredByProviderDeadline);

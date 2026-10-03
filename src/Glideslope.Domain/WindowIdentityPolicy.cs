namespace Glideslope.Domain;

/// <summary>
/// Provider reset timestamps can jitter between polls or describe a window that has not started yet.
/// This policy stabilizes identities before snapshots reach the card, coordinator, or history store.
/// </summary>
public static class WindowIdentityPolicy
{
    /// <summary>Rule 1: resets are kept to the minute.</summary>
    public static readonly TimeSpan ResetGranularity = TimeSpan.FromMinutes(1);

    /// <summary>Rule 2: a rounded reset within this distance of the previously published one for the same
    /// bucket is the same window (a poll that rounds across a minute boundary the other way).</summary>
    public static readonly TimeSpan JitterTolerance = TimeSpan.FromMinutes(2);

    /// <summary>Rule 3: a bucket at 100 % whose nominal start is this close to the observation is a window
    /// that has not started; the source is describing the window that would begin now.</summary>
    public static readonly TimeSpan NotStartedTolerance = TimeSpan.FromSeconds(60);

    public static DateTimeOffset RoundReset(DateTimeOffset reset)
    {
        var unit = ResetGranularity.Ticks;
        var rounded = (reset.UtcTicks + unit / 2) / unit * unit;
        return new DateTimeOffset(rounded, TimeSpan.Zero);
    }

    public static bool IsNotStarted(QuotaBucket bucket, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        return bucket.Duration is { } duration && bucket.ResetAtUtc is { } reset && bucket.RemainingFraction >= 1.0 &&
               (observedAtUtc.ToUniversalTime() - (reset - duration)).Duration() <= NotStartedTolerance;
    }

    /// <summary>Applies rules 1–3 to one bucket. <paramref name="previousReset"/> is the reset last published
    /// for the same provider, scope and bucket id, or null on the first poll of a launch.</summary>
    public static QuotaBucket Stabilize(QuotaBucket bucket, DateTimeOffset observedAtUtc, DateTimeOffset? previousReset)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        if (bucket.ResetAtUtc is not { } reset) return bucket;
        var started = !IsNotStarted(bucket, observedAtUtc);
        var rounded = RoundReset(reset);
        if (started && previousReset is { } previous && (rounded - previous).Duration() <= JitterTolerance)
            rounded = previous;
        return new QuotaBucket(bucket.Id, bucket.Role, bucket.RemainingFraction, bucket.Duration, rounded,
            bucket.SourceSemantics, started);
    }

    public static AccountSnapshot Stabilize(AccountSnapshot snapshot, Func<string, DateTimeOffset?> previousReset)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(previousReset);
        return new AccountSnapshot(snapshot.ProviderId, snapshot.AccountScope, snapshot.Plan, snapshot.ObservedAtUtc,
            snapshot.ReceivedAtUtc, snapshot.SourceId,
            snapshot.Buckets.Select(bucket => Stabilize(bucket, snapshot.ObservedAtUtc, previousReset(bucket.Id))),
            snapshot.ResetCredits);
    }
}

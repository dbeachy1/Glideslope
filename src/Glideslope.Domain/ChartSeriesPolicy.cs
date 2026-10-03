using System.Collections.Immutable;

namespace Glideslope.Domain;

/// <summary>Identity of one real provider-reported quota window for history/chart partitioning.</summary>
public sealed record UsageWindowIdentity
{
    public UsageWindowIdentity(
        string accountScope,
        string providerId,
        string bucketId,
        DateTimeOffset nominalStartUtc,
        DateTimeOffset resetAtUtc,
        string sourceSemantics)
    {
        if (string.IsNullOrWhiteSpace(accountScope)) throw new ArgumentException("Opaque account scope is required.", nameof(accountScope));
        if (!ProviderIds.IsKnown(providerId)) throw new ArgumentException("Unknown provider ID.", nameof(providerId));
        if (string.IsNullOrWhiteSpace(bucketId)) throw new ArgumentException("Bucket ID is required.", nameof(bucketId));
        if (string.IsNullOrWhiteSpace(sourceSemantics)) throw new ArgumentException("Stable source semantics are required.", nameof(sourceSemantics));
        var start = nominalStartUtc.ToUniversalTime();
        var reset = resetAtUtc.ToUniversalTime();
        if (reset <= start) throw new ArgumentException("Window reset must follow its nominal start.", nameof(resetAtUtc));

        AccountScope = accountScope;
        ProviderId = providerId;
        BucketId = bucketId;
        NominalStartUtc = start;
        ResetAtUtc = reset;
        SourceSemantics = sourceSemantics;
    }

    public string AccountScope { get; }
    public string ProviderId { get; }
    public string BucketId { get; }
    public DateTimeOffset NominalStartUtc { get; }
    public DateTimeOffset ResetAtUtc { get; }
    public string SourceSemantics { get; }
}

public sealed record UsageObservation
{
    public UsageObservation(UsageWindowIdentity window, DateTimeOffset observedAtUtc, double remainingFraction)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!double.IsFinite(remainingFraction) || remainingFraction is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(remainingFraction));
        var observed = observedAtUtc.ToUniversalTime();
        if (observed < window.NominalStartUtc || observed >= window.ResetAtUtc)
            throw new ArgumentOutOfRangeException(nameof(observedAtUtc), "Observation must fall inside its reported window.");

        Window = window;
        ObservedAtUtc = observed;
        RemainingFraction = remainingFraction;
    }

    public UsageWindowIdentity Window { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public double RemainingFraction { get; }
}

public sealed record ChartSegment(
    UsageObservation From,
    UsageObservation To,
    TimeSpan GapDuration,
    TimeSpan LatestSampleAge,
    bool IsUnobservedGap);

public sealed record ChartSeries(
    ImmutableArray<UsageObservation> Samples,
    ImmutableArray<ChartSegment> Segments);

/// <summary>Filters actual observations to one window and joins only consecutive real samples.</summary>
public static class ChartSeriesSelector
{
    public static ChartSeries Select(
        IEnumerable<UsageObservation> observations,
        UsageWindowIdentity window,
        TimeSpan samplingInterval,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(window);
        if (samplingInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(samplingInterval));
        var now = nowUtc.ToUniversalTime();

        var candidates = observations
            .Where(sample => sample.Window == window && sample.ObservedAtUtc <= now)
            .GroupBy(sample => sample.ObservedAtUtc)
            .Select(group =>
            {
                var first = group.First();
                return group.All(sample => sample.RemainingFraction.Equals(first.RemainingFraction))
                    ? first
                    : null;
            })
            .Where(sample => sample is not null)
            .Cast<UsageObservation>()
            .OrderBy(sample => sample.ObservedAtUtc)
            .ToImmutableArray();

        var gapThreshold = TimeSpan.FromTicks(checked(samplingInterval.Ticks * 2));
        var segments = ImmutableArray.CreateBuilder<ChartSegment>(Math.Max(0, candidates.Length - 1));
        for (var index = 1; index < candidates.Length; index++)
        {
            var from = candidates[index - 1];
            var to = candidates[index];
            var gap = to.ObservedAtUtc - from.ObservedAtUtc;
            segments.Add(new ChartSegment(from, to, gap, now - to.ObservedAtUtc, gap > gapThreshold));
        }

        return new ChartSeries(candidates, segments.MoveToImmutable());
    }
}

public static class UsageWindowIdentityFactory
{
    public static UsageWindowIdentity? From(AccountSnapshot snapshot, QuotaBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(bucket);
        if (snapshot.Buckets.All(candidate => candidate.Id != bucket.Id))
            throw new ArgumentException("Bucket does not belong to the snapshot.", nameof(bucket));
        if (bucket.Duration is not { } duration || bucket.ResetAtUtc is not { } reset)
            return null;
        // Window-interaction design §17 rule 3: a window that has not started has no identity, so no
        // observation is stored for it and the card has no current window for the bucket.
        if (!bucket.WindowStarted)
            return null;

        return new UsageWindowIdentity(snapshot.AccountScope, snapshot.ProviderId, bucket.Id,
            reset - duration, reset, bucket.SourceSemantics);
    }
}

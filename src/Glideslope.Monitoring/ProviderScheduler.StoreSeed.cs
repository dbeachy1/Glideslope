using Glideslope.Domain;

namespace Glideslope.Monitoring;

// Restores reset continuity from stored windows on the first poll and coordinates one lookup per bucket.
public sealed partial class ProviderScheduler
{
    // The first poll has no published reset to compare against; adopt a stored reset within the jitter tolerance.
    private readonly StoredWindowLookup? _storedWindowLookup;
    // Answered lookups applied this launch, guarded by _gate. Failed, cancelled, or discarded reads remain
    // eligible for retry; an applied lookup is consulted only once per bucket per launch.
    private readonly HashSet<string> _seedConsulted = new(StringComparer.Ordinal);
    // Pending lookups are owned by a generation. Generation changes evict stale claims, and a late release
    // cannot remove a claim already acquired by a newer generation. Guarded by _gate.
    private readonly Dictionary<string, long> _seedPendingOwner = new(StringComparer.Ordinal);

    /// <summary>Looks up each eligible bucket and adopts the newest matching stored window. Claims are committed
    /// only when the requesting read is applied; failed or cancelled lookups remain eligible for retry.</summary>
    private async Task<SeedOutcome?> SeedResetsFromStoreAsync(AccountSnapshot snapshot, long generation, CancellationToken cancellationToken)
    {
        if (_storedWindowLookup is null) return null;
        Dictionary<string, DateTimeOffset>? seeded = null;
        var answered = new List<string>();
        var adoptions = new List<SeedAdoption>();
        try
        {
            foreach (var bucket in snapshot.Buckets)
            {
                if (bucket.Duration is not { } duration || bucket.ResetAtUtc is not { } reset) continue;
                var key = ResetKey(snapshot, bucket.Id);
                lock (_gate)
                {
                    if (_seedConsulted.Contains(key) || _seedPendingOwner.ContainsKey(key)) continue;
                    _seedPendingOwner[key] = generation;
                }
                var rounded = WindowIdentityPolicy.RoundReset(reset);
                var keyAnswered = false;
                try
                {
                    // Bound the lookup by the read token even if the lookup ignores cancellation.
                    var stored = await _storedWindowLookup(snapshot.AccountScope, snapshot.ProviderId, bucket.Id, cancellationToken)
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    keyAnswered = true;
                    answered.Add(key);
                    var candidate = stored
                        .Where(window => window.ResetAtUtc - window.NominalStartUtc == duration &&
                                         (window.ResetAtUtc - rounded).Duration() <= WindowIdentityPolicy.JitterTolerance)
                        .OrderByDescending(window => window.ResetAtUtc)
                        .FirstOrDefault();
                    if (candidate is null || candidate.ResetAtUtc == rounded) continue;
                    seeded ??= new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                    seeded[key] = candidate.ResetAtUtc;
                    adoptions.Add(new SeedAdoption(bucket.Id, rounded, candidate.ResetAtUtc));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Leave the bucket eligible so a later poll can retry the unavailable store.
                    PublishDiagnostic("window_reset_store_lookup_failed", snapshot.ProviderId,
                        $"bucket={bucket.Id},{ex.GetType().Name},retry=next_poll");
                }
                finally
                {
                    if (!keyAnswered)
                    {
                        lock (_gate) ReleaseSeedClaimLocked(key, generation);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled pass applies nothing, so release its answered claims too.
            lock (_gate)
            {
                foreach (var key in answered) ReleaseSeedClaimLocked(key, generation);
            }
            throw;
        }
        return new SeedOutcome(seeded, answered, adoptions);
    }

    /// <summary>Under _gate, marks answered keys consulted only when their read was applied. Otherwise releases
    /// them for retry. A release affects only claims still owned by <paramref name="generation"/>.</summary>
    private void SettleSeedClaimsLocked(SeedOutcome? seed, bool applied, long generation)
    {
        if (seed is null) return;
        foreach (var key in seed.AnsweredKeys)
        {
            ReleaseSeedClaimLocked(key, generation);
            if (applied) _seedConsulted.Add(key);
        }
    }

    /// <summary>Under _gate, releases a pending claim only if <paramref name="generation"/> still owns it.</summary>
    private void ReleaseSeedClaimLocked(string key, long generation)
    {
        if (_seedPendingOwner.TryGetValue(key, out var owner) && owner == generation)
            _seedPendingOwner.Remove(key);
    }

    /// <summary>Under _gate, releases this provider's claims owned by a stale generation and returns the count.
    /// Provider ID is part of each key because generation numbers are scoped to individual slots.</summary>
    private int EvictSeedClaimsForStaleGenerationLocked(string providerId, long staleGeneration)
    {
        var prefix = providerId + "|";
        List<string>? toRelease = null;
        foreach (var (key, owner) in _seedPendingOwner)
        {
            if (owner == staleGeneration && key.StartsWith(prefix, StringComparison.Ordinal))
                (toRelease ??= new List<string>()).Add(key);
        }
        if (toRelease is null) return 0;
        foreach (var key in toRelease) _seedPendingOwner.Remove(key);
        return toRelease.Count;
    }

    private void PublishSeedOutcome(string providerId, SeedOutcome seed, bool applied)
    {
        if (applied)
        {
            foreach (var adoption in seed.Adoptions)
                PublishDiagnostic("window_reset_adopted_from_store", providerId,
                    $"bucket={adoption.BucketId},reported={adoption.Reported:u},stored={adoption.Stored:u}");
        }
        else if (seed.AnsweredKeys.Count > 0)
        {
            PublishDiagnostic("window_reset_seed_discarded", providerId, $"answered={seed.AnsweredKeys.Count},retry=next_poll");
        }
    }

    private sealed record SeedAdoption(string BucketId, DateTimeOffset Reported, DateTimeOffset Stored);

    private sealed record SeedOutcome(
        Dictionary<string, DateTimeOffset>? Seeded,
        List<string> AnsweredKeys,
        List<SeedAdoption> Adoptions);
}

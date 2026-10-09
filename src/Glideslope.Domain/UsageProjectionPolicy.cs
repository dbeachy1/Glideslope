namespace Glideslope.Domain;

/// <summary>A constant-rate estimate from the current provider window's reported usage.</summary>
public sealed record UsageProjection(DateTimeOffset RunsOutAtUtc);

/// <summary>Projects quota exhaustion from cumulative usage observed since the provider window began.</summary>
public static class UsageProjectionCalculator
{
    public static UsageProjection? Calculate(
        UsageWindowIdentity? window,
        UsageObservation? latest,
        SnapshotFreshness freshness,
        DateTimeOffset nowUtc)
    {
        if (window is null || latest is null || freshness != SnapshotFreshness.Fresh) return null;
        if (latest.Window != window) return null;

        var now = nowUtc.ToUniversalTime();
        if (now < window.NominalStartUtc || now >= window.ResetAtUtc || latest.ObservedAtUtc > now) return null;

        var elapsed = latest.ObservedAtUtc - window.NominalStartUtc;
        var consumed = 1 - latest.RemainingFraction;
        if (elapsed <= TimeSpan.Zero || consumed <= 0 || latest.RemainingFraction <= 0) return null;

        var ratePerSecond = consumed / elapsed.TotalSeconds;
        var remainingSeconds = latest.RemainingFraction / ratePerSecond;
        var representableSeconds = (DateTimeOffset.MaxValue - latest.ObservedAtUtc).TotalSeconds;
        if (!double.IsFinite(remainingSeconds) || remainingSeconds <= 0 || remainingSeconds > representableSeconds)
            return null;

        var runOut = latest.ObservedAtUtc.AddSeconds(remainingSeconds);
        if (runOut <= now) return null;
        return new UsageProjection(runOut);
    }
}

namespace Glideslope.Domain;

/// <summary>The unconstrained least-squares line fitted to current-window remaining-fraction samples.</summary>
public sealed record UsageProjection(double Intercept, double SlopePerSecond, DateTimeOffset RunsOutAtUtc)
{
    /// <summary>Evaluates the same fitted line used for the runout estimate.</summary>
    public double RemainingAt(DateTimeOffset instantUtc, UsageWindowIdentity window) =>
        Intercept + SlopePerSecond * (instantUtc.ToUniversalTime() - window.NominalStartUtc).TotalSeconds;
}

/// <summary>Fits quota remaining against the timestamps of selected real observations.</summary>
public static class UsageProjectionCalculator
{
    public static UsageProjection? Calculate(
        UsageWindowIdentity? window,
        IEnumerable<UsageObservation> observations,
        TimeSpan samplingInterval,
        SnapshotFreshness freshness,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (window is null || freshness != SnapshotFreshness.Fresh) return null;

        var now = nowUtc.ToUniversalTime();
        if (now < window.NominalStartUtc || now >= window.ResetAtUtc) return null;

        var samples = ChartSeriesSelector.Select(observations, window, samplingInterval, now).Samples;
        if (samples.Length < 2) return null;
        if (samples[^1].RemainingFraction is <= 0 or >= 1) return null;

        // Centering timestamps before summing keeps regression arithmetic precise for large UTC dates.
        var origin = samples[0].ObservedAtUtc;
        var meanX = samples.Average(sample => (sample.ObservedAtUtc - origin).TotalSeconds);
        var meanY = samples.Average(sample => sample.RemainingFraction);
        var covariance = 0d;
        var variance = 0d;
        foreach (var sample in samples)
        {
            var x = (sample.ObservedAtUtc - origin).TotalSeconds - meanX;
            covariance += x * (sample.RemainingFraction - meanY);
            variance += x * x;
        }
        if (variance <= 0) return null;

        var slope = covariance / variance;
        if (!double.IsFinite(slope) || slope >= 0) return null;
        var interceptAtOrigin = meanY - slope * meanX;
        var intercept = interceptAtOrigin - slope * (origin - window.NominalStartUtc).TotalSeconds;
        if (!double.IsFinite(intercept)) return null;

        var runOutSeconds = -intercept / slope;
        var representableSeconds = (DateTimeOffset.MaxValue - window.NominalStartUtc).TotalSeconds;
        if (!double.IsFinite(runOutSeconds) || runOutSeconds <= 0 || runOutSeconds > representableSeconds)
            return null;

        DateTimeOffset runOut;
        try
        {
            runOut = window.NominalStartUtc.AddSeconds(runOutSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        if (runOut <= now) return null;
        return new UsageProjection(intercept, slope, runOut);
    }
}

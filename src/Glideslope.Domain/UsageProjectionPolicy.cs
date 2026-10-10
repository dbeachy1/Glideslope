namespace Glideslope.Domain;

/// <summary>A fixed-origin projection using the mean of per-observation average burn rates.</summary>
public sealed record UsageProjection(double AverageBurnPerSecond, DateTimeOffset RunsOutAtUtc)
{
    /// <summary>Evaluates the same fixed-origin line used for the runout estimate.</summary>
    public double RemainingAt(DateTimeOffset instantUtc, UsageWindowIdentity window) =>
        1 - AverageBurnPerSecond * (instantUtc.ToUniversalTime() - window.NominalStartUtc).TotalSeconds;
}

/// <summary>Projects from 100% at the nominal window start using the mean sample burn rate.</summary>
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
        if (samples.IsEmpty) return null;
        if (samples[^1].RemainingFraction is <= 0 or >= 1) return null;

        var burnRates = new List<double>(samples.Length);
        foreach (var sample in samples)
        {
            var elapsedSeconds = (sample.ObservedAtUtc - window.NominalStartUtc).TotalSeconds;
            if (elapsedSeconds <= 0) continue;
            var burnRate = (1 - sample.RemainingFraction) / elapsedSeconds;
            if (!double.IsFinite(burnRate) || burnRate < 0) return null;
            burnRates.Add(burnRate);
        }
        if (burnRates.Count == 0) return null;
        var averageBurnRate = burnRates.Average();
        if (!double.IsFinite(averageBurnRate) || averageBurnRate <= 0) return null;

        var runOutSeconds = 1 / averageBurnRate;
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
        return new UsageProjection(averageBurnRate, runOut);
    }
}

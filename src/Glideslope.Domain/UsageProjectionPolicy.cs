namespace Glideslope.Domain;

/// <summary>A fixed-origin projection at the cumulative burn rate of the latest reading.</summary>
public sealed record UsageProjection(double BurnPerSecond, DateTimeOffset RunsOutAtUtc)
{
    /// <summary>Evaluates the same fixed-origin line used for the runout estimate.</summary>
    public double RemainingAt(DateTimeOffset instantUtc, UsageWindowIdentity window) =>
        1 - BurnPerSecond * (instantUtc.ToUniversalTime() - window.NominalStartUtc).TotalSeconds;
}

/// <summary>Projects the straight line from 100% at the nominal window start through the latest reading.</summary>
/// <remarks>
/// Each reading is the window's cumulative usage, so the latest one already accounts for every earlier quiet or busy
/// period. This is the same total-used-over-elapsed rate the weekly pace reports, so the two agree. Do not
/// average the rates of earlier readings: that counts early quiet periods twice and overstates the headroom.
/// </remarks>
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
        var latest = samples[^1];
        if (latest.RemainingFraction is <= 0 or >= 1) return null;

        var elapsedSeconds = (latest.ObservedAtUtc - window.NominalStartUtc).TotalSeconds;
        if (elapsedSeconds <= 0) return null;
        var burnRate = (1 - latest.RemainingFraction) / elapsedSeconds;
        if (!double.IsFinite(burnRate) || burnRate <= 0) return null;

        var runOutSeconds = 1 / burnRate;
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
        return new UsageProjection(burnRate, runOut);
    }
}

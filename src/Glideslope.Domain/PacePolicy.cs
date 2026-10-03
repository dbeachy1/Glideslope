namespace Glideslope.Domain;

public enum SnapshotFreshness
{
    Fresh,
    Stale,
    AuthenticationFailed,
    AccountChanged,
    RestoredHistorical
}

public enum PaceUnavailableReason
{
    None,
    MissingDuration,
    MissingReset,
    StaleSource,
    AuthenticationFailed,
    AccountChanged,
    HistoricalOnly,
    FutureWindowStart,
    WindowReset,
    InsufficientElapsed
}

public enum PaceBand
{
    StrongCushion,
    OnPace,
    SlightlyOver,
    Over,
    SeverelyOver,
    CriticalOver
}

/// <summary>Pure pace output. Null fields retain their typed suppression reason.</summary>
public sealed record PaceResult(
    double RemainingFraction,
    double? ExpectedRemainingFraction,
    double? DeltaFraction,
    TimeSpan? TimeEquivalent,
    double? PacePercent,
    PaceBand? Band,
    PaceUnavailableReason DeltaUnavailableReason,
    PaceUnavailableReason PacePercentUnavailableReason);

/// <summary>Calculates pace from a validated quota bucket and an explicit UTC clock value.</summary>
public static class PaceCalculator
{
    private const double MinimumElapsedFractionForPercent = 0.01;
    private static readonly TimeSpan WeeklyReference = TimeSpan.FromHours(168);

    /// <summary>Rounds the absolute time equivalent to the amount shown in the pace summary.</summary>
    public static double RoundTimeEquivalentForDisplay(double equivalentTicks, bool shortWindow)
    {
        var amount = equivalentTicks / (shortWindow ? TimeSpan.TicksPerMinute : TimeSpan.TicksPerHour);
        return Math.Round(amount, shortWindow ? 0 : 1, MidpointRounding.AwayFromZero);
    }

    public static PaceResult Calculate(QuotaBucket bucket, DateTimeOffset nowUtc, SnapshotFreshness freshness)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        var now = nowUtc.ToUniversalTime();
        var noPace = new PaceResult(bucket.RemainingFraction, null, null, null, null, null,
            PaceUnavailableReason.None, PaceUnavailableReason.None);

        if (bucket.Duration is not { } duration)
            return noPace with
            {
                DeltaUnavailableReason = PaceUnavailableReason.MissingDuration,
                PacePercentUnavailableReason = PaceUnavailableReason.MissingDuration
            };
        if (bucket.ResetAtUtc is not { } reset)
            return noPace with
            {
                DeltaUnavailableReason = PaceUnavailableReason.MissingReset,
                PacePercentUnavailableReason = PaceUnavailableReason.MissingReset
            };

        var start = reset - duration;
        if (now < start)
            return noPace with
            {
                DeltaUnavailableReason = PaceUnavailableReason.FutureWindowStart,
                PacePercentUnavailableReason = PaceUnavailableReason.FutureWindowStart
            };
        if (now >= reset)
            return noPace with
            {
                DeltaUnavailableReason = PaceUnavailableReason.WindowReset,
                PacePercentUnavailableReason = PaceUnavailableReason.WindowReset
            };

        var freshnessReason = freshness switch
        {
            SnapshotFreshness.Fresh => PaceUnavailableReason.None,
            SnapshotFreshness.Stale => PaceUnavailableReason.StaleSource,
            SnapshotFreshness.AuthenticationFailed => PaceUnavailableReason.AuthenticationFailed,
            SnapshotFreshness.AccountChanged => PaceUnavailableReason.AccountChanged,
            SnapshotFreshness.RestoredHistorical => PaceUnavailableReason.HistoricalOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(freshness))
        };
        if (freshnessReason != PaceUnavailableReason.None)
            return noPace with
            {
                DeltaUnavailableReason = freshnessReason,
                PacePercentUnavailableReason = freshnessReason
            };

        var elapsedFraction = Math.Clamp((now - start).Ticks / (double)duration.Ticks, 0, 1);
        var expectedRemaining = 1 - elapsedFraction;
        var delta = bucket.RemainingFraction - expectedRemaining;
        var band = BandFor(delta, duration);
        var timeEquivalent = TimeSpan.FromTicks((long)Math.Round(Math.Abs(delta) * duration.Ticks, MidpointRounding.AwayFromZero));
        if (elapsedFraction < MinimumElapsedFractionForPercent)
            return new PaceResult(bucket.RemainingFraction, expectedRemaining, delta, timeEquivalent, null, band,
                PaceUnavailableReason.None, PaceUnavailableReason.InsufficientElapsed);

        var pacePercent = (1 - bucket.RemainingFraction) / elapsedFraction * 100;
        return new PaceResult(bucket.RemainingFraction, expectedRemaining, delta, timeEquivalent, pacePercent, band,
            PaceUnavailableReason.None, PaceUnavailableReason.None);
    }

    /// <summary>Applies the six weekly reference boundaries proportionally to the bucket duration.</summary>
    public static PaceBand BandFor(double deltaFraction, TimeSpan duration)
    {
        if (!double.IsFinite(deltaFraction)) throw new ArgumentOutOfRangeException(nameof(deltaFraction));
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));

        // A displayed zero is on pace in either direction. Keep the existing tick rounding
        // used by Calculate, and avoid converting very large finite inputs to long.
        var equivalentTicks = Math.Abs(deltaFraction) * duration.Ticks;
        if (equivalentTicks <= TimeSpan.MaxValue.Ticks)
        {
            var roundedTicks = Math.Round(equivalentTicks, MidpointRounding.AwayFromZero);
            if (RoundTimeEquivalentForDisplay(roundedTicks, duration <= TimeSpan.FromHours(24)) == 0)
                return PaceBand.OnPace;
        }

        // The weekly thresholds are scaled by duration / 168h. Dividing the window's
        // time equivalent by that scale leaves deltaFraction * 168h for every window.
        var equivalentWeeklyHours = deltaFraction * WeeklyReference.TotalHours;
        if (equivalentWeeklyHours >= 12) return PaceBand.StrongCushion;
        if (equivalentWeeklyHours >= 0) return PaceBand.OnPace;
        var overHours = -equivalentWeeklyHours;
        if (overHours <= 6) return PaceBand.SlightlyOver;
        if (overHours <= 12) return PaceBand.Over;
        if (overHours < 24) return PaceBand.SeverelyOver;
        return PaceBand.CriticalOver;
    }
}

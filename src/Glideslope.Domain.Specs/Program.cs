using Glideslope.Domain;

namespace Glideslope.Domain.Specs;

internal static class Program
{
    private static int Main()
    {
        ProviderContractsAreImmutableAndTyped();
        PaceMathAndBoundariesAreDeterministic();
        ChartSelectionUsesOnlyMatchingRealSamples();
        CreditInventoryUsesAuthoritativeCountAndSafeDetails();
        WindowIdentityIsStabilizedBeforeUse();
        Console.WriteLine("Glideslope domain specs passed.");
        return 0;
    }

    /// <summary>Window-interaction design §17: resets are kept to the minute, a rounded reset
    /// within two minutes of the previously published one keeps that one, a bucket at 100 % whose window
    /// starts at the observation has not started and gets no identity.</summary>
    private static void WindowIdentityIsStabilizedBeforeUse()
    {
        var reset = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var week = TimeSpan.FromDays(7);

        // Rule 1: Claude's jitter (09:59:59.5 … 10:00:00.4) rounds to the same minute either way.
        Assert(WindowIdentityPolicy.RoundReset(reset.AddMilliseconds(-450)) == reset, "a reset just before the minute rounds up to it");
        Assert(WindowIdentityPolicy.RoundReset(reset.AddMilliseconds(450)) == reset, "a reset just after the minute rounds down to it");
        Assert(WindowIdentityPolicy.RoundReset(reset.AddSeconds(29)) == reset, "29 s past the minute rounds down");
        Assert(WindowIdentityPolicy.RoundReset(reset.AddSeconds(31)) == reset.AddMinutes(1), "31 s past the minute rounds up");

        var observed = reset - TimeSpan.FromDays(2);
        QuotaBucket Weekly(double remaining, DateTimeOffset at) =>
            new("weekly", QuotaBucketRole.Weekly, remaining, week, at, "test-weekly");

        // Rule 2: a rounded reset within the jitter tolerance of the previous one keeps the previous value.
        var first = WindowIdentityPolicy.Stabilize(Weekly(0.8, reset.AddMilliseconds(-450)), observed, previousReset: null);
        Assert(first.ResetAtUtc == reset && first.WindowStarted, "the first poll publishes the rounded reset");
        var boundary = WindowIdentityPolicy.Stabilize(Weekly(0.79, reset.AddSeconds(31)), observed, first.ResetAtUtc);
        Assert(boundary.ResetAtUtc == reset, "a poll that rounds to the next minute keeps the published reset (jitter tolerance)");
        var moved = WindowIdentityPolicy.Stabilize(Weekly(0.5, reset.AddMinutes(3)), observed, first.ResetAtUtc);
        Assert(moved.ResetAtUtc == reset.AddMinutes(3), "a shift of more than two minutes is a new window");

        // Rule 3: 100 % remaining with the nominal start at the observation is a window that has not started.
        var idle = new QuotaBucket("short", QuotaBucketRole.Short, 1.0, week, observed + week + TimeSpan.FromSeconds(1), "test-short");
        Assert(WindowIdentityPolicy.IsNotStarted(idle, observed), "a sliding 'resets one duration from now' bucket has not started");
        var idleStabilized = WindowIdentityPolicy.Stabilize(idle, observed, previousReset: null);
        Assert(!idleStabilized.WindowStarted && idleStabilized.ResetAtUtc is not null, "a not-started bucket keeps its reset for display but is flagged");
        Assert(!WindowIdentityPolicy.IsNotStarted(Weekly(0.99, observed + week), observed), "any usage means the window started");
        Assert(!WindowIdentityPolicy.IsNotStarted(Weekly(1.0, observed + week - TimeSpan.FromMinutes(5)), observed),
            "100 % remaining five minutes into a fixed window is a started window with no usage yet");

        var snapshot = new AccountSnapshot(ProviderIds.Codex, "scope", null, observed, observed, "test.source", [idle, Weekly(0.8, reset.AddMilliseconds(400))]);
        var stabilized = WindowIdentityPolicy.Stabilize(snapshot, _ => null);
        Assert(UsageWindowIdentityFactory.From(stabilized, stabilized.Buckets[0]) is null, "a not-started bucket has no window identity");
        var identity = UsageWindowIdentityFactory.From(stabilized, stabilized.Buckets[1]);
        Assert(identity is not null && identity.ResetAtUtc == reset && identity.NominalStartUtc == reset - week,
            "a started bucket's identity uses the rounded reset and the start derived from it");
        Assert(new QuotaBucket("b", QuotaBucketRole.Weekly, 0.5, week, reset, "s").WindowStarted, "providers leave WindowStarted true");
    }

    private static void ProviderContractsAreImmutableAndTyped()
    {
        var buckets = new List<QuotaBucket> { Bucket("weekly", QuotaBucketRole.Weekly, 0.7, Hours(168), Utc(2025, 1, 8), "weekly-limit") };
        var snapshot = Snapshot(buckets);
        buckets.Clear();
        Assert(snapshot.Buckets.Length == 1, "snapshot must copy its bucket collection");
        Assert(snapshot.AccountScope == "scope-a", "snapshot must carry only its opaque history scope");
        Assert(snapshot.ObservedAtUtc.Offset == TimeSpan.Zero && snapshot.ReceivedAtUtc.Offset == TimeSpan.Zero, "snapshot instants must normalize to UTC");

        Throws<ArgumentOutOfRangeException>(() => Bucket("bad", QuotaBucketRole.Weekly, double.NaN, Hours(168), Utc(2025, 1, 8), "weekly-limit"));
        Throws<ArgumentOutOfRangeException>(() => Bucket("bad", QuotaBucketRole.Weekly, 1.1, Hours(168), Utc(2025, 1, 8), "weekly-limit"));
        Throws<ArgumentOutOfRangeException>(() => Bucket("bad", QuotaBucketRole.Weekly, 0.5, TimeSpan.Zero, null, "weekly-limit"));
        Throws<ArgumentException>(() => new ProviderReadResult(ProviderStatus.Ready));
        var throttledWithoutProviderDeadline = new ProviderReadResult(ProviderStatus.RateLimited, snapshot);
        Assert(throttledWithoutProviderDeadline.RetryAfterUtc is null, "provider deadline remains absent when the source did not supply one");
        var failedWithLastGood = new ProviderReadResult(ProviderStatus.Offline, snapshot, safeErrorCode: "network_unavailable");
        Assert(failedWithLastGood.Snapshot == snapshot, "failed reads may retain an explicitly last-good snapshot");
        Throws<ArgumentException>(() => new ProviderReadResult(ProviderStatus.Offline, safeErrorCode: "C:\\Users\\private"));
        Throws<ArgumentException>(() => new ProviderAction(ProviderActionKind.OpenOfficialInstructions, "http://example.test"));
    }

    private static void PaceMathAndBoundariesAreDeterministic()
    {
        var start = Utc(2025, 1, 1);
        var duration = Hours(168);
        var reset = start + duration;
        var bucket = Bucket("weekly", QuotaBucketRole.Weekly, 0.7, duration, reset, "weekly-limit");
        var midpoint = PaceCalculator.Calculate(bucket, start + Hours(84), SnapshotFreshness.Fresh);
        Near(midpoint.ExpectedRemainingFraction!.Value, 0.5, "expected remaining at midpoint");
        Near(midpoint.DeltaFraction!.Value, 0.2, "positive delta means under budget");
        Near(midpoint.PacePercent!.Value, 60, "pace percent is use divided by expected use");
        Near(midpoint.TimeEquivalent!.Value.TotalHours, 33.6, "time equivalent uses absolute delta times duration");
        Assert(midpoint.Band == PaceBand.StrongCushion, "band uses unrounded weekly-equivalent delta");

        Assert(PaceCalculator.BandFor(12d / 168, duration) == PaceBand.StrongCushion, "12-hour cushion belongs to strongest band");
        Assert(PaceCalculator.BandFor(0, duration) == PaceBand.OnPace, "zero delta belongs to light green band");
        Assert(PaceCalculator.BandFor(-6d / 168, duration) == PaceBand.SlightlyOver, "six-hour over boundary stays slight");
        Assert(PaceCalculator.BandFor(-12d / 168, duration) == PaceBand.Over, "twelve-hour over boundary belongs to over band");
        Assert(PaceCalculator.BandFor(-24d / 168, duration) == PaceBand.CriticalOver, "24-hour over boundary is critical");
        Assert(PaceCalculator.BandFor(12d / 168, Hours(5)) == PaceBand.StrongCushion, "short-window thresholds scale with duration");
        Assert(PaceCalculator.BandFor(0.049d / 168, duration) == PaceBand.OnPace &&
               PaceCalculator.BandFor(-0.049d / 168, duration) == PaceBand.OnPace,
            "weekly amounts below 0.05 hour display on pace for either sign");
        Assert(PaceCalculator.BandFor(0.05d / 168, duration) == PaceBand.OnPace &&
               PaceCalculator.BandFor(-0.05d / 168, duration) == PaceBand.SlightlyOver,
            "weekly half-tenth-hour midpoint rounds away from zero");
        Assert(PaceCalculator.BandFor(0.051d / 168, duration) == PaceBand.OnPace &&
               PaceCalculator.BandFor(-0.051d / 168, duration) == PaceBand.SlightlyOver,
            "weekly amounts above the display midpoint retain their ordinary bands");
        var fiveHours = Hours(5);
        foreach (var seconds in new[] { 29.9, 30.0, 30.1 })
        {
            var delta = TimeSpan.FromSeconds(seconds).Ticks / (double)fiveHours.Ticks;
            var expectedUnder = PaceBand.OnPace;
            var expectedOver = seconds < 30 ? PaceBand.OnPace : PaceBand.SlightlyOver;
            Assert(PaceCalculator.BandFor(delta, fiveHours) == expectedUnder &&
                   PaceCalculator.BandFor(-delta, fiveHours) == expectedOver,
                $"short-window {seconds} second display boundary agrees for either sign");
        }
        Assert(PaceCalculator.BandFor(double.MaxValue, duration) == PaceBand.StrongCushion &&
               PaceCalculator.BandFor(-double.MaxValue, duration) == PaceBand.CriticalOver,
            "large finite band inputs retain their prior behavior without overflow");

        var shortWindow = Bucket("short", QuotaBucketRole.Short, 0.9, Hours(5), start + Hours(5), "session-limit");
        var shortPace = PaceCalculator.Calculate(shortWindow, start + Hours(2.5), SnapshotFreshness.Fresh);
        Near(shortPace.ExpectedRemainingFraction!.Value, 0.5, "short window uses its actual duration");
        Near(shortPace.TimeEquivalent!.Value.TotalMinutes, 120, "short time equivalent is in the source window duration");

        foreach (var role in new[] { QuotaBucketRole.Weekly, QuotaBucketRole.FeaturedWeekly })
        {
            var tinyDelta = 0.04 / 168;
            foreach (var sign in new[] { -1d, 1d })
            {
                var tinyBucket = Bucket("tiny", role, 0.5 + sign * tinyDelta, duration, reset, "tiny-limit");
                var tinyPace = PaceCalculator.Calculate(tinyBucket, start + Hours(84), SnapshotFreshness.Fresh);
                Assert(tinyPace.Band == PaceBand.OnPace && tinyPace.DeltaFraction != 0 &&
                       Math.Abs(tinyPace.TimeEquivalent!.Value.TotalHours - 0.04) < 1e-9,
                    $"weekly and Fable tiny {sign} deltas stay on pace while preserving raw values");
            }
        }

        var nearStart = PaceCalculator.Calculate(bucket, start + TimeSpan.FromTicks((long)(duration.Ticks * 0.009)), SnapshotFreshness.Fresh);
        Assert(nearStart.DeltaFraction is not null && nearStart.Band is not null, "fresh delta remains available near the window start");
        Assert(nearStart.PacePercent is null && nearStart.PacePercentUnavailableReason == PaceUnavailableReason.InsufficientElapsed,
            "pace percent is suppressed below one percent elapsed with a typed explanation but without suppressing delta");

        var stale = PaceCalculator.Calculate(bucket, start + Hours(84), SnapshotFreshness.Stale);
        Assert(stale.RemainingFraction == 0.7 && stale.DeltaFraction is null && stale.Band is null &&
               stale.DeltaUnavailableReason == PaceUnavailableReason.StaleSource,
            "stale value remains displayable with neutral pace presentation");
        Assert(PaceCalculator.Calculate(bucket, reset, SnapshotFreshness.Fresh).DeltaUnavailableReason == PaceUnavailableReason.WindowReset,
            "old quota window is not paced at or after reset");
        Assert(PaceCalculator.Calculate(bucket, start - TimeSpan.FromTicks(1), SnapshotFreshness.Fresh).DeltaUnavailableReason == PaceUnavailableReason.FutureWindowStart,
            "future nominal start suppresses pace");

        var noReset = Bucket("unknown-reset", QuotaBucketRole.Other, 0.5, duration, null, "other-meter");
        var noDuration = Bucket("unknown-duration", QuotaBucketRole.Other, 0.5, null, reset, "other-meter");
        Assert(PaceCalculator.Calculate(noReset, start, SnapshotFreshness.Fresh).DeltaUnavailableReason == PaceUnavailableReason.MissingReset,
            "missing reset has a typed reason");
        Assert(PaceCalculator.Calculate(noDuration, start, SnapshotFreshness.Fresh).DeltaUnavailableReason == PaceUnavailableReason.MissingDuration,
            "missing duration has a typed reason");
    }

    private static void ChartSelectionUsesOnlyMatchingRealSamples()
    {
        var start = Utc(2025, 1, 1);
        var reset = start + Hours(168);
        var window = new UsageWindowIdentity("scope-a", ProviderIds.Codex, "weekly", start, reset, "weekly-limit");
        var movedWindow = new UsageWindowIdentity("scope-b", ProviderIds.Codex, "weekly", start, reset, "weekly-limit");
        var samples = new[]
        {
            Observation(window, start + Hours(1), 0.95),
            Observation(window, start + Hours(1) + TimeSpan.FromMinutes(5), 0.9),
            Observation(window, start + Hours(7) + TimeSpan.FromMinutes(5), 0.8),
            Observation(movedWindow, start + Hours(9), 0.7),
            Observation(window, start + Hours(20), 0.5)
        };

        var series = ChartSeriesSelector.Select(samples, window, TimeSpan.FromMinutes(5), start + Hours(21));
        Assert(series.Samples.Length == 4, "selector keeps every actual sample in the requested account/window only");
        Assert(series.Samples[0].ObservedAtUtc == start + Hours(1), "samples sort by observed UTC time");
        Assert(series.Segments.Length == 3, "only consecutive real observations get connecting segments");
        Assert(!series.Segments[0].IsUnobservedGap && series.Segments[1].IsUnobservedGap,
            "gap is dashed only when it exceeds twice the configured interval");
        Assert(series.Segments[1].From.ObservedAtUtc == start + Hours(1) + TimeSpan.FromMinutes(5) &&
               series.Segments[1].To.ObservedAtUtc == start + Hours(7) + TimeSpan.FromMinutes(5) &&
               series.Segments[1].GapDuration == Hours(6),
            "gap preserves its real endpoint timestamps and duration");
        Assert(series.Segments[^1].LatestSampleAge == Hours(1), "hover age is measured from the latest real sample");

        var conflictingDuplicates = samples.Append(Observation(window, start + Hours(1) + TimeSpan.FromMinutes(5), 0.89));
        var withoutAmbiguousTimestamp = ChartSeriesSelector.Select(conflictingDuplicates, window, TimeSpan.FromMinutes(5), start + Hours(21));
        Assert(withoutAmbiguousTimestamp.Samples.Length == 3 && withoutAmbiguousTimestamp.Samples.All(sample => sample.ObservedAtUtc != start + Hours(1) + TimeSpan.FromMinutes(5)),
            "conflicting observations at the same unique-key instant are omitted instead of picked arbitrarily");
        Assert(ChartSeriesSelector.Select([], window, TimeSpan.FromMinutes(5), start).Samples.IsEmpty,
            "no blue trace exists before any real sample");

        var accountChanged = new UsageWindowIdentity("scope-b", ProviderIds.Codex, "weekly", start, reset, "weekly-limit");
        Assert(window != accountChanged, "matching reset instants cannot bridge two account scopes");
        Throws<ArgumentOutOfRangeException>(() => Observation(window, start - TimeSpan.FromTicks(1), 0.4));
    }

    private static void CreditInventoryUsesAuthoritativeCountAndSafeDetails()
    {
        var now = Utc(2025, 1, 1);
        var rows = new[]
        {
            new ResetCreditCandidate("z", "usage-reset", now + Hours(48)),
            new ResetCreditCandidate("b", "usage-reset", null),
            new ResetCreditCandidate("a", "usage-reset", now + Hours(24)),
            new ResetCreditCandidate("expired", "usage-reset", now),
            new ResetCreditCandidate("other", "other-credit", now + Hours(1)),
            new ResetCreditCandidate("conflict", "usage-reset", now + Hours(1)),
            new ResetCreditCandidate("conflict", "usage-reset", now + Hours(2)),
            new ResetCreditCandidate(null, "usage-reset", null)
        };

        var countSmaller = ResetCreditNormalizer.Normalize(2, rows, "usage-reset", now);
        Assert(countSmaller.Inventory is { AvailableCount: 2, UndisclosedCount: 0 }, "authoritative smaller total bounds details");
        Assert(countSmaller.Inventory!.Details.Select(detail => detail.Id).SequenceEqual(["a", "z"]),
            "valid details sort by known expiration before unknown dates");
        Assert(countSmaller.Warnings.Contains(CreditSchemaWarning.ConflictingDetailId) &&
               countSmaller.Warnings.Contains(CreditSchemaWarning.InvalidDetail),
            "malformed and conflicting rows create sanitized warning codes only");

        var countLarger = ResetCreditNormalizer.Normalize(5, rows, "usage-reset", now);
        Assert(countLarger.Inventory is { AvailableCount: 5, UndisclosedCount: 2 },
            "missing detail rows remain an aggregate undisclosed count");
        Assert(countLarger.Inventory!.Details.Length == 3, "normalizer retains only valid, unexpired, supported details");
        var oneCredit = ResetCreditNormalizer.Normalize(1, rows, "usage-reset", now).Inventory!;
        Assert(oneCredit.AdditionalDetails.IsEmpty, "one credit does not create a misleading empty additional-credit list");

        var missingDetails = ResetCreditNormalizer.Normalize(3, null, "usage-reset", now).Inventory!;
        Assert(missingDetails.AvailableCount == 3 && missingDetails.Details.IsEmpty && missingDetails.UndisclosedCount == 3,
            "positive total survives an absent details array");
        var knownZero = ResetCreditNormalizer.Normalize(0, rows, "usage-reset", now).Inventory!;
        Assert(knownZero.HasKnownZero && knownZero.Details.IsEmpty, "known zero suppresses stray detail rows");
        Assert(ResetCreditNormalizer.Normalize(1.5m, rows, "usage-reset", now).Inventory is null,
            "fractional authoritative count is unknown rather than inferred");
        Assert(ResetCreditNormalizer.Normalize(-1, rows, "usage-reset", now).Inventory is null,
            "negative authoritative count is unknown");
    }

    private static AccountSnapshot Snapshot(IEnumerable<QuotaBucket> buckets) => new(
        ProviderIds.Codex, "scope-a", "Plus", Utc(2025, 1, 4), Utc(2025, 1, 4).AddSeconds(1), "codex.app-server", buckets);

    private static QuotaBucket Bucket(string id, QuotaBucketRole role, double remaining, TimeSpan? duration, DateTimeOffset? reset, string semantics) =>
        new(id, role, remaining, duration, reset, semantics);

    private static UsageObservation Observation(UsageWindowIdentity window, DateTimeOffset at, double remaining) => new(window, at, remaining);
    private static DateTimeOffset Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);
    private static TimeSpan Hours(double hours) => TimeSpan.FromHours(hours);

    private static void Near(double actual, double expected, string message)
    {
        if (Math.Abs(actual - expected) > 0.000001) throw new InvalidOperationException($"FAIL: {message} (actual={actual}, expected={expected})");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"FAIL: expected {typeof(TException).Name}");
    }
}

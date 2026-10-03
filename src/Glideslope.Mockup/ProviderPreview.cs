namespace Glideslope.Mockup;

// All values are synthetic. The current point is 60% through a week and 50% through five hours.
internal sealed record ProviderPreview(
    string Name,
    string Plan,
    string WeeklyReset,
    double WeeklyRemaining,
    double FiveHourRemaining,
    bool HasResetCredits,
    double? FableWeeklyRemaining = null)
{
    internal double WeeklyDelta => WeeklyRemaining - 0.40;
    internal double FiveHourDelta => FiveHourRemaining - 0.50;
    internal PaceBand WeeklyBand => PacePreviewPolicy.BandFor(WeeklyDelta);
    internal PaceBand FiveHourBand => PacePreviewPolicy.BandFor(FiveHourDelta);
    // The synthetic Fable and all-model weekly buckets share this sample's reset time.
    internal double? FableWeeklyDelta => FableWeeklyRemaining - 0.40;
    internal PaceBand? FableWeeklyBand => FableWeeklyDelta is { } delta
        ? PacePreviewPolicy.BandFor(delta) : null;
    internal string? FableWeeklyDeltaLabel => FableWeeklyDelta is { } delta
        ? $"{Math.Abs(delta * 168):0.0} hours {(delta >= 0 ? "under" : "over")} budget · " +
          $"{Math.Round((1 - FableWeeklyRemaining!.Value) / 0.60 * 100, MidpointRounding.AwayFromZero):0}% pace"
        : null;
    internal int WeeklyPacePercent => (int)Math.Round((1 - WeeklyRemaining) / 0.60 * 100,
        MidpointRounding.AwayFromZero);
    internal int FiveHourPacePercent => (int)Math.Round((1 - FiveHourRemaining) / 0.50 * 100,
        MidpointRounding.AwayFromZero);

    internal string WeeklyDeltaLabel =>
        $"{Math.Abs(WeeklyDelta * 168):0.0} hours {(WeeklyDelta >= 0 ? "under" : "over")} budget  ·  {WeeklyPacePercent}% pace";

    internal string FiveHourDeltaLabel =>
        $"{Math.Round(Math.Abs(FiveHourDelta * 300), MidpointRounding.AwayFromZero):0} minutes " +
        $"{(FiveHourDelta >= 0 ? "under" : "over")} budget · {FiveHourPacePercent}% pace";

    internal static ProviderPreview Codex { get; } = new(
        "CODEX", "Plus", "RESETS SUN, SEPTEMBER 27 · 11:17 AM", 0.32, 0.54, true);

    internal static ProviderPreview Claude { get; } = new(
        "CLAUDE", "Max 5x", "RESETS MON, SEPTEMBER 28 · 9:00 AM", 0.48, 0.72, false, 0.77);

    internal static ProviderPreview Gemini { get; } = new(
        "GEMINI", "Google AI Pro", "RESETS TUE, SEPTEMBER 29 · 2:30 PM", 0.38, 0.44, false);
}

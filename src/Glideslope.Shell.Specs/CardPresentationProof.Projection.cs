using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.Shell.Specs;

internal static partial class CardPresentationProof
{
    private static void AssertUsageProjectionPresentation()
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var reset = start.AddDays(7);
        var window = new UsageWindowIdentity("projection-proof", ProviderIds.Claude, "weekly", start, reset, "projection-proof");
        var beyondReset = new UsageProjection(start.AddDays(10));
        Near(WeeklyHistoryChart.RemainingAt(window, beyondReset, reset), 0.3,
            "the dotted line reaches 30% remaining at reset when the average rate projects ten days from week start");
        Near(WeeklyHistoryChart.RemainingAt(window, beyondReset, start.AddDays(1)), 0.9,
            "the start-to-reset line passes through the real day-one reading");
        Assert(WeeklyHistoryChart.ProjectionBand(beyondReset, window) == PaceBand.StrongCushion,
            "a projection that lasts through reset uses the existing green pace band");
        Assert(WeeklyHistoryChart.ProjectionBand(new UsageProjection(reset.AddHours(-4)), window) == PaceBand.SlightlyOver,
            "a shortfall below the first pace boundary still uses a visible yellow band");
        Assert(WeeklyHistoryChart.ProjectionBand(new UsageProjection(reset.AddHours(-24)), window) == PaceBand.CriticalOver,
            "a 24-hour shortfall uses the existing critical red band");
        Assert(LocalizedText.ChartProjectionRunOut(reset, TimeSpan.FromHours(1.5)).StartsWith("Projected runout ", StringComparison.Ordinal) &&
               LocalizedText.ChartProjectionAfterReset(TimeSpan.FromHours(120)).Contains("120.0 hours", StringComparison.Ordinal),
            "projection labels use localized runout date/duration and post-reset hours resources");

        var now = DateTimeOffset.UtcNow;
        var liveStart = now.AddDays(-6);
        var liveReset = liveStart.AddDays(7);
        var bucket = new QuotaBucket("weekly", QuotaBucketRole.Weekly, 0.1, TimeSpan.FromDays(7), liveReset, "projection-proof");
        var snapshot = new AccountSnapshot(ProviderIds.Claude, "projection-proof", "Synthetic", now, now,
            "projection-proof", [bucket]);
        var card = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: false, _ => { });
        try
        {
            card.UpdateState(State(snapshot));
            Assert(card.ChartControl.HasProjection, "fresh current snapshot projects even before history is available");
            card.Show();
            card.SetUsageProjectionHeld(true);
            Assert(card.ChartControl.IsProjectionHeld, "Shift enables the projection on a visible full card");
            card.SetUsageProjectionHeld(false);
            Assert(!card.ChartControl.IsProjectionHeld, "releasing Shift hides the projection");
            card.ApplyMode(CardMode.Mini);
            card.SetUsageProjectionHeld(true);
            Assert(!card.ChartControl.IsProjectionHeld, "a normal mini card hides the full-card projection");
            card.ApplyMode(CardMode.Full);
            card.IsPeeking = true;
            card.SetUsageProjectionHeld(true);
            Assert(card.ChartControl.IsProjectionHeld, "the full card body shown during Ctrl peek can show the projection");
            card.IsPeeking = false;
            card.ApplyMode(CardMode.Mini);

            card.UpdateState(State(snapshot) with { Freshness = SnapshotFreshness.Stale });
            Assert(!card.ChartControl.HasProjection, "a stale live snapshot suppresses the projection");
            card.UpdateState(State(snapshot));
            var pastWindow = new UsageWindowIdentity("projection-proof", ProviderIds.Claude, "weekly",
                liveStart.AddDays(-7), liveReset.AddDays(-7), "projection-proof");
            card.SetHistoryView(pastWindow, [], TimeSpan.FromMinutes(5), historyAvailable: true, offset: 1, count: 2);
            Assert(!card.ChartControl.HasProjection, "browsing a past week suppresses the current projection");
        }
        finally { card.CloseProgrammatically(); }
    }

    public static void CaptureUsageProjectionScreenshots(string outputDirectory)
    {
        AppBuilder.Configure<PresentationTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        using var english = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-US"));
        Directory.CreateDirectory(outputDirectory);
        var now = DateTimeOffset.UtcNow;
        var start = now.AddDays(-6);
        var reset = start.AddDays(7);
        var cases = new[]
        {
            ("default", 100, 940d, 680d, 0.1),
            ("narrow", 100, CardLayoutTiers.MinWidth, 680d, 0.1),
            ("small", CardScale.MinimumPercent, ProviderUsageCardWindow.DefaultCardWidth * 0.7, ProviderUsageCardWindow.DefaultCardHeight * 0.7, 0.1),
            ("near-reset", 100, 940d, 680d, 25d / 169),
            ("beyond-reset-large", CardScale.MaximumPercent, ProviderUsageCardWindow.DefaultCardWidth * 1.5, ProviderUsageCardWindow.DefaultCardHeight * 1.5, 0.5)
        };

        foreach (var (name, percent, width, height, remaining) in cases)
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var bucket = new QuotaBucket("weekly", QuotaBucketRole.Weekly, remaining, TimeSpan.FromDays(7), reset, "projection-proof");
            var snapshot = new AccountSnapshot(ProviderIds.Claude, "projection-proof", "Synthetic", now, now,
                "projection-proof", [bucket]);
            var identity = UsageWindowIdentityFactory.From(snapshot, bucket)!;
            var card = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: false, _ => { })
            {
                RequestedThemeVariant = theme
            };
            try
            {
                var scale = percent / 100d;
                card.MinWidth = CardLayoutTiers.MinWidth * scale;
                card.MinHeight = CardLayoutTiers.MinHeight * scale;
                card.ApplyCardScale(percent);
                card.Width = width;
                card.Height = height;
                card.UpdateState(State(snapshot));
                card.SetHistory(identity,
                [
                    new UsageObservation(identity, start.AddDays(1), 0.85),
                    new UsageObservation(identity, now, remaining)
                ], TimeSpan.FromMinutes(5));
                card.Show();
                card.SetUsageProjectionHeld(true);
                using var bitmap = card.CaptureRenderedFrame() ?? throw new InvalidOperationException("usage_projection_screenshot_render_failed");
                var filename = $"usage-projection-{name}-{theme}.png";
                bitmap.Save(Path.Combine(outputDirectory, filename), PngBitmapEncoderOptions.Default);
                Console.WriteLine($"Captured {filename}");
            }
            finally { card.CloseProgrammatically(); }
        }
    }

    private static void Near(double actual, double expected, string message)
    {
        Assert(Math.Abs(actual - expected) < 1e-9, $"{message} (expected {expected:0.###}, got {actual:0.###})");
    }
}

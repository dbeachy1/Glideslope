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

        var projectionPlot = new Rect(8, 18, 720, 180);
        AssertProjectionLabelPlacement(new Point(8, 18), new Point(180, 198), new Size(150, 28), projectionPlot,
            true, "early steep projection");
        AssertProjectionLabelPlacement(new Point(8, 18), new Point(700, 198), new Size(180, 28), projectionPlot,
            true, "late runout projection");
        AssertProjectionLabelPlacement(new Point(8, 18), new Point(700, 112), new Size(180, 28), projectionPlot,
            true, "shallow beyond-reset projection");
        var narrowPlot = new Rect(8, 18, 370, 150);
        AssertProjectionLabelPlacement(new Point(8, 18), new Point(360, 168), new Size(165, 28), narrowPlot,
            false, "narrow near-reset diagonal");

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
        var scenarios = new[]
        {
            (Name: "early-steep", ElapsedDays: 1d, Remaining: 0.2, Scale: 100, Width: 940d, Height: 680d),
            (Name: "early-steep-narrow", ElapsedDays: 1d, Remaining: 0.2, Scale: 100, Width: 790d, Height: 680d),
            (Name: "near-reset-narrow", ElapsedDays: 6d, Remaining: 0.05, Scale: 100, Width: 790d, Height: 680d),
            (Name: "near-reset-scaled", ElapsedDays: 6d, Remaining: 0.05, Scale: 80, Width: 790d, Height: 680d),
            (Name: "beyond-reset", ElapsedDays: 6d, Remaining: 0.5, Scale: 100, Width: 940d, Height: 680d)
        };
        var providers = new[] { ProviderIds.Claude, ProviderIds.Codex };
        foreach (var providerId in providers)
        foreach (var scenario in scenarios)
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var start = now.AddDays(-scenario.ElapsedDays);
            var reset = start.AddDays(7);
            var buckets = new List<QuotaBucket>
            {
                new("weekly", QuotaBucketRole.Weekly, scenario.Remaining, TimeSpan.FromDays(7), reset, "projection-proof")
            };
            if (providerId == ProviderIds.Codex)
                buckets.Add(new QuotaBucket("short", QuotaBucketRole.Short, 0.42, TimeSpan.FromHours(5), now.AddHours(2), "projection-proof"));
            var snapshot = new AccountSnapshot(providerId, "projection-proof", "Synthetic", now, now,
                "projection-proof", buckets);
            var bucket = buckets[0];
            var identity = UsageWindowIdentityFactory.From(snapshot, bucket)!;
            var card = new ProviderUsageCardWindow(providerId, showMark: false, _ => { })
            {
                RequestedThemeVariant = theme
            };
            try
            {
                var scale = scenario.Scale / 100d;
                card.MinWidth = CardLayoutTiers.MinWidth * scale;
                card.MinHeight = CardLayoutTiers.MinHeight * scale;
                card.ApplyCardScale(scenario.Scale);
                card.Width = scenario.Width;
                card.Height = scenario.Height;
                card.UpdateState(State(snapshot));
                card.SetHistory(identity,
                [
                    new UsageObservation(identity, start.AddHours(scenario.ElapsedDays * 24 / 2), (1 + scenario.Remaining) / 2),
                    new UsageObservation(identity, now, scenario.Remaining)
                ], TimeSpan.FromMinutes(5));
                card.Show();
                card.SetUsageProjectionHeld(true);
                using var bitmap = card.CaptureRenderedFrame() ?? throw new InvalidOperationException("usage_projection_screenshot_render_failed");
                var filename = $"usage-projection-{providerId}-{scenario.Name}-{theme}.png";
                bitmap.Save(Path.Combine(outputDirectory, filename), PngBitmapEncoderOptions.Default);
                Console.WriteLine($"Captured {filename}");
            }
            finally { card.CloseProgrammatically(); }
        }
    }

    private static void AssertProjectionLabelPlacement(Point start, Point end, Size size, Rect plot,
        bool expectMidpointSide, string description)
    {
        var bounds = WeeklyHistoryChart.FindProjectionLabelBounds(start, end, size, plot);
        Assert(bounds is { } placed, $"{description} has an in-plot projection label placement");
        var label = bounds!.Value;
        Assert(label.Left >= plot.Left && label.Top >= plot.Top && label.Right <= plot.Right && label.Bottom <= plot.Bottom,
            $"{description} keeps the complete padded label inside the plot");
        Assert(WeeklyHistoryChart.ProjectionLabelClearsSegment(label, start, end),
            $"{description} keeps the label background clear of the dotted stroke");
        var midpoint = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        if (expectMidpointSide)
            Assert(Math.Abs(label.Top + label.Height / 2 - midpoint.Y) < 1e-9 &&
                   (Math.Abs(label.Right - midpoint.X) > 0 || Math.Abs(label.Left - midpoint.X) > 0),
                $"{description} puts the label beside the segment midpoint");
        else
            Assert(Math.Abs(label.Left + label.Width / 2 - midpoint.X) < 1e-9 && label.Top < midpoint.Y,
                $"{description} uses the nearest clear position above the midpoint when a side box cannot fit");
    }

    private static void Near(double actual, double expected, string message)
    {
        Assert(Math.Abs(actual - expected) < 1e-9, $"{message} (expected {expected:0.###}, got {actual:0.###})");
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.Shell.Specs;

internal static partial class CardPresentationProof
{
    /// <summary>Runs the 2.7 §3 full-card minimum proof against each shipped UI culture and its satellite resources.</summary>
    public static void RunLocalizedLayoutAcceptanceProof()
    {
        AppBuilder.Configure<PresentationTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var failures = new List<string>();
        foreach (var language in AppSettings.SupportedLanguageChoices.Where(language => language != "auto"))
        {
            var culture = CultureInfo.GetCultureInfo(language);
            using var localized = LocalizedText.OverrideForSpecs(culture, culture);
            var requiredHeight = LocalizedText.CardMinimumHeight;
            Console.WriteLine($"localized layout: {language}, minimum {CardLayoutTiers.MinWidth:0}x{requiredHeight:0} logical px (effective MinHeight {requiredHeight:0}; text floor {ProviderUsageCardWindow.CardTextFloor:0}px)");

            var now = DateTimeOffset.UtcNow;
            var claudeBuckets = new[]
            {
                Bucket("claude.short", QuotaBucketRole.Short, now),
                Bucket("claude.fable.weekly", QuotaBucketRole.FeaturedWeekly, now),
            };
            var cases = new (string Name, string Provider, ProviderDisplayState State)[]
            {
                ("history-scope", ProviderIds.Claude,
                    State(Snapshot(now, claudeBuckets)) with { SafeErrorCode = "claude_history_scope_unavailable" }),
                ("schema-error", ProviderIds.Claude,
                    State(Snapshot(now, claudeBuckets)) with { Status = ProviderStatus.SchemaChanged }),
                ("codex-credits", ProviderIds.Codex,
                    State(Snapshot(now, [Bucket("codex.short", QuotaBucketRole.Short, now)], Credits(3, now), ProviderIds.Codex))),
            };

            foreach (var (name, provider, state) in cases)
            {
                var window = new ProviderUsageCardWindow(provider, showMark: true, _ => { });
                try
                {
                    window.UpdateState(state);
                    window.Show();
                    AssertLayoutAtEverySize(window, $"{language} {provider} {name}",
                        layoutSizes: [(CardLayoutTiers.MinWidth, requiredHeight)], english: false, localizedMinimum: true);
                    Assert(window.MinHeight == requiredHeight,
                        $"{language} {name}: production minimum height stays at the localized {requiredHeight:0}px resource");
                    Console.WriteLine($"localized layout passed: {language} {name} at {window.ClientSize.Width:0}x{window.ClientSize.Height:0}; MinHeight {window.MinHeight:0}");
                }
                catch (Exception exception)
                {
                    var failure = $"{language} {name} at {CardLayoutTiers.MinWidth:0}x{requiredHeight:0} (resource/effective minimum {requiredHeight:0}): {exception.Message}";
                    failures.Add(failure);
                    Console.Error.WriteLine($"localized layout failed: {failure}");
                    var measuredHeight = MeasurePassingMinimumHeight(window, language, provider, name, requiredHeight);
                    Console.Error.WriteLine(measuredHeight is null
                        ? $"localized layout height evidence: {language} {name} did not pass through {requiredHeight + 512:0}px"
                        : $"localized layout height evidence: {language} {name} first passes at {measuredHeight:0}px; resource supplies {requiredHeight:0}px");
                }
                finally
                {
                    window.CloseProgrammatically();
                }
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException("Localized card minimum layout failures:\n" + string.Join("\n", failures));
    }

    private static double? MeasurePassingMinimumHeight(ProviderUsageCardWindow window, string language, string provider,
        string state, double requiredHeight)
    {
        bool Passes(double height)
        {
            try
            {
                AssertLayoutAtEverySize(window, $"{language} {provider} {state} height probe", layoutSizes:
                    [(CardLayoutTiers.MinWidth, height)], english: false, localizedMinimum: true);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var low = (int)Math.Ceiling(requiredHeight);
        var step = 1;
        var high = low;
        while (step <= 512)
        {
            high = low + step;
            if (Passes(high)) break;
            step *= 2;
        }
        if (step > 512) return null;

        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (Passes(middle)) high = middle;
            else low = middle;
        }
        return high;
    }
}

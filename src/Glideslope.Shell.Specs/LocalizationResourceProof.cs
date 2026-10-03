using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Confirms the neutral resource set is complete for the migrated presentation
/// surfaces and remains the fallback for a future culture without translations.
/// (also proves that lookups follow the UI culture and formats
/// follow the format culture, the plural rules of the twelve release languages, the full-sentence keys, that the
/// English wording is unchanged, and the specs' pseudo-long locale.)
/// </summary>
internal static class LocalizationResourceProof
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static void Run()
    {
        // Runs first, outside any override: with no override LocalizedText follows the thread's cultures.
        RunCulturesFollowTheMachine();
        using var english = LocalizedText.OverrideForSpecs(English, English);

        Assert(LocalizedText.AppName == "Glideslope", "neutral app name");
        Assert(LocalizedText.TrayShow == "Show Glideslope" && LocalizedText.TrayExit == "Exit", "neutral tray labels");
        Assert(LocalizedText.ProviderName(ProviderCatalog.Gemini) == "Gemini", "provider display name belongs to presentation");
        Assert(!string.IsNullOrWhiteSpace(LocalizedText.CardFooterNoSynthetic), "card footer resource");

        var manager = new ResourceManager("Glideslope.App.Resources.Strings", typeof(LocalizedText).Assembly);
        Assert(manager.GetString("Tray_Exit", CultureInfo.GetCultureInfo("af-ZA")) == "Exit", "unsupported culture falls back to neutral resources");
        Assert(typeof(LocalizedText).Assembly.GetCustomAttribute<NeutralResourcesLanguageAttribute>()?.CultureName == "en-US",
            "review fix (2026-09-26): the app declares its neutral resources as en-US");

        var missingLauncher = LocalizedText.StartupRegistrationIssue(new StartupRegistrationState(false, "startup_launcher_missing"));
        Assert(missingLauncher == "Couldn't turn on start at sign-in (launcher unavailable).", "known issue code mapping");
        var futureIssue = LocalizedText.StartupRegistrationIssue(new StartupRegistrationState(false, "future_protocol_code"));
        Assert(futureIssue == "Couldn't turn on start at sign-in (unavailable).", "unknown issue code fallback");
        var noIssue = LocalizedText.StartupRegistrationIssue(new StartupRegistrationState(true));
        Assert(noIssue is null, "a healthy registration state that matches what was asked for has no status line to show");
        var couldNotDisable = LocalizedText.StartupRegistrationIssue(new StartupRegistrationState(true, "startup_registration_io_error"));
        Assert(couldNotDisable == "Couldn't turn off start at sign-in (unavailable).",
            "registration that is still on after a requested disable reports the disable failure, not the enable wording");
        Assert(!LocalizedText.SettingsApplyFailed("settings_revision_conflict").Contains("settings_revision_conflict", StringComparison.Ordinal), "diagnostic issue IDs stay out of UI text");

        Assert(LocalizedText.SettingsSnapToScreenEdge == "Snap cards together and to screen edges", "updated snap label (design doc §12)");

        RunPluralTable(manager);
        RunCreditExpirationDate();
        RunEveryOneKeyHasAnOtherTwin(manager);
        RunNoHardCodedOrdinalSuffix(manager);
        RunOldKeysAreGone(manager);
        RunEnglishIsUnchanged();
        RunResetTimeLeft();
        RunFiveHourPaceUnits();
        RunOnBudgetFollowsTheDisplayedValue();
        RunCalculatedPaceBandMatchesItsDisplayedAmount();
        RunPluralRules();
        RunLocaleResolver();
        RunLookupsCarryTheUiCulture();
        RunFormatsFollowTheFormatCulture();
        RunPseudoLocale(manager);
    }

    internal static void RunSatelliteResourceValidation()
    {
        var manager = new ResourceManager("Glideslope.App.Resources.Strings", typeof(LocalizedText).Assembly);
        RunSatelliteResources(manager);
    }

    /// <summary>
    /// Design doc §12 English table: "1 minute, 2 minutes, 0.8 hour, 1.0 hour, 1.1 hours, 1 day,
    /// 26 days, and today." Minutes and days come from <see cref="LocalizedText.FormatAge"/> at
    /// real thresholds (24 hours old is exactly "1 day", 26*24 hours old is exactly "26 days");
    /// the fractional-hour cases come from <see cref="LocalizedText.PaceSummary"/>'s long-window
    /// branch, which uses hours regardless of magnitude, proving the "0.96 hours displays as 1.0
    /// and selects One" rounding rule against the real formatting code, not a re-implementation of it.
    /// </summary>
    private static void RunPluralTable(ResourceManager manager)
    {
        Assert(LocalizedText.FormatAge(TimeSpan.FromMinutes(1)) == "1 minute", "plural table: 1 minute");
        Assert(LocalizedText.FormatAge(TimeSpan.FromMinutes(2)) == "2 minutes", "plural table: 2 minutes");
        Assert(LocalizedText.FormatAge(TimeSpan.FromMinutes(0)) == "0 minutes", "English rule: zero is plural");
        Assert(LocalizedText.FormatAge(TimeSpan.FromHours(24)) == "1 day", "plural table: 1 day (24 hours old rounds to exactly one day)");
        Assert(LocalizedText.FormatAge(TimeSpan.FromHours(24 * 26)) == "26 days", "plural table: 26 days");

        var pointEight = PaceSummaryWithHours(deltaFraction: 0.05, equivalentHours: 0.8);
        Assert(pointEight.Contains("0.8 hour under budget", StringComparison.Ordinal), $"plural table: 0.8 hour, got '{pointEight}'");

        var pointNineSix = PaceSummaryWithHours(deltaFraction: 0.05, equivalentHours: 0.96);
        Assert(pointNineSix.Contains("1.0 hour under budget", StringComparison.Ordinal),
            $"design doc §12: 0.96 hours displays as '1.0' and selects One, got '{pointNineSix}'");

        var oneEven = PaceSummaryWithHours(deltaFraction: 0.05, equivalentHours: 1.0);
        Assert(oneEven.Contains("1.0 hour under budget", StringComparison.Ordinal), $"plural table: 1.0 hour, got '{oneEven}'");

        var oneOne = PaceSummaryWithHours(deltaFraction: 0.05, equivalentHours: 1.1);
        Assert(oneOne.Contains("1.1 hours under budget", StringComparison.Ordinal), $"plural table: 1.1 hours, got '{oneOne}'");

        Assert(manager.GetString("Card_CreditExpiresToday", CultureInfo.InvariantCulture) == "today", "plural table: today");
    }

    /// <summary>Builds a long-window (weekly) <c>PaceSummary</c> call so the hours branch is used regardless of magnitude.</summary>
    private static string PaceSummaryWithHours(double deltaFraction, double equivalentHours,
        double? pacePercent = 90.0, PaceUnavailableReason percentReason = PaceUnavailableReason.None) =>
        LocalizedText.PaceSummary(Pace(deltaFraction, TimeSpan.FromHours(equivalentHours), pacePercent, percentReason), TimeSpan.FromHours(168));

    private static PaceResult Pace(double deltaFraction, TimeSpan equivalent, double? pacePercent = 90.0,
        PaceUnavailableReason percentReason = PaceUnavailableReason.None) => new(
        RemainingFraction: 0.5,
        ExpectedRemainingFraction: 0.5 - deltaFraction,
        DeltaFraction: deltaFraction,
        TimeEquivalent: equivalent,
        PacePercent: pacePercent,
        Band: PaceBand.OnPace,
        DeltaUnavailableReason: PaceUnavailableReason.None,
        PacePercentUnavailableReason: percentReason);

    /// <summary>
    /// Design doc §12: the credit box reads "THU Oct 22 (in 26 days)" for local 2026-10-22 with
    /// now — three-letter uppercase weekday, three-letter month, the bare day number
    /// (no "22nd"), and the plural in-days phrase. Both instants use the machine's current local
    /// UTC offset for their calendar date so <c>DateTimeOffset.ToLocalTime()</c> inside
    /// <c>LocalizedText.CreditExpiration</c> is a no-op regardless of which time zone this test runs in.
    /// </summary>
    private static void RunCreditExpirationDate()
    {
        var expirationDate = new DateTime(2026, 10, 22);
        var nowDate = new DateTime(2026, 9, 26);
        var expiration = new DateTimeOffset(expirationDate, TimeZoneInfo.Local.GetUtcOffset(expirationDate));
        var now = new DateTimeOffset(nowDate, TimeZoneInfo.Local.GetUtcOffset(nowDate));

        var result = LocalizedText.CreditExpiration(expiration, now);
        Assert(result == "THU Oct 22 (in 26 days)", $"credit expiration date wording, got '{result}'");

        var today = LocalizedText.CreditExpiration(expiration, expiration);
        Assert(today == "THU Oct 22 (today)", $"credit expiration on the expiration day itself reads 'today', got '{today}'");
    }

    /// <summary>Every "_One" resource key (design doc §12) must have a matching "_Other" key.</summary>
    private static void RunEveryOneKeyHasAnOtherTwin(ResourceManager manager)
    {
        var keys = AllResourceKeys(manager);
        foreach (var oneKey in keys.Where(k => k.EndsWith("_One", StringComparison.Ordinal)))
        {
            var otherKey = oneKey[..^"_One".Length] + "_Other";
            Assert(keys.Contains(otherKey), $"'{oneKey}' has no '{otherKey}' twin");
        }
    }

    private static void RunSatelliteResources(ResourceManager manager)
    {
        var neutral = AllResourceKeys(manager).ToHashSet(StringComparer.Ordinal);
        var pluralBases = neutral.Where(key => key.EndsWith("_One", StringComparison.Ordinal))
            .Select(key => key[..^"_One".Length]).ToArray();
        var placeholder = new Regex(@"\{(\d+)(?:[,}:])", RegexOptions.CultureInvariant);
        foreach (var language in AppSettings.SupportedLanguageChoices.Where(id => id != "auto"))
        {
            var culture = CultureInfo.GetCultureInfo(language);
            var set = language == "en-US"
                ? manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)
                : manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
            Assert(set is not null, $"{language}: the neutral or actual satellite resource set is present");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in set!)
            {
                var key = (string)entry.Key;
                var value = entry.Value as string;
                Assert(!string.IsNullOrWhiteSpace(value), $"{language}: '{key}' is a nonempty string");
                values.Add(key, value!);
            }

            var expected = new HashSet<string>(neutral, StringComparer.Ordinal);
            if (language == "ru")
                foreach (var pluralBase in pluralBases)
                {
                    expected.Add(pluralBase + "_Few");
                    expected.Add(pluralBase + "_Many");
                }
            Assert(values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected),
                $"{language}: resource keys exactly match the active key set");

            foreach (var key in neutral)
            {
                var neutralValue = manager.GetString(key, CultureInfo.InvariantCulture)!;
                var resourceValue = values[key];
                var neutralIndices = placeholder.Matches(neutralValue).Select(match => match.Groups[1].Value)
                    .OrderBy(index => index, StringComparer.Ordinal);
                var resourceIndices = placeholder.Matches(resourceValue).Select(match => match.Groups[1].Value)
                    .OrderBy(index => index, StringComparer.Ordinal);
                Assert(neutralIndices.SequenceEqual(resourceIndices), $"{language}: '{key}' preserves placeholder indices");
            }

            foreach (var pluralBase in pluralBases)
            {
                foreach (var category in language == "ru" ? new[] { "One", "Few", "Many", "Other" } : ["One", "Other"])
                    Assert(values.ContainsKey(pluralBase + "_" + category),
                        $"{language}: '{pluralBase}' supplies the {category} category");
            }

            Assert(double.TryParse(values["Card_MinimumHeight"], NumberStyles.Float, CultureInfo.InvariantCulture, out var minimumHeight) &&
                   double.IsFinite(minimumHeight) && minimumHeight >= 680,
                $"{language}: Card_MinimumHeight is finite and at least the original 680-pixel minimum floor");
            foreach (var patternKey in neutral.Where(key => key.EndsWith("Pattern", StringComparison.Ordinal)))
            {
                var pattern = values[patternKey];
                try { _ = DateTimeOffset.UtcNow.ToString(pattern, culture); }
                catch (FormatException) { throw new InvalidOperationException($"FAIL: {language}: '{patternKey}' is not a valid date/time format pattern: '{pattern}'"); }
            }
        }
    }

    /// <summary>No resource value bakes in an ordinal suffix ("1st", "22nd", "3rd", "4th") next to a numeric placeholder; the day number is always bare.</summary>
    private static void RunNoHardCodedOrdinalSuffix(ResourceManager manager)
    {
        var ordinalAfterPlaceholder = new Regex(@"\{\d+\}\s*(st|nd|rd|th)\b", RegexOptions.IgnoreCase);
        var resourceSet = manager.GetResourceSet(CultureInfo.InvariantCulture, true, true)
            ?? throw new InvalidOperationException("Could not load the neutral resource set.");
        foreach (System.Collections.DictionaryEntry entry in resourceSet)
        {
            var key = (string)entry.Key;
            var value = (string)entry.Value!;
            Assert(!ordinalAfterPlaceholder.IsMatch(value), $"'{key}' bakes in a hard-coded ordinal suffix: '{value}'");
        }
        Assert(manager.GetString("Card_CreditExpirationDatePattern", CultureInfo.InvariantCulture) == "MMM d",
            "the credit expiration date pattern resource has no ordinal specifier");
    }

    /// <summary>The pre-plural Card_Minutes/Card_Hours/Card_UnderBudget/Card_OverBudget keys (design doc §12) are gone.
    /// (so are the fragment keys the full sentences replaced.)</summary>
    private static void RunOldKeysAreGone(ResourceManager manager)
    {
        var oldKeys = new[] { "Card_Minutes", "Card_Hours", "Card_UnderBudget", "Card_OverBudget", "Card_CreditTooltipSingle",
            "Card_CreditTooltip_One", "Card_CreditTooltip_Other",
            "Card_PaceSuppressed", "Card_PacePercentUnavailable", "Card_ReasonMissingDuration", "Card_ReasonMissingReset",
            "Card_ReasonStale", "Card_ReasonAuthentication", "Card_ReasonAccountChanged", "Card_ReasonHistorical",
            "Card_ReasonWindowNotStarted", "Card_ReasonWindowReset", "Card_ReasonInsufficientElapsed", "Card_ReasonUnavailable",
            "Settings_ApplyFailed", "Settings_StartupCouldNotEnable", "Settings_StartupCouldNotDisable", "Settings_IssueUnavailable",
            "Settings_IssueNotOwned", "Settings_IssueStale", "Settings_IssueLauncherMissing", "Settings_IssueRevisionConflict",
            "Settings_IssueHistoryUnavailable", "Settings_IssueHistoryPruneFailed" };
        foreach (var oldKey in oldKeys)
            Assert(manager.GetString(oldKey, CultureInfo.InvariantCulture) is null, $"old key '{oldKey}' should have been removed");
    }

    /// <summary>
    /// Under en-US, UI strings use English wording and match their expected formatted output.
    /// </summary>
    private static void RunEnglishIsUnchanged()
    {
        var morning = new DateTime(2026, 9, 26, 6, 0, 0);
        var evening = new DateTime(2026, 9, 26, 18, 5, 0);
        var table = new (string Actual, string Expected)[]
        {
            (LocalizedText.CardResetAt(morning), "Resets Sat, Sep 26 · 6:00 AM"),
            (LocalizedText.CardFableThisWeekWithReset(morning), "FABLE THIS WEEK · Resets Sat, Sep 26 · 6:00 AM"),
            (LocalizedText.ChartSampleTooltip(evening, 0.63, TimeSpan.FromMinutes(12)), "Observed Sat, Sep 26 · 6:05 PM: 63% remaining · sample age 12 minutes"),
            (LocalizedText.ChartDay(morning), "SAT"),
            (LocalizedText.Percentage(0.63), "63%"),
            (LocalizedText.CardPastWindow(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero)), "Past week · Sep 19 – Sep 25"),
            (LocalizedText.CardResetAfterFirstUse(TimeSpan.FromDays(7)), "Resets 7 days after first use"),
            (LocalizedText.CardResetAfterFirstUse(TimeSpan.FromDays(1)), "Resets 1 day after first use"),
            (LocalizedText.CardResetAfterFirstUse(TimeSpan.FromHours(5)), "Resets 5 hours after first use"),
            (LocalizedText.CardResetAfterFirstUse(TimeSpan.FromHours(1)), "Resets 1 hour after first use"),
            (LocalizedText.CreditTooltip(1), "You have 1 usage reset."),
            (LocalizedText.CreditTooltip(3), "You have 3 usage resets total. Click to see the remaining 2."),
            (LocalizedText.CreditTooltip(0), "You have 0 usage resets."),
            (LocalizedText.CreditUndisclosed(1), "1 additional usage reset with undisclosed expiration"),
            (LocalizedText.CreditUndisclosed(2), "2 additional usage resets with undisclosed expiration"),
            (LocalizedText.SettingsRefreshInterval(5, 30), "Refresh interval (5–30 minutes)"),
            (LocalizedText.SettingsRetentionDays(7, 365), "Keep usage history (7–365 days)"),
            (LocalizedText.SettingsApplyFailed("settings_save_io_error"), "Could not apply settings: unavailable. Review the message and try again."),
            (LocalizedText.SettingsApplyFailed("startup_registration_not_owned"), "Could not apply settings: not recognized. Review the message and try again."),
            (LocalizedText.SettingsApplyFailed("startup_registration_stale"), "Could not apply settings: stale launcher. Review the message and try again."),
            (LocalizedText.SettingsApplyFailed("startup_launcher_missing"), "Could not apply settings: launcher unavailable. Review the message and try again."),
            (LocalizedText.SettingsApplyFailed("settings_revision_conflict"), "Could not apply settings: settings changed elsewhere. Review the message and try again."),
            (LocalizedText.SettingsApplyFailed("history_unavailable"), "Could not apply settings: usage history is unavailable, so retention could not be applied. Review the message and try again."),
            (LocalizedText.SettingsApplyFailed("history_prune_failed"), "Could not apply settings: old usage history could not be removed. Review the message and try again."),
            (LocalizedText.StartupRegistrationIssue(new StartupRegistrationState(false, "startup_registration_not_owned"))!, "Couldn't turn on start at sign-in (not recognized)."),
            (LocalizedText.StartupRegistrationIssue(new StartupRegistrationState(true, "startup_registration_stale"))!, "Couldn't turn off start at sign-in (stale launcher)."),
            (LocalizedText.StartupRegistrationIssue(new StartupRegistrationState(true, "startup_launcher_missing"))!, "Couldn't turn off start at sign-in (launcher unavailable)."),
            (PaceSummaryWithHours(0.2, 33.8, 65), "33.8 hours under budget · 65% pace"),
            (PaceSummaryWithHours(-0.3, 58.6, 194), "58.6 hours over budget · 194% pace"),
            (PaceSummaryWithHours(0.05, 8.4, null, PaceUnavailableReason.InsufficientElapsed), "8.4 hours under budget · pace percent unavailable: window just started"),
            (LocalizedText.PaceSummary(Pace(0.1, TimeSpan.FromMinutes(30), 67), TimeSpan.FromHours(5), compact: true), "30 minutes under budget · 67% pace"),
        };
        foreach (var (actual, expected) in table)
            Assert(actual == expected, $"English is unchanged: expected '{expected}', got '{actual}'");

        var suppressed = new (PaceUnavailableReason Reason, string Expected)[]
        {
            (PaceUnavailableReason.MissingDuration, "Pace unavailable: window duration unavailable"),
            (PaceUnavailableReason.MissingReset, "Pace unavailable: reset time unavailable"),
            (PaceUnavailableReason.StaleSource, "Pace unavailable: reading is stale"),
            (PaceUnavailableReason.AuthenticationFailed, "Pace unavailable: sign-in needs attention"),
            (PaceUnavailableReason.AccountChanged, "Pace unavailable: account changed"),
            (PaceUnavailableReason.HistoricalOnly, "Pace unavailable: waiting for a fresh reading"),
            (PaceUnavailableReason.FutureWindowStart, "Pace unavailable: window has not started"),
            (PaceUnavailableReason.WindowReset, "Pace unavailable: window has reset"),
            (PaceUnavailableReason.InsufficientElapsed, "Pace unavailable: window just started"),
            ((PaceUnavailableReason)99, "Pace unavailable: source unavailable"),
            // None has a concise fallback label even though no caller currently produces it.
            (PaceUnavailableReason.None, "Pace unavailable"),
        };
        foreach (var (reason, expected) in suppressed)
        {
            var noPace = new PaceResult(0.5, null, null, null, null, null, reason, reason);
            var actual = LocalizedText.PaceSummary(noPace, TimeSpan.FromDays(7));
            Assert(actual == expected, $"suppressed pace for {reason}: expected '{expected}', got '{actual}'");
        }

        var now = DateTimeOffset.UtcNow;
        var snapshot = new AccountSnapshot(ProviderIds.Claude, "synthetic-shell-spec-scope", "max", now, now, "synthetic.shell.spec",
            [new QuotaBucket("weekly", QuotaBucketRole.Weekly, 0.63, TimeSpan.FromDays(7), now.AddDays(3), "synthetic.shell.spec")]);
        var ready = new ProviderDisplayState(ProviderIds.Claude, 1, true, false, ProviderStatus.Ready, snapshot, SnapshotFreshness.Fresh,
            null, [], null, TimeSpan.Zero, now);
        var parts = LocalizedText.StatusLine(ready);
        Assert(parts.Count == 3 && parts[0].IsTitle && !parts[1].IsTitle && !parts[2].IsTitle &&
               string.Concat(parts.Select(part => part.Text)) == LocalizedText.ProviderStatus(ready) + " · " + LocalizedText.ProviderStatusDetails(ready, snapshot),
            "the status line is the SemiBold title, \" · \" and the details, as before");
        var retry = new DateTimeOffset(2026, 9, 26, 18, 5, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 26, 18, 5, 0)));
        var limited = ready with { Status = ProviderStatus.RateLimited, Snapshot = null, RetryAtUtc = retry.ToUniversalTime() };
        var limitedText = LocalizedText.ProviderStatusDetails(limited, null);
        Assert(limitedText == "The provider allows another read after 6:05 PM.", $"the rate-limit time reads as before (got '{limitedText}')");
    }

    /// <summary>Only the five-hour pace amount uses minutes below one hour and tenths of an hour above it.</summary>
    private static void RunFiveHourPaceUnits()
    {
        foreach (var compact in new[] { false, true })
        foreach (var sign in new[] { -1d, 1d })
        foreach (var (amount, expected) in new[]
                 {
                     (TimeSpan.FromMinutes(1), "1 minute"),
                     (TimeSpan.FromMinutes(30), "30 minutes"),
                     (TimeSpan.FromMinutes(59.4), "59 minutes"),
                     (TimeSpan.FromHours(1) - TimeSpan.FromTicks(1), "60 minutes"),
                     (TimeSpan.FromHours(1), "1.0 hour"),
                     (TimeSpan.FromHours(1.04), "1.0 hour"),
                     (TimeSpan.FromHours(1.05), "1.1 hours"),
                     (TimeSpan.FromHours(2.4), "2.4 hours"),
                     (TimeSpan.FromMinutes(200), "3.3 hours")
                 })
        {
            var direction = sign > 0 ? "under" : "over";
            var summary = LocalizedText.PaceSummary(Pace(sign * 0.1, amount, 87), TimeSpan.FromHours(5), compact);
            Assert(summary == $"{expected} {direction} budget · 87% pace",
                $"five-hour pace {amount} in compact={compact}: '{summary}'");
        }

        Assert(PaceSummaryWithHours(0.1, 0.8) == "0.8 hour under budget · 90% pace",
            "weekly/Fable pace keeps fractional hours below one hour");
        using var germanFormat = LocalizedText.OverrideForSpecs(English, CultureInfo.GetCultureInfo("de-DE"));
        Assert(LocalizedText.PaceSummary(Pace(0.1, TimeSpan.FromHours(2.4)), TimeSpan.FromHours(5), compact: true)
               == "2,4 hours under budget · 90% pace", "five-hour hours follow the user's number format");
    }

    /// <summary>Values that round to zero display "On budget"; positive time below one minute uses
    /// the appropriate fractional-minute label.</summary>
    private static void RunOnBudgetFollowsTheDisplayedValue()
    {
        var cases = new (string Actual, string Expected)[]
        {
            (PaceSummaryWithHours(0.0002, 0.03), "On budget · 90% pace"),
            (PaceSummaryWithHours(-0.0002, 0.04), "On budget · 90% pace"),
            (PaceSummaryWithHours(0.0002, 0.049), "On budget · 90% pace"),
            (PaceSummaryWithHours(-0.0002, 0.049), "On budget · 90% pace"),
            (PaceSummaryWithHours(0.0002, 0.05), "0.1 hour under budget · 90% pace"),
            (PaceSummaryWithHours(-0.0002, 0.05), "0.1 hour over budget · 90% pace"),
            (PaceSummaryWithHours(-0.0002, 0.051), "0.1 hour over budget · 90% pace"),
            (PaceSummaryWithHours(0.0003, 0.05), "0.1 hour under budget · 90% pace"),
            (PaceSummaryWithHours(0, 0), "On budget · 90% pace"),
            (LocalizedText.PaceSummary(Pace(0.001, TimeSpan.FromSeconds(20)), TimeSpan.FromHours(5), compact: true), "On budget · 90% pace"),
            (LocalizedText.PaceSummary(Pace(-0.001, TimeSpan.FromSeconds(29.9)), TimeSpan.FromHours(5), compact: true), "On budget · 90% pace"),
            (LocalizedText.PaceSummary(Pace(0.001, TimeSpan.FromSeconds(30)), TimeSpan.FromHours(5), compact: true), "1 minute under budget · 90% pace"),
            (LocalizedText.PaceSummary(Pace(-0.001, TimeSpan.FromSeconds(30)), TimeSpan.FromHours(5), compact: true), "1 minute over budget · 90% pace"),
            (LocalizedText.PaceSummary(Pace(-0.001, TimeSpan.FromSeconds(30.1)), TimeSpan.FromHours(5), compact: true), "1 minute over budget · 90% pace"),
            (LocalizedText.PaceSummary(Pace(-0.001, TimeSpan.FromSeconds(40)), TimeSpan.FromHours(5), compact: true), "1 minute over budget · 90% pace"),
        };
        foreach (var (actual, expected) in cases)
            Assert(actual == expected, $"on budget by the displayed value: expected '{expected}', got '{actual}'");
    }

    /// <summary>Uses the production calculator and label together at the display midpoint for weekly, Fable, and short windows.</summary>
    private static void RunCalculatedPaceBandMatchesItsDisplayedAmount()
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var (role, duration, amounts) in new[]
                 {
                     (QuotaBucketRole.Weekly, TimeSpan.FromDays(7), new[] { 0.049, 0.05, 0.051 }),
                     (QuotaBucketRole.FeaturedWeekly, TimeSpan.FromDays(7), new[] { 0.049, 0.05, 0.051 }),
                     (QuotaBucketRole.Short, TimeSpan.FromHours(5), new[] { 0.49, 0.5, 0.51 })
                 })
        {
            foreach (var amount in amounts)
            foreach (var sign in new[] { -1d, 1d })
            {
                var equivalent = role == QuotaBucketRole.Short
                    ? TimeSpan.FromMinutes(amount)
                    : TimeSpan.FromHours(amount);
                var delta = equivalent.Ticks / (double)duration.Ticks;
                var bucket = new QuotaBucket("pace-proof", role, 0.5 + sign * delta, duration, start + duration, "pace-proof");
                var result = PaceCalculator.Calculate(bucket, start + TimeSpan.FromTicks(duration.Ticks / 2), SnapshotFreshness.Fresh);
                var summary = LocalizedText.PaceSummary(result, duration, compact: role == QuotaBucketRole.Short);
                var displaysZero = summary.StartsWith("On budget", StringComparison.Ordinal);
                var roundedToZero = amount < (role == QuotaBucketRole.Short ? 0.5 : 0.05);
                var expectedBand = sign > 0 || roundedToZero ? PaceBand.OnPace : PaceBand.SlightlyOver;
                Assert(displaysZero == roundedToZero && result.Band == expectedBand,
                    $"calculated {role} amount {amount} with sign {sign} keeps label and band aligned: '{summary}', {result.Band}");
            }
        }

        var week = TimeSpan.FromDays(7);
        var weekStart = start;
        var slightOverBucket = new QuotaBucket("pace-proof", QuotaBucketRole.Weekly,
            0.5 - 0.1 / week.TotalHours, week, weekStart + week, "pace-proof");
        var slightOver = PaceCalculator.Calculate(slightOverBucket, weekStart + TimeSpan.FromHours(84), SnapshotFreshness.Fresh);
        var slightOverSummary = LocalizedText.PaceSummary(slightOver, week);
        Assert(slightOver.Band == PaceBand.SlightlyOver && slightOverSummary == "0.1 hour over budget · 100% pace",
            $"a pace percent rounded to 100 does not erase displayed overage: '{slightOverSummary}', {slightOver.Band}");
    }

    /// <summary>
    /// the plural rules for the twelve release languages, keyed on the
    /// wording culture's full name, with the number of visible decimals. English uses the rule that 0 &lt; n ≤ 1 is
    /// One, design doc §12); the others follow CLDR.
    /// </summary>
    private static void RunPluralRules()
    {
        var cases = new (string Culture, double Value, int Decimals, PluralCategory Expected)[]
        {
            ("en-US", 0, 0, PluralCategory.Other), ("en-US", 0.8, 1, PluralCategory.One), ("en-US", 1, 0, PluralCategory.One),
            ("en-US", 1, 1, PluralCategory.One), ("en-US", 1.1, 1, PluralCategory.Other), ("en-GB", 2, 0, PluralCategory.Other),
            ("es-ES", 1, 0, PluralCategory.One), ("es-ES", 1, 1, PluralCategory.One), ("es-ES", 0, 0, PluralCategory.Other), ("es-ES", 0.5, 1, PluralCategory.Other),
            ("fr-FR", 0, 0, PluralCategory.One), ("fr-FR", 0.5, 1, PluralCategory.One), ("fr-FR", 1.9, 1, PluralCategory.One), ("fr-FR", 2, 0, PluralCategory.Other),
            ("pt-BR", 0, 0, PluralCategory.One), ("pt-BR", 1.5, 1, PluralCategory.One), ("pt-BR", 2, 0, PluralCategory.Other),
            ("pt", 1, 1, PluralCategory.One),
            ("pt-PT", 1, 0, PluralCategory.One), ("pt-PT", 1, 1, PluralCategory.Other), ("pt-PT", 0, 0, PluralCategory.Other),
            ("de-DE", 1, 0, PluralCategory.One), ("de-DE", 1, 1, PluralCategory.Other), ("de-DE", 0, 0, PluralCategory.Other), ("de-DE", 2, 0, PluralCategory.Other),
            ("it-IT", 1, 0, PluralCategory.One), ("it-IT", 1, 1, PluralCategory.Other), ("it-IT", 0.5, 1, PluralCategory.Other),
            ("ja-JP", 1, 0, PluralCategory.Other),
            ("ru-RU", 0, 0, PluralCategory.Many), ("ru-RU", 1, 0, PluralCategory.One),
            ("ru-RU", 2, 0, PluralCategory.Few), ("ru-RU", 5, 0, PluralCategory.Many),
            ("ru-RU", 11, 0, PluralCategory.Many), ("ru-RU", 21, 0, PluralCategory.One),
            ("ru-RU", 22, 0, PluralCategory.Few), ("ru-RU", 1.2, 1, PluralCategory.Other),
        };
        foreach (var (culture, value, decimals, expected) in cases)
        {
            var actual = PluralRules.Select(CultureInfo.GetCultureInfo(culture), value, decimals);
            Assert(actual == expected, $"plural rule {culture} {value} with {decimals} decimals: expected {expected}, got {actual}");
        }
    }

    private static void RunLocaleResolver()
    {
        var en = CultureInfo.GetCultureInfo("en-US");
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var de = CultureInfo.GetCultureInfo("de-DE");
        static Dictionary<string, string?> Env(params (string Key, string? Value)[] values) =>
            values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var languageList = LocaleResolver.Resolve("auto", true,
            Env(("LANGUAGE", "fr_FR:en_US"), ("LANG", "en_US.UTF-8")), en, en);
        Assert(languageList.UiCulture.Name == "fr" && languageList.FormatCulture.Name == "en-US",
            "Linux LANGUAGE chooses French wording while LANG keeps English formatting");

        var messages = LocaleResolver.Resolve("auto", true,
            Env(("LC_MESSAGES", "fr_FR"), ("LANG", "en_US.UTF-8")), en, en);
        Assert(messages.UiCulture.Name == "fr" && messages.FormatCulture.Name == "en-US",
            "Linux LC_MESSAGES chooses French wording independently from LANG formatting");

        foreach (var cLocale in new[] { "C", "C.UTF-8", "POSIX", "POSIX.UTF-8" })
        {
            var resolved = LocaleResolver.Resolve("auto", true,
                Env(("LANGUAGE", "fr_FR"), ("LC_ALL", cLocale), ("LANG", "de_DE.UTF-8")), fr, de);
            Assert(resolved.UiCulture.Name == "en-US" && resolved.FormatCulture.Name.Length == 0,
                $"explicit LC_ALL={cLocale} selects English wording and invariant formatting");
        }

        var malformed = LocaleResolver.Resolve("auto", true,
            Env(("LANGUAGE", "bad@;xx_YY"), ("LC_ALL", "broken"), ("LC_MESSAGES", "not-a-locale"), ("LANG", "zz_ZZ")), fr, de);
        Assert(malformed.UiCulture.Name == "fr" && malformed.FormatCulture.Name == "de-DE",
            $"malformed and unsupported environment values fall back to captured cultures (ui {malformed.UiCulture.Name}, format {malformed.FormatCulture.Name})");

        var invalidAllWithValidLang = LocaleResolver.Resolve("auto", true,
            Env(("LC_ALL", "broken"), ("LANGUAGE", "es_ES"), ("LANG", "fr_FR.UTF-8")), en, de);
        Assert(invalidAllWithValidLang.UiCulture.Name == "es" && invalidAllWithValidLang.FormatCulture.Name == "fr-FR",
            "an invalid LC_ALL lets the resolver continue to valid LANGUAGE and LANG inputs");

        var portuguesePortugal = LocaleResolver.Resolve("auto", true,
            Env(("LC_MESSAGES", "pt_PT")), en, en);
        Assert(portuguesePortugal.UiCulture.Name == "en-US", "pt-PT does not map to the Brazilian Portuguese resource");

        var manual = LocaleResolver.Resolve("ru", true,
            Env(("LANGUAGE", "fr_FR"), ("LANG", "de_DE.UTF-8")), fr, de);
        Assert(manual.UiCulture.Name == "ru" && manual.FormatCulture.Name == "ru",
            "manual Russian choice controls wording and formatting despite conflicting OS values");

        var simplifiedChinese = LocaleResolver.Resolve("auto", true,
            Env(("LANG", "zh_SG.UTF-8")), en, en);
        var traditionalChinese = LocaleResolver.Resolve("auto", true,
            Env(("LANG", "zh_HK.UTF-8")), en, en);
        Assert(simplifiedChinese.UiCulture.Name == "zh-Hans" && traditionalChinese.UiCulture.Name == "zh-Hant",
            $"Chinese region aliases resolve to their script resource cultures (simplified {simplifiedChinese.UiCulture.Name}, traditional {traditionalChinese.UiCulture.Name})");

        var neutralTraditional = LocaleResolver.Resolve("auto", true,
            Env(("LANGUAGE", "zh-Hant:en_US")), en, en);
        var qualifiedSimplified = LocaleResolver.Resolve("auto", true,
            Env(("LANG", "zh-Hans-HK.UTF-8")), en, en);
        Assert(neutralTraditional.UiCulture.Name == "zh-Hant" && qualifiedSimplified.UiCulture.Name == "zh-Hans",
            $"Linux automatic selection honors explicit scripts for neutral and qualified cultures (neutral {neutralTraditional.UiCulture.Name}, qualified {qualifiedSimplified.UiCulture.Name})");

        var windows = LocaleResolver.Resolve("auto", false, Env(("LANGUAGE", "de_DE")), fr, de);
        Assert(windows.UiCulture.Name == "fr" && windows.FormatCulture.Name == "de-DE",
            "Windows automatic selection uses the captured UI and format cultures");

        var windowsNeutralScript = LocaleResolver.Resolve("auto", false, Env(),
            CultureInfo.GetCultureInfo("zh-Hant"), en);
        var windowsQualifiedScript = LocaleResolver.Resolve("auto", false, Env(),
            CultureInfo.GetCultureInfo("zh-Hans-HK"), en);
        Assert(windowsNeutralScript.UiCulture.Name == "zh-Hant" && windowsQualifiedScript.UiCulture.Name == "zh-Hans",
            $"Windows automatic selection honors explicit scripts in captured neutral and qualified cultures (neutral {windowsNeutralScript.UiCulture.Name}, qualified {windowsQualifiedScript.UiCulture.Name})");
    }

    /// <summary>Without an override, LocalizedText takes its wording culture from CurrentUICulture and its
    /// number format culture from CurrentCulture.</summary>
    private static void RunCulturesFollowTheMachine()
    {
        var savedUi = CultureInfo.CurrentUICulture;
        var savedFormat = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert(LocalizedText.UiCulture.Name == "fr-FR" && LocalizedText.FormatCulture.Name == "de-DE",
                $"with no override the cultures follow the thread (ui {LocalizedText.UiCulture.Name}, format {LocalizedText.FormatCulture.Name})");
            Assert(LocalizedText.CardRefresh == "Actualiser", "French wording uses the French satellite resources");
        }
        finally
        {
            CultureInfo.CurrentUICulture = savedUi;
            CultureInfo.CurrentCulture = savedFormat;
        }
    }

    /// <summary>Resource lookups use the wording culture, and a German count of one selects the "_One" form.</summary>
    private static void RunLookupsCarryTheUiCulture()
    {
        var recording = new RecordingResources();
        var german = CultureInfo.GetCultureInfo("de-DE");
        using (LocalizedText.OverrideForSpecs(german, german, recording))
        {
            _ = LocalizedText.CardRefresh;
            _ = LocalizedText.CardResetAfterFirstUse(TimeSpan.FromDays(1));
            _ = LocalizedText.FormatAge(TimeSpan.FromHours(1));
        }
        Assert(recording.Requests.Count > 0 && recording.Requests.All(request => request.Culture == "de-DE"),
            $"every lookup carries the wording culture (got {string.Join(", ", recording.Requests.Select(r => $"{r.Name}@{r.Culture}"))})");
        Assert(recording.Requests.Any(request => request.Name == "Card_DurationDays_One"), "German 1 day selects the _One form");
        Assert(recording.Requests.Any(request => request.Name == "Card_AgeHours_Other"), "German 1.0 hours (one visible decimal) selects the _Other form");
    }

    /// <summary>The reset line ends with the time left, to the nearest tenth of a day,
    /// hour under a day, or minute under an hour, each unit rounded before the next one up is chosen. A reset
    /// that has passed shows no time left. "1.0 day" is singular under the English plural rule.</summary>
    private static void RunResetTimeLeft()
    {
        using var scope = LocalizedText.OverrideForSpecs(English, English);
        var reset = new DateTimeOffset(new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Local));
        const string moment = "Resets Thu, Oct 1 · 6:00 AM";
        var table = new (TimeSpan Left, string Expected)[]
        {
            (TimeSpan.FromDays(2.1), moment + " (2.1 days)"),
            (TimeSpan.FromDays(2.14), moment + " (2.1 days)"),
            (TimeSpan.FromDays(2.16), moment + " (2.2 days)"),
            (TimeSpan.FromDays(6.96), moment + " (7.0 days)"),
            (TimeSpan.FromHours(26.5), moment + " (1.1 days)"),
            (TimeSpan.FromHours(24), moment + " (1.0 day)"),
            (TimeSpan.FromHours(23.6), moment + " (1.0 day)"),
            (TimeSpan.FromHours(23.4), moment + " (23 hours)"),
            (TimeSpan.FromHours(12), moment + " (12 hours)"),
            (TimeSpan.FromMinutes(90), moment + " (2 hours)"),
            (TimeSpan.FromMinutes(61), moment + " (1 hour)"),
            (TimeSpan.FromMinutes(59.6), moment + " (1 hour)"),
            (TimeSpan.FromMinutes(59.4), moment + " (59 minutes)"),
            (TimeSpan.FromMinutes(12), moment + " (12 minutes)"),
            (TimeSpan.FromMinutes(1), moment + " (1 minute)"),
            (TimeSpan.FromSeconds(10), moment + " (1 minute)"),
            (TimeSpan.Zero, moment),
            (TimeSpan.FromMinutes(-5), moment),
        };
        foreach (var (left, expected) in table)
        {
            var actual = LocalizedText.CardResetAt(reset, reset - left);
            Assert(actual == expected, $"reset {left} away: expected '{expected}', got '{actual}'");
        }

        // The 5-hour line shows hours to the nearest tenth, and minutes under an hour.
        var tenths = new (TimeSpan Left, string Expected)[]
        {
            (TimeSpan.FromHours(4.97), moment + " (5.0 hours)"),
            (TimeSpan.FromMinutes(66), moment + " (1.1 hours)"),
            (TimeSpan.FromMinutes(62), moment + " (1.0 hour)"),
            (TimeSpan.FromMinutes(60), moment + " (1.0 hour)"),
            (TimeSpan.FromMinutes(59.6), moment + " (1.0 hour)"),
            (TimeSpan.FromMinutes(59.4), moment + " (59 minutes)"),
            (TimeSpan.FromMinutes(3), moment + " (3 minutes)"),
            (TimeSpan.FromSeconds(10), moment + " (1 minute)"),
            (TimeSpan.Zero, moment),
        };
        foreach (var (left, expected) in tenths)
        {
            var actual = LocalizedText.CardResetAt(reset, reset - left, tenthsOfHours: true);
            Assert(actual == expected, $"5-hour reset {left} away: expected '{expected}', got '{actual}'");
        }
    }

    /// <summary>Numbers, percentages, and times use the format culture while wording uses the wording culture.</summary>
    private static void RunFormatsFollowTheFormatCulture()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");
        using var scope = LocalizedText.OverrideForSpecs(English, german);
        var local = new DateTime(2026, 9, 26, 18, 5, 0);
        Assert(LocalizedText.Percentage(0.63) == 0.63.ToString("P0", german), $"percent in the format culture (got '{LocalizedText.Percentage(0.63)}')");
        var reset = LocalizedText.CardResetAt(local);
        var expectedReset = "Resets " + local.ToString("ddd, MMM d", german) + " · " + local.ToString("t", german);
        Assert(reset == expectedReset, $"reset time in the format culture ('{expectedReset}', got '{reset}')");
        var hours = PaceSummaryWithHours(0.2, 33.8);
        Assert(hours.StartsWith(33.8.ToString("0.0", german) + " hours under budget", StringComparison.Ordinal),
            $"hours in the format culture's decimal separator (got '{hours}')");
    }

    /// <summary>The pseudo-long locale preserves placeholders and date patterns, lengthens worded values, and
    /// supplies strings through LocalizedText.</summary>
    private static void RunPseudoLocale(ResourceManager manager)
    {
        var pseudo = new PseudoLocaleResources();
        var placeholder = new Regex(@"\{\d+\}");
        foreach (var key in AllResourceKeys(manager))
        {
            var english = manager.GetString(key, CultureInfo.InvariantCulture)!;
            var value = pseudo.GetString(key, CultureInfo.GetCultureInfo("qps-ploc"))!;
            var englishPlaceholders = placeholder.Matches(english).Select(match => match.Value).OrderBy(text => text, StringComparer.Ordinal);
            var pseudoPlaceholders = placeholder.Matches(value).Select(match => match.Value).OrderBy(text => text, StringComparer.Ordinal);
            Assert(englishPlaceholders.SequenceEqual(pseudoPlaceholders), $"pseudo '{key}' keeps its placeholders ('{value}')");
            if (key.EndsWith("Pattern", StringComparison.Ordinal) || english.Count(char.IsAsciiLetter) < 2)
                Assert(value == english, $"pseudo '{key}' leaves a format pattern or a separator unchanged ('{value}')");
            else
                Assert(value.Length >= english.Length * (1 + PseudoLocaleResources.Expansion), $"pseudo '{key}' is at least 30 % longer ('{value}')");
        }
        using var scope = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("qps-ploc"), English, pseudo);
        Assert(LocalizedText.CardRefresh == PseudoLocaleResources.Pseudo("Refresh"), $"LocalizedText reads the pseudo-long locale (got '{LocalizedText.CardRefresh}')");
    }

    private static List<string> AllResourceKeys(ResourceManager manager)
    {
        var resourceSet = manager.GetResourceSet(CultureInfo.InvariantCulture, true, true)
            ?? throw new InvalidOperationException("Could not load the neutral resource set.");
        var keys = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in resourceSet)
            keys.Add((string)entry.Key);
        return keys;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL: {message}");
    }

    /// <summary>Records the key and culture of every lookup, answering from the neutral resources.</summary>
    private sealed class RecordingResources : ResourceManager
    {
        private readonly ResourceManager _neutral = new("Glideslope.App.Resources.Strings", typeof(LocalizedText).Assembly);
        public List<(string Name, string? Culture)> Requests { get; } = [];
        public override string? GetString(string name) => GetString(name, null);

        public override string? GetString(string name, CultureInfo? culture)
        {
            Requests.Add((name, culture?.Name));
            return _neutral.GetString(name, culture);
        }
    }

    internal sealed class RussianCreditTooltipResources : ResourceManager
    {
        private readonly ResourceManager _neutral = new("Glideslope.App.Resources.Strings", typeof(LocalizedText).Assembly);
        public override string? GetString(string name) => GetString(name, null);
        public override string? GetString(string name, CultureInfo? culture) => name switch
        {
            "Card_CreditTooltip_Click_One" => "click one {0} {1}",
            "Card_CreditTooltip_Click_Few" => "click few {0} {1}",
            "Card_CreditTooltip_Click_Many" => "click many {0} {1}",
            "Card_CreditTooltip_Click_Other" => "click other {0} {1}",
            "Card_CreditTooltip_NoClick_One" => "no click one {0}",
            "Card_CreditTooltip_NoClick_Few" => "no click few {0}",
            "Card_CreditTooltip_NoClick_Many" => "no click many {0}",
            "Card_CreditTooltip_NoClick_Other" => "no click other {0}",
            _ => _neutral.GetString(name, culture),
        };
    }

    internal sealed class TallerCardMinimumResources : ResourceManager
    {
        private readonly ResourceManager _neutral = new("Glideslope.App.Resources.Strings", typeof(LocalizedText).Assembly);
        public override string? GetString(string name) => GetString(name, null);
        public override string? GetString(string name, CultureInfo? culture) => name == "Card_MinimumHeight"
            ? "790"
            : _neutral.GetString(name, culture);
    }

    /// <summary>
    /// the pseudo-long locale, used only by the specs to prove the
    /// card and Settings reflow for longer text. Every English value comes back with its letters accented and 30 %
    /// longer (a padding word group at the end), its {n} placeholders untouched; the .NET date patterns (keys ending
    /// in "Pattern") and values with fewer than two letters (separators, bare placeholders, numbers) come back
    /// unchanged. It is generated from the neutral resources in memory rather than checked in as
    /// Strings.qps-ploc.resx, because Glideslope.App.csproj would build a checked-in file into a qps-ploc satellite
    /// assembly that ships with the app.
    /// </summary>
    internal sealed class PseudoLocaleResources : ResourceManager
    {
        internal const double Expansion = 0.3;
        private const string Filler = "ẋýžẋŵ";
        private static readonly Dictionary<char, char> Accents = new()
        {
            ['a'] = 'á', ['e'] = 'é', ['i'] = 'í', ['o'] = 'ö', ['u'] = 'ü', ['c'] = 'ç', ['n'] = 'ñ', ['y'] = 'ý', ['s'] = 'š', ['z'] = 'ž',
            ['A'] = 'Á', ['E'] = 'É', ['I'] = 'Í', ['O'] = 'Ö', ['U'] = 'Ü', ['C'] = 'Ç', ['N'] = 'Ñ', ['Y'] = 'Ý', ['S'] = 'Š', ['Z'] = 'Ž',
        };
        private readonly ResourceManager _neutral = new("Glideslope.App.Resources.Strings", typeof(LocalizedText).Assembly);

        public override string? GetString(string name) => GetString(name, null);

        public override string? GetString(string name, CultureInfo? culture)
        {
            var english = _neutral.GetString(name, CultureInfo.InvariantCulture);
            return english is null || name.EndsWith("Pattern", StringComparison.Ordinal) ? english : Pseudo(english);
        }

        internal static string Pseudo(string english)
        {
            if (english.Count(char.IsAsciiLetter) < 2) return english;
            var builder = new StringBuilder(english.Length * 2);
            foreach (var character in english)
                builder.Append(Accents.TryGetValue(character, out var accented) ? accented : character);
            var extra = (int)Math.Ceiling(english.Length * Expansion);
            var padding = new StringBuilder(extra);
            for (var index = 0; index < extra; index++)
                padding.Append(index % 6 == 5 ? ' ' : Filler[index % Filler.Length]);
            var pad = padding.ToString().TrimEnd();
            var upper = english.Count(char.IsUpper) > english.Count(char.IsLower);
            return builder.Append(' ').Append(upper ? pad.ToUpperInvariant() : pad).ToString();
        }
    }
}

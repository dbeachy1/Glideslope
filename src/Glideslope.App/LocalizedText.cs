using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.App;

/// <summary>
/// Presentation-only English resource access. Provider IDs, issue codes, and
/// diagnostics remain stable values owned by Core; this type supplies their UI wording.
/// Wording uses <see cref="UiCulture"/>; numbers and dates use <see cref="FormatCulture"/>.
/// </summary>
internal static class LocalizedText
{
    /// <summary>The language of the wording: which Strings.{culture}.resx is read and which plural rule
    /// applies. The user's display language unless a spec overrides it.</summary>
    public static CultureInfo UiCulture => _uiCultureOverride ?? _uiCulture ?? CultureInfo.CurrentUICulture;

    /// <summary>The culture numbers, percentages, times and month and day names are formatted in: the user's
    /// regional format unless a spec overrides it. The date patterns themselves are resources, so a translator
    /// can reorder them.</summary>
    public static CultureInfo FormatCulture => _formatCultureOverride ?? _formatCulture ?? CultureInfo.CurrentCulture;

    private static readonly ResourceManager NeutralResources = new("Glideslope.App.Resources.Strings", typeof(LocalizedText).Assembly);
    private static readonly Regex StatusLinePlaceholder = new(@"\{([01])\}", RegexOptions.CultureInvariant);
    private static CultureInfo? _uiCultureOverride;
    private static CultureInfo? _formatCultureOverride;
    private static CultureInfo? _uiCulture;
    private static CultureInfo? _formatCulture;
    private static ResourceManager? _resourcesOverride;
    private static ResourceManager Resources => _resourcesOverride ?? NeutralResources;

    internal static void SetCultures(CultureInfo uiCulture, CultureInfo formatCulture)
    {
        _uiCulture = uiCulture ?? throw new ArgumentNullException(nameof(uiCulture));
        _formatCulture = formatCulture ?? throw new ArgumentNullException(nameof(formatCulture));
    }

    /// <summary>Temporarily overrides cultures and, optionally, the resource source for specs.</summary>
    internal static IDisposable OverrideForSpecs(CultureInfo uiCulture, CultureInfo formatCulture, ResourceManager? resources = null)
    {
        ArgumentNullException.ThrowIfNull(uiCulture);
        ArgumentNullException.ThrowIfNull(formatCulture);
        var previous = (_uiCultureOverride, _formatCultureOverride, _resourcesOverride);
        (_uiCultureOverride, _formatCultureOverride, _resourcesOverride) = (uiCulture, formatCulture, resources);
        return new OverrideScope(() => (_uiCultureOverride, _formatCultureOverride, _resourcesOverride) = previous);
    }

    private sealed class OverrideScope(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose()
        {
            _restore?.Invoke();
            _restore = null;
        }
    }

    public static string AppName => Get("App_Name");
    public static string TrayShow => Get("Tray_Show");
    public static string TrayExit => Get("Tray_Exit");
    public static string TrayToolTip => Get("Tray_ToolTip");

    public static string SettingsTitle => Get("Settings_Title");
    public static string SettingsHeading => Get("Settings_Heading");
    public static string SettingsEnabledProviders => Get("Settings_EnabledProviders");
    public static string SettingsStartAtSignIn => Get("Settings_StartAtSignIn");
    public static string SettingsCancel => Get("Settings_Cancel");
    public static string SettingsSave => Get("Settings_Save");
    public static string SettingsLanguage => Get("Settings_Language");
    public static string SettingsLanguageSystem => Get("Settings_LanguageSystem");
    public static string SettingsLanguageRestart => Get("Settings_LanguageRestart");
    public static string SettingsLanguageSavedRestart => Get("Settings_LanguageSavedRestart");
    public static string SettingsRestartNow => Get("Settings_RestartNow");
    public static string SettingsRestartFailed => Get("Settings_RestartFailed");

    public static string ProviderName(string providerId) => providerId switch
    {
        ProviderIds.Codex => Get("Provider_Codex"),
        ProviderIds.Claude => Get("Provider_Claude"),
        ProviderIds.Gemini => Get("Provider_Gemini"),
        _ => providerId,
    };

    public static string CardTitle(string providerId) => Format("Card_Title", ProviderName(providerId));
    public static string CardSettingsTooltip => Get("Card_SettingsTooltip");
    public static string CardResetSizeTooltip => Get("Card_ResetSizeTooltip");

    /// <summary>The mode-toggle button's tooltip and accessible name, which
    /// follow the mode the button would switch to next.</summary>
    public static string CardMiniModeTooltip => Get("Card_MiniModeTooltip");
    public static string CardFullModeTooltip => Get("Card_FullModeTooltip");

    /// <summary>The mini card's remaining bar tooltip, "{0} remaining".</summary>
    public static string CardRemainingTooltip(double remainingFraction) => Format("Card_RemainingTooltip", Percentage(remainingFraction));
    public static string CardCloseTooltip(string providerId) => Format("Card_CloseTooltip", ProviderName(providerId));
    public static string CardMinimizeTooltip => Get("Card_MinimizeTooltip");
    public static string CardFooterNoSynthetic => Get("Card_FooterNoSynthetic");
    public static string CardWeeklyRemaining => Get("Card_WeeklyRemaining");
    public static string CardCreditExpires => Get("Card_CreditExpires");

    /// <summary>The orange credit-strip notice for a signed-out or missing CLI.</summary>
    public static string CardNoticeSignInHeading => Get("Card_NoticeSignInHeading");
    public static string CardNoticeMissingHeading => Get("Card_NoticeMissingHeading");
    public static string CardNoticeSignIn(string providerId) => providerId switch
    {
        Glideslope.Domain.ProviderIds.Codex => Get("Card_NoticeSignInCodex"),
        Glideslope.Domain.ProviderIds.Gemini => Get("Card_NoticeSignInGemini"),
        _ => Get("Card_NoticeSignInClaude")
    };
    public static string CardNoticeMissing(string providerId) => providerId switch
    {
        Glideslope.Domain.ProviderIds.Codex => Get("Card_NoticeMissingCodex"),
        Glideslope.Domain.ProviderIds.Gemini => Get("Card_NoticeMissingGemini"),
        _ => Get("Card_NoticeMissingClaude")
    };
    public static string CardCreditExpirationUnknown => Get("Card_CreditExpirationUnknown");
    public static string CardFiveHourDelta => Get("Card_FiveHourDelta");
    public static string CardFiveHourRemaining => Get("Card_FiveHourRemaining");
    public static string CardWeeklyGlideSlope => Get("Card_WeeklyGlideSlope");
    public static string CardObservedLegend => Get("Card_ObservedLegend");
    public static string CardEvenPaceLegend => Get("Card_EvenPaceLegend");
    public static string CardWeeklyDeltaTitle => Get("Card_WeeklyDeltaTitle");
    public static string CardWeeklyRemainingSection => Get("Card_WeeklyRemainingSection");
    public static string CardFableThisWeek => Get("Card_FableThisWeek");
    public static string CardFableDeltaTitle => Get("Card_FableDeltaTitle");
    public static string CardChartWindowHint => Get("Card_ChartWindowHint");
    public static string CardResetUnavailable => Get("Card_ResetUnavailable");
    public static string CardWindowUnavailable => Get("Card_WindowUnavailable");
    public static string CardPaceUnavailable => Get("Card_PaceUnavailable");

    /// <summary>Message for a window that has not started accumulating usage.</summary>
    public static string CardWindowNotStarted => Get("Card_WindowNotStarted");

    /// <summary>Short not-started message for the narrow Fable pace cell.</summary>
    public static string CardNoUsageYet => Get("Card_NoUsageYet");

    public static string CardResetAfterFirstUse(TimeSpan duration) => Format("Card_ResetAfterFirstUse", DurationWords(duration));

    private static string DurationWords(TimeSpan duration)
    {
        // Use shared plural rules so localized unit forms are handled consistently.
        if (duration >= TimeSpan.FromDays(1))
        {
            var days = (int)Math.Round(duration.TotalDays);
            return Plural("Card_DurationDays", days, 0, Count(days));
        }
        var hours = (int)Math.Round(duration.TotalHours);
        return Plural("Card_DurationHours", hours, 0, Count(hours));
    }
    public static string CardNoSamplesYet => Get("Card_NoSamplesYet");
    public static string CardConnecting => Get("Card_Connecting");
    public static string CardRefresh => Get("Card_Refresh");
    public static string CardUndock => Get("Card_Undock");
    public static string CardHistoryUnavailable => Get("Card_HistoryUnavailable");
    public static string CardDockPreview => Get("Card_DockPreview");
    public static string SettingsTheme => Get("Settings_Theme");
    public static string SettingsThemeSystem => Get("Settings_ThemeSystem");
    public static string SettingsThemeDark => Get("Settings_ThemeDark");
    public static string SettingsThemeLight => Get("Settings_ThemeLight");
    public static string SettingsAlwaysOnTop => Get("Settings_AlwaysOnTop");
    public static string SettingsSnapToScreenEdge => Get("Settings_SnapToScreenEdge");
    public static string SettingsResetPositions => Get("Settings_ResetPositions");
    public static string SettingsAtLeastOneProvider => Get("Settings_AtLeastOneProvider");

    /// <summary>Formats range values using the limits shared with their controls.</summary>
    public static string SettingsRefreshInterval(int minimum, int maximum) => Format("Settings_RefreshInterval", Count(minimum), Count(maximum));
    public static string SettingsRetentionDays(int minimum, int maximum) => Format("Settings_RetentionDays", Count(minimum), Count(maximum));

    /// <summary>The title-row button's tooltip, its flyout heading,
    /// and the Settings row label.</summary>
    public static string CardSize => Get("Card_Size");
    public static string SettingsCardSize => Get("Settings_CardSize");

    /// <summary>The "Mini card size" row directly under "Card size", and the
    /// "Start in mini mode" checkbox directly under that.</summary>
    public static string SettingsMiniCardSize => Get("Settings_MiniCardSize");
    public static string SettingsStartInMiniMode => Get("Settings_StartInMiniMode");
    // 2.1 Ctrl peek design §2: the checkbox directly under Start in mini mode.
    public static string SettingsCtrlPeek => Get("Settings_CtrlPeek");
    public static string SettingsLogFile => Get("Settings_LogFile");
    public static string SettingsOpenLogFolder => Get("Settings_OpenLogFolder");

    /// <summary>Minimum card height for the current UI language, supplied by the string table.</summary>
    public static double CardMinimumHeight
    {
        get
        {
            if (!double.TryParse(Get("Card_MinimumHeight"), NumberStyles.Float, CultureInfo.InvariantCulture, out var height) ||
                !double.IsFinite(height) || height < CardLayoutTiers.MinHeight)
                throw new InvalidDataException("Invalid localized card minimum height resource.");
            return Math.Max(CardLayoutTiers.MinHeight, height);
        }
    }

    /// <summary>Title row text: "{provider} • Glideslope {version}".</summary>
    public static string CardAppNameVersion(string version) => Format("Card_AppNameVersion", version);

    /// <summary>The chart heading's second line, "Plan type: {plan}".</summary>
    public static string CardPlanType(string plan) => Format("Card_PlanType", plan);

    /// <summary>Week-browsing button tooltips and the current-week label.</summary>
    public static string CardPreviousWeekTooltip => Get("Card_PreviousWeekTooltip");
    public static string CardNextWeekTooltip => Get("Card_NextWeekTooltip");
    public static string CardCurrentWeek => Get("Card_CurrentWeek");
    public static string CardCurrentWeekTooltip => Get("Card_CurrentWeekTooltip");

    /// <summary>Formats an inclusive past-week range, ending the day before the reset.</summary>
    public static string CardPastWindow(DateTimeOffset localStart, DateTimeOffset localReset)
    {
        var pattern = Get("Card_CreditExpirationDatePattern");
        var lastDay = localReset.AddDays(-1);
        return Format("Card_PastWindow", localStart.ToString(pattern, FormatCulture), lastDay.ToString(pattern, FormatCulture));
    }

    public static string Percentage(double value) => value.ToString("P0", FormatCulture);
    public static string CardResetAt(DateTime localTime) => Format("Card_ResetAt", ResetMoment(localTime));

    /// <summary>Formats a reset time with a rounded countdown when the reset is in the future. Short windows can
    /// use tenths of an hour; expired resets show only the reset time.</summary>
    public static string CardResetAt(DateTimeOffset reset, DateTimeOffset now, bool tenthsOfHours = false)
    {
        var moment = ResetMoment(reset.ToLocalTime().LocalDateTime);
        var left = reset - now;
        return left > TimeSpan.Zero
            ? Format("Card_ResetAtWithTimeLeft", moment, TimeLeftWords(left, tenthsOfHours))
            : Format("Card_ResetAt", moment);
    }

    // Each unit is rounded first and the next unit up is taken when the rounded value reaches it, so 59.6 minutes
    // reads "1 hour" ("1.0 hour" in tenths), not "60 minutes", and 23.6 hours reads "1.0 days", not "24 hours".
    private static string TimeLeftWords(TimeSpan left, bool tenthsOfHours)
    {
        var minutes = Math.Max(1, Math.Round(left.TotalMinutes, MidpointRounding.AwayFromZero));
        if (minutes < 60)
            return Plural("Card_TimeLeftMinutes", minutes, 0, Number(minutes, 0));
        var hourDecimals = tenthsOfHours ? 1 : 0;
        var hours = Math.Round(left.TotalHours, hourDecimals, MidpointRounding.AwayFromZero);
        if (hours < 24)
            return Plural("Card_TimeLeftHours", hours, hourDecimals, Number(hours, hourDecimals));
        var days = Math.Round(left.TotalDays, 1, MidpointRounding.AwayFromZero);
        return Plural("Card_TimeLeftDays", days, 1, Number(days, 1));
    }

    /// <summary>Formats a whole number using the format culture.</summary>
    internal static string Count(int value) => value.ToString(FormatCulture);

    /// <summary>Formats a number with a fixed number of decimals using the format culture.</summary>
    internal static string Number(double value, int decimals) =>
        value.ToString(decimals <= 0 ? "0" : "0." + new string('0', decimals), FormatCulture);

    /// <summary>
    /// Formats the Fable weekly reset when it differs from the main weekly reset.
    /// </summary>
    public static string CardFableThisWeekWithReset(DateTime localTime) => Format("Card_FableThisWeekWithReset", ResetMoment(localTime));

    // Both reset lines share resource-backed date/time formatting so translations can reorder the components.
    private static string ResetMoment(DateTime localTime) => Format("Card_DateTimeFormat",
        localTime.ToString(Get("Card_WeekdayDatePattern"), FormatCulture), localTime.ToString("t", FormatCulture));
    /// <summary>
    /// Design doc §12: the credit box reads "THU Oct 22 (in 26 days)" — a three-letter uppercase
    /// weekday, a three-letter month with a bare day number (no "22nd"), and a plural-aware
    /// in-days phrase that reads "today" instead of "in 0 days" when the expiration is today.
    /// The date pattern itself is a resource (`Card_CreditExpirationDatePattern`) so another
    /// language can reorder it (for example "d MMM"). No ordinal-suffix code remains: the day
    /// number is never followed by "st"/"nd"/"rd"/"th".
    /// </summary>
    public static string CreditExpiration(DateTimeOffset localExpiration, DateTimeOffset now)
    {
        var weekday = FormatCulture.TextInfo.ToUpper(localExpiration.ToString(Get("Card_CreditWeekdayPattern"), FormatCulture));
        var date = localExpiration.ToString(Get("Card_CreditExpirationDatePattern"), FormatCulture);
        var remainingDays = Math.Max(0, (int)Math.Ceiling((localExpiration.Date - now.ToLocalTime().Date).TotalDays));
        var inPhrase = remainingDays == 0
            ? Get("Card_CreditExpiresToday")
            : Plural("Card_CreditExpiresIn", remainingDays, 0, Count(remainingDays));
        return Format("Card_CreditExpiration", weekday, date, inPhrase);
    }

    /// <summary>
    /// The singular sentence has no "remaining" clause (there is nothing left to click through to
    /// past the one reset), so the one/other split changes sentence shape, not just the unit word;
    /// the "_Other" template still receives the unused-by-"_One" remaining count as its second argument.
    /// </summary>
    public static string CreditTooltip(int count) => count > 1
        ? Plural("Card_CreditTooltip_Click", count, 0, Count(count), Count(count - 1))
        : Plural("Card_CreditTooltip_NoClick", count, 0, Count(count));
    public static string CreditUndisclosed(int count) => Plural("Card_CreditUndisclosed", count, 0, Count(count));
    public static string ChartDay(DateTime localTime) =>
        FormatCulture.TextInfo.ToUpper(localTime.ToString(Get("Card_ChartDayPattern"), FormatCulture));
    public static string ChartEmptyState(bool unavailable) => unavailable ? CardHistoryUnavailable : CardNoSamplesYet;
    public static string ChartSampleTooltip(DateTime localTime, double remaining, TimeSpan age) =>
        Format("Card_ChartSampleTooltip", ResetMoment(localTime), Percentage(remaining), FormatAge(age));

    public static string PaceSummary(PaceResult pace, TimeSpan? duration, bool compact = false, double? remainingFraction = null)
    {
        // Each suppression reason is a complete localized sentence.
        if (pace.DeltaFraction is not { } delta || pace.TimeEquivalent is not { } equivalent)
            return Get(PaceSuppressedKey(pace.DeltaUnavailableReason));

        var under = delta > 0;
        var shortWindow = compact || (duration is { } windowDuration && windowDuration <= TimeSpan.FromHours(24));
        var paceText = pace.PacePercent is { } percent
            ? Format("Card_PacePercent", Number(percent, 0))
            : Get(PacePercentUnavailableKey(pace.PacePercentUnavailableReason));

        // The unit lives inside each full sentence (design doc §12) so a translator can reorder or
        // inflect it; the displayed, rounded value is what selects the plural category.
        // Use the rounded display value for the budget label so rounding cannot show zero time under or over budget.
        // Short windows use minutes below one hour; other windows display hours to one decimal place.
        if (shortWindow && equivalent < TimeSpan.FromHours(1))
        {
            var minutes = PaceCalculator.RoundTimeEquivalentForDisplay(equivalent.Ticks, shortWindow: true);
            if (minutes == 0) return Format("Card_OnBudget", paceText);
            return Plural(under ? "Card_UnderBudgetMinutes" : "Card_OverBudgetMinutes", minutes, 0, Number(minutes, 0), paceText);
        }
        var hours = PaceCalculator.RoundTimeEquivalentForDisplay(equivalent.Ticks, shortWindow: false);
        if (hours == 0) return Format("Card_OnBudget", paceText);
        return Plural(under ? "Card_UnderBudgetHours" : "Card_OverBudgetHours", hours, 1, Number(hours, 1), paceText);
    }

    public static string ProviderStatus(ProviderStatus? status, bool isReading) =>
        status switch
        {
            null when isReading => Get("Card_Reading"),
            null => Get("Card_Connecting"),
            Glideslope.Domain.ProviderStatus.Ready => Get("Card_ProviderReady"),
            Glideslope.Domain.ProviderStatus.MissingApplication => Get("Card_MissingApplication"),
            Glideslope.Domain.ProviderStatus.NeedsSignIn => Get("Card_NeedsSignIn"),
            Glideslope.Domain.ProviderStatus.AuthenticationExpired => Get("Card_AuthenticationExpired"),
            Glideslope.Domain.ProviderStatus.RateLimited => Get("Card_RateLimited"),
            Glideslope.Domain.ProviderStatus.Offline => Get("Card_ProviderOffline"),
            Glideslope.Domain.ProviderStatus.UnsupportedAccount => Get("Card_UnsupportedAccount"),
            Glideslope.Domain.ProviderStatus.SchemaChanged => Get("Card_SchemaChanged"),
            _ => Get("Card_ProviderUnavailable")
        };

    /// <summary>Timeout and unresponsive-provider codes shown as a no-response status.</summary>
    private static readonly HashSet<string> NoResponseCodes = new(StringComparer.Ordinal)
    {
        "antigravity_unresponsive", "antigravity_usage_timeout", "antigravity_read_timeout",
        "claude_unresponsive", "claude_cli_timeout", "claude_read_timeout", "claude_auth_status_timeout",
        "codex_app_server_timeout",
    };

    private static bool IsNoResponse(ProviderDisplayState? state) =>
        state is { IsReading: false, Status: Glideslope.Domain.ProviderStatus.Offline, SafeErrorCode: { } code } &&
        NoResponseCodes.Contains(code);

    public static string ProviderStatus(ProviderDisplayState? state)
    {
        if (IsNoResponse(state)) return Format("Card_NoResponse", ProviderName(state!.ProviderId));
        if (state?.Status == Glideslope.Domain.ProviderStatus.Ready && state.Freshness == SnapshotFreshness.Stale)
            return Get("Card_StaleTitle");
        if (state?.Status == Glideslope.Domain.ProviderStatus.Ready && state.Freshness == SnapshotFreshness.RestoredHistorical)
            return Get("Card_HistoricalTitle");
        return ProviderStatus(state?.Status, state?.IsReading == true);
    }

    public static string ProviderStatusDetails(ProviderDisplayState? state, AccountSnapshot? snapshot)
    {
        if (state is null) return Get("Card_ConnectingDetails");
        if (state.IsReading && state.Status is null) return Get("Card_ReadingDetails");
        if (IsNoResponse(state)) return Get("Card_NoResponseDetails");
        // This unsupported-command state requires restarting the provider; sign-in or retry guidance would mislead.
        if (state.SafeErrorCode == "antigravity_usage_command_unsupported") return Get("Card_QuotaGuardDetails");
        if (state.Status == Glideslope.Domain.ProviderStatus.Ready && snapshot is not null)
        {
            if (state.SafeErrorCode is "claude_history_scope_unavailable" or "codex_history_scope_unavailable")
                return Get("Card_HistoryScopeUnavailable");
            if (state.Freshness == SnapshotFreshness.Stale) return Get("Card_StaleDetails");
            if (state.Freshness == SnapshotFreshness.RestoredHistorical) return Get("Card_HistoricalDetails");
            var observed = snapshot.ObservedAtUtc.ToLocalTime();
            return Format("Card_ObservedAt", ResetMoment(observed.DateTime));
        }
        return state.Status switch
        {
            Glideslope.Domain.ProviderStatus.MissingApplication => Get("Card_MissingApplicationDetails"),
            Glideslope.Domain.ProviderStatus.NeedsSignIn => Get("Card_NeedsSignInDetails"),
            Glideslope.Domain.ProviderStatus.AuthenticationExpired => Get("Card_AuthenticationExpiredDetails"),
            Glideslope.Domain.ProviderStatus.RateLimited => state.RetryAtUtc is { } retry
                ? Format("Card_RateLimitedUntil", retry.ToLocalTime().ToString("t", FormatCulture))
                : Get("Card_RateLimitedDetails"),
            Glideslope.Domain.ProviderStatus.Offline => Get("Card_OfflineDetails"),
            Glideslope.Domain.ProviderStatus.UnsupportedAccount => Get("Card_UnsupportedAccountDetails"),
            Glideslope.Domain.ProviderStatus.SchemaChanged => Get("Card_SchemaChangedDetails"),
            _ => Get("Card_ProviderUnavailableDetails")
        };
    }

    /// <summary>One piece of the card's status line; <see cref="IsTitle"/> pieces are SemiBold.</summary>
    internal readonly record struct StatusLinePart(string Text, bool IsTitle);

    /// <summary>
    /// The localized template controls title and details order and separators; title placeholders are marked bold.
    /// </summary>
    public static IReadOnlyList<StatusLinePart> StatusLine(ProviderDisplayState? state)
    {
        var title = ProviderStatus(state);
        var details = ProviderStatusDetails(state, state?.Snapshot);
        var template = Get("Card_StatusLine");
        var parts = new List<StatusLinePart>(3);
        var index = 0;
        foreach (Match match in StatusLinePlaceholder.Matches(template))
        {
            if (match.Index > index) parts.Add(new StatusLinePart(template[index..match.Index], false));
            parts.Add(match.Groups[1].Value == "0" ? new StatusLinePart(title, true) : new StatusLinePart(details, false));
            index = match.Index + match.Length;
        }
        if (index < template.Length) parts.Add(new StatusLinePart(template[index..], false));
        return parts;
    }

    /// <summary>Returns the complete localized message for a settings-apply issue.</summary>
    public static string SettingsApplyFailed(string? issueCode) => Get(issueCode switch
    {
        "startup_registration_not_owned" => "Settings_ApplyFailedNotOwned",
        "startup_registration_stale" => "Settings_ApplyFailedStale",
        "startup_launcher_missing" => "Settings_ApplyFailedLauncherMissing",
        "settings_revision_conflict" => "Settings_ApplyFailedRevisionConflict",
        "history_unavailable" => "Settings_ApplyFailedHistoryUnavailable",
        "history_prune_failed" => "Settings_ApplyFailedHistoryPruneFailed",
        _ => "Settings_ApplyFailedUnavailable",
    });

    /// <summary>
    /// Returns a message when startup registration does not match the requested state; returns null when they match.
    /// <paramref name="state"/> reflects the actual result after reconciliation.
    /// </summary>
    public static string? StartupRegistrationIssue(StartupRegistrationState state)
    {
        if (state.IssueCode is null) return null;
        var reason = state.IssueCode switch
        {
            "startup_registration_not_owned" => "NotOwned",
            "startup_registration_stale" => "Stale",
            "startup_launcher_missing" => "LauncherMissing",
            _ => "Unavailable",
        };
        return Get((state.IsRegistered ? "Settings_StartupCouldNotDisable" : "Settings_StartupCouldNotEnable") + reason);
    }

    private static string Get(string key) => Resources.GetString(key, UiCulture)
        ?? throw new MissingManifestResourceException($"Missing neutral UI resource: {key}");

    private static string Format(string key, params object[] arguments) => string.Format(FormatCulture, Get(key), arguments);

    /// <summary>
    /// Selects the culture-specific cardinal category for the displayed value and formats that resource.
    /// <see cref="PluralRules.Select"/> for <paramref name="value"/> — the number as displayed,
    /// after rounding, not the raw unrounded value — then formats the resolved template with
    /// <paramref name="args"/>. Falls back to "_Other" when the selected category's resource is
    /// missing, since a language need not define every category for every string.
    /// <paramref name="visibleFractionDigits"/> is the displayed precision used by <see cref="PluralRules"/>.
    /// </summary>
    public static string Plural(string key, double value, int visibleFractionDigits, params object[] args)
    {
        var category = PluralRules.Select(UiCulture, value, visibleFractionDigits);
        var resolvedKey = key + "_" + category;
        var template = Resources.GetString(resolvedKey, UiCulture)
            ?? Resources.GetString(key + "_Other", UiCulture)
            ?? throw new MissingManifestResourceException($"Missing neutral UI resource: {resolvedKey}");
        return string.Format(FormatCulture, template, args);
    }

    private static string PaceSuppressedKey(PaceUnavailableReason reason) => reason switch
    {
        PaceUnavailableReason.None => "Card_PaceUnavailable",
        PaceUnavailableReason.MissingDuration => "Card_PaceSuppressedMissingDuration",
        PaceUnavailableReason.MissingReset => "Card_PaceSuppressedMissingReset",
        PaceUnavailableReason.StaleSource => "Card_PaceSuppressedStale",
        PaceUnavailableReason.AuthenticationFailed => "Card_PaceSuppressedAuthentication",
        PaceUnavailableReason.AccountChanged => "Card_PaceSuppressedAccountChanged",
        PaceUnavailableReason.HistoricalOnly => "Card_PaceSuppressedHistorical",
        PaceUnavailableReason.FutureWindowStart => "Card_PaceSuppressedWindowNotStarted",
        PaceUnavailableReason.WindowReset => "Card_PaceSuppressedWindowReset",
        PaceUnavailableReason.InsufficientElapsed => "Card_PaceSuppressedInsufficientElapsed",
        _ => "Card_PaceSuppressedUnknown"
    };

    private static string PacePercentUnavailableKey(PaceUnavailableReason reason) => reason switch
    {
        PaceUnavailableReason.MissingDuration => "Card_PacePercentUnavailableMissingDuration",
        PaceUnavailableReason.MissingReset => "Card_PacePercentUnavailableMissingReset",
        PaceUnavailableReason.StaleSource => "Card_PacePercentUnavailableStale",
        PaceUnavailableReason.AuthenticationFailed => "Card_PacePercentUnavailableAuthentication",
        PaceUnavailableReason.AccountChanged => "Card_PacePercentUnavailableAccountChanged",
        PaceUnavailableReason.HistoricalOnly => "Card_PacePercentUnavailableHistorical",
        PaceUnavailableReason.FutureWindowStart => "Card_PacePercentUnavailableWindowNotStarted",
        PaceUnavailableReason.WindowReset => "Card_PacePercentUnavailableWindowReset",
        PaceUnavailableReason.InsufficientElapsed => "Card_PacePercentUnavailableInsufficientElapsed",
        _ => "Card_PacePercentUnavailableUnknown"
    };

    /// <summary>
    /// internal rather than private so <c>LocalizationResourceProof</c> can exercise the plural
    /// table (design doc §12) directly against known ages, via the InternalsVisibleTo grant to
    /// Glideslope.Shell.Specs.
    /// </summary>
    internal static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
        {
            var days = Math.Round(age.TotalDays, MidpointRounding.AwayFromZero);
            return Plural("Card_AgeDays", days, 0, Number(days, 0));
        }
        if (age.TotalHours >= 1)
        {
            var hours = Math.Round(age.TotalHours, 1, MidpointRounding.AwayFromZero);
            return Plural("Card_AgeHours", hours, 1, Number(hours, 1));
        }
        var minutes = Math.Max(0, (int)age.TotalMinutes);
        return Plural("Card_AgeMinutes", minutes, 0, Count(minutes));
    }
}

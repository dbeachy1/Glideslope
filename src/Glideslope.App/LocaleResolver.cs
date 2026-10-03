using System.Globalization;
using Glideslope.Core;

namespace Glideslope.App;

internal sealed record ResolvedLocale(CultureInfo UiCulture, CultureInfo FormatCulture, string LanguageChoice);

/// <summary>Resolves the wording and formatting cultures once, from launch inputs and the saved preference.</summary>
internal static class LocaleResolver
{
    private static readonly HashSet<string> KnownCultureNames = CultureInfo.GetCultures(CultureTypes.AllCultures)
        .Select(culture => culture.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ResourceCultureAliases = new(StringComparer.OrdinalIgnoreCase)
        { "zh", "zh-CN", "zh-SG", "zh-Hans", "zh-TW", "zh-HK", "zh-Hant" };

    internal static IReadOnlyDictionary<string, string> DisplayNames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["auto"] = "System language", ["en-US"] = "English (United States)", ["es"] = "Español", ["fr"] = "Français",
        ["de"] = "Deutsch", ["it"] = "Italiano", ["pt-BR"] = "Português (Brasil)", ["ja"] = "日本語",
        ["zh-Hans"] = "简体中文", ["ko"] = "한국어", ["ru"] = "Русский", ["id"] = "Bahasa Indonesia",
        ["zh-Hant"] = "繁體中文",
    };

    internal static ResolvedLocale Resolve(
        string choice,
        bool isLinux,
        IReadOnlyDictionary<string, string?> environment,
        CultureInfo capturedUiCulture,
        CultureInfo capturedFormatCulture)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(capturedUiCulture);
        ArgumentNullException.ThrowIfNull(capturedFormatCulture);
        if (!AppSettings.SupportedLanguageChoices.Contains(choice, StringComparer.Ordinal)) choice = "auto";

        if (choice != "auto")
        {
            var manual = CultureInfo.GetCultureInfo(choice);
            return new ResolvedLocale(manual, manual, choice);
        }

        var format = isLinux
            ? ResolveFormatCulture(environment, capturedFormatCulture)
            : capturedFormatCulture;
        var displayCulture = ResolveDisplayCulture(isLinux, environment, capturedUiCulture);
        var id = MatchSupportedLanguage(displayCulture);
        return new ResolvedLocale(CultureForLanguage(id), format, "auto");
    }

    internal static string? MatchSupportedLanguage(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var name = culture.Name;
        if (name.Length == 0) return "en-US";
        var normalized = name.Replace('_', '-');
        if (normalized.Equals("zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
        if (normalized.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
        {
            var subtags = normalized.Split('-');
            if (subtags.Any(subtag => subtag.Equals("Hant", StringComparison.OrdinalIgnoreCase))) return "zh-Hant";
            if (subtags.Any(subtag => subtag.Equals("Hans", StringComparison.OrdinalIgnoreCase))) return "zh-Hans";
            var region = subtags.LastOrDefault();
            return region?.ToUpperInvariant() is "TW" or "HK" or "MO" ? "zh-Hant" : "zh-Hans";
        }
        if (normalized.Equals("pt-BR", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        if (normalized.StartsWith("pt-", StringComparison.OrdinalIgnoreCase)) return null;
        var language = normalized.Split('-')[0].ToLowerInvariant();
        return language is "en" or "es" or "fr" or "de" or "it" or "ja" or "ko" or "ru" or "id"
            ? language == "en" ? "en-US" : language
            : null;
    }

    private static CultureInfo ResolveDisplayCulture(bool isLinux, IReadOnlyDictionary<string, string?> env, CultureInfo captured)
    {
        if (!isLinux) return captured;
        var all = First(env, "LC_ALL");
        if (IsCLocale(all)) return CultureInfo.GetCultureInfo("en-US");
        if (TryFirstSupportedLanguageList(First(env, "LANGUAGE"), out var fromLanguage)) return fromLanguage;
        foreach (var value in new[] { all, First(env, "LC_MESSAGES"), First(env, "LANG"), captured.Name })
            if (TryParseCulture(value, out var culture) && MatchSupportedLanguage(culture) is not null) return culture;
        return CultureInfo.GetCultureInfo("en-US");
    }

    private static CultureInfo ResolveFormatCulture(IReadOnlyDictionary<string, string?> environment, CultureInfo captured)
    {
        foreach (var key in new[] { "LC_ALL", "LANG" })
        {
            var value = First(environment, key);
            if (IsCLocale(value)) return CultureInfo.InvariantCulture;
            if (TryParseCulture(value, out var parsed)) return parsed;
        }
        return captured;
    }

    private static bool TryFirstSupportedLanguageList(string? value, out CultureInfo culture)
    {
        if (value is not null)
        {
            foreach (var item in value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (TryParseCulture(item, out culture) && MatchSupportedLanguage(culture) is not null) return true;
        }
        culture = CultureInfo.InvariantCulture;
        return false;
    }

    private static bool TryParseCulture(string? value, out CultureInfo culture)
    {
        culture = CultureInfo.InvariantCulture;
        if (string.IsNullOrWhiteSpace(value) || IsCLocale(value)) return false;
        var clean = value.Split('.', 2)[0].Split('@', 2)[0].Replace('_', '-');
        if (clean.Length == 0 || (!KnownCultureNames.Contains(clean) && !ResourceCultureAliases.Contains(clean))) return false;
        try { culture = CultureInfo.GetCultureInfo(clean); return culture.Name.Length > 0; }
        catch (CultureNotFoundException) { return false; }
    }

    private static bool IsCLocale(string? value) =>
        value is not null && (value.Equals("C", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("POSIX", StringComparison.OrdinalIgnoreCase) ||
                              value.StartsWith("C.", StringComparison.OrdinalIgnoreCase) ||
                              value.StartsWith("POSIX.", StringComparison.OrdinalIgnoreCase));

    private static string? First(IReadOnlyDictionary<string, string?> env, params string[] keys)
    {
        foreach (var key in keys)
            if (env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    private static CultureInfo CultureForLanguage(string? id) => CultureInfo.GetCultureInfo(id ?? "en-US");
}

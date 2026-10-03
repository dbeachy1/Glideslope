using System.Globalization;

namespace Glideslope.App;

/// <summary>
/// CLDR-style plural categories for count-plus-unit resource strings (design doc §12).
/// Russian uses all four cardinal categories; the other shipped languages use One/Other or Other.
/// </summary>
internal enum PluralCategory
{
    One,
    Few,
    Many,
    Other,
}

/// <summary>
/// Selects which plural category a displayed number falls into, per culture, so
/// <c>LocalizedText.Plural</c> can select the culture's resource category.
/// </summary>
internal static class PluralRules
{
    /// <summary>
    /// Selects the plural category for <paramref name="value"/> in <paramref name="culture"/>.
    /// <paramref name="value"/> must be the number as displayed, after rounding — for example
    /// 0.96 hours displays as "1.0" and selects <see cref="PluralCategory.One"/>, not the raw 0.96.
    /// <paramref name="visibleFractionDigits"/> preserves the displayed precision required by some plural rules.
    /// <paramref name="culture"/> is the wording culture; Portuguese regional variants use their respective rules.
    /// </summary>
    internal static PluralCategory Select(CultureInfo culture, double value, int visibleFractionDigits)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var n = Math.Abs(value);
        var i = Math.Floor(n);
        var v = Math.Max(0, visibleFractionDigits);
        switch (culture.TwoLetterISOLanguageName)
        {
            case "en":
                // The UI uses singular for values greater than zero and at most one.
                return n > 0 && n <= 1 ? PluralCategory.One : PluralCategory.Other;
            case "es":
                // CLDR es: one when n = 1 ("1 día", "1,0 hora", "2 días").
                return n == 1 ? PluralCategory.One : PluralCategory.Other;
            case "fr":
                // CLDR fr: one when i = 0 or 1 ("0 jour", "1,5 heure", "2 jours").
                return i is 0d or 1d ? PluralCategory.One : PluralCategory.Other;
            case "pt":
                // CLDR pt (Brazilian): one when i = 0 or 1, like fr. CLDR pt-PT, and the other pt regions that
                // inherit from it: one only when i = 1 and v = 0, like de.
                return IsBrazilianPortuguese(culture)
                    ? (i is 0d or 1d ? PluralCategory.One : PluralCategory.Other)
                    : (i == 1 && v == 0 ? PluralCategory.One : PluralCategory.Other);
            case "de":
            case "it":
                // CLDR de, it: one when i = 1 and v = 0 ("1 Tag", but "1,0 Stunden").
                return i == 1 && v == 0 ? PluralCategory.One : PluralCategory.Other;
            case "ru":
                // CLDR ru cardinal: fractions are Other; whole values use the final one/two digits.
                if (v != 0) return PluralCategory.Other;
                var mod10 = i % 10;
                var mod100 = i % 100;
                if (mod10 == 1 && mod100 != 11) return PluralCategory.One;
                if (mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14)) return PluralCategory.Few;
                if (mod10 == 0 || mod10 is >= 5 and <= 9 || mod100 is >= 11 and <= 14) return PluralCategory.Many;
                return PluralCategory.Other;
            default:
                // No rule defined for this language; Other is always the safe fallback.
                return PluralCategory.Other;
        }
    }

    private static bool IsBrazilianPortuguese(CultureInfo culture) =>
        culture.Name.Equals("pt", StringComparison.OrdinalIgnoreCase) ||
        culture.Name.Equals("pt-BR", StringComparison.OrdinalIgnoreCase);
}

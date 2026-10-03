using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Computes per-mode card scale values and corresponding minimum and default window sizes. Integer percentage
/// arithmetic keeps whole-pixel dimensions exact.
/// </summary>
internal static class CardScale
{
    public const int MinimumPercent = AppSettings.MinimumCardScalePercent;
    public const int MaximumPercent = AppSettings.MaximumCardScalePercent;
    public const int StepPercent = AppSettings.CardScaleStepPercent;
    /// <summary>The default, and the size the 16 px text floor is defined at.</summary>
    public const int DefaultPercent = 100;

    /// <summary>The nearest step inside the range for a slider value (any double); a non-finite value reads as
    /// the default. So an off-step value (a hand-edited settings file, a keyboard step, a value set in code)
    /// always lands on a 10 % step inside 70–150 %.</summary>
    public static int Snap(double percent)
    {
        if (!double.IsFinite(percent)) return DefaultPercent;
        var clamped = Math.Clamp(percent, MinimumPercent, MaximumPercent);
        // Same rule as AppSettings.NormalizeCardScalePercent, on the unrounded value (rounding to a whole
        // percent first would turn 74.6 into 75 and then 80).
        return (int)(Math.Round(clamped / StepPercent, MidpointRounding.AwayFromZero) * StepPercent);
    }

    // Both modes use the same scale range and normalization rule.
    public static int PercentFor(AppSettings settings, CardMode mode)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return mode switch
        {
            CardMode.Full => settings.FullCardScalePercent,
            CardMode.Mini => settings.MiniCardScalePercent,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    public static void SetPercent(AppSettings settings, CardMode mode, int percent)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (mode)
        {
            case CardMode.Full:
                settings.FullCardScalePercent = Normalize(percent);
                break;
            case CardMode.Mini:
                settings.MiniCardScalePercent = Normalize(percent);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>Clamped to 70–150 and rounded to the slider's 10% steps (a 5 rounds up).</summary>
    public static int Normalize(int percent) => AppSettings.NormalizeCardScalePercent(percent);

    public static LogicalSize MinimumSize(CardMode mode, int percent) => mode switch
    {
        CardMode.Full => new LogicalSize(CardLayoutTiers.MinWidth * percent / 100d, LocalizedText.CardMinimumHeight * percent / 100d),
        CardMode.Mini => new LogicalSize(CardLayoutTiers.MiniMinWidth * percent / 100d, CardLayoutTiers.MiniMinHeight * percent / 100d),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static LogicalSize DefaultSize(CardMode mode, int percent) => mode switch
    {
        CardMode.Full => new LogicalSize(ProviderUsageCardWindow.DefaultCardWidth * percent / 100d,
            Math.Max(ProviderUsageCardWindow.DefaultCardHeight, LocalizedText.CardMinimumHeight) * percent / 100d),
        CardMode.Mini => new LogicalSize(CardLayoutTiers.MiniDefaultWidth * percent / 100d,
            CardLayoutTiers.MiniDefaultHeight * percent / 100d),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>The minimum the layout policies are given: window design Appendix H.3's one logical pixel of slack
    /// below the window minimum, because at 125% or 175% a window at its minimum can read back a fraction under it.</summary>
    public static LogicalSize UsableMinimum(CardMode mode, int percent)
    {
        var minimum = MinimumSize(mode, percent);
        return new LogicalSize(minimum.Width - 1, minimum.Height - 1);
    }
}

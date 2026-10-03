using Avalonia.Media;

namespace Glideslope.Mockup;

internal enum PaceBand
{
    StrongCushion,
    OnPace,
    SlightlyOver,
    Over,
    SeverelyOver,
    CriticalOver
}

// Weekly hour thresholds scale proportionally for the five-hour window.
internal static class PacePreviewPolicy
{
    private const double WeeklyHours = 168;

    internal static PaceBand BandFor(double deltaFraction)
    {
        var cushionHours = deltaFraction * WeeklyHours;
        if (cushionHours >= 12) return PaceBand.StrongCushion;
        if (cushionHours >= 0) return PaceBand.OnPace;
        var overHours = -cushionHours;
        if (overHours <= 6) return PaceBand.SlightlyOver;
        if (overHours <= 12) return PaceBand.Over;
        if (overHours < 24) return PaceBand.SeverelyOver;
        return PaceBand.CriticalOver;
    }

    internal static string Hex(bool dark, PaceBand band) => (dark, band) switch
    {
        (true, PaceBand.StrongCushion) => "#39FF88",
        (true, PaceBand.OnPace) => "#8FE36B",
        (true, PaceBand.SlightlyOver) => "#E9D66B",
        (true, PaceBand.Over) => "#FFC928",
        (true, PaceBand.SeverelyOver) => "#FF8A65",
        (true, PaceBand.CriticalOver) => "#FF3B30",
        (false, PaceBand.StrongCushion) => "#008F46",
        (false, PaceBand.OnPace) => "#4E833C",
        (false, PaceBand.SlightlyOver) => "#806B00",
        (false, PaceBand.Over) => "#A85A00",
        (false, PaceBand.SeverelyOver) => "#BB4932",
        (false, PaceBand.CriticalOver) => "#C62828",
        _ => throw new ArgumentOutOfRangeException(nameof(band))
    };

    internal static SolidColorBrush Brush(bool dark, PaceBand band) => new(Color.Parse(Hex(dark, band)));
}

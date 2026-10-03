using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// The boundary a card window must offer so its mode, content scale, and
/// its window minimum can be applied without the coordinator depending on the concrete Avalonia window type.
/// <see cref="ProviderUsageCardWindow"/> implements this (its <c>ApplyMode</c> and <c>ApplyCardScale</c> are
/// public so the implementation is implicit; <c>MinWidth</c>/<c>MinHeight</c> are Avalonia's own public
/// Layoutable properties). <see cref="CardModeApplierSpecs"/> drives <see cref="Apply"/> against a fake that
/// behaves like a real window (it grows when its minimum rises above its current size, and clamps a size set
/// below its minimum) without needing Avalonia at all.
/// </summary>
internal interface ICardModeWindow
{
    string ProviderId { get; }
    void ApplyMode(CardMode mode);
    void ApplyCardScale(int percent);
    double MinWidth { get; set; }
    double MinHeight { get; set; }
}

/// <summary>
/// The one place a card's mode, content scale, and window minimum are
/// applied, in the one order that is safe. <b>Why the order matters:</b> a window refuses a size below its
/// minimum, and it grows at once the moment its minimum is set above its current size. A full card going mini
/// must have the small mini minimum in place *before* it is asked for the small mini size, or it would be
/// clamped back up to the (still full) minimum and never reach the mini size. A mini card going full grows to
/// the full minimum the instant the minimum is raised here; that is harmless only because the caller always
/// applies the coordinator's own geometry (a real window size, at or above every minimum this method sets)
/// immediately afterward, while <c>_applyingLayout</c> is set. So the order is always: apply the mode, then the
/// scale for that mode, then the minimum for that mode and scale - never the minimum first. The window's actual
/// size is the caller's job, applied after every target here has been updated.
/// </summary>
internal static class CardModeApplier
{
    public static void Apply(IEnumerable<(ICardModeWindow Window, CardMode Mode, int Percent)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var (window, mode, percent) in targets)
        {
            window.ApplyMode(mode);
            window.ApplyCardScale(percent);
            var minimum = CardScale.MinimumSize(mode, percent);
            window.MinWidth = minimum.Width;
            window.MinHeight = minimum.Height;
        }
    }

    /// <summary>Applies mode and scale before geometry, lowering minimums when needed. Delaying any increase until
    /// after geometry avoids an intermediate resize; the caller then applies the final minimum.</summary>
    public static void ApplyBeforeGeometry(IEnumerable<(ICardModeWindow Window, CardMode Mode, int Percent)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var (window, mode, percent) in targets)
        {
            window.ApplyMode(mode);
            window.ApplyCardScale(percent);
            var minimum = CardScale.MinimumSize(mode, percent);
            if (minimum.Width < window.MinWidth) window.MinWidth = minimum.Width;
            if (minimum.Height < window.MinHeight) window.MinHeight = minimum.Height;
        }
    }

    /// <summary>Applies each window's final mode-and-scale minimum after its geometry is set.</summary>
    public static void ApplyMinimum(IEnumerable<(ICardModeWindow Window, CardMode Mode, int Percent)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var (window, mode, percent) in targets)
        {
            var minimum = CardScale.MinimumSize(mode, percent);
            window.MinWidth = minimum.Width;
            window.MinHeight = minimum.Height;
        }
    }
}

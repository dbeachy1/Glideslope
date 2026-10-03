namespace Glideslope.Core;

/// <summary>
/// 2.0 mini mode (design doc §2.2): whether a card shows today's full content or the compact mini body. A
/// mode belongs to a card group: a docked group is always all <see cref="Full"/> or all <see cref="Mini"/>,
/// and a detached card is its own group. This enum lives in Core (not the App project) because
/// <see cref="LayoutPolicy"/> needs it for <see cref="LayoutPolicy.SwitchGroupMode"/> and
/// <see cref="LayoutPolicy.PrepareForMode"/>.
/// </summary>
public enum CardMode
{
    Full,
    Mini,
}

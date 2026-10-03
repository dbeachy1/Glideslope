using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Decides whether a mini card should appear full while Ctrl is held. CardPeekController is the only component
/// that applies these decisions.
/// </summary>
internal enum PeekActionKind
{
    None,
    Start,
    End,
}

/// <summary>
/// Design doc §3.1's <c>PeekAction = None | Start(card) | End(reason)</c>, as a record struct: exactly one of
/// <see cref="Card"/> (set only for <see cref="PeekActionKind.Start"/>) or <see cref="Reason"/> (set only for
/// <see cref="PeekActionKind.End"/>) is non-null, matched by <see cref="Kind"/>.
/// </summary>
internal readonly record struct PeekAction(PeekActionKind Kind, string? Card = null, string? Reason = null)
{
    public static readonly PeekAction None = new(PeekActionKind.None);
    public static PeekAction Start(string card) => new(PeekActionKind.Start, Card: card);
    public static PeekAction End(string reason) => new(PeekActionKind.End, Reason: reason);
}

/// <summary>
/// Snapshot of the state used to decide whether a peek starts or ends. Mode and layout-busy state are read fresh
/// for each decision. After most end reasons, <see cref="Armed"/> stays false until Ctrl is released; a start also
/// requires <see cref="HoldElapsed"/> to indicate continuous Ctrl input during the current hover.
/// </summary>
internal readonly record struct PeekInputs(
    bool SettingOn,
    string? HoveredCard,
    CardMode? HoveredCardMode,
    bool CtrlHeld,
    bool AnyButtonHeld,
    bool LayoutBusy,
    string? PeekingCard,
    bool Armed,
    bool HoldElapsed,
    // Without an input probe, a Ctrl release outside the card can go unseen, so pointer exit ends the peek.
    bool EndWhenPointerLeaves = false);

/// <summary>Design doc §3.1: the one place that decides whether a peek starts or ends. Every input change (a
/// hover, a Ctrl or button change, a probe tick, or the coordinator pushing LayoutBusy) runs this and the
/// caller acts on the result; nothing else starts or ends a peek (design doc §5.1).</summary>
internal static class CardPeekDecision
{
    public static PeekAction Decide(PeekInputs inputs)
    {
        if (inputs.PeekingCard is { } peeking)
        {
            // Design doc §3.1: the first reason that applies, in this order.
            if (!inputs.SettingOn) return PeekAction.End("setting_off");
            if (inputs.AnyButtonHeld) return PeekAction.End("button_down");
            if (!inputs.CtrlHeld) return PeekAction.End("ctrl_released");
            // Keep a probed peek active when the pointer leaves; resizing can itself generate exit/enter events.
            // Without a probe, pointer exit is needed because Ctrl release may be missed (see EndWhenPointerLeaves).
            if (inputs.EndWhenPointerLeaves && inputs.HoveredCard != peeking) return PeekAction.End("pointer_left_no_probe");
            if (inputs.LayoutBusy) return PeekAction.End("layout_busy");
            return PeekAction.None;
        }

        // Design doc §10.2: Armed and HoldElapsed gate the start exactly like the other conditions - none of
        // them is a priority order (unlike the End reasons above), every one must hold at once.
        if (inputs.SettingOn && inputs.HoveredCard is { } hovered && inputs.HoveredCardMode == CardMode.Mini &&
            inputs.CtrlHeld && !inputs.AnyButtonHeld && !inputs.LayoutBusy && inputs.Armed && inputs.HoldElapsed)
            return PeekAction.Start(hovered);

        return PeekAction.None;
    }
}

/// <summary>Places a peeking card at its mini card's top-left corner, clamping it to the work area when needed.</summary>
internal static class CardPeekPlacement
{
    public static LogicalRect Compute(LogicalRect miniRect, LogicalSize fullSize, LogicalRect workArea)
    {
        var desiredX = miniRect.X;
        var desiredY = miniRect.Y;
        // Match LayoutPolicy.FitGroupToWorkArea: move the card as little as possible, then pin it to the work area.
        var x = ClampOrPinToStart(desiredX, workArea.X, workArea.Right - fullSize.Width);
        var y = ClampOrPinToStart(desiredY, workArea.Y, workArea.Bottom - fullSize.Height);
        return new LogicalRect(x, y, fullSize.Width, fullSize.Height);
    }

    /// <summary>True when <see cref="Compute"/> had to move the rect away from the mini card's exact top-left
    /// corner to fit (or pin) it inside the work area; the coordinator logs this (card_peek_started, shifted=).</summary>
    public static bool WouldShift(LogicalRect miniRect, LogicalSize fullSize, LogicalRect workArea)
    {
        var placed = Compute(miniRect, fullSize, workArea);
        return placed.X != miniRect.X || placed.Y != miniRect.Y;
    }

    private static double ClampOrPinToStart(double value, double low, double high) =>
        high < low ? low : Math.Clamp(value, low, high);
}

/// <summary>Design doc §3.3: the size a peek shows the card at - its remembered full size, or the full
/// default when it has none, raised to the full usable minimum.</summary>
internal static class CardPeekSize
{
    /// <summary><paramref name="savedFullWidth"/>/<paramref name="savedFullHeight"/> are a mini card's layout
    /// entry FullWidth/FullHeight (2.0 design §3.6/§3.7); both zero means the card has no remembered full size
    /// yet, so the full default at <paramref name="fullScalePercent"/> is used instead.</summary>
    public static LogicalSize Compute(double savedFullWidth, double savedFullHeight, int fullScalePercent)
    {
        var size = savedFullWidth > 0 && savedFullHeight > 0
            ? new LogicalSize(savedFullWidth, savedFullHeight)
            : CardScale.DefaultSize(CardMode.Full, fullScalePercent);
        var minimum = CardScale.UsableMinimum(CardMode.Full, fullScalePercent);
        return new LogicalSize(Math.Max(size.Width, minimum.Width), Math.Max(size.Height, minimum.Height));
    }
}

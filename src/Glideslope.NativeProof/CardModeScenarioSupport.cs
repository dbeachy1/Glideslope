using Glideslope.Core;

namespace Glideslope.NativeProof;

/// <summary>Shared helpers for native card-mode scenarios: drive a dock gesture and read committed
/// window geometry or settings after the coordinator reports that the layout has been saved.</summary>
internal static class CardModeScenarioSupport
{
    /// <summary>Drags <paramref name="hwnd"/> so it ends within LayoutPolicy.DefaultInterCardGap of
    /// <paramref name="anchor"/>'s right edge, top-aligned to it, then waits for
    /// layout_gesture_committed for <paramref name="providerId"/>. Mirrors WorkflowScenario's own dock
    /// step exactly (same envelope, same target math); returns the committed log line, or null on
    /// timeout (the caller reports ProofLog.TailSince as evidence, the same convention every scenario
    /// uses).</summary>
    public static LogLine? DockToRightOf(nint hwnd, string providerId, NativeMethods.RECT anchor,
        ScenarioContext context, string label)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var start)) return null;
        var width = start.Right - start.Left;
        var height = start.Bottom - start.Top;
        var gap = (int)LayoutPolicy.DefaultInterCardGap;
        var target = new NativeMethods.RECT
        {
            Left = anchor.Right + gap,
            Top = anchor.Top,
            Right = anchor.Right + gap + width,
            Bottom = anchor.Top + height,
        };

        var since = context.Log.CurrentLength();
        GestureDriver.Drive(hwnd, GestureDriver.Interpolate(start, target, stepCount: 6), context.Diagnostics, label);
        return context.Log.WaitForLine(since,
            line => line.Code == "layout_gesture_committed" && line.ProviderId == providerId);
    }

    public static (int Width, int Height) Size(NativeMethods.RECT rect) => (rect.Right - rect.Left, rect.Bottom - rect.Top);

    /// <summary>Checks each seeded card's actual window size once. Session startup waits for layout
    /// restoration before scenarios run, so a mismatch indicates that the seeded layout was not applied.</summary>
    public static bool WaitForSeededLayout(ScenarioContext context, List<string> evidence)
    {
        var ready = true;
        foreach (var card in context.SeededLayout.Cards)
        {
            if (!context.CardWindows.TryGetValue(card.ProviderId, out var hwnd)) return false;
            NativeMethods.GetWindowRect(hwnd, out var rect);
            if (Size(rect) != (card.Width, card.Height))
            {
                evidence.Add($"seeded_layout_not_restored {card.ProviderId}_size={Size(rect)} expected=({card.Width}, {card.Height})");
                ready = false;
            }
        }
        return ready;
    }

    /// <summary>Waits once for the layout_saved line at or after <paramref name="sinceOffset"/>. The line is
    /// emitted after the settings save completes and group geometry has been applied. Then reads
    /// <paramref name="hwnd"/>'s real window rect exactly once. Returns the
    /// rect read either way (layout_saved not being seen at all is itself a hang-watchdog failure the log
    /// tail already reports); the caller compares the rect to what it expects and reports the rect as
    /// evidence regardless of the outcome.</summary>
    public static NativeMethods.RECT RectAfterLayoutSaved(ScenarioContext context, long sinceOffset, nint hwnd)
    {
        context.Log.WaitForLine(sinceOffset, line => line.Code == "layout_saved");
        NativeMethods.GetWindowRect(hwnd, out var rect);
        return rect;
    }

    /// <summary>Waits for the coordinator's layout_saved line, which follows the durable settings write,
    /// then reads settings.json once. Earlier gesture and mode events precede the asynchronous save and
    /// cannot establish that the file contains the committed layout. Returns null if the event or read fails.</summary>
    public static AppSettings? WaitForSavedSettings(ScenarioContext context, long sinceOffset, Action<string> log)
    {
        var saved = context.Log.WaitForLine(sinceOffset, line => line.Code == "layout_saved");
        return saved is null ? null : ProofSettingsReader.TryRead(context.SettingsFile, log);
    }
}

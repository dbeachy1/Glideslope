using Glideslope.App;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Level 1 spec for design doc §7 / §13 / Appendix C.8: WindowCoordinator.RaiseOrder decides the order
/// in which the coordinator raises the visible cards when one of them is activated. The other visible
/// cards come least recently activated first (cards never activated come first of all), and the activated
/// card comes last, exactly once, so it ends up on top. Plain strings stand in for the card windows;
/// RaiseOrder compares by reference, so each name below is a single shared string instance.
/// </summary>
internal static class BringToFrontOrderSpecs
{
    public static void Run()
    {
        const string codex = "codex";
        const string claude = "claude";
        const string gemini = "gemini";

        // All three were activated before: Gemini most recently, then Claude, then Codex. Activating
        // Claude raises Codex (least recent), then Gemini, then Claude on top.
        AssertOrder(claude, [codex, claude, gemini], [claude, gemini, codex], [codex, gemini, claude],
            "others come least recently activated first, the activated card last");

        // Codex was never activated: it comes before every card that was.
        AssertOrder(gemini, [codex, claude, gemini], [gemini, claude], [codex, claude, gemini],
            "a card never activated is raised first of all");

        // Nothing was ever recorded: the activated card is still last and appears exactly once.
        AssertOrder(codex, [claude, codex, gemini], [], null,
            "with no activation history the activated card is still last");

        // The activated card is not in the visible list it was handed: it is still added, once, at the end.
        AssertOrder(gemini, [codex, claude], [claude, codex], [codex, claude, gemini],
            "the activated card is added last even when the visible list omits it");

        // The activated card listed twice in the visible cards still appears exactly once.
        AssertOrder(codex, [codex, claude, codex], [codex, claude], [claude, codex],
            "the activated card appears exactly once even if the visible list repeats it");

        // Ignore activation echoes only when the raise actually activates the window. Windows and X11 raises
        // do not activate; the fallback raiser does.
        Assert(WindowCoordinator.GroupRaiseEchoWindow(raiserActivates: false) == TimeSpan.Zero,
            "with a raise that does not activate (Windows, X11) no activation is ignored after a group raise");
        Assert(WindowCoordinator.GroupRaiseEchoWindow(raiserActivates: true) == TimeSpan.FromMilliseconds(250),
            "with the activating fallback the raise's own activation echoes are ignored for 250 ms");
    }

    private static void AssertOrder(string activated, string[] visible, string[] mostRecentFirst, string[]? expected, string message)
    {
        var actual = WindowCoordinator.RaiseOrder(activated, visible, mostRecentFirst);
        var described = $"(activated {activated}, visible [{string.Join(", ", visible)}], most recent first [{string.Join(", ", mostRecentFirst)}] -> [{string.Join(", ", actual)}])";
        Assert(actual.Count > 0 && ReferenceEquals(actual[^1], activated), $"{message}: the activated card is last {described}");
        Assert(actual.Count(card => ReferenceEquals(card, activated)) == 1, $"{message}: the activated card appears exactly once {described}");
        Assert(actual.Count == visible.Count(card => !ReferenceEquals(card, activated)) + 1,
            $"{message}: every other visible card is raised once {described}");
        if (expected is not null)
            Assert(actual.SequenceEqual(expected), $"{message}: expected [{string.Join(", ", expected)}] {described}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }
}

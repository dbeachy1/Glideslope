using Avalonia;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>On X11, a requested move is the card's
/// position until the window manager answers, and the answer is classified as an echo or an adjustment.</summary>
internal static class PendingPlacementsSpecs
{
    public static void Run()
    {
        var clock = new LayoutGestureTrackerSpecs.ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var pending = new PendingPlacements(clock);
        Assert(!pending.TryGet(ProviderCatalog.Codex, out _), "nothing is pending before a request");
        Assert(pending.Confirm(ProviderCatalog.Codex, new PixelPoint(5, 5), out _) == PlacementConfirmation.None,
            "a report with no request pending is unrelated to us (a user or window-manager move)");

        pending.Record(ProviderCatalog.Codex, new PixelPoint(2089, 232));
        Assert(pending.TryGet(ProviderCatalog.Codex, out var requested) && requested == new PixelPoint(2089, 232),
            "the requested position is reported until the window manager answers");
        Assert(!pending.TryGet(ProviderCatalog.Claude, out _), "a request belongs to its own card only");

        Assert(pending.Confirm(ProviderCatalog.Codex, new PixelPoint(2089, 232), out _) == PlacementConfirmation.Echo,
            "an answer at the requested position is the echo");
        Assert(!pending.TryGet(ProviderCatalog.Codex, out _), "the request is forgotten once answered");

        // GNOME keeps app-placed windows inside the work area: asked for 2089 on a 3000-wide screen, it lands at 2060.
        pending.Record(ProviderCatalog.Gemini, new PixelPoint(2089, 232));
        Assert(pending.Confirm(ProviderCatalog.Gemini, new PixelPoint(2060, 232), out var asked) == PlacementConfirmation.Adjusted &&
               asked == new PixelPoint(2089, 232),
            "an answer elsewhere is the window manager's adjustment, and reports what was asked");
        Assert(pending.Confirm(ProviderCatalog.Gemini, new PixelPoint(2100, 232), out _) == PlacementConfirmation.None,
            "only the first report answers a request; a later move is the user's again");

        pending.Record(ProviderCatalog.Claude, new PixelPoint(10, 10));
        pending.Record(ProviderCatalog.Claude, new PixelPoint(20, 10));
        Assert(pending.Confirm(ProviderCatalog.Claude, new PixelPoint(20, 10), out _) == PlacementConfirmation.Echo,
            "a newer request replaces an unanswered older one");

        // A window manager may not report a requested move, so expire unanswered requests after their lifetime.
        pending.Record(ProviderCatalog.Codex, new PixelPoint(3100, 232));
        clock.Advance(PendingPlacements.Lifetime - TimeSpan.FromTicks(1));
        Assert(pending.TryGet(ProviderCatalog.Codex, out _), "a request stands until its lifetime ends");
        clock.Advance(TimeSpan.FromTicks(1));
        Assert(!pending.TryGet(ProviderCatalog.Codex, out _), "an unanswered request expires, and capture reads the native position again");
        Assert(pending.Confirm(ProviderCatalog.Codex, new PixelPoint(2060, 232), out var reportedBack) == PlacementConfirmation.None &&
               reportedBack == new PixelPoint(2060, 232),
            "a report after expiry is the user's move again, not the window manager's answer");
        pending.Record(ProviderCatalog.Gemini, new PixelPoint(3100, 232));
        clock.Advance(PendingPlacements.Lifetime);
        Assert(pending.Confirm(ProviderCatalog.Gemini, new PixelPoint(2060, 232), out _) == PlacementConfirmation.None,
            "an expired request is not answered by a late report either");

        // Size requests may arrive in separate width and height changes; intermediate values must remain part of
        // the pending request rather than being treated as a user resize.
        Assert(pending.ConfirmSize(ProviderCatalog.Claude, new LogicalSize(883, 639)) == PlacementConfirmation.None,
            "a size change with no size request standing is the user's");
        pending.RecordSize(ProviderCatalog.Claude, new LogicalSize(530, 270));
        Assert(pending.ConfirmSize(ProviderCatalog.Claude, new LogicalSize(530, 680)) == PlacementConfirmation.Adjusted,
            "the width arriving first with the old height answers the request");
        Assert(pending.ConfirmSize(ProviderCatalog.Claude, new LogicalSize(530.4, 269.7)) == PlacementConfirmation.Echo,
            "the height completing it (within DPI rounding) is the echo");
        Assert(pending.ConfirmSize(ProviderCatalog.Claude, new LogicalSize(600, 300)) == PlacementConfirmation.None,
            "after the echo a size change is the user's again");
        pending.RecordSize(ProviderCatalog.Codex, new LogicalSize(530, 270));
        clock.Advance(PendingPlacements.Lifetime);
        Assert(pending.ConfirmSize(ProviderCatalog.Codex, new LogicalSize(883, 639)) == PlacementConfirmation.None,
            "an unanswered size request expires like a position request");

        var windows = new AvaloniaCardWindowGeometryBackend(asynchronousPlacement: false);
        Assert(!windows.WindowManagerConfinesPlacedWindows,
            "where moves take effect at once (Windows) nothing is confined and nothing is pending");
        Assert(new AvaloniaCardWindowGeometryBackend(asynchronousPlacement: true).WindowManagerConfinesPlacedWindows,
            "where a move is a request (Linux), the window manager is taken to confine app-placed windows");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Pending placement spec failed: {message}.");
    }
}

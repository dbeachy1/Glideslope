using System.Collections.Immutable;
using Glideslope.App;
using Glideslope.Domain;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Tests the coordinator's week-browsing arithmetic from design doc §15.3: which windows can be browsed,
/// where a step request heads, how the offset is clamped once the count is known, and which stored window
/// an offset names. This covers indexing and clamping independently of window presentation.
/// </summary>
internal static class HistoryBrowseSpecs
{
    public static void Run()
    {
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var week = TimeSpan.FromDays(7);
        UsageWindowIdentity Window(int index) =>
            new("scope-browse", ProviderIds.Claude, "claude.weekly", start + week * index, start + week * (index + 1), "weekly");
        var oldest = Window(0);
        var middle = Window(1);
        var current = Window(2);

        // BrowsableWindows: the current window is appended only when the store has not recorded it yet,
        // so offset 0 always names the current week.
        var stored = ImmutableArray.Create(oldest, middle);
        var appended = WindowCoordinator.BrowsableWindows(stored, current);
        Assert(appended.Length == 3 && appended[2] == current, "a current window not yet stored is appended last");
        var kept = WindowCoordinator.BrowsableWindows(ImmutableArray.Create(oldest, middle, current), current);
        Assert(kept.Length == 3 && kept[2] == current, "a stored current window is not appended twice");
        var only = WindowCoordinator.BrowsableWindows(ImmutableArray<UsageWindowIdentity>.Empty, current);
        Assert(only.Length == 1 && only[0] == current, "with nothing stored the current window is the only one");
        // A pending current window still occupies offset 0 as an empty live slot; stored windows begin at 1.
        var noCurrent = WindowCoordinator.BrowsableWindows(stored, null);
        Assert(noCurrent.Length == 3 && WindowCoordinator.BrowsableWindowAt(noCurrent, 0) is null &&
               WindowCoordinator.BrowsableWindowAt(noCurrent, 1) == middle &&
               WindowCoordinator.BrowsableWindowAt(noCurrent, 2) == oldest,
            "with no current identity offset 0 is empty and stored weeks begin at offset 1");
        var pendingOnly = WindowCoordinator.BrowsableWindows(ImmutableArray<UsageWindowIdentity>.Empty, null);
        Assert(pendingOnly.Length == 1 && WindowCoordinator.BrowsableWindowAt(pendingOnly, 0) is null,
            "with no current identity and no stored weeks the live slot remains browseable and empty");

        // BrowsableWindowAt: offset 0 is the current (last) window, each older week one step toward the front.
        Assert(WindowCoordinator.BrowsableWindowAt(appended, 0) == current, "offset 0 is the current week");
        Assert(WindowCoordinator.BrowsableWindowAt(appended, 1) == middle, "offset 1 is the week before");
        Assert(WindowCoordinator.BrowsableWindowAt(appended, 2) == oldest, "offset count − 1 is the oldest stored week");

        // NextPendingHistoryOffset: ‹ is +1, › is −1, Now is 0, never below 0, and each step moves from
        // the pending offset so two quick ‹ clicks head for 2, not 1.
        Assert(WindowCoordinator.NextPendingHistoryOffset(+1, 0) == 1, "‹ from the current week heads for offset 1");
        Assert(WindowCoordinator.NextPendingHistoryOffset(+1, 1) == 2, "a second ‹ heads for offset 2 before the first reply lands");
        Assert(WindowCoordinator.NextPendingHistoryOffset(-1, 2) == 1, "› heads one week newer");
        Assert(WindowCoordinator.NextPendingHistoryOffset(-1, 0) == 0, "› at the current week stays at 0");
        Assert(WindowCoordinator.NextPendingHistoryOffset(0, 2) == 0, "Now heads for the current week");

        // ResolveHistoryOffset: clamped to the windows that exist, and safe with a count of 1 or 0.
        Assert(WindowCoordinator.ResolveHistoryOffset(2, 3) == 2, "an offset inside the stored range is kept");
        Assert(WindowCoordinator.ResolveHistoryOffset(5, 3) == 2, "an offset past the oldest week clamps to count − 1");
        Assert(WindowCoordinator.ResolveHistoryOffset(1, 1) == 0, "with one window every offset resolves to the current week");
        Assert(WindowCoordinator.ResolveHistoryOffset(0, 0) == 0, "a count of 0 resolves to 0 without throwing");

        // The three together: ‹, ‹, ‹, ›, Now over three windows.
        var pending = 0;
        var trail = new List<UsageWindowIdentity?>();
        foreach (var step in new[] { +1, +1, +1, -1, 0 })
        {
            pending = WindowCoordinator.NextPendingHistoryOffset(step, pending);
            var offset = WindowCoordinator.ResolveHistoryOffset(pending, appended.Length);
            pending = offset;   // SetHistoryView resets the pending offset to what it shows
            trail.Add(WindowCoordinator.BrowsableWindowAt(appended, offset));
        }
        Assert(trail.SequenceEqual([middle, oldest, oldest, middle, current]),
            "‹ ‹ ‹ › Now visits the week before, the oldest, the oldest again, the week before, then the current week");

        var pendingOffset = WindowCoordinator.ResolveHistoryOffset(WindowCoordinator.NextPendingHistoryOffset(+1, 0), noCurrent.Length);
        Assert(pendingOffset == 1 && WindowCoordinator.BrowsableWindowAt(noCurrent, pendingOffset) == middle,
            "the previous-week button from a pending live slot opens the newest stored week");
        Assert(WindowCoordinator.BrowsableWindowAt(noCurrent,
                   WindowCoordinator.ResolveHistoryOffset(WindowCoordinator.NextPendingHistoryOffset(0, pendingOffset), noCurrent.Length)) is null,
            "Now from a stored week returns to the empty pending live slot");

        // a failed read of the latest request puts the pending offset back to the week shown,
        // so the next ‹ heads for the week before it again instead of skipping one; a newer request's offset is kept.
        Assert(WindowCoordinator.PendingOffsetAfterFailedRead(requestSequence: 4, latestSequence: 4, displayedOffset: 0) == 0,
            "the latest request's failure resets the pending offset to the displayed one");
        Assert(WindowCoordinator.PendingOffsetAfterFailedRead(requestSequence: 4, latestSequence: 5, displayedOffset: 0) is null,
            "an older request's failure leaves the newer request's pending offset alone");
        var afterFailure = WindowCoordinator.NextPendingHistoryOffset(+1, 0);
        afterFailure = WindowCoordinator.PendingOffsetAfterFailedRead(1, 1, 0) ?? afterFailure;
        Assert(WindowCoordinator.NextPendingHistoryOffset(+1, afterFailure) == 1,
            "after ‹ failed at the current week, the next ‹ heads for the week before (1), not two weeks back");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }
}

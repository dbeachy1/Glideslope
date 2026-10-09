using System.Collections.Immutable;
using Avalonia.Threading;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;
using Glideslope.Storage;

namespace Glideslope.App;

// WindowCoordinator, weekly history: loads a card's current weekly window from the history store and serves the
// card's week buttons (previous week, next week, Now) over the stored windows, the current week last.
// Governed by window design §15.3 (week navigation), §16.3 (stamped browse requests) and §17 (window identity).
internal sealed partial class WindowCoordinator
{
    private async void LoadHistoryForWindow(ProviderUsageCardWindow window, ProviderDisplayState state)
    {
        var snapshot = state.Snapshot;
        var weekly = snapshot?.Buckets.FirstOrDefault(bucket => bucket.Role == QuotaBucketRole.Weekly);
        var history = _monitoring?.History;
        var identity = snapshot is not null && weekly is not null ? UsageWindowIdentityFactory.From(snapshot, weekly) : null;
        if (identity is null || history is null)
        {
            window.SetHistory(identity, [], TimeSpan.FromMinutes(_settings.RefreshMinutes), historyAvailable: false);
            return;
        }

        try
        {
            var result = await history.QueryWindowAsync(identity, identity.NominalStartUtc, DateTimeOffset.UtcNow,
                SqliteUsageHistoryStore.MaximumQuerySamples).ConfigureAwait(false);
            // Update the stored-window count so week navigation is enabled when history exists.
            var count = BrowsableWindows(await history.ListWindowsAsync(identity.AccountScope, identity.ProviderId,
                identity.BucketId).ConfigureAwait(false), identity).Length;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_closing && _windows.TryGetValue(window.ProviderId, out var current) && ReferenceEquals(current, window) &&
                    !current.SetHistory(identity, result.Samples, TimeSpan.FromMinutes(_settings.RefreshMinutes), result.IsAvailable, count) &&
                    current.HistoryWindowOffset > 0)
                    _diagnostics.Record(new DiagnosticEvent("card_history_live_deferred", state.ProviderId,
                        $"offset={current.HistoryWindowOffset},count={count}"));
            });
        }
        catch (Exception ex)
        {
            _diagnostics.Record(new DiagnosticEvent("usage_history_query_failed", state.ProviderId, $"history_unavailable,{ex.GetType().Name}"));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_closing && _windows.TryGetValue(window.ProviderId, out var current) && ReferenceEquals(current, window))
                    current.SetHistory(identity, [], TimeSpan.FromMinutes(_settings.RefreshMinutes), historyAvailable: false);
            });
        }
    }

    /// <summary>
    /// A card's previous-week (+1), next-week (−1) or Now (0) button,
    /// or its weekly rollover (0). Lists the stored windows of the card's current weekly bucket, computes
    /// offset = step == 0 ? 0 : clamp(current + step, 0, count − 1), reads windows[count − 1 − offset] over
    /// its whole span, and shows it with SetHistoryView. Offset 0 is always the live slot; when its identity is
    /// null because the window has not started, that slot is empty and stored windows begin at offset 1. With no
    /// weekly bucket, no history store, or nothing
    /// stored, the buttons are disabled through SetHistoryView with count 0. A failure logs
    /// history_window_view_failed and leaves the view unchanged.
    /// </summary>
    private async void OnHistoryWindowStepRequested(ProviderUsageCardWindow window, int step)
    {
        var identity = window.CurrentHistoryWindow;
        // The weekly bucket key remains available while its window has not started. Keep offset 0 reserved for
        // that empty live slot so the newest stored week remains at offset 1.
        var bucket = window.HistoryBucket;
        // Step from the pending target and stamp requests so quick clicks accumulate and older replies cannot overwrite newer views.
        var sequence = ++window.HistoryBrowseSequence;
        var pendingOffset = NextPendingHistoryOffset(step, window.HistoryBrowsePendingOffset);
        window.HistoryBrowsePendingOffset = pendingOffset;
        var samplingInterval = TimeSpan.FromMinutes(_settings.RefreshMinutes);
        var history = _monitoring?.History;
        if (bucket is null || history is null)
        {
            _diagnostics.Record(new DiagnosticEvent("card_history_window_unavailable", window.ProviderId,
                bucket is null ? $"no_weekly_bucket,step={step}" : $"no_history_store,step={step}"));
            window.SetHistoryView(identity, [], samplingInterval, historyAvailable: false, offset: 0, count: 0);
            return;
        }

        try
        {
            var windows = BrowsableWindows(await history.ListWindowsAsync(bucket.AccountScope, bucket.ProviderId,
                bucket.BucketId).ConfigureAwait(false), identity);
            var count = windows.Length;
            if (count == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_closing || !_windows.TryGetValue(window.ProviderId, out var current) || !ReferenceEquals(current, window) ||
                        current.HistoryBrowseSequence != sequence || current.CurrentHistoryWindow != identity)
                        return;
                    _diagnostics.Record(new DiagnosticEvent("card_history_window_unavailable", window.ProviderId,
                        $"no_stored_windows,step={step}"));
                    current.SetHistoryView(null, [], samplingInterval, historyAvailable: true, offset: 0, count: 0);
                });
                return;
            }
            var offset = ResolveHistoryOffset(pendingOffset, count);
            var target = BrowsableWindowAt(windows, offset);
            if (target is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_closing || !_windows.TryGetValue(window.ProviderId, out var current) || !ReferenceEquals(current, window) ||
                        current.HistoryBrowseSequence != sequence || current.CurrentHistoryWindow != identity)
                        return;
                    current.SetHistoryView(null, [], samplingInterval, historyAvailable: true, offset, count);
                });
                return;
            }
            var result = await history.QueryWindowAsync(target, target.NominalStartUtc, target.ResetAtUtc,
                SqliteUsageHistoryStore.MaximumQuerySamples).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_closing || !_windows.TryGetValue(window.ProviderId, out var current) || !ReferenceEquals(current, window))
                {
                    _diagnostics.Record(new DiagnosticEvent("card_history_window_view_skipped", window.ProviderId, "card_closed"));
                    return;
                }
                if (current.HistoryBrowseSequence != sequence)
                {
                    _diagnostics.Record(new DiagnosticEvent("card_history_window_view_skipped", window.ProviderId,
                        $"superseded,sequence={sequence},latest={current.HistoryBrowseSequence}"));
                    return;
                }
                if (current.CurrentHistoryWindow != identity)
                {
                    // The weekly window rolled over while this read ran; the rollover asks for its own reload.
                    _diagnostics.Record(new DiagnosticEvent("card_history_window_view_skipped", window.ProviderId, "weekly_window_changed"));
                    return;
                }
                current.SetHistoryView(target, result.Samples, samplingInterval, result.IsAvailable, offset, count);
                _diagnostics.Record(new DiagnosticEvent("card_history_window_viewed", window.ProviderId,
                    $"offset={offset},count={count},step={step},samples={result.Samples.Length},available={result.IsAvailable}"));
            });
        }
        catch (Exception ex)
        {
            _diagnostics.Record(new DiagnosticEvent("history_window_view_failed", window.ProviderId,
                $"{ex.GetType().Name},step={step}"));
            // Reset the pending offset after the latest request fails; a newer request retains ownership of it.
            Dispatcher.UIThread.Post(() =>
            {
                if (_closing || !_windows.TryGetValue(window.ProviderId, out var current) || !ReferenceEquals(current, window)) return;
                if (PendingOffsetAfterFailedRead(sequence, current.HistoryBrowseSequence, current.HistoryWindowOffset) is not { } reset)
                    return;
                _diagnostics.Record(new DiagnosticEvent("card_history_pending_offset_reset", window.ProviderId,
                    $"pending={current.HistoryBrowsePendingOffset},displayed={reset},sequence={sequence}"));
                current.HistoryBrowsePendingOffset = reset;
            });
        }
    }

    /// <summary>After a failed week-browse read, the pending offset to use: the displayed
    /// offset when the failed request is still the card's latest, else null (a newer request owns it).</summary>
    internal static int? PendingOffsetAfterFailedRead(int requestSequence, int latestSequence, int displayedOffset) =>
        requestSequence == latestSequence ? displayedOffset : null;

    /// <summary>
    /// The list ends with the current live slot. A null current identity still occupies that slot while the
    /// window is pending, so offset 0 remains live and stored weeks start at offset 1.
    /// </summary>
    internal static ImmutableArray<UsageWindowIdentity?> BrowsableWindows(ImmutableArray<UsageWindowIdentity> stored,
        UsageWindowIdentity? current) =>
        current is null ? ImmutableArray.CreateRange<UsageWindowIdentity?>(stored).Add(null) :
            stored.Contains(current) ? ImmutableArray.CreateRange<UsageWindowIdentity?>(stored) :
            ImmutableArray.CreateRange<UsageWindowIdentity?>(stored).Add(current);

    /// <summary>The offset a step request heads for, before the
    /// window count is known. Now (0) always returns to the current week; ‹ (+1) and › (−1) move from the
    /// pending offset and never below 0. The upper clamp is applied by <see cref="ResolveHistoryOffset"/>
    /// once the stored windows are listed.</summary>
    internal static int NextPendingHistoryOffset(int step, int pendingOffset) =>
        step == 0 ? 0 : Math.Max(0, pendingOffset + step);

    /// <summary>Clamps the pending offset to the browsable windows: 0 is the current week, count − 1 the
    /// oldest stored one.</summary>
    internal static int ResolveHistoryOffset(int pendingOffset, int count) =>
        Math.Clamp(pendingOffset, 0, Math.Max(0, count - 1));

    /// <summary>The stored windows arrive oldest first and end with the current one, so offset 0 is the last
    /// element and each older week is one step toward the front.</summary>
    internal static UsageWindowIdentity? BrowsableWindowAt(ImmutableArray<UsageWindowIdentity?> windows, int offset) =>
        windows[windows.Length - 1 - offset];
}

using Avalonia.Automation;
using Avalonia.Controls;
using Glideslope.Domain;

namespace Glideslope.App;

// Week browsing on the card: the previous/next/Now buttons, the stored-window offset and count, the live and
// browsed history the coordinator hands the chart, and the browse bookkeeping the coordinator stamps requests
// with. Governed by window design §15.2, §15.3, §16.3 and §17 rule 4.
internal sealed partial class ProviderUsageCardWindow
{
    /// <summary>Window design §15.2: a weekly rollover (or the weekly bucket going away) ends any browsing. The
    /// offset returns to 0 and the coordinator is asked, with step 0, to reload the view and the count.</summary>
    private void OnCurrentWeeklyWindowChanged(UsageWindowIdentity? current)
    {
        _windowOffset = 0;
        _viewedHistoryWindow = null;
        if (current is null) _windowCount = 0;
        RefreshWeekButtons();
        ApplyStatusLine();
        HistoryWindowStepRequested?.Invoke(this, 0);
    }

    private void RequestHistoryStep(int step) => HistoryWindowStepRequested?.Invoke(this, step);

    /// <summary>Window design §15.2's enable rules: previous while an older window exists, next and Now while an
    /// earlier week is shown.</summary>
    private void RefreshWeekButtons()
    {
        _previousWeek.IsEnabled = _windowOffset + 1 < _windowCount;
        _nextWeek.IsEnabled = _windowOffset > 0;
        _currentWeek.IsEnabled = _windowOffset > 0;
    }

    private static Button WeekButton(string content, double size, string tooltip)
    {
        var button = CardHeaderButtons.ActionButton(content, size, CardTextFloor);
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    public void SetHistory(UsageWindowIdentity? window, IEnumerable<UsageObservation> samples, TimeSpan samplingInterval)
    {
        if (_currentHistoryWindow != window) return;
        if (_viewedHistoryWindow is not null) return;   // window design §15.2 / §17 rule 4: a past week on view is kept
        _chart.SamplingInterval = samplingInterval;
        _chart.SetSeries(window, samples);
    }

    /// <summary>The live path's history for the current weekly window. It is ignored
    /// while a past week is on view,
    /// except that <paramref name="windowCount"/>, when given, still updates the stored-window count and the
    /// week buttons. Returns whether the chart was replaced, so the coordinator can log a deferred update.</summary>
    public bool SetHistory(UsageWindowIdentity? window, IEnumerable<UsageObservation> samples,
        TimeSpan samplingInterval, bool historyAvailable, int? windowCount = null)
    {
        if (_currentHistoryWindow != window) return false;
        if (windowCount is { } count)
        {
            _windowCount = count;
            RefreshWeekButtons();
        }
        // Window design §15.2 / §17 rule 4: while a past week is on view the live chart waits. With no current
        // window, offset 0 itself shows the newest stored week, so the guard is the viewed window, not the offset.
        if (_viewedHistoryWindow is not null) return false;
        _chart.SamplingInterval = samplingInterval;
        _chart.SetSeries(window, samples, historyAvailable);
        return true;
    }

    /// <summary>
    /// Window design §15.2: shows one stored weekly window in the chart (offset 0 is the current window, 1 the
    /// one before, and so on), sets the offset and count, refreshes the week buttons, and while the shown
    /// window is not the current one replaces the live status text with "Past week · {start} – {reset}"
    /// (§17 rule 4: with no current window, offset 0 is the newest stored week and reads as a past week).
    /// The summary and bars keep showing live data; only the chart and that one line browse. A null window
    /// (nothing stored, no current window) clears the chart.
    /// </summary>
    public void SetHistoryView(UsageWindowIdentity? window, IEnumerable<UsageObservation> samples,
        TimeSpan samplingInterval, bool historyAvailable, int offset, int count)
    {
        _windowOffset = Math.Max(0, offset);
        _windowCount = Math.Max(0, count);
        HistoryBrowsePendingOffset = _windowOffset;
        // Window design §17 rule 4: a window other than the current one is a past week, at any offset (with no
        // current window, offset 0 is the newest stored week).
        _viewedHistoryWindow = window is not null && window != _currentHistoryWindow ? window : null;
        _chart.SamplingInterval = samplingInterval;
        _chart.SetSeries(window, samples, historyAvailable);
        RefreshWeekButtons();
        ApplyStatusLine();
    }

    internal int HistoryWindowOffset => _windowOffset;
    internal int HistoryWindowCount => _windowCount;

    /// <summary>Window design §17 rule 4: the weekly bucket's key, known whenever the weekly bucket exists, even while
    /// its window has not started and <see cref="CurrentHistoryWindow"/> is null. The coordinator lists stored
    /// weeks by it.</summary>
    internal HistoryBucketKey? HistoryBucket { get; private set; }

    /// <summary>The coordinator's browse bookkeeping, touched on the UI thread only.
    /// The sequence stamps each step request, so a slow reply to an earlier click cannot overwrite the view a
    /// later click (or Now) produced. The pending offset is where the latest request is heading, so two quick
    /// ‹ clicks step twice instead of both stepping from the displayed offset. SetHistoryView resets the
    /// pending offset to the offset it shows.</summary>
    internal int HistoryBrowseSequence { get; set; }
    internal int HistoryBrowsePendingOffset { get; set; }
    /// <summary>The card's current weekly window identity, from the last state's snapshot (window design §15.3).</summary>
    internal UsageWindowIdentity? CurrentHistoryWindow => _currentHistoryWindow;
}

using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.App;

// The card's live content: UpdateState fills both bodies (full and mini) from the scheduler's state, the
// Fable heading and its separate-reset rule, the status line (live or "Past week"), the credit strip wrapper
// and reset text.
internal sealed partial class ProviderUsageCardWindow
{
    public void UpdateState(ProviderDisplayState? state)
    {
        _state = state;
        var snapshot = state?.Snapshot;
        var weekly = snapshot?.Buckets.FirstOrDefault(bucket => bucket.Role == QuotaBucketRole.Weekly);
        var shortWindow = snapshot?.Buckets.FirstOrDefault(bucket => bucket.Role == QuotaBucketRole.Short);
        _planType.IsVisible = snapshot?.Plan is { Length: > 0 };
        _planType.Text = snapshot?.Plan is { Length: > 0 } plan ? LocalizedText.CardPlanType(plan) : string.Empty;
        ApplyStatusLine();
        _refresh.IsEnabled = state?.IsEnabled != false && state?.IsReading != true;
        // Use the same enabled rule for the mini header's Refresh button.
        _miniRefresh.IsEnabled = _refresh.IsEnabled;
        _weeklyReset.Text = weekly is null ? LocalizedText.CardResetUnavailable : FormatReset(weekly);
        UpdateStrip(state);
        // 2.0 design §4.1: both bodies are updated on every UpdateState, so a mode switch shows current data at
        // once; the mini body decides its own Ready/message-block split from the state.
        _miniBody.UpdateState(state);
        _shortPanel.IsVisible = shortWindow is not null;
        if (weekly is null)
        {
            var previousWindow = _currentHistoryWindow;
            var previousBucket = HistoryBucket;
            _currentHistoryWindow = null;
            HistoryBucket = null;
            // A card can be viewing a stored week while its current
            // window is null, so the bucket going away must end browsing too, or "Past week" and enabled
            // buttons would outlive the bucket.
            if (previousWindow is not null || previousBucket is not null) OnCurrentWeeklyWindowChanged(null);
            _weeklyRemaining.Text = "—";
            _weeklyBarValue.Text = "—";
            _weeklySummary.Text = LocalizedText.CardWindowUnavailable;
            _weeklyRemaining.Foreground = PaceBrush(null);
            _weeklySummary.Foreground = PaceBrush(null);
            _weeklyRemainingBar.SetValue(null, null, null);
            _weeklyDeltaBar.SetValue(null, null, null);
            _chart.SetSeries(null, []);
        }
        else
        {
            _weeklyRemaining.Text = LocalizedText.Percentage(weekly.RemainingFraction);
            _weeklyBarValue.Text = LocalizedText.Percentage(weekly.RemainingFraction);
            var pace = PaceCalculator.Calculate(weekly, DateTimeOffset.UtcNow, state?.Freshness ?? SnapshotFreshness.RestoredHistorical);
            // A window the source reports as not started (window design §17 rule 3) reads
            // "No usage yet", not a pace line, and its reset is one duration after the first request.
            _weeklySummary.Text = weekly.WindowStarted ? LocalizedText.PaceSummary(pace, weekly.Duration) : LocalizedText.CardWindowNotStarted;
            _weeklyRemaining.Foreground = PaceBrush(pace.Band);
            _weeklySummary.Foreground = weekly.WindowStarted ? PaceBrush(pace.Band) : PaceBrush(null);
            _weeklyRemainingBar.SetValue(weekly.RemainingFraction, pace.Band, null);
            _weeklyDeltaBar.SetValue(weekly.RemainingFraction, pace.Band, weekly.WindowStarted ? pace.DeltaFraction : null);
            if (!weekly.WindowStarted && weekly.Duration is { } weeklyDuration)
                _weeklyReset.Text = LocalizedText.CardResetAfterFirstUse(weeklyDuration);
            var identity = UsageWindowIdentityFactory.From(snapshot!, weekly);
            var bucketKey = new HistoryBucketKey(snapshot!.AccountScope, snapshot.ProviderId, weekly.Id);
            var bucketChanged = HistoryBucket != bucketKey;
            HistoryBucket = bucketKey;
            if (_currentHistoryWindow != identity || bucketChanged)
            {
                var previousWindow = _currentHistoryWindow;
                _currentHistoryWindow = identity;
                _windowOffset = 0;
                _viewedHistoryWindow = null;
                // Return the queued browse target to the current week and discard replies for the old window.
                HistoryBrowsePendingOffset = 0;
                HistoryBrowseSequence++;
                _chart.SetSeries(identity, []);
                // The launch's first identity is loaded by the coordinator's live path. A later change is a
                // rollover, and a bucket whose window has not started (identity null, window design §17 rule 4) has
                // nothing for the live path to load; both ask the coordinator for step 0.
                if (previousWindow is not null || identity is null) OnCurrentWeeklyWindowChanged(identity);
                else { RefreshWeekButtons(); ApplyStatusLine(); }
            }
        }

        if (shortWindow is null)
        {
            _shortSummary.Text = string.Empty;
            _shortSummary.Foreground = PaceBrush(null);
            _shortReset.Text = string.Empty;
            _shortRemainingValue.Text = string.Empty;
            _shortRemainingValue.Foreground = PaceBrush(null);
            _shortRemainingBar.SetValue(null, null, null);
            _shortDeltaBar.SetValue(null, null, null);
        }
        else
        {
            var pace = PaceCalculator.Calculate(shortWindow, DateTimeOffset.UtcNow, state?.Freshness ?? SnapshotFreshness.RestoredHistorical);
            // The same "No usage yet" treatment as the weekly window (window design §17 rule 3).
            _shortSummary.Text = shortWindow.WindowStarted
                ? LocalizedText.PaceSummary(pace, shortWindow.Duration, compact: true, remainingFraction: shortWindow.RemainingFraction)
                : LocalizedText.CardWindowNotStarted;
            _shortSummary.Foreground = shortWindow.WindowStarted ? PaceBrush(pace.Band) : PaceBrush(null);
            _shortReset.Text = !shortWindow.WindowStarted && shortWindow.Duration is { } shortDuration
                ? LocalizedText.CardResetAfterFirstUse(shortDuration)
                : shortWindow.ResetAtUtc is { } shortReset
                    ? LocalizedText.CardResetAt(shortReset, DateTimeOffset.UtcNow, tenthsOfHours: true)
                    : LocalizedText.CardResetUnavailable;
            _shortRemainingValue.Text = LocalizedText.Percentage(shortWindow.RemainingFraction);
            _shortRemainingValue.Foreground = PaceBrush(pace.Band);
            _shortRemainingBar.SetValue(shortWindow.RemainingFraction, pace.Band, null);
            _shortDeltaBar.SetValue(shortWindow.RemainingFraction, pace.Band, shortWindow.WindowStarted ? pace.DeltaFraction : null);
        }

        var fable = snapshot?.Buckets.FirstOrDefault(bucket => bucket.Role == QuotaBucketRole.FeaturedWeekly);
        _fableRow.IsVisible = fable is not null;
        if (fable is not null)
        {
            var pace = PaceCalculator.Calculate(fable, DateTimeOffset.UtcNow, state?.Freshness ?? SnapshotFreshness.RestoredHistorical);
            _fableRemainingValue.Text = LocalizedText.Percentage(fable.RemainingFraction);
            _fableRemainingValue.Foreground = PaceBrush(pace.Band);
            _fableRemainingBar.SetValue(fable.RemainingFraction, pace.Band, null);
            // An unused Fable window has no pace fill and reads "No usage yet" until the window starts.
            _fableDeltaBar.SetValue(fable.RemainingFraction, pace.Band, fable.WindowStarted ? pace.DeltaFraction : null);
            _fableDeltaSummary.Text = fable.WindowStarted ? LocalizedText.PaceSummary(pace, fable.Duration) : LocalizedText.CardNoUsageYet;
            _fableDeltaSummary.Foreground = fable.WindowStarted ? PaceBrush(pace.Band) : PaceBrush(null);
        }
        _fableHeading.Text = FableHeadingText(fable, weekly);
        var fableStarted = fable?.WindowStarted;
        if (fableStarted != _fableStartedShown)
        {
            // Logged on a change only, not every poll.
            _fableStartedShown = fableStarted;
            if (fableStarted is { } started)
                _diagnostics.Record(new DiagnosticEvent("card_fable_window", _providerId, started ? "started" : "not_started"));
        }
    }

    /// <summary>Window design §15.6: the Fable heading shows its reset only when it differs from the weekly
    /// bucket's reset by at least 60 minutes.</summary>
    internal static readonly TimeSpan SeparateFableResetThreshold = TimeSpan.FromMinutes(60);

    // Both windows must have started; an unstarted window's reset slides with each poll.
    private static string FableHeadingText(QuotaBucket? fable, QuotaBucket? weekly) =>
        fable is { WindowStarted: true, ResetAtUtc: { } fableReset } && weekly is { WindowStarted: true, ResetAtUtc: { } weeklyReset } &&
        (fableReset - weeklyReset).Duration() >= SeparateFableResetThreshold
            ? LocalizedText.CardFableThisWeekWithReset(fableReset.ToLocalTime().LocalDateTime)
            : LocalizedText.CardFableThisWeek;

    /// <summary>Window design §15.2: the status line. While a stored week other than the current one is shown it
    /// reads "Past week · {start} – {reset}" in place of the live status (§17 rule 4: with no current window
    /// that can be offset 0); otherwise the live status applies.</summary>
    private void ApplyStatusLine()
    {
        if (_viewedHistoryWindow is { } viewed)
        {
            SetStatusParts([new LocalizedText.StatusLinePart(
                LocalizedText.CardPastWindow(viewed.NominalStartUtc.ToLocalTime(), viewed.ResetAtUtc.ToLocalTime()), true)]);
            return;
        }

        SetStatusParts(LocalizedText.StatusLine(_state));
    }

    /// <summary>One Run per status-line piece, SemiBold for the
    /// title, in the order the Card_StatusLine resource puts them.</summary>
    private void SetStatusParts(IReadOnlyList<LocalizedText.StatusLinePart> parts)
    {
        var inlines = new InlineCollection();
        foreach (var part in parts)
            inlines.Add(new Run(part.Text) { FontWeight = part.IsTitle ? FontWeight.SemiBold : FontWeight.Normal });
        _statusLine.Inlines = inlines;
    }

    /// <summary>The strip owns its content and theme updates.</summary>
    private void UpdateStrip(ProviderDisplayState? state)
    {
        _creditStrip.Update(state);
        _creditStrip.ApplyTheme(ActualThemeVariant != ThemeVariant.Light);
    }

    private string FormatReset(QuotaBucket bucket) => bucket.ResetAtUtc is { } reset
        ? LocalizedText.CardResetAt(reset, DateTimeOffset.UtcNow)
        : LocalizedText.CardResetUnavailable;
}

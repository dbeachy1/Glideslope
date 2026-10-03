using Glideslope.Core;

namespace Glideslope.App;

// WindowCoordinator, Settings: opening the Settings window, applying a Settings save (providers, preferences,
// card scales, start at sign-in, history retention) and disabling a provider from its card's X.
// Applies Settings saves and provider removals.
internal sealed partial class WindowCoordinator
{
    // 2.1 Ctrl peek (2.1 design §10.5): true from just before ApplySettingsAsync's disk commit through its live
    // apply steps (scale, theme, mode). Part of IsLayoutBusy (WindowCoordinator.Peek.cs), read live by
    // CardPeekController, so a peek cannot start (or stay up) mid-save even between the explicit
    // EndPeekForLayoutBusy calls §5.4 lists.
    private bool _applyingSettingsSave;
    // The card scale the open Settings window was given, per mode; its slider counts as moved when a Save sends
    // another value (2.0 design §5.1: "one baseline per mode").
    private int _settingsWindowFullScaleBaseline = 100;
    private int _settingsWindowMiniScaleBaseline = 100;
    private int SettingsWindowScaleBaseline(CardMode mode) =>
        mode == CardMode.Full ? _settingsWindowFullScaleBaseline : _settingsWindowMiniScaleBaseline;
    private void SetSettingsWindowScaleBaseline(CardMode mode, int percent)
    {
        if (mode == CardMode.Full) _settingsWindowFullScaleBaseline = percent;
        else _settingsWindowMiniScaleBaseline = percent;
    }

    private async Task DisableProviderAsync(string providerId)
    {
        await using var intentLease = await _intentGate.EnterAsync().ConfigureAwait(true);
        if (_closing)
        {
            return;
        }

        if (!_windows.ContainsKey(providerId) || !_settings.EnabledSet.Contains(providerId))
        {
            _diagnostics.Record(new DiagnosticEvent("provider_disable_skipped", providerId,
                _windows.ContainsKey(providerId) ? "not_enabled" : "no_window"));
            return;
        }

        // Finish any in-flight gesture before changing the provider set.
        InterruptActiveGesture("provider_selection_changed");

        var enabledProviderIds = _settings.EnabledProviderIds.Where(id => id != providerId).ToArray();
        var candidate = SettingsEditPolicy.ApplyProviderSelection(_settings, enabledProviderIds);
        var save = await _settingsStore.SaveAsync(candidate, _settings.Revision).ConfigureAwait(true);
        if (!save.Succeeded)
        {
            _diagnostics.Record(new DiagnosticEvent("provider_disable_save_failed", providerId, save.IssueCode));
            return;
        }

        if (_closing)
        {
            return;
        }

        // A gesture may complete while the save is in flight. Finish it before adopting the commit so its live
        // layout is preserved while this provider is removed.
        InterruptActiveGesture("provider_selection_changed");
        AdoptCommittedSettings(candidate, save.CommittedRevision ?? candidate.Revision, settingsFieldsChanged: true,
            commitOwnsFullScale: false, commitOwnsMiniScale: false, source: "provider_disabled");
        if (_monitoring is not null)
            await _monitoring.ApplyConfigurationAsync(_settings).ConfigureAwait(true);
        if (_windows.Remove(providerId, out var window))
        {
            // Select the next representative while the old native window still exists.
            UpdateTaskbarRepresentative();
            window.CompleteUserClose();
            // 2.0 mini mode (2.0 design §5.10): a disabled provider's mode is no longer tracked.
            _modes.Remove(providerId);
        }
        if (TryCaptureGeometry(out var survivingGeometry))
        {
            _layoutGeometry = survivingGeometry;
            RefreshCardGroupState(survivingGeometry);
        }
        _diagnostics.Record(new DiagnosticEvent("provider_disabled", providerId, "window_closed"));
    }

    private async Task OpenSettingsAsync()
    {
        if (_closing || _settingsWindow is not null || _settingsOpening)
            return;

        // The drag's button is up when the gear is clicked, so finish the gesture instead of rolling it back.
        InterruptActiveGesture("settings_opened");
        _settingsOpening = true;
        try
        {
            var registration = await _startup.ReadAsync().ConfigureAwait(true);
            if (_closing)
            {
                return;
            }

            _startupRegistered = registration.IsRegistered;
            // The window opens with the live scale; a Save that sends another value means the Settings slider moved.
            // Track a separate baseline for each card mode.
            SetSettingsWindowScaleBaseline(CardMode.Full, CardScale.PercentFor(_settings, CardMode.Full));
            SetSettingsWindowScaleBaseline(CardMode.Mini, CardScale.PercentFor(_settings, CardMode.Mini));
            var settings = new SettingsWindow(_settings.Clone(), registration,
                ApplySettingsAsync, startupChoicePending: _startupChoicePending,
                resetPositions: () => _ = ResetCardPositionsAsync(), diagnostics: _diagnostics, logFilePath: _logFilePath);
            _settingsWindow = settings;
            settings.Closed += (_, _) =>
            {
                if (ReferenceEquals(_settingsWindow, settings))
                    _settingsWindow = null;
            };
            settings.Show();
        }
        finally
        {
            _settingsOpening = false;
        }
    }

    /// <summary>
    /// Applies staged Settings controls together in one settings-store write.
    /// </summary>
    private async Task<SettingsSaveResult> ApplySettingsAsync(AppSettings requested, long expectedRevision, bool explicitRetentionChange)
    {
        await using var intentLease = await _intentGate.EnterAsync().ConfigureAwait(true);
        // Rebase over layout and card-size commits because Settings does not edit those fields. A concurrent
        // Settings-field commit or an external store revision remains a conflict.
        var revisionDecision = DecideSettingsRequestRevision(expectedRevision, _settings.Revision, _lastSettingsFieldsRevision);
        if (revisionDecision == SettingsRequestRevision.Conflict)
        {
            _diagnostics.Record(new DiagnosticEvent("settings_revision_conflict", Status:
                $"expected={expectedRevision},current={_settings.Revision},lastSettingsCommit={_lastSettingsFieldsRevision}"));
            return new SettingsSaveResult(false, "settings_revision_conflict");
        }
        if (revisionDecision == SettingsRequestRevision.Rebased)
            _diagnostics.Record(new DiagnosticEvent("settings_request_rebased", Status:
                $"expected={expectedRevision},current={_settings.Revision},lastSettingsCommit={_lastSettingsFieldsRevision}"));

        // Update the controller before interrupting a gesture so disabling peek records the specific setting change.
        var ctrlPeekChanged = _settings.CtrlPeekFullCard != requested.CtrlPeekFullCard;
        if (ctrlPeekChanged)
        {
            _diagnostics.Record(new DiagnosticEvent("settings_ctrl_peek", Status: $"enabled={requested.CtrlPeekFullCard}"));
            UpdatePeekSetting(requested.CtrlPeekFullCard);
        }

        // Finish an active gesture before applying the provider configuration.
        InterruptActiveGesture("configuration_changed");

        var previousRefreshMinutes = _settings.RefreshMinutes;
        var previousEnabledProviderIds = _settings.EnabledProviderIds.ToList();
        var candidate = SettingsEditPolicy.ApplyProviderSelectionAndPreferences(
            _settings, requested.EnabledProviderIds, requested, _startupChoicePending);
        // StartInMiniMode affects the next launch only, so stage it separately from preferences applied live.
        candidate.StartInMiniMode = requested.StartInMiniMode;
        // 2.1 Ctrl peek (2.1 design §2): the field itself is still staged onto candidate here; the controller was
        // already updated and the change already logged, above, before the first InterruptActiveGesture.
        candidate.CtrlPeekFullCard = requested.CtrlPeekFullCard;
        // Settings and card size buttons edit one scale per mode. Use the Settings value only when that mode's
        // slider moved during this session; otherwise preserve any live card change made while Settings was open.
        // DecideSettingsCardScale is mode-independent, so evaluate it once for each mode.
        var liveFullScale = CardScale.PercentFor(_settings, CardMode.Full);
        var requestedFullScale = CardScale.Normalize(CardScale.PercentFor(requested, CardMode.Full));
        var fullScale = DecideSettingsCardScale(requestedFullScale, SettingsWindowScaleBaseline(CardMode.Full), liveFullScale);
        CardScale.SetPercent(candidate, CardMode.Full, fullScale);
        var settingsSetFullScale = fullScale != liveFullScale;
        var liveMiniScale = CardScale.PercentFor(_settings, CardMode.Mini);
        var requestedMiniScale = CardScale.Normalize(CardScale.PercentFor(requested, CardMode.Mini));
        var miniScale = DecideSettingsCardScale(requestedMiniScale, SettingsWindowScaleBaseline(CardMode.Mini), liveMiniScale);
        CardScale.SetPercent(candidate, CardMode.Mini, miniScale);
        var settingsSetMiniScale = miniScale != liveMiniScale;
        var validationIssue = candidate.Validate();
        if (validationIssue is not null)
        {
            _diagnostics.Record(new DiagnosticEvent("settings_save_rejected", Status: validationIssue));
            // Restore the previous peek setting when validation rejects the save.
            if (ctrlPeekChanged) UpdatePeekSetting(_settings.CtrlPeekFullCard);
            return new SettingsSaveResult(false, validationIssue);
        }

        // Turning snapping off breaks snapped groups while preserving card positions.
        var unsnapped = LayoutForSnapSetting(candidate.Layout, candidate.SnapToScreenEdge);
        if (!ReferenceEquals(unsnapped, candidate.Layout))
        {
            _diagnostics.Record(new DiagnosticEvent("layout_unsnapped_by_setting", Status: $"edges={candidate.Layout.Edges.Count}"));
            candidate.Layout = unsnapped;
        }

        // 2.1 Ctrl peek (2.1 design §10.5): set around the disk commit and every live-apply step below, so
        // IsLayoutBusy reports busy for the whole span even though most of it has no InterruptActiveGesture call
        // of its own to end a peek explicitly - a poll tick landing in the middle of a Settings save must not
        // start (or leave standing) a peek that the save's own apply steps are about to move or resize.
        _applyingSettingsSave = true;
        try
        {
            var save = await _settingsStore.SaveAsync(candidate, _settings.Revision).ConfigureAwait(true);
            if (!save.Succeeded)
            {
                _diagnostics.Record(new DiagnosticEvent("settings_save_failed", Status: save.IssueCode));
                if (ctrlPeekChanged) UpdatePeekSetting(_settings.CtrlPeekFullCard);
                return save;
            }

            if (_closing)
                return new SettingsSaveResult(false, "shell_closing", candidate.Revision);

            // Preserve layout changes made while the save was in flight, then normalize for the committed provider
            // set and Snap setting.
            InterruptActiveGesture("configuration_changed");
            // Push each committed revision so Settings stays synchronized if a later apply step fails.
            AdoptCommittedSettings(candidate, save.CommittedRevision ?? candidate.Revision, settingsFieldsChanged: true,
                commitOwnsFullScale: settingsSetFullScale, commitOwnsMiniScale: settingsSetMiniScale, source: "settings_saved");
            SetSettingsWindowScaleBaseline(CardMode.Full, CardScale.PercentFor(_settings, CardMode.Full));
            SetSettingsWindowScaleBaseline(CardMode.Mini, CardScale.PercentFor(_settings, CardMode.Mini));
            var enabledProvidersChanged = !previousEnabledProviderIds.SequenceEqual(_settings.EnabledProviderIds, StringComparer.Ordinal);
            ApplyApplicationTheme();
            ApplyAlwaysOnTop(_windows.Values, _settings.AlwaysOnTop);
            RaiseSettingsAboveCards("always_on_top_applied");
            if (_monitoring is not null && (previousRefreshMinutes != _settings.RefreshMinutes || enabledProvidersChanged))
                await _monitoring.ApplyConfigurationAsync(_settings).ConfigureAwait(true);

            var removedWindows = _windows
                .Where(pair => !_settings.EnabledSet.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToList();
            foreach (var window in removedWindows)
                _windows.Remove(window.ProviderId);
            UpdateTaskbarRepresentative();
            foreach (var window in removedWindows)
                window.CloseProgrammatically();
            if (TryCaptureGeometry(out var survivingGeometry))
            {
                _layoutGeometry = survivingGeometry;
                RefreshCardGroupState(survivingGeometry);
            }
            if (enabledProvidersChanged) ShowAll();
            // Apply changed scales after ShowAll so new cards are included in the layout save.
            var committedFullScale = CardScale.PercentFor(_settings, CardMode.Full);
            if (committedFullScale != AppliedScale(CardMode.Full))
                ApplyCardScale(CardMode.Full, committedFullScale, "settings_saved", persistNow: true);
            var committedMiniScale = CardScale.PercentFor(_settings, CardMode.Mini);
            if (committedMiniScale != AppliedScale(CardMode.Mini))
                ApplyCardScale(CardMode.Mini, committedMiniScale, "settings_saved", persistNow: true);
            _diagnostics.Record(new DiagnosticEvent("settings_saved", Status:
                $"providers={_settings.EnabledProviderIds.Count},theme={_settings.ThemeMode},alwaysOnTop={_settings.AlwaysOnTop},snap={_settings.SnapToScreenEdge},refreshMinutes={_settings.RefreshMinutes},retentionDays={_settings.RetentionDays},startInMiniMode={_settings.StartInMiniMode},fullScale={committedFullScale},miniScale={committedMiniScale}"));
        }
        finally { _applyingSettingsSave = false; }

        string? retentionIssue = null;
        if (explicitRetentionChange)
        {
            retentionIssue = await SettingsEditPolicy.ApplyRetentionIntentAsync(
                _monitoring?.History, _settings.RetentionDays, explicitChange: true).ConfigureAwait(true);
            if (retentionIssue is not null)
                _diagnostics.Record(new DiagnosticEvent("usage_history_retention_apply_failed", Status: retentionIssue));
        }

        // Report startup-registration failures only when this Save requested a change; unrelated saves should not
        // surface problems discovered while simply reading the current registration.
        var startupChangeRequested = requested.StartAtSignIn != _startupRegistered;
        var state = await ReconcileStartupRegistrationAsync(
            _startup, requested.StartAtSignIn, _startupRegistered, _launcherPath).ConfigureAwait(true);
        if (state.IssueCode is not null)
            _diagnostics.Record(new DiagnosticEvent(state.IssueCode, Status: "startup_registration_failed"));
        if (_closing)
        {
            return new SettingsSaveResult(false, "shell_closing", candidate.Revision);
        }

        _startupRegistered = state.IsRegistered;
        _settingsWindow?.SetStartupState(state);
        if (_startupChoicePending)
        {
            // The completion save owns one field. It starts from what the store holds,
            // not from _settings, whose live layout may already be ahead of the disk, and adopts only that field.
            var completed = _persisted.Clone();
            completed.StartupChoiceCompleted = true;
            var completionSave = await _settingsStore.SaveAsync(completed, _settings.Revision).ConfigureAwait(true);
            if (!completionSave.Succeeded)
            {
                _diagnostics.Record(new DiagnosticEvent("settings_preferences_save_failed", Status: completionSave.IssueCode));
                return new SettingsSaveResult(false, completionSave.IssueCode, _settings.Revision);
            }

            if (_closing)
            {
                return new SettingsSaveResult(false, "shell_closing", _settings.Revision);
            }

            completed.Revision = completionSave.CommittedRevision ?? completed.Revision;
            _settings.StartupChoiceCompleted = true;
            _settings.Revision = completed.Revision;
            _persisted = completed;
            _lastSettingsFieldsRevision = completed.Revision;
            _startupChoicePending = false;
            PushCommittedSettingsToWindow("startup_choice_completed");
        }

        // Return the final committed revision, including any first-launch completion save.
        return SettingsSaveOutcome(retentionIssue, startupChangeRequested ? state.IssueCode : null, _settings.Revision);
    }

    internal enum SettingsRequestRevision { Current, Rebased, Conflict }

    /// <summary>Whether a Settings request made at <paramref name="expectedRevision"/>
    /// may be applied now. Current: nothing committed since. Rebased: only layout or card-size saves committed since
    /// (every revision after the last Settings-field commit is one). Conflict: a Settings-field commit came after the
    /// request's revision, or the revision is one this coordinator never had.</summary>
    internal static SettingsRequestRevision DecideSettingsRequestRevision(long expectedRevision, long currentRevision,
        long lastSettingsFieldsRevision)
    {
        if (expectedRevision == currentRevision) return SettingsRequestRevision.Current;
        if (expectedRevision < currentRevision && expectedRevision >= lastSettingsFieldsRevision) return SettingsRequestRevision.Rebased;
        return SettingsRequestRevision.Conflict;
    }

    /// <summary>The Settings slider wins when its value differs from the session baseline; otherwise preserve a
    /// live value that may have changed through a card's size button.</summary>
    internal static int DecideSettingsCardScale(int requestedPercent, int windowBaselinePercent, int livePercent) =>
        requestedPercent != windowBaselinePercent ? requestedPercent : livePercent;

    /// <summary>The result a Settings Save returns once its settings are saved and applied.
    /// A history retention failure is still a failure (the window keeps retention unapplied, so the next Save retries
    /// the prune). A start-at-sign-in registration failure is a warning on a successful result
    /// (SettingsSaveResult.WarningCode); the window shows only the startup line for it.</summary>
    internal static SettingsSaveResult SettingsSaveOutcome(string? retentionIssue, string? startupIssue, long revision) =>
        retentionIssue is not null
            ? new SettingsSaveResult(false, retentionIssue, revision)
            : new SettingsSaveResult(true, CommittedRevision: revision, WarningCode: startupIssue);

    /// <summary>
    /// Adopts a Settings-field store commit as the live settings. Keep the current layout so gestures completed
    /// during the write survive, normalize it for the committed providers and Snap setting, and preserve each live
    /// card scale unless this commit owns that mode's scale. Then update the open Settings window.
    /// </summary>
    private void AdoptCommittedSettings(AppSettings committed, long revision, bool settingsFieldsChanged,
        bool commitOwnsFullScale, bool commitOwnsMiniScale, string source)
    {
        var written = committed.Clone();
        written.Revision = revision;
        var next = written.Clone();
        next.Layout = LayoutForSettings(_settings.Layout, next.EnabledProviderIds, next.SnapToScreenEdge, out var issue);
        if (issue is not null)
            _diagnostics.Record(new DiagnosticEvent("layout_reconcile_failed", Status: $"source={source},{issue}"));
        if (!commitOwnsFullScale)
            CardScale.SetPercent(next, CardMode.Full, CardScale.PercentFor(_settings, CardMode.Full));
        if (!commitOwnsMiniScale)
            CardScale.SetPercent(next, CardMode.Mini, CardScale.PercentFor(_settings, CardMode.Mini));
        _settings = next;
        _persisted = written;
        if (settingsFieldsChanged) _lastSettingsFieldsRevision = revision;
        _diagnostics.Record(new DiagnosticEvent("settings_commit_adopted", Status:
            $"source={source},revision={revision},cards={next.Layout.Cards.Count},edges={next.Layout.Edges.Count}," +
            $"fullScale={CardScale.PercentFor(next, CardMode.Full)},miniScale={CardScale.PercentFor(next, CardMode.Mini)}"));
        PushCommittedSettingsToWindow(source);
    }

    /// <summary>
    /// Pushes the committed revision, layout, providers, and scales to an open Settings window so its next save
    /// uses the revision on disk and its controls reflect committed values. In-memory gesture edits are not commits.
    /// </summary>
    private void PushCommittedSettingsToWindow(string source)
    {
        var window = _settingsWindow;
        if (window is null) return;
        var committedFullScale = CardScale.PercentFor(_persisted, CardMode.Full);
        var committedMiniScale = CardScale.PercentFor(_persisted, CardMode.Mini);
        var applied = window.UpdateCommitted(_persisted.Revision, _persisted.Layout, _persisted.EnabledProviderIds,
            fullCardScalePercent: committedFullScale, miniCardScalePercent: committedMiniScale);
        if (applied)
        {
            SetSettingsWindowScaleBaseline(CardMode.Full, committedFullScale);
            SetSettingsWindowScaleBaseline(CardMode.Mini, committedMiniScale);
        }
        _diagnostics.Record(new DiagnosticEvent("settings_window_commit_pushed", Status:
            $"source={source},revision={_persisted.Revision},providers={_persisted.EnabledProviderIds.Count}," +
            $"fullScale={committedFullScale},miniScale={committedMiniScale},applied={applied}"));
    }

    internal static async Task<StartupRegistrationState> ReconcileStartupRegistrationAsync(
        IStartupRegistration startup,
        bool desired,
        bool observed,
        string? launcherPath)
    {
        ArgumentNullException.ThrowIfNull(startup);
        if (desired == observed)
            return await startup.ReadAsync().ConfigureAwait(true);

        var setState = launcherPath is null && desired
            ? new StartupRegistrationState(false, "startup_launcher_missing")
            : await startup.SetAsync(desired, launcherPath ?? string.Empty).ConfigureAwait(true);
        StartupRegistrationState actual;
        try
        {
            actual = await startup.ReadAsync().ConfigureAwait(true);
        }
        catch (Exception) when (setState.IssueCode is not null)
        {
            return setState;
        }

        if (setState.IssueCode is not null)
            return new StartupRegistrationState(actual.IsRegistered, setState.IssueCode);
        return actual.IsRegistered == desired
            ? actual
            : new StartupRegistrationState(actual.IsRegistered, "startup_registration_io_error");
    }
}

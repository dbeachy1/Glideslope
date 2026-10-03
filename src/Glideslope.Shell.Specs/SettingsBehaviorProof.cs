using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Storage;

namespace Glideslope.Shell.Specs;

/// <summary>Exercises SettingsWindow controls and production edit/retention policies without touching user data.</summary>
internal static class SettingsBehaviorProof
{
    /// <summary>Verifies that the Settings window fits its working area: rows scroll when needed, while
    /// messages and Save/Cancel remain visible.</summary>
    private static void SettingsFitsTheWorkingArea()
    {
        // 1920 x 1040 at 100 %: the language selector may make rows scroll, but the footer remains visible.
        var roomy = NewSettingsWindow();
        try
        {
            roomy.Show();
            roomy.FitToWorkingArea(new PixelRect(0, 0, 1920, 1040), 1.0);
            using (roomy.CaptureRenderedFrame()) { }
            var scroll = roomy.ScrollControl;
            Assert(scroll.Extent.Height > scroll.Viewport.Height + 0.5 && roomy.SizeToContent == SizeToContent.Manual,
                $"on a 1920 x 1040 working area the Settings rows scroll (content {scroll.Extent.Height:0} px, viewport {scroll.Viewport.Height:0} px)");
            Assert(roomy.ClientSize.Height <= 1040 - 2 * SettingsWindow.WorkingAreaMargin + 0.5, "the roomy window fits its working area");
            var roomyClient = new Rect(0, 0, roomy.ClientSize.Width, roomy.ClientSize.Height);
            Assert(roomyClient.Contains(BoundsInWindow(roomy.ButtonRow, roomy)) &&
                   roomyClient.Contains(BoundsInWindow(roomy.MessageArea, roomy)),
                "the message area and Save/Cancel remain inside the window while rows scroll");
            scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
            using (roomy.CaptureRenderedFrame()) { }
            var roomyRows = (StackPanel)scroll.Content!;
            var roomyLastRow = BoundsInWindow(roomyRows.Children[^1], roomy);
            Assert(BoundsInWindow(scroll, roomy).Contains(roomyLastRow),
                $"on the 1920 x 1040 work area the last Settings row remains reachable ({roomyLastRow})");
            // Settings shows the log file location as a link that opens its folder.
            var link = roomy.LogFileLinkControl;
            Assert(link is { IsVisible: true } && (link.Content as TextBlock)?.Text == SampleLogFilePath &&
                   BoundsInWindow(link, roomy).Right <= roomy.ClientSize.Width + 0.5,
                "Settings shows the log file path as a link, inside the window");
        }
        finally
        {
            roomy.Close();
        }

        // 1366 x 768 at 125 % with a 40 px taskbar: 1366 x 728 physical pixels, about 582 DIP tall. The window is
        // capped at 582 - 2 x 16 = about 550, sits inside the area, keeps Save and Cancel inside its client area,
        // and scrolls the rows, the last of which is reachable.
        var fitLog = new RecordingSink();
        var small = NewSettingsWindow(fitLog);
        try
        {
            small.Show();
            var area = new PixelRect(0, 0, 1366, 728);
            const double scaling = 1.25;
            small.FitToWorkingArea(area, scaling);
            using (small.CaptureRenderedFrame()) { }
            var client = small.ClientSize;
            var cap = area.Height / scaling - 2 * SettingsWindow.WorkingAreaMargin;
            // One pixel of tolerance: the platform rounds the capped height (550.4) to whole pixels.
            Assert(client.Height <= cap + 1 && client.Height >= small.MinHeight - 0.5,
                $"on 1366 x 768 at 125 % the window is capped at {cap:0} DIP (client {client.Height:0}; logged {string.Join(" | ", fitLog.Events.Select(e => $"{e.Code} {e.Status}"))})");
            Assert(small.Position.Y >= area.Y && small.Position.Y + client.Height * scaling <= area.Bottom + 1,
                $"the capped window sits inside the working area (top {small.Position.Y}, height {client.Height * scaling:0} px, area bottom {area.Bottom})");
            var buttons = BoundsInWindow(small.ButtonRow, small);
            Assert(new Rect(0, 0, client.Width, client.Height).Contains(buttons),
                $"Save and Cancel stay inside the capped window (buttons {buttons}, client {client})");
            var scroll = small.ScrollControl;
            Assert(scroll.Extent.Height > scroll.Viewport.Height + 0.5, "the rows scroll in the capped window");
            scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
            using (small.CaptureRenderedFrame()) { }
            var rows = (StackPanel)scroll.Content!;
            var lastRow = BoundsInWindow(rows.Children[^1], small);
            var viewport = BoundsInWindow(scroll, small);
            Assert(lastRow.Top >= viewport.Top - 0.5 && lastRow.Bottom <= viewport.Bottom + 0.5,
                $"scrolled to the end, the last row is in view (row {lastRow}, viewport {viewport})");
        }
        finally
        {
            small.Close();
        }
    }

    // The fit proofs include the log file row (shown when the coordinator passes a path), with a long
    // realistic Windows path so it wraps.
    internal const string SampleLogFilePath = @"C:\Users\someone.with.a.long.name\AppData\Local\Glideslope\logs\glideslope.log";

    private static SettingsWindow NewSettingsWindow(IDiagnosticSink? diagnostics = null) => new(AppSettings.CreateDefault(), new StartupRegistrationState(false),
        (_, _, _) => Task.FromResult(new SettingsSaveResult(true, null)), resetPositions: () => { }, diagnostics: diagnostics,
        logFilePath: SampleLogFilePath);

    /// <summary>Keeps every diagnostic a window records, so a spec can check what was logged.</summary>
    private sealed class RecordingSink : IDiagnosticSink
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public void Record(DiagnosticEvent diagnostic) => Events.Add(diagnostic);
    }

    public static void Run()
    {
        RefreshFloorRaisesOldValuesWithoutRejectingSettings();
        MessagesStayAboveTheButtons();
        SettingsFitsTheWorkingArea();
        CardSizeRowIsStagedUntilSave();
        MiniCardSizeAndStartInMiniModeAreStagedUntilSave();
        CtrlPeekFullCardIsStagedUntilSave();
        SavedWithRegistrationWarningShowsOnlyTheWarning();
        RetentionRangeMatchesValidation();
        SettingsFitsThePseudoLocale();

        var current = AppSettings.CreateDefault();
        current.StartAtSignIn = true;
        var resetCalls = 0;
        var saveCalls = 0;
        AppSettings? lastSaveRequest = null;
        var nextResult = new SettingsSaveResult(false, "settings_save_io_error");
        var settings = new SettingsWindow(
            current,
            new StartupRegistrationState(false),
            (requested, _, _) =>
            {
                saveCalls++;
                lastSaveRequest = requested;
                return Task.FromResult(nextResult);
            },
            resetPositions: () => resetCalls++);

        Assert(!settings.StartAtSignInIsChecked,
            "startup checkbox follows the observed registration state instead of stale persisted preference");
        Assert(settings.SnapToScreenEdgeControl.IsChecked == current.SnapToScreenEdge,
            "screen edge snap control reflects the persisted preference");
        Assert(settings.Topmost, "item 2: Settings stays above the cards while open, scoped to this window instance");
        Assert(settings.ButtonRow.Margin.Right >= 16 && settings.ButtonRow.Margin.Bottom >= 16 && settings.ButtonRow.Spacing > 0,
            "item 3: the button row keeps a margin from the window edges and a gap between Cancel and Save");
        settings.ResetPositionsControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(resetCalls == 1 && saveCalls == 0,
            "Reset window positions remains an immediate action, not a staged setting that waits for Save");
        var firstLaunchOffer = new SettingsWindow(
            current,
            new StartupRegistrationState(false),
            (_, _, _) => Task.FromResult(new SettingsSaveResult(false, "settings_save_io_error")),
            startupChoicePending: true);
        Assert(firstLaunchOffer.StartAtSignInIsChecked,
            "the pending Linux first-launch offer is checked by default without changing observed registration");
        firstLaunchOffer.Close();

        var startup = new SyntheticStartupRegistration();
        var enabledStartup = WindowCoordinator.ReconcileStartupRegistrationAsync(startup, true, observed: false, "launcher.exe")
            .GetAwaiter().GetResult();
        Assert(enabledStartup.IsRegistered && startup.SetCalls == 1 && startup.LastRequestedState,
            "an explicit startup-on choice registers and rereads the actual state");
        var disabledStartup = WindowCoordinator.ReconcileStartupRegistrationAsync(startup, false, observed: true, "launcher.exe")
            .GetAwaiter().GetResult();
        Assert(!disabledStartup.IsRegistered && startup.SetCalls == 2 && !startup.LastRequestedState,
            "an explicit startup-off choice unregisters and rereads the actual state");
        startup.FailNextSet = true;
        var failedStartup = WindowCoordinator.ReconcileStartupRegistrationAsync(startup, true, observed: false, "launcher.exe")
            .GetAwaiter().GetResult();
        Assert(!failedStartup.IsRegistered && failedStartup.IssueCode == "startup_registration_io_error",
            "startup registration failure reports the actual off state with a visible safe error");

        // Every control stages its change and applies nothing until Save.
        var card = new ProviderUsageCardWindow(ProviderIds.Codex, showMark: false, _ => { });
        try
        {
            var codexBox = settings.ProviderCheckbox(ProviderIds.Codex);
            var geminiBox = settings.ProviderCheckbox(ProviderIds.Gemini);
            var claudeBox = settings.ProviderCheckbox(ProviderIds.Claude);
            codexBox.IsChecked = false;
            settings.ThemeSelector.SelectedIndex = 1;
            settings.AlwaysOnTopControl.IsChecked = true;
            settings.SnapToScreenEdgeControl.IsChecked = true;
            Assert(saveCalls == 0, "toggling provider checkboxes, theme, keep-above, and snap stages the change without saving");
            Assert(Application.Current?.RequestedThemeVariant != ThemeVariant.Dark,
                "the dark theme selection is staged only; the shared application theme does not change before Save");
            Assert(!card.Topmost, "keep-above is staged only; an existing card's Topmost does not change before Save");

            // Unchecking every provider and pressing Save keeps the existing at-least-one rule,
            // applies nothing, and never even calls the save delegate.
            geminiBox.IsChecked = false;
            claudeBox.IsChecked = false;
            settings.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(saveCalls == 0 && settings.ErrorText == LocalizedText.SettingsAtLeastOneProvider,
                "unchecking every provider and pressing Save is rejected locally before any save attempt");

            // Re-check one provider, then a failed save applies nothing: the error is shown and the
            // staged edits remain exactly as typed so the user can retry.
            claudeBox.IsChecked = true;
            nextResult = new SettingsSaveResult(false, "settings_save_io_error");
            var closedAfterFailure = false;
            settings.Closed += (_, _) => closedAfterFailure = true;
            settings.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(saveCalls == 1 && !closedAfterFailure && settings.ErrorText == LocalizedText.SettingsApplyFailed("settings_save_io_error"),
                "a failed save shows the existing error, applies nothing, and leaves the window open");
            Assert(!card.Topmost && Application.Current?.RequestedThemeVariant != ThemeVariant.Dark,
                "a failed save does not apply the staged theme or keep-above changes either");

            // A successful save commits every staged control in the one call.
            nextResult = new SettingsSaveResult(true, CommittedRevision: current.Revision + 1);
            settings.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(saveCalls == 2 && closedAfterFailure,
                "a successful save closes the window after committing everything in one settings-store write");
            Assert(lastSaveRequest is not null &&
                   !lastSaveRequest.EnabledProviderIds.Contains(ProviderIds.Codex) &&
                   lastSaveRequest.EnabledProviderIds.Contains(ProviderIds.Claude) &&
                   !lastSaveRequest.EnabledProviderIds.Contains(ProviderIds.Gemini) &&
                   lastSaveRequest.ThemeMode == "dark" && lastSaveRequest.AlwaysOnTop && lastSaveRequest.SnapToScreenEdge,
                "the single committed save carries every staged control's value together");
        }
        finally
        {
            card.CloseProgrammatically();
            settings.Close();
        }

        // Cancel (and, equivalently, the title bar's X) discards every staged change: nothing is
        // ever applied outside the window, so simply closing it is a full discard.
        var cancelSaveCalls = 0;
        var cancelSettings = new SettingsWindow(
            current,
            new StartupRegistrationState(false),
            (_, _, _) =>
            {
                cancelSaveCalls++;
                return Task.FromResult(new SettingsSaveResult(true, CommittedRevision: current.Revision + 1));
            });
        cancelSettings.ProviderCheckbox(ProviderIds.Codex).IsChecked = false;
        cancelSettings.ThemeSelector.SelectedIndex = 1;
        cancelSettings.AlwaysOnTopControl.IsChecked = true;
        cancelSettings.CancelControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(cancelSaveCalls == 0, "Cancel discards every staged change without ever calling save");

        var withLayout = AppSettings.CreateDefault();
        withLayout.SnapToScreenEdge = true;
        withLayout.Layout.Cards.Add(new CardLayoutSettings { ProviderId = ProviderIds.Claude, Width = 777, Height = 555 });
        withLayout.Layout.Cards.Add(new CardLayoutSettings { ProviderId = ProviderIds.Codex, Width = 888, Height = 600 });
        withLayout.Layout.Cards.Add(new CardLayoutSettings { ProviderId = ProviderIds.Gemini, Width = 810, Height = 570 });
        withLayout.Layout.Edges.Add(new DockingEdgeSettings
        {
            FirstProviderId = ProviderIds.Claude,
            SecondProviderId = ProviderIds.Codex,
            FirstSide = CardDockSide.Right,
            Gap = 18
        });
        withLayout.Layout.Edges.Add(new DockingEdgeSettings
        {
            FirstProviderId = ProviderIds.Claude,
            SecondProviderId = ProviderIds.Gemini,
            FirstSide = CardDockSide.Bottom,
            Gap = 22
        });
        var requested = withLayout.Clone();
        requested.ThemeMode = "dark";
        requested.AlwaysOnTop = true;
        requested.RefreshMinutes = 12;
        requested.RetentionDays = 90;
        requested.StartAtSignIn = false;
        requested.SnapToScreenEdge = false;
        requested.FullCardScalePercent = 120;
        var edited = SettingsEditPolicy.ApplyPreferences(withLayout, requested, startupChoicePending: false);
        // The coordinator owns the committed card scale; preference edits preserve it.
        Assert(edited.FullCardScalePercent == withLayout.FullCardScalePercent,
            $"preference edits leave the card size to the coordinator (got {edited.FullCardScalePercent})");
        Assert(edited.EnabledProviderIds.SequenceEqual(withLayout.EnabledProviderIds) && !edited.SnapToScreenEdge,
            "preference edits preserve provider selection and apply the screen snap setting");
        Assert(edited.Layout.Cards.Count == 3 && edited.Layout.Cards[0].Width == 777 &&
               edited.Layout.Edges.Count == 2 && edited.Layout.Edges[0].Gap == 18 &&
               !ReferenceEquals(edited.Layout, withLayout.Layout),
            "preference edits preserve cloned per-card geometry and docking edges");
        Assert(edited.ThemeMode == "dark" && edited.AlwaysOnTop && edited.RefreshMinutes == 12 && edited.RetentionDays == 90,
            "requested appearance, topmost, refresh, and retention values are applied");

        var disabledCodex = SettingsEditPolicy.ApplyProviderSelection(withLayout,
            withLayout.EnabledProviderIds.Where(id => id != ProviderIds.Codex));
        Assert(disabledCodex.Validate() is null &&
               disabledCodex.Layout.Cards.All(cardLayout => cardLayout.ProviderId != ProviderIds.Codex) &&
               disabledCodex.Layout.Edges.All(edge => edge.FirstProviderId != ProviderIds.Codex && edge.SecondProviderId != ProviderIds.Codex),
            "disabling a provider removes only its saved node and incident docking edges");
        Assert(disabledCodex.Layout.Cards.Single(cardLayout => cardLayout.ProviderId == ProviderIds.Claude).Width == 777 &&
               disabledCodex.Layout.Edges.Single().SecondProviderId == ProviderIds.Gemini,
            "disable preserves surviving card geometry and unrelated docking edges");
        var reenabledCodex = SettingsEditPolicy.ApplyProviderSelection(disabledCodex, withLayout.EnabledProviderIds);
        Assert(reenabledCodex.Validate() is null && reenabledCodex.Layout.Cards.All(cardLayout => cardLayout.ProviderId != ProviderIds.Codex),
            "re-enabled cards have no old saved placement and therefore receive detached default placement");

        var history = new SyntheticHistoryStore();
        var noIntentIssue = SettingsEditPolicy.ApplyRetentionIntentAsync(history, 90, explicitChange: false).GetAwaiter().GetResult();
        Assert(noIntentIssue is null && history.RetentionUpdates == 0,
            "unrelated configuration changes never invoke retention pruning");
        var intentIssue = SettingsEditPolicy.ApplyRetentionIntentAsync(history, 90, explicitChange: true).GetAwaiter().GetResult();
        Assert(intentIssue is null && history.RetentionUpdates == 1 && history.LastRetentionDays == 90,
            "an explicit retention change updates the shared history store");
        history.FailRetention = true;
        var failureIssue = SettingsEditPolicy.ApplyRetentionIntentAsync(history, 120, explicitChange: true).GetAwaiter().GetResult();
        Assert(failureIssue == "history_prune_failed" && history.RetentionUpdates == 2,
            "retention prune failure remains a visible safe result");

        // the window-level half of SettingsWindowRevisionProof needs a real
        // SettingsWindow, so it runs here inside the headless Avalonia session.
        SettingsWindowRevisionProof.RunWindowLevel();
    }

    private static void RefreshFloorRaisesOldValuesWithoutRejectingSettings()
    {
        var saved = AppSettings.CreateDefault();
        saved.RefreshMinutes = 2;
        saved.RetentionDays = 90;
        Assert(saved.Validate() is null && saved.RefreshMinutes == AppSettings.MinimumRefreshMinutes &&
               saved.RetentionDays == 90,
            "a file saved under the old two-minute floor loads at the five-minute floor with other settings kept");

        var tooLow = AppSettings.CreateDefault();
        tooLow.RefreshMinutes = 1;
        Assert(tooLow.Validate() == "invalid_refresh_minutes", "values below the original floor remain invalid");
        var tooHigh = AppSettings.CreateDefault();
        tooHigh.RefreshMinutes = AppSettings.MaximumRefreshMinutes + 1;
        Assert(tooHigh.Validate() == "invalid_refresh_minutes", "values above thirty minutes remain invalid");
    }

    /// <summary>
    /// Design doc §16.2: with both message lines holding long text, the message area sits
    /// inside the window at its default size and ends at least 12 px above the button row.
    /// </summary>
    private static void MessagesStayAboveTheButtons()
    {
        var window = new SettingsWindow(
            AppSettings.CreateDefault(),
            new StartupRegistrationState(false),
            (_, _, _) => Task.FromResult(new SettingsSaveResult(false, "settings_save_io_error")));
        try
        {
            const string sentence = "Glideslope could not complete this request, and this sentence repeats to stand in for a long message. ";
            var longText = string.Concat(Enumerable.Repeat(sentence, 3)).TrimEnd();
            Assert(longText.Length is >= 290 and <= 320, $"the long message fixture is about 300 characters ({longText.Length})");
            window.StartupStatusControl.Text = longText;
            window.ErrorControl.Text = longText;
            Assert(window.StartupStatusText == longText && window.ErrorText == longText,
                "the existing text accessors read the message controls");

            window.Show();
            // Fit to an explicit working area because Avalonia's headless screen reports a working area taller
            // than its render surface.
            window.FitToWorkingArea(new PixelRect(0, 0, 1920, 1040), 1.0);
            window.UpdateLayout();

            var client = window.ClientSize;
            // The height is sized to the content, so only the width is fixed.
            Assert(Math.Abs(client.Width - window.Width) < 0.5 && client.Height >= window.MinHeight,
                $"the settings window lays out at its default width and at least its minimum height ({client.Width:0}x{client.Height:0}, expected {window.Width:0}x>={window.MinHeight:0})");

            var area = BoundsInWindow(window.MessageArea, window);
            var buttons = BoundsInWindow(window.ButtonRow, window);
            var status = BoundsInWindow(window.StartupStatusControl, window);
            var error = BoundsInWindow(window.ErrorControl, window);
            Assert(status.Height > window.StartupStatusControl.FontSize * 1.5 && error.Height > window.ErrorControl.FontSize * 1.5,
                $"both long messages wrap onto more than one line (status {status.Height:0.0}, error {error.Height:0.0})");
            Assert(area.Contains(status) && area.Contains(error),
                $"both messages sit inside the message area (area {area}, status {status}, error {error})");
            Assert(buttons.Top - area.Bottom >= 12 - 0.5,
                $"the message area ends at least 12 px above the button row (area bottom {area.Bottom:0.0}, buttons top {buttons.Top:0.0})");
            var clientRect = new Rect(0, 0, client.Width, client.Height);
            Assert(clientRect.Contains(area),
                $"the whole message area is inside the window's client bounds (area {area}, client {clientRect})");
            Assert(clientRect.Contains(buttons),
                $"the button row stays inside the window's client bounds (buttons {buttons}, client {clientRect})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The Settings row is a horizontal 70–150 % slider in 10 % ticks with
    /// the value shown, staged until Save like every other control; an off-step value snaps; a push from the
    /// coordinator re-syncs it only when the committed size changed; Save carries the staged size.
    /// </summary>
    private static void CardSizeRowIsStagedUntilSave()
    {
        AppSettings? saved = null;
        var current = AppSettings.CreateDefault();
        var window = new SettingsWindow(current, new StartupRegistrationState(false), (requested, _, _) =>
        {
            saved = requested;
            return Task.FromResult(new SettingsSaveResult(true, CommittedRevision: current.Revision + 3));
        });
        try
        {
            window.Show();
            var slider = window.CardScaleControl;
            Assert(slider.Orientation == Orientation.Horizontal && slider.Minimum == 70 && slider.Maximum == 150 &&
                   slider.TickFrequency == 10 && slider.IsSnapToTickEnabled && slider.Value == 100 && window.CardScaleValueText == "100%",
                $"Settings shows a 70-150 % card size slider in 10 % ticks at 100 % (value {slider.Value}, text '{window.CardScaleValueText}')");
            slider.Value = 123;
            Assert(slider.Value == 120 && window.CardScaleValueText == "120%" && saved is null,
                $"moving the slider stages a snapped 120 % and saves nothing (value {slider.Value})");
            window.UpdateCommitted(current.Revision + 1, current.Layout, current.EnabledProviderIds, fullCardScalePercent: 100);
            Assert(slider.Value == 120, "a push that does not change the committed size keeps the staged edit");
            window.UpdateCommitted(current.Revision + 2, current.Layout, current.EnabledProviderIds, fullCardScalePercent: 130);
            Assert(slider.Value == 130 && window.CardScaleValueText == "130%",
                "a push that changes the committed size (a card's own size flyout) re-syncs the slider");
            slider.Value = 80;
            window.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(saved?.FullCardScalePercent == 80, $"Save carries the staged card size (got {saved?.FullCardScalePercent})");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 2.0 mini mode (design doc §5.10): "Mini card size" is a second 70-150 % slider, staged like the full one,
    /// directly under "Card size"; "Start in mini mode" is a checkbox directly under that, staged like every
    /// other checkbox. Neither is applied until Save, and a push from the coordinator re-syncs the mini slider
    /// only when the committed mini size actually changed - the identical rule CardSizeRowIsStagedUntilSave
    /// proves for the full slider.
    /// </summary>
    private static void MiniCardSizeAndStartInMiniModeAreStagedUntilSave()
    {
        AppSettings? saved = null;
        var current = AppSettings.CreateDefault();
        var window = new SettingsWindow(current, new StartupRegistrationState(false), (requested, _, _) =>
        {
            saved = requested;
            return Task.FromResult(new SettingsSaveResult(true, CommittedRevision: current.Revision + 3));
        });
        try
        {
            window.Show();
            var slider = window.MiniCardScaleControl;
            Assert(slider.Orientation == Orientation.Horizontal && slider.Minimum == 70 && slider.Maximum == 150 &&
                   slider.TickFrequency == 10 && slider.IsSnapToTickEnabled && slider.Value == 100 && window.MiniCardScaleValueText == "100%",
                $"Settings shows a 70-150 % mini card size slider in 10 % ticks at 100 % (value {slider.Value}, text '{window.MiniCardScaleValueText}')");
            slider.Value = 123;
            Assert(slider.Value == 120 && window.MiniCardScaleValueText == "120%" && saved is null,
                $"moving the mini slider stages a snapped 120 % and saves nothing (value {slider.Value})");
            window.UpdateCommitted(current.Revision + 1, current.Layout, current.EnabledProviderIds, miniCardScalePercent: 100);
            Assert(slider.Value == 120, "a push that does not change the committed mini size keeps the staged edit");
            window.UpdateCommitted(current.Revision + 2, current.Layout, current.EnabledProviderIds, miniCardScalePercent: 130);
            Assert(slider.Value == 130 && window.MiniCardScaleValueText == "130%",
                "a push that changes the committed mini size re-syncs the mini slider");
            slider.Value = 80;

            Assert(window.StartInMiniModeControl.IsChecked == false, "Start in mini mode is unchecked by default");
            window.StartInMiniModeControl.IsChecked = true;
            Assert(saved is null, "checking Start in mini mode stages the change without saving");

            window.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(saved?.MiniCardScalePercent == 80, $"Save carries the staged mini card size (got {saved?.MiniCardScalePercent})");
            Assert(saved?.StartInMiniMode == true, "Save carries the staged Start in mini mode checkbox");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>2.1 Ctrl peek (design doc §2): the checkbox directly under Start in mini mode starts checked,
    /// and a staged opt-out is not saved until Save is pressed.</summary>
    private static void CtrlPeekFullCardIsStagedUntilSave()
    {
        AppSettings? saved = null;
        var current = AppSettings.CreateDefault();
        var window = new SettingsWindow(current, new StartupRegistrationState(false), (requested, _, _) =>
        {
            saved = requested;
            return Task.FromResult(new SettingsSaveResult(true, CommittedRevision: current.Revision + 1));
        });
        try
        {
            window.Show();
            Assert(window.CtrlPeekFullCardControl.IsChecked == true, "Hold Ctrl over a mini card to see it full is checked by default");
            window.CtrlPeekFullCardControl.IsChecked = false;
            Assert(saved is null, "unchecking it stages the opt-out without saving");
            window.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(saved?.CtrlPeekFullCard == false, "Save carries the staged Ctrl peek opt-out");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// when the settings saved but start-at-sign-in registration did not take (Succeeded
    /// with a WarningCode), Settings stays open and shows only the start-at-sign-in line, never "Could not apply
    /// settings".
    /// </summary>
    private static void SavedWithRegistrationWarningShowsOnlyTheWarning()
    {
        var current = AppSettings.CreateDefault();
        SettingsWindow? window = null;
        // like the coordinator (ApplySettingsAsync), the save pushes the real
        // registration state to the window before it returns; the window no longer infers that state itself.
        window = new SettingsWindow(current, new StartupRegistrationState(false), (_, _, _) =>
        {
            window!.SetStartupState(new StartupRegistrationState(false, "startup_registration_io_error"));
            return Task.FromResult(new SettingsSaveResult(true, CommittedRevision: current.Revision + 1, WarningCode: "startup_registration_io_error"));
        });
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            var startAtSignIn = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(box => box.Content as string == LocalizedText.SettingsStartAtSignIn);
            startAtSignIn.IsChecked = true;
            window.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!closed, "a save with a registration warning keeps Settings open");
            Assert(window.ErrorText.Length == 0, $"a save with a registration warning shows no error (got '{window.ErrorText}')");
            Assert(window.StartupStatusText == "Couldn't turn on start at sign-in (unavailable).",
                $"a save with a registration warning shows only the start-at-sign-in line (got '{window.StartupStatusText}')");
            Assert(!window.StartAtSignInIsChecked, "the checkbox shows the registration that is actually in place");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Settings' retention range matches the values accepted by AppSettings.Validate.</summary>
    private static void RetentionRangeMatchesValidation()
    {
        foreach (var (days, valid) in new[]
                 {
                     (SettingsWindow.MinimumRetentionDays - 1, false), (SettingsWindow.MinimumRetentionDays, true),
                     (SettingsWindow.MaximumRetentionDays, true), (SettingsWindow.MaximumRetentionDays + 1, false),
                 })
        {
            var settings = AppSettings.CreateDefault();
            settings.RetentionDays = days;
            Assert((settings.Validate() is null) == valid, $"AppSettings.Validate {(valid ? "accepts" : "rejects")} {days} retention days, as Settings assumes");
        }
        var window = NewSettingsWindow();
        try
        {
            Assert(window.RetentionDaysControl.Minimum == SettingsWindow.MinimumRetentionDays &&
                   window.RetentionDaysControl.Maximum == SettingsWindow.MaximumRetentionDays,
                "the retention control allows exactly that range");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Localization readiness (with every string 30 % longer (the pseudo-long
    /// locale) no Settings text is clipped and nothing runs past the window's width; longer labels wrap.</summary>
    private static void SettingsFitsThePseudoLocale()
    {
        using var pseudo = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("qps-ploc"), CultureInfo.GetCultureInfo("en-US"),
            new LocalizationResourceProof.PseudoLocaleResources());
        var window = NewSettingsWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            var client = window.ClientSize;
            // The controls and text the window lays out; template internals (a Viewbox's inner container reports
            // its unscaled size) are not checked.
            foreach (var control in window.GetVisualDescendants().OfType<Control>()
                         .Where(control => control is TextBlock or Button or CheckBox or ComboBox or NumericUpDown or Slider)
                         .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0))
            {
                var bounds = BoundsInWindow(control, window);
                Assert(bounds.Right <= client.Width + 0.5, $"pseudo-long Settings: {control.GetType().Name} ends inside the window ({bounds}, width {client.Width:0})");
                if (control is TextBlock block && !string.IsNullOrEmpty(block.Text))
                {
                    var neededWidth = block.DesiredSize.Width - block.Margin.Left - block.Margin.Right;
                    var neededHeight = block.DesiredSize.Height - block.Margin.Top - block.Margin.Bottom;
                    Assert(neededWidth <= block.Bounds.Width + 0.5 && neededHeight <= block.Bounds.Height + 0.5,
                        $"pseudo-long Settings: '{block.Text}' is not clipped ({neededWidth:0}x{neededHeight:0} in {block.Bounds.Size})");
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static Rect BoundsInWindow(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException($"FAIL: {control.GetType().Name} is not in the settings window's visual tree");
        return new Rect(origin, control.Bounds.Size);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    private sealed class SyntheticHistoryStore : IUsageHistoryStore
    {
        public int RetentionUpdates { get; private set; }
        public int LastRetentionDays { get; private set; }
        public bool FailRetention { get; set; }
        public HistoryStorageHealth Health { get; private set; } = new(true, null);
        public event EventHandler<UsageHistoryHealthChangedEventArgs>? HealthChanged
        {
            add { }
            remove { }
        }

        public bool TryAppend(UsageObservation observation) => true;

        public Task<UsageHistoryQueryResult> QueryWindowAsync(UsageWindowIdentity window, DateTimeOffset fromUtc,
            DateTimeOffset throughUtc, int maxSamples, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UsageHistoryQueryResult(true, ImmutableArray<UsageObservation>.Empty, null));

        // Design doc §15.1: the settings proof never browses windows, so the fake lists none.
        public Task<ImmutableArray<UsageWindowIdentity>> ListWindowsAsync(string accountScope, string providerId,
            string bucketId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ImmutableArray<UsageWindowIdentity>.Empty);

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetRetentionDaysAsync(int retentionDays, CancellationToken cancellationToken = default)
        {
            RetentionUpdates++;
            LastRetentionDays = retentionDays;
            Health = FailRetention ? new HistoryStorageHealth(false, "history_prune_failed") : new HistoryStorageHealth(true, null);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SyntheticStartupRegistration : IStartupRegistration
    {
        public int SetCalls { get; private set; }
        public bool LastRequestedState { get; private set; }
        public bool FailNextSet { get; set; }
        public bool IsRegistered { get; private set; }

        public Task<StartupRegistrationState> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StartupRegistrationState(IsRegistered));

        // This synthetic double never owns a pre-rename artifact, so reconciliation is always a no-op read.
        public Task<StartupRegistrationState> ReconcileLegacyArtifactAsync(string? launcherPath, CancellationToken cancellationToken = default) =>
            ReadAsync(cancellationToken);

        public Task<StartupRegistrationState> SetAsync(bool enabled, string launcherPath, CancellationToken cancellationToken = default)
        {
            SetCalls++;
            LastRequestedState = enabled;
            if (FailNextSet)
            {
                FailNextSet = false;
                return Task.FromResult(new StartupRegistrationState(IsRegistered, "startup_registration_io_error"));
            }
            IsRegistered = enabled;
            return Task.FromResult(new StartupRegistrationState(IsRegistered));
        }
    }
}

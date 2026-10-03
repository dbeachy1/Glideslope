using Avalonia.Controls;
using Avalonia.Interactivity;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;

namespace Glideslope.Shell.Specs;

/// <summary>Verifies that committed settings revisions reach an open Settings window, preventing stale edits
/// from overwriting newer state. Run covers SettingsCommitTracker and SettingsEditPolicy without a window;
/// RunWindowLevel checks the production window in the Shell suite's headless session.</summary>
internal static class SettingsWindowRevisionProof
{
    public static void Run()
    {
        var initial = AppSettings.CreateDefault();
        initial.Revision = 4;
        var store = new FakeSettingsStore(initial);
        var coordinator = initial.Clone();

        // The Settings window opens on the committed settings.
        var tracker = new SettingsCommitTracker(coordinator);
        Assert(tracker.ExpectedRevision == 4 && tracker.EnabledProviderIds.Contains(ProviderIds.Claude),
            "an opened window starts from the committed revision and selection");

        // The user closes the Claude card while Settings is open: the coordinator commits the disable.
        coordinator = DisableProvider(store, coordinator, ProviderIds.Claude);
        Assert(coordinator.Revision == 5, "the disable commits the next revision");

        // Control: without the push, the window's Save sends the stale revision and conflicts.
        var staleRequest = tracker.CreateRequest();
        var stale = store.Save(Candidate(coordinator, staleRequest), tracker.ExpectedRevision);
        Assert(!stale.Succeeded && stale.IssueCode == "settings_revision_conflict",
            "a window that was not told about the disable saves with a stale revision and conflicts");

        // The coordinator pushes the commit.
        Assert(tracker.ApplyCommitted(coordinator.Revision, coordinator.Layout, coordinator.EnabledProviderIds),
            "the pushed commit is taken");
        Assert(tracker.ExpectedRevision == 5, "the push moves the window to the committed revision");
        Assert(!tracker.EnabledProviderIds.Contains(ProviderIds.Claude) && tracker.EnabledProviderIds.Count == 2,
            "the push carries the committed selection the checkboxes re-sync to");

        // Save from the window with the pushed revision: it succeeds.
        var request = tracker.CreateRequest();
        request.ThemeMode = "dark";
        var candidate = Candidate(coordinator, request);
        var save = store.Save(candidate, tracker.ExpectedRevision);
        Assert(save.Succeeded && save.CommittedRevision == 6, "a save with the pushed revision succeeds");
        candidate.Revision = save.CommittedRevision!.Value;
        coordinator = candidate;
        tracker.RecordSaveResult(request, save);
        Assert(tracker.ExpectedRevision == 6 && !store.Committed.EnabledProviderIds.Contains(ProviderIds.Claude) &&
               store.Committed.ThemeMode == "dark",
            "the saved settings keep the disable and carry the staged change");

        // A push older than the revision already held changes nothing.
        Assert(!tracker.ApplyCommitted(5, coordinator.Layout, ProviderCatalog.DefaultEnabledProviderIds),
            "a push older than the held revision is refused");
        Assert(tracker.ExpectedRevision == 6 && !tracker.EnabledProviderIds.Contains(ProviderIds.Claude),
            "a refused push leaves revision and selection alone");

        // A push can land while the window's own save result is still returning; the older result must not
        // move the window back.
        tracker.ApplyCommitted(8, coordinator.Layout, coordinator.EnabledProviderIds);
        tracker.RecordSaveResult(request, new SettingsSaveResult(false, "startup_registration_io_error", 7));
        Assert(tracker.ExpectedRevision == 8, "an older save result never moves the window below a pushed revision");

        // Retention bookkeeping is unchanged: applied on success or a non-history failure, not on a
        // history failure.
        var retention = tracker.CreateRequest();
        retention.RetentionDays = tracker.AppliedRetentionDays + 30;
        tracker.RecordSaveResult(retention, new SettingsSaveResult(false, "history_prune_failed", 9));
        Assert(tracker.ExpectedRevision == 9 && tracker.AppliedRetentionDays != retention.RetentionDays,
            "a history failure commits the revision but leaves retention unapplied");
        tracker.RecordSaveResult(retention, new SettingsSaveResult(true, CommittedRevision: 10));
        Assert(tracker.ExpectedRevision == 10 && tracker.AppliedRetentionDays == retention.RetentionDays,
            "a successful save applies retention");
    }

    /// <summary>
    /// Runs inside the headless Avalonia session (called from SettingsBehaviorProof.Run). A real
    /// SettingsWindow is open, the coordinator commits a provider disable and pushes it with
    /// UpdateCommitted, and the window's Save then succeeds with the pushed revision. The control case shows
    /// the conflict a window gets without the push.
    /// </summary>
    public static void RunWindowLevel()
    {
        var initial = AppSettings.CreateDefault();
        initial.Revision = 7;
        var store = new FakeSettingsStore(initial);
        var coordinator = initial.Clone();
        var receivedRevisions = new List<long>();

        // Mirrors WindowCoordinator.ApplySettingsAsync's revision gate and candidate policy.
        Task<SettingsSaveResult> ApplySettings(AppSettings requested, long expectedRevision, bool retentionChanged)
        {
            receivedRevisions.Add(expectedRevision);
            if (coordinator.Revision != expectedRevision)
                return Task.FromResult(new SettingsSaveResult(false, "settings_revision_conflict"));
            var candidate = Candidate(coordinator, requested);
            var result = store.Save(candidate, expectedRevision);
            if (result.Succeeded)
            {
                candidate.Revision = result.CommittedRevision!.Value;
                coordinator = candidate;
            }
            return Task.FromResult(result);
        }

        var window = new SettingsWindow(coordinator.Clone(), new StartupRegistrationState(false), ApplySettings);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            Assert(window.LanguageSelector.Items.Count == AppSettings.SupportedLanguageChoices.Count &&
                   (window.LanguageSelector.Items.OfType<ComboBoxItem>().First().Content as string) == LocalizedText.SettingsLanguageSystem,
                "the language selector contains exactly the supported choices and starts with System language");
            window.LanguageSelector.SelectedIndex = Array.IndexOf(AppSettings.SupportedLanguageChoices.ToArray(), "es");
            coordinator = DisableProvider(store, coordinator, ProviderIds.Claude);
            Assert(window.UpdateCommitted(coordinator.Revision, coordinator.Layout, coordinator.EnabledProviderIds),
                "the open window takes the pushed disable");
            Assert(window.ProviderCheckbox(ProviderIds.Claude).IsChecked == false &&
                   window.ProviderCheckbox(ProviderIds.Codex).IsChecked == true &&
                   window.ProviderCheckbox(ProviderIds.Gemini).IsChecked == true,
                "the push re-syncs the checkboxes: the closed provider is no longer shown checked");

            window.ThemeSelector.SelectedIndex = 1;
            window.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(receivedRevisions.SequenceEqual([8L]), "the window's Save sends the pushed revision");
            Assert(closed && store.Committed.Revision == 9 && store.Committed.ThemeMode == "dark" &&
                   !store.Committed.EnabledProviderIds.Contains(ProviderIds.Claude) && store.Committed.LanguageChoice == "es",
                "the save succeeds, keeps the disable and staged language, and closes the window");
        }
        finally
        {
            if (!closed) window.Close();
        }

        // Control on a second window: a disable that is not pushed makes Save conflict; the
        // push makes the same Save succeed. A stale push afterwards is refused and changes no checkbox.
        receivedRevisions.Clear();
        var second = new SettingsWindow(coordinator.Clone(), new StartupRegistrationState(false), ApplySettings);
        var secondClosed = false;
        second.Closed += (_, _) => secondClosed = true;
        try
        {
            coordinator = DisableProvider(store, coordinator, ProviderIds.Gemini);
            second.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!secondClosed && second.ErrorText == LocalizedText.SettingsApplyFailed("settings_revision_conflict"),
                "without the push the window's Save conflicts and stays open");

            Assert(second.UpdateCommitted(coordinator.Revision, coordinator.Layout, coordinator.EnabledProviderIds),
                "the window takes the push after the conflict");
            Assert(!second.UpdateCommitted(coordinator.Revision - 1, coordinator.Layout, ProviderCatalog.DefaultEnabledProviderIds),
                "an older push is refused");
            Assert(second.ProviderCheckbox(ProviderIds.Gemini).IsChecked == false &&
                   second.ProviderCheckbox(ProviderIds.Claude).IsChecked == false &&
                   second.ProviderCheckbox(ProviderIds.Codex).IsChecked == true,
                "a refused push leaves the checkboxes on the committed selection");

            second.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(receivedRevisions.SequenceEqual([9L, 10L]), "the retry sends the pushed revision");
            Assert(secondClosed && store.Committed.Revision == 11 &&
                   store.Committed.EnabledProviderIds.SequenceEqual([ProviderIds.Codex]),
                "after the push the same Save succeeds with only the remaining provider");
        }
        finally
        {
            if (!secondClosed) second.Close();
        }

        // A push re-syncs only the checkboxes whose committed state changed. A
        // layout save (every card drag) pushes an unchanged selection and must not undo the user's staged
        // toggles; a push that removes Claude unchecks Claude and still leaves the user's Gemini choice.
        var third = AppSettings.CreateDefault();
        third.Revision = 20;
        var thirdStore = new FakeSettingsStore(third);
        var thirdCoordinator = third.Clone();
        var thirdRevisions = new List<long>();
        Task<SettingsSaveResult> ApplyThird(AppSettings requested, long expectedRevision, bool retentionChanged)
        {
            thirdRevisions.Add(expectedRevision);
            if (thirdCoordinator.Revision != expectedRevision)
                return Task.FromResult(new SettingsSaveResult(false, "settings_revision_conflict"));
            var candidate = Candidate(thirdCoordinator, requested);
            var result = thirdStore.Save(candidate, expectedRevision);
            if (result.Succeeded)
            {
                candidate.Revision = result.CommittedRevision!.Value;
                thirdCoordinator = candidate;
            }
            return Task.FromResult(result);
        }

        var staged = new SettingsWindow(thirdCoordinator.Clone(), new StartupRegistrationState(false), ApplyThird);
        var stagedClosed = false;
        staged.Closed += (_, _) => stagedClosed = true;
        try
        {
            staged.ProviderCheckbox(ProviderIds.Gemini).IsChecked = false;

            // A card drag: the coordinator saves the layout with the provider set unchanged, and pushes.
            var layoutOnly = thirdCoordinator.Clone();
            var layoutSave = thirdStore.Save(layoutOnly, thirdCoordinator.Revision);
            Assert(layoutSave.Succeeded, "the layout-only commit succeeds");
            layoutOnly.Revision = layoutSave.CommittedRevision!.Value;
            thirdCoordinator = layoutOnly;
            Assert(staged.UpdateCommitted(thirdCoordinator.Revision, thirdCoordinator.Layout, thirdCoordinator.EnabledProviderIds),
                "the layout push is taken");
            Assert(staged.ProviderCheckbox(ProviderIds.Gemini).IsChecked == false &&
                   staged.ProviderCheckbox(ProviderIds.Claude).IsChecked == true &&
                   staged.ProviderCheckbox(ProviderIds.Codex).IsChecked == true,
                "a push with an unchanged provider set leaves the user's unchecked Gemini alone");

            // The user closes the Claude card: the push removes Claude.
            thirdCoordinator = DisableProvider(thirdStore, thirdCoordinator, ProviderIds.Claude);
            Assert(staged.UpdateCommitted(thirdCoordinator.Revision, thirdCoordinator.Layout, thirdCoordinator.EnabledProviderIds),
                "the disable push is taken");
            Assert(staged.ProviderCheckbox(ProviderIds.Claude).IsChecked == false &&
                   staged.ProviderCheckbox(ProviderIds.Gemini).IsChecked == false &&
                   staged.ProviderCheckbox(ProviderIds.Codex).IsChecked == true,
                "a push that removes Claude unchecks Claude and keeps the user's Gemini choice");

            staged.SaveControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(thirdRevisions.SequenceEqual([22L]), "the Save sends the latest pushed revision");
            Assert(stagedClosed && thirdStore.Committed.EnabledProviderIds.SequenceEqual([ProviderIds.Codex]),
                "the Save commits the user's Gemini toggle together with the pushed Claude removal");
        }
        finally
        {
            if (!stagedClosed) staged.Close();
        }
    }

    /// <summary>The coordinator's DisableProviderAsync commit: the production selection policy, then a
    /// save against the committed revision.</summary>
    private static AppSettings DisableProvider(FakeSettingsStore store, AppSettings current, string providerId)
    {
        var candidate = SettingsEditPolicy.ApplyProviderSelection(current,
            current.EnabledProviderIds.Where(id => id != providerId).ToArray());
        var save = store.Save(candidate, current.Revision);
        Assert(save.Succeeded, $"the coordinator's disable of {providerId} commits");
        candidate.Revision = save.CommittedRevision!.Value;
        return candidate;
    }

    /// <summary>The coordinator's ApplySettingsAsync candidate: the requested selection and preferences on
    /// top of the coordinator's committed settings.</summary>
    private static AppSettings Candidate(AppSettings coordinator, AppSettings requested) =>
        SettingsEditPolicy.ApplyProviderSelectionAndPreferences(coordinator, requested.EnabledProviderIds, requested,
            startupChoicePending: false);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    /// <summary>In-memory stand-in for SettingsStore with its revision contract: a save whose expected
    /// revision is not the committed one is rejected with settings_revision_conflict and writes nothing;
    /// otherwise it commits the next revision.</summary>
    private sealed class FakeSettingsStore(AppSettings initial)
    {
        public AppSettings Committed { get; private set; } = initial.Clone();

        public SettingsSaveResult Save(AppSettings candidate, long expectedRevision)
        {
            if (expectedRevision != Committed.Revision)
                return new SettingsSaveResult(false, "settings_revision_conflict");
            var committed = candidate.Clone();
            committed.Revision = Committed.Revision + 1;
            Committed = committed;
            return new SettingsSaveResult(true, CommittedRevision: committed.Revision);
        }
    }
}

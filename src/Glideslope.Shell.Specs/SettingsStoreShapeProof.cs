using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Checks that malformed settings are preserved for recovery, scale and mode fields load with defaults and
/// normalize invalid values, and Ctrl-peek settings round-trip without a schema change.
/// </summary>
internal static class SettingsStoreShapeProof
{
    public static async Task RunAsync(string tempRoot)
    {
        await NonObjectRootsAreCorruptAsync(tempRoot).ConfigureAwait(false);
        await NullCollectionsAreInvalidAsync(tempRoot).ConfigureAwait(false);
        await FullCardScaleAsync(tempRoot).ConfigureAwait(false);
        await MiniModeFieldsAsync(tempRoot).ConfigureAwait(false);
    }

    private static async Task NonObjectRootsAreCorruptAsync(string tempRoot)
    {
        var index = 0;
        foreach (var document in new[] { "[1,2]", "null", "\"settings\"", "42", "true" })
        {
            var paths = new FilePaths(Path.Combine(tempRoot, "settings-shape-root", $"case-{index++}"));
            await File.WriteAllTextAsync(paths.SettingsFile, document).ConfigureAwait(false);
            var sink = new RecordingSink();
            var store = new SettingsStore(paths, sink);
            var loaded = await store.LoadAsync().ConfigureAwait(false);
            Assert(loaded.IssueCode == "settings_corrupt_preserved", $"root {document} should load as a preserved corrupt file, got {loaded.IssueCode}");
            Assert(loaded.Settings.EnabledSet.SetEquals(ProviderCatalog.DefaultEnabledProviderIds), $"root {document} falls back to the defaults");
            Assert(sink.Events.Any(e => e.Code == "settings_document_rejected" && e.Status!.StartsWith("root_", StringComparison.Ordinal)),
                $"root {document} is logged with its JSON kind");
            var copies = Directory.EnumerateFiles(paths.Folder, "settings.json.corrupt-*").ToList();
            Assert(copies.Count == 1 && await File.ReadAllTextAsync(copies[0]).ConfigureAwait(false) == document, $"root {document} is preserved byte for byte");
            var save = await store.SaveAsync(loaded.Settings).ConfigureAwait(false);
            Assert(save.Succeeded, $"after a {document} root the defaults must be savable, got {save.IssueCode}");
        }
    }

    private static async Task NullCollectionsAreInvalidAsync(string tempRoot)
    {
        var cases = new (string Document, string Issue, long Revision)[]
        {
            ("{\"schemaVersion\":2,\"revision\":4,\"enabledProviderIds\":null}", "invalid_enabled_providers", 4),
            ("{\"schemaVersion\":2,\"revision\":5,\"layout\":null}", "invalid_layout_graph", 5),
            ("{\"schemaVersion\":2,\"revision\":6,\"layout\":{\"cards\":null}}", "invalid_layout_graph", 6),
            ("{\"schemaVersion\":2,\"revision\":7,\"layout\":{\"edges\":[null]}}", "invalid_layout_edge", 7),
            ("{\"schemaVersion\":2,\"revision\":8,\"themeMode\":null}", "invalid_theme", 8),
            ("{\"schemaVersion\":1,\"revision\":9,\"enabledProviderIds\":null}", "invalid_enabled_providers", 9),
        };

        var index = 0;
        foreach (var (document, issue, revision) in cases)
        {
            var paths = new FilePaths(Path.Combine(tempRoot, "settings-shape-null", $"case-{index++}"));
            await File.WriteAllTextAsync(paths.SettingsFile, document).ConfigureAwait(false);
            var store = new SettingsStore(paths, new RecordingSink());
            var loaded = await store.LoadAsync().ConfigureAwait(false);
            Assert(loaded.IssueCode == issue, $"{document} should report {issue}, got {loaded.IssueCode}");
            Assert(loaded.Settings.Revision == revision, $"{document} defaults start at the file's revision");
            Assert(Directory.EnumerateFiles(paths.Folder, "settings.json.invalid-*").Count() == 1, $"{document} is preserved beside settings.json");
            var save = await store.SaveAsync(loaded.Settings).ConfigureAwait(false);
            Assert(save.Succeeded && save.CommittedRevision == revision + 1, $"{document}: the defaults save at the next revision");
        }
    }

    private static async Task FullCardScaleAsync(string tempRoot)
    {
        var paths = new FilePaths(Path.Combine(tempRoot, "settings-card-scale"));

        // A 1.0 settings file has no fullCardScalePercent: it reads as 100, with no issue and no normalization.
        await File.WriteAllTextAsync(paths.SettingsFile, "{\"schemaVersion\":2,\"revision\":3,\"enabledProviderIds\":[\"codex\"]}").ConfigureAwait(false);
        var sink = new RecordingSink();
        var store = new SettingsStore(paths, sink);
        var loaded = await store.LoadAsync().ConfigureAwait(false);
        Assert(loaded.IssueCode is null && loaded.Settings.FullCardScalePercent == 100 && loaded.Settings.Revision == 3,
            "a file without the card scale reads as 100");
        Assert(!sink.Events.Any(e => e.Code == "settings_full_card_scale_normalized"), "a missing field is the default, not a normalization");

        var candidate = loaded.Settings.Clone();
        candidate.FullCardScalePercent = 120;
        Assert(candidate.Clone().FullCardScalePercent == 120, "Clone copies the card scale");
        var saved = await store.SaveAsync(candidate).ConfigureAwait(false);
        Assert(saved.Succeeded && saved.CommittedRevision == 4, "a card scale of 120 saves");
        Assert((await File.ReadAllTextAsync(paths.SettingsFile).ConfigureAwait(false)).Contains("\"fullCardScalePercent\": 120", StringComparison.Ordinal),
            "the scale is stored under its camelCase JSON name");
        var reloaded = await store.LoadAsync().ConfigureAwait(false);
        Assert(reloaded.IssueCode is null && reloaded.Settings.FullCardScalePercent == 120, "the card scale round-trips");

        var revision = reloaded.Settings.Revision;
        foreach (var (raw, stored, expected) in new[] { ("75", 75, 80), ("40", 40, 70), ("1000", 1000, 150), ("-5", -5, 70), ("144", 144, 140), ("145", 145, 150), ("\"110\"", 110, 110), ("150", 150, 150) })
        {
            await File.WriteAllTextAsync(paths.SettingsFile,
                $"{{\"schemaVersion\":2,\"revision\":{revision},\"enabledProviderIds\":[\"codex\"],\"fullCardScalePercent\":{raw}}}").ConfigureAwait(false);
            var caseSink = new RecordingSink();
            var result = await new SettingsStore(paths, caseSink).LoadAsync().ConfigureAwait(false);
            Assert(result.IssueCode is null, $"a card scale of {raw} is normalized, never a reason to discard the file (got {result.IssueCode})");
            Assert(result.Settings.FullCardScalePercent == expected, $"a card scale of {raw} reads as {expected}, got {result.Settings.FullCardScalePercent}");
            var normalized = caseSink.Events.Where(e => e.Code == "settings_full_card_scale_normalized").ToList();
            Assert(stored == expected
                    ? normalized.Count == 0
                    : normalized.Count == 1 && normalized[0].Status == $"load,from_{stored}_to_{expected}",
                $"a card scale of {raw} is logged only when it changes");
        }

        Assert(!Directory.EnumerateFiles(paths.Folder, "settings.json.invalid-*").Any() &&
               !Directory.EnumerateFiles(paths.Folder, "settings.json.corrupt-*").Any(),
            "no card-scale value made the file invalid or corrupt");

        var saveSink = new RecordingSink();
        var saveStore = new SettingsStore(paths, saveSink);
        var current = await saveStore.LoadAsync().ConfigureAwait(false);
        var offStep = current.Settings.Clone();
        offStep.FullCardScalePercent = 133;
        var offStepSave = await saveStore.SaveAsync(offStep).ConfigureAwait(false);
        Assert(offStepSave.Succeeded && offStep.FullCardScalePercent == 130, "an off-step save is normalized");
        Assert(saveSink.Events.Any(e => e.Code == "settings_full_card_scale_normalized" && e.Status == "save,from_133_to_130"), "the save-time normalization is logged");
        Assert((await saveStore.LoadAsync().ConfigureAwait(false)).Settings.FullCardScalePercent == 130, "the normalized value is what is stored");

        Assert(AppSettings.NormalizeCardScalePercent(AppSettings.MinimumCardScalePercent) == 70 &&
               AppSettings.NormalizeCardScalePercent(AppSettings.MaximumCardScalePercent) == 150 &&
               AppSettings.NormalizeCardScalePercent(int.MinValue) == 70 &&
               AppSettings.NormalizeCardScalePercent(int.MaxValue) == 150,
            "normalization clamps at both ends");
    }

    /// <summary>2.0 mini mode (design doc §8.1, SettingsStoreShapeProof bullets).</summary>
    private static async Task MiniModeFieldsAsync(string tempRoot)
    {
        var newSettings = AppSettings.CreateDefault();
        Assert(newSettings.CtrlPeekFullCard, "new settings enable Ctrl peek by default");

        var missingFilePaths = new FilePaths(Path.Combine(tempRoot, "settings-ctrl-peek-new"));
        var missingFile = await new SettingsStore(missingFilePaths, new RecordingSink()).LoadAsync().ConfigureAwait(false);
        Assert(missingFile.IssueCode is null && missingFile.Settings.CtrlPeekFullCard,
            "a missing settings file enables Ctrl peek by default");

        var paths = new FilePaths(Path.Combine(tempRoot, "settings-mini-mode"));

        // A 1.1 settings file has none of the new fields: it loads with their defaults, with no issue code,
        // and no normalization is logged (a missing field is the default, not an off-step value).
        await File.WriteAllTextAsync(paths.SettingsFile,
            "{\"schemaVersion\":2,\"revision\":11,\"enabledProviderIds\":[\"codex\"],\"fullCardScalePercent\":120}").ConfigureAwait(false);
        var loadSink = new RecordingSink();
        var loadStore = new SettingsStore(paths, loadSink);
        var loaded = await loadStore.LoadAsync().ConfigureAwait(false);
        Assert(loaded.IssueCode is null && loaded.Settings.Revision == 11, "a 1.1 file with none of the mini fields is not invalid");
        Assert(!loaded.Settings.StartInMiniMode && loaded.Settings.MiniCardScalePercent == 100,
            "a 1.1 file reads StartInMiniMode false and MiniCardScalePercent 100, the defaults");
        Assert(loaded.Settings.CtrlPeekFullCard, "a file with no ctrlPeekFullCard field reads true, the default");
        Assert(loaded.Settings.Layout.Cards.Count == 0,
            "a 1.1 file has no card entries, so no FullWidth/FullHeight or MiniWidth/MiniHeight to read either (design doc §3.7: an early-2.0 file with no mini fields loads unchanged)");
        Assert(!loadSink.Events.Any(e => e.Code is "settings_mini_card_scale_normalized"),
            "a missing mini scale field is the default, not a normalization");

        // The new fields round-trip: StartInMiniMode, MiniCardScalePercent, and a card's FullWidth/FullHeight
        // and MiniWidth/MiniHeight (design doc §3.7, §8.1 "SettingsStoreShapeProof round-trips miniWidth/miniHeight").
        var candidate = loaded.Settings.Clone();
        candidate.StartInMiniMode = true;
        candidate.MiniCardScalePercent = 120;
        candidate.CtrlPeekFullCard = true;
        candidate.Layout.Cards.Add(new CardLayoutSettings
        {
            ProviderId = ProviderCatalog.Codex,
            Width = 400,
            Height = 300,
            FullWidth = 940,
            FullHeight = 680,
            MiniWidth = 470,
            MiniHeight = 330,
        });
        var saved = await loadStore.SaveAsync(candidate).ConfigureAwait(false);
        Assert(saved.Succeeded, $"a settings object with the mini fields set saves, got {saved.IssueCode}");
        var writtenJson = await File.ReadAllTextAsync(paths.SettingsFile).ConfigureAwait(false);
        Assert(writtenJson.Contains("\"startInMiniMode\": true", StringComparison.Ordinal) &&
               writtenJson.Contains("\"miniCardScalePercent\": 120", StringComparison.Ordinal) &&
               writtenJson.Contains("\"ctrlPeekFullCard\": true", StringComparison.Ordinal) &&
               writtenJson.Contains("\"fullWidth\": 940", StringComparison.Ordinal) &&
               writtenJson.Contains("\"fullHeight\": 680", StringComparison.Ordinal) &&
               writtenJson.Contains("\"miniWidth\": 470", StringComparison.Ordinal) &&
               writtenJson.Contains("\"miniHeight\": 330", StringComparison.Ordinal),
            "the new fields are stored under their camelCase JSON names");
        var reloaded = await loadStore.LoadAsync().ConfigureAwait(false);
        var reloadedCard = reloaded.Settings.Layout.Cards.Single(card => card.ProviderId == ProviderCatalog.Codex);
        Assert(reloaded.IssueCode is null && reloaded.Settings.StartInMiniMode && reloaded.Settings.MiniCardScalePercent == 120 &&
               reloaded.Settings.CtrlPeekFullCard &&
               reloadedCard.FullWidth == 940 && reloadedCard.FullHeight == 680 &&
               reloadedCard.MiniWidth == 470 && reloadedCard.MiniHeight == 330,
            "StartInMiniMode, MiniCardScalePercent, CtrlPeekFullCard, and a card's FullWidth/FullHeight and MiniWidth/MiniHeight all round-trip");

        // An explicit opt-out is authoritative across load, Clone, save and reload.
        var falseJson = $"{{\"schemaVersion\":2,\"revision\":{reloaded.Settings.Revision},\"enabledProviderIds\":[\"codex\"],\"ctrlPeekFullCard\":false}}";
        await File.WriteAllTextAsync(paths.SettingsFile, falseJson).ConfigureAwait(false);
        var explicitFalse = await loadStore.LoadAsync().ConfigureAwait(false);
        Assert(explicitFalse.IssueCode is null && !explicitFalse.Settings.CtrlPeekFullCard,
            "an explicit saved false remains disabled on load");
        var falseClone = explicitFalse.Settings.Clone();
        Assert(!falseClone.CtrlPeekFullCard, "Clone preserves an explicit false");
        var falseSave = await loadStore.SaveAsync(falseClone).ConfigureAwait(false);
        Assert(falseSave.Succeeded, "an explicit false saves");
        var falseWrittenJson = await File.ReadAllTextAsync(paths.SettingsFile).ConfigureAwait(false);
        Assert(falseWrittenJson.Contains("\"ctrlPeekFullCard\": false", StringComparison.Ordinal),
            "an explicit false is stored under its camelCase JSON name");
        var falseReloaded = await loadStore.LoadAsync().ConfigureAwait(false);
        Assert(falseReloaded.IssueCode is null && !falseReloaded.Settings.CtrlPeekFullCard,
            "an explicit false round-trips unchanged");

        // An off-step mini scale is normalized and logged the same way the full scale is (FullCardScaleAsync
        // above proves the full-scale case in detail; this proves the mini scale gets the identical treatment).
        var revision = falseReloaded.Settings.Revision;
        await File.WriteAllTextAsync(paths.SettingsFile,
            $"{{\"schemaVersion\":2,\"revision\":{revision},\"enabledProviderIds\":[\"codex\"],\"miniCardScalePercent\":133}}").ConfigureAwait(false);
        var offStepSink = new RecordingSink();
        var offStepLoad = await new SettingsStore(paths, offStepSink).LoadAsync().ConfigureAwait(false);
        Assert(offStepLoad.IssueCode is null && offStepLoad.Settings.MiniCardScalePercent == 130,
            $"a mini scale of 133 is normalized to 130, never a reason to discard the file (got {offStepLoad.IssueCode})");
        Assert(offStepSink.Events.Any(e => e.Code == "settings_mini_card_scale_normalized" && e.Status == "load,from_133_to_130"),
            "the mini scale normalization is logged the same way the full scale's is");

        var saveSink = new RecordingSink();
        var saveStore = new SettingsStore(paths, saveSink);
        var current = await saveStore.LoadAsync().ConfigureAwait(false);
        var offStepSave = current.Settings.Clone();
        offStepSave.MiniCardScalePercent = 144;
        var offStepSaveResult = await saveStore.SaveAsync(offStepSave).ConfigureAwait(false);
        Assert(offStepSaveResult.Succeeded && offStepSave.MiniCardScalePercent == 140, "an off-step mini scale save is normalized");
        Assert(saveSink.Events.Any(e => e.Code == "settings_mini_card_scale_normalized" && e.Status == "save,from_144_to_140"),
            "the save-time mini scale normalization is logged");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    private sealed class FilePaths : IUserPathProvider
    {
        private readonly UserPaths _paths;

        public FilePaths(string directory)
        {
            System.IO.Directory.CreateDirectory(directory);
            Folder = directory;
            SettingsFile = Path.Combine(directory, "settings.json");
            _paths = new UserPaths(SettingsFile, directory, directory, directory, directory);
        }

        public string Folder { get; }
        public string SettingsFile { get; }
        public UserPaths Get() => _paths;
    }

    private sealed class RecordingSink : IDiagnosticSink
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public void Record(DiagnosticEvent diagnostic) => Events.Add(diagnostic);
    }
}

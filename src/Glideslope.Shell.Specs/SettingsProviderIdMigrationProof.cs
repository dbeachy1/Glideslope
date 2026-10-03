using System.Text.Json;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

internal static class SettingsProviderIdMigrationProof
{
    private const string LegacyId = "antigravity";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task RunAsync(string tempRoot)
    {
        await OldOnlyAndRestart(tempRoot).ConfigureAwait(false);
        await V1WithoutLegacyIdMigratesSchema(tempRoot).ConfigureAwait(false);
        await NewOnlyIsUnchanged(tempRoot).ConfigureAwait(false);
        await BothIdsPreferGemini(tempRoot).ConfigureAwait(false);
        await MigratedCycleDetachesOnlyGemini(tempRoot).ConfigureAwait(false);
        await MigratedCollisionDetachesOnlyGemini(tempRoot).ConfigureAwait(false);
        await MalformedGeminiLayoutIsDetached(tempRoot).ConfigureAwait(false);
        await FailedAtomicMigrationCanRetry(tempRoot).ConfigureAwait(false);
        await NewerSchemaWithLegacyIdIsUntouched(tempRoot).ConfigureAwait(false);
        await CurrentSchemaWithLegacyIdIsUntouched(tempRoot).ConfigureAwait(false);
    }

    public static async Task<int> RunStandaloneAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"Glideslope.SettingsProviderIdMigrationProof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            await RunAsync(tempRoot).ConfigureAwait(false);
            Console.WriteLine("settings_provider_id_migration_proof=passed");
            return 0;
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            if (Directory.Exists(tempRoot)) throw new IOException("owned_settings_migration_temp_root_survived_cleanup");
        }
    }

    private static async Task OldOnlyAndRestart(string root)
    {
        var file = Path.Combine(root, "migration-old-only", "settings.json");
        var settings = CreateSettings(
            [ProviderCatalog.Codex, LegacyId, ProviderCatalog.Claude],
            [Card(ProviderCatalog.Codex, 880, "monitor-a", 0.2, 0.3, 0),
             Card(ProviderCatalog.Claude, 760, "monitor-b", 0.6, 0.7, 1),
             Card(LegacyId, 810, "monitor-c", 0.4, 0.5, 2)],
            [Edge(ProviderCatalog.Claude, LegacyId, CardDockSide.Bottom, 14),
             Edge(ProviderCatalog.Codex, ProviderCatalog.Claude, CardDockSide.Right, 11)]);
        settings.Revision = 18;
        await WriteAsync(file, settings).ConfigureAwait(false);

        var store = new SettingsStore(new Paths(file));
        var migrated = await store.LoadAsync().ConfigureAwait(false);
        Assert(migrated.WasMigrated && migrated.IssueCode is null, "legacy-only settings should migrate and persist");
        Assert(migrated.Settings.Revision == 19, "migration should advance exactly one settings revision");
        Assert(migrated.Settings.SchemaVersion == 2 && AppSettings.CurrentSchemaVersion == 2 && 2 > 1,
            "the v1 migration should persist schema v2 so older v1 readers reject it");
        Assert(migrated.Settings.EnabledProviderIds.SequenceEqual([ProviderCatalog.Codex, ProviderCatalog.Gemini, ProviderCatalog.Claude]),
            "legacy enabled order should be retained at the replacement provider");
        Assert(migrated.Settings.ThemeMode == "dark" && migrated.Settings.AlwaysOnTop &&
               migrated.Settings.SnapToScreenEdge && !migrated.Settings.StartAtSignIn &&
               migrated.Settings.StartupChoiceCompleted && migrated.Settings.RefreshMinutes == 9 &&
               migrated.Settings.RetentionDays == 90,
            "non-provider settings should survive migration");
        var cards = migrated.Settings.Layout.Cards.ToDictionary(card => card.ProviderId, StringComparer.Ordinal);
        Assert(cards[ProviderCatalog.Codex].Width == 880 && cards[ProviderCatalog.Claude].MonitorId == "monitor-b",
            "unaffected card geometry should survive migration");
        Assert(cards[ProviderCatalog.Gemini].Width == 810 && cards[ProviderCatalog.Gemini].MonitorId == "monitor-c" &&
               cards[ProviderCatalog.Gemini].AnchorX == 0.4 && cards[ProviderCatalog.Gemini].GroupOrder == 2,
            "legacy Gemini placement should retain its geometry and group order");
        Assert(migrated.Settings.Layout.Edges.Any(edge =>
                edge.FirstProviderId == ProviderCatalog.Claude && edge.SecondProviderId == ProviderCatalog.Gemini &&
                edge.FirstSide == CardDockSide.Bottom && edge.Gap == 14),
            "legacy-only docking edges should be renamed with their orientation and gap");
        var persisted = await File.ReadAllTextAsync(file).ConfigureAwait(false);
        Assert(!persisted.Contains(LegacyId, StringComparison.Ordinal), "persisted settings should contain no legacy provider ID");

        var stableBytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        var restarted = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
        Assert(!restarted.WasMigrated && restarted.Settings.Revision == 19,
            "a restart should recognize the committed migration without another revision");
        Assert((await File.ReadAllBytesAsync(file).ConfigureAwait(false)).SequenceEqual(stableBytes),
            "a restart should not rewrite already migrated settings");
    }

    private static async Task V1WithoutLegacyIdMigratesSchema(string root)
    {
        var file = Path.Combine(root, "migration-v1-current-providers", "settings.json");
        var settings = CreateSettings(
            [ProviderCatalog.Codex, ProviderCatalog.Claude],
            [Card(ProviderCatalog.Codex, 880, "monitor-a", 0.2, 0.3, 0),
             Card(ProviderCatalog.Claude, 760, "monitor-b", 0.6, 0.7, 1)],
            [Edge(ProviderCatalog.Codex, ProviderCatalog.Claude, CardDockSide.Right, 11)]);
        settings.Revision = 12;
        await WriteAsync(file, settings).ConfigureAwait(false);

        var loaded = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
        Assert(loaded.WasMigrated && loaded.IssueCode is null,
            "v1 files without the legacy provider should still advance schema without Gemini recovery");
        Assert(loaded.Settings.SchemaVersion == 2 && loaded.Settings.Revision == 13 &&
               loaded.Settings.EnabledProviderIds.SequenceEqual([ProviderCatalog.Codex, ProviderCatalog.Claude]) &&
               loaded.Settings.Layout.Edges.Single().SecondProviderId == ProviderCatalog.Claude,
            "a v1 Codex/Claude-only file should retain its values while advancing one revision");
    }

    private static async Task NewOnlyIsUnchanged(string root)
    {
        var file = Path.Combine(root, "migration-new-only", "settings.json");
        var settings = CreateSettings(
            [ProviderCatalog.Codex, ProviderCatalog.Gemini, ProviderCatalog.Claude],
            [Card(ProviderCatalog.Codex, 880, "monitor-a", 0.2, 0.3, 0),
             Card(ProviderCatalog.Claude, 760, "monitor-b", 0.6, 0.7, 1),
             Card(ProviderCatalog.Gemini, 810, "monitor-c", 0.4, 0.5, 2)],
            [Edge(ProviderCatalog.Claude, ProviderCatalog.Gemini, CardDockSide.Bottom, 14)]);
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        settings.Revision = 7;
        await WriteAsync(file, settings).ConfigureAwait(false);
        var original = await File.ReadAllBytesAsync(file).ConfigureAwait(false);

        var loaded = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
        Assert(!loaded.WasMigrated && loaded.Settings.Revision == 7,
            "new-only settings should not be treated as a migration");
        Assert((await File.ReadAllBytesAsync(file).ConfigureAwait(false)).SequenceEqual(original),
            "new-only settings should remain byte-for-byte unchanged");
    }

    private static async Task BothIdsPreferGemini(string root)
    {
        var file = Path.Combine(root, "migration-both-ids", "settings.json");
        var settings = CreateSettings(
            [ProviderCatalog.Codex, LegacyId, ProviderCatalog.Claude, ProviderCatalog.Gemini],
            [Card(ProviderCatalog.Codex, 880, "monitor-a", 0.2, 0.3, 0),
             Card(ProviderCatalog.Claude, 760, "monitor-b", 0.6, 0.7, 1),
             Card(LegacyId, 810, "old-monitor", 0.4, 0.5, 2),
             Card(ProviderCatalog.Gemini, 790, "new-monitor", 0.45, 0.55, 3)],
            [Edge(ProviderCatalog.Codex, ProviderCatalog.Claude, CardDockSide.Right, 11),
             Edge(ProviderCatalog.Codex, LegacyId, CardDockSide.Bottom, 14),
             Edge(ProviderCatalog.Codex, ProviderCatalog.Gemini, CardDockSide.Bottom, 20),
             Edge(ProviderCatalog.Gemini, ProviderCatalog.Codex, CardDockSide.Top, 99)]);
        await WriteAsync(file, settings).ConfigureAwait(false);

        var loaded = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
        Assert(loaded.WasMigrated && loaded.IssueCode is null, "dual-ID settings should migrate");
        Assert(loaded.Settings.EnabledProviderIds.SequenceEqual(
                [ProviderCatalog.Codex, ProviderCatalog.Claude, ProviderCatalog.Gemini]),
            "Gemini ordering should take precedence and both enabled entries should collapse to one");
        var geminiCard = loaded.Settings.Layout.Cards.Single(card => card.ProviderId == ProviderCatalog.Gemini);
        Assert(geminiCard.Width == 790 && geminiCard.MonitorId == "new-monitor" &&
               geminiCard.AnchorX == 0.45 && geminiCard.GroupOrder == 3,
            "the existing Gemini card geometry should take precedence over legacy geometry");
        Assert(loaded.Settings.Layout.Edges.Count == 2 &&
               loaded.Settings.Layout.Edges.Any(edge => edge.FirstProviderId == ProviderCatalog.Codex &&
                   edge.SecondProviderId == ProviderCatalog.Claude) &&
               loaded.Settings.Layout.Edges.Count(edge => edge.FirstProviderId == ProviderCatalog.Codex &&
                   edge.SecondProviderId == ProviderCatalog.Gemini) == 1 &&
               loaded.Settings.Layout.Edges.All(edge => edge.FirstProviderId != LegacyId && edge.SecondProviderId != LegacyId),
            "only new-ID docking edges should survive, with duplicate relationships removed");
    }

    private static async Task MalformedGeminiLayoutIsDetached(string root)
    {
        var file = Path.Combine(root, "migration-malformed-layout", "settings.json");
        var settings = CreateSettings(
            [ProviderCatalog.Codex, ProviderCatalog.Claude, LegacyId],
            [Card(ProviderCatalog.Codex, 880, "monitor-a", 0.2, 0.3, 0),
             Card(ProviderCatalog.Claude, 760, "monitor-b", 0.6, 0.7, 1),
             Card(LegacyId, 0, "monitor-c", 0.4, 0.5, 2)],
            [Edge(ProviderCatalog.Codex, ProviderCatalog.Claude, CardDockSide.Right, 11),
             Edge(ProviderCatalog.Claude, LegacyId, CardDockSide.Bottom, 14),
             Edge(LegacyId, ProviderCatalog.Codex, CardDockSide.Left, 16)]);
        await WriteAsync(file, settings).ConfigureAwait(false);
        var diagnostics = new RecordingDiagnosticSink();

        var loaded = await new SettingsStore(new Paths(file), diagnostics).LoadAsync().ConfigureAwait(false);
        Assert(loaded.WasMigrated && loaded.IssueCode is null, "malformed Gemini geometry should not discard the settings file");
        Assert(loaded.Settings.EnabledSet.Contains(ProviderCatalog.Gemini),
            "detaching invalid Gemini placement should keep the Gemini provider enabled");
        Assert(loaded.Settings.Layout.Cards.All(card => card.ProviderId != ProviderCatalog.Gemini),
            "invalid Gemini geometry should be removed so startup can seed a fresh placement");
        Assert(loaded.Settings.Layout.Cards.Any(card => card.ProviderId == ProviderCatalog.Codex && card.Width == 880) &&
               loaded.Settings.Layout.Cards.Any(card => card.ProviderId == ProviderCatalog.Claude && card.MonitorId == "monitor-b"),
            "valid non-Gemini placements should survive malformed Gemini layout recovery");
        Assert(loaded.Settings.Layout.Edges.Count == 1 &&
               loaded.Settings.Layout.Edges.Single().FirstProviderId == ProviderCatalog.Codex &&
               loaded.Settings.Layout.Edges.Single().SecondProviderId == ProviderCatalog.Claude,
            "detaching Gemini should preserve unrelated docking edges and remove its incident edges");
        Assert(diagnostics.Events.Any(item => item.Code == "settings_gemini_layout_detached"),
            "malformed Gemini layout recovery should emit a sanitized warning");
    }

    private static async Task MigratedCollisionDetachesOnlyGemini(string root)
    {
        var file = Path.Combine(root, "migration-colliding-dock", "settings.json");
        var settings = CreateSettings(
            [ProviderCatalog.Codex, ProviderCatalog.Claude, LegacyId],
            [Card(ProviderCatalog.Codex, 700, "monitor-a", 0.3, 0.3, 0),
             Card(ProviderCatalog.Claude, 700, "monitor-a", 0.3, 0.3, 1),
             Card(LegacyId, 700, "monitor-a", 0.3, 0.3, 2)],
            [Edge(ProviderCatalog.Codex, ProviderCatalog.Claude, CardDockSide.Bottom, 10),
             Edge(ProviderCatalog.Codex, LegacyId, CardDockSide.Bottom, 10)]);
        await WriteAsync(file, settings).ConfigureAwait(false);

        var loaded = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
        Assert(loaded.WasMigrated && loaded.IssueCode is null, "valid legacy geometry should migrate before platform placement");
        var areas = new Dictionary<string, LogicalWorkArea>(StringComparer.Ordinal)
        {
            ["monitor-a"] = new("monitor-a", new LogicalRect(0, 0, 2400, 1800), isPrimary: true),
        };
        var restarted = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
        Assert(!restarted.WasMigrated && restarted.Settings.SchemaVersion == 2,
            "a process restart after the atomic migration commit should load current schema without an ephemeral migration flag");
        var recovered = WindowCoordinator.RecoverGeminiLayout(
            restarted.Settings.Layout, restarted.Settings.EnabledProviderIds, areas, "monitor-a", new LogicalSize(620, 440));
        Assert(recovered.DetachedGemini && recovered.Result.Succeeded && recovered.Result.ProposedSettings is not null,
            "startup recovery should detach Gemini after restart when migrated geometry collides");
        var recoveredSettings = recovered.Result.ProposedSettings ?? throw new InvalidOperationException("recovered_layout_missing_settings");
        Assert(recoveredSettings.Edges.Count == 1 &&
               recoveredSettings.Edges.Single().FirstProviderId == ProviderCatalog.Codex &&
               recoveredSettings.Edges.Single().SecondProviderId == ProviderCatalog.Claude,
            "collision recovery should preserve unrelated docking relationships");
        var placements = recoveredSettings.Cards.ToDictionary(card => card.ProviderId, StringComparer.Ordinal);
        Assert(placements[ProviderCatalog.Codex].Width == 700 &&
               placements[ProviderCatalog.Claude].Width == 700 &&
               placements[ProviderCatalog.Gemini].Width == 700,
            "collision recovery should preserve all valid per-card geometry");
    }

    private static async Task MigratedCycleDetachesOnlyGemini(string root)
    {
        var file = Path.Combine(root, "migration-cyclic-dock", "settings.json");
        var settings = CreateSettings(
            [ProviderCatalog.Codex, ProviderCatalog.Claude, LegacyId],
            [Card(ProviderCatalog.Codex, 880, "monitor-a", 0.2, 0.3, 0),
             Card(ProviderCatalog.Claude, 760, "monitor-b", 0.6, 0.7, 1),
             Card(LegacyId, 810, "monitor-c", 0.4, 0.5, 2)],
            [Edge(ProviderCatalog.Codex, ProviderCatalog.Claude, CardDockSide.Right, 11),
             Edge(ProviderCatalog.Claude, LegacyId, CardDockSide.Bottom, 14),
             Edge(LegacyId, ProviderCatalog.Codex, CardDockSide.Left, 16)]);
        await WriteAsync(file, settings).ConfigureAwait(false);
        var diagnostics = new RecordingDiagnosticSink();

        var loaded = await new SettingsStore(new Paths(file), diagnostics).LoadAsync().ConfigureAwait(false);
        Assert(loaded.WasMigrated && loaded.IssueCode is null,
            "a cycle introduced by legacy-edge remapping should recover without discarding settings");
        Assert(loaded.Settings.Layout.Edges.Count == 1 &&
               loaded.Settings.Layout.Edges.Single().FirstProviderId == ProviderCatalog.Codex &&
               loaded.Settings.Layout.Edges.Single().SecondProviderId == ProviderCatalog.Claude,
            "cycle recovery should remove only Gemini's docking edges");
        Assert(loaded.Settings.Layout.Cards.Any(card => card.ProviderId == ProviderCatalog.Gemini && card.Width == 810) &&
               loaded.Settings.Layout.Cards.Any(card => card.ProviderId == ProviderCatalog.Codex && card.Width == 880) &&
               loaded.Settings.Layout.Cards.Any(card => card.ProviderId == ProviderCatalog.Claude && card.Width == 760),
            "cycle recovery should preserve Gemini and non-Gemini card geometry");
        Assert(diagnostics.Events.Any(item => item.Code == "settings_gemini_layout_detached"),
            "cycle recovery should emit a sanitized migration warning");
    }

    private static async Task FailedAtomicMigrationCanRetry(string root)
    {
        var directory = Path.Combine(root, "migration-failed-write");
        var file = Path.Combine(directory, "settings.json");
        var settings = CreateSettings(
            [ProviderCatalog.Codex, LegacyId, ProviderCatalog.Claude],
            [Card(ProviderCatalog.Codex, 880, "monitor-a", 0.2, 0.3, 0),
             Card(ProviderCatalog.Claude, 760, "monitor-b", 0.6, 0.7, 1),
             Card(LegacyId, 810, "monitor-c", 0.4, 0.5, 2)],
            [Edge(ProviderCatalog.Claude, LegacyId, CardDockSide.Bottom, 14)]);
        settings.Revision = 31;
        await WriteAsync(file, settings).ConfigureAwait(false);
        var original = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        var attributes = File.GetAttributes(file);
        var unixMode = OperatingSystem.IsWindows() ? (UnixFileMode?)null : File.GetUnixFileMode(directory);
        try
        {
            if (OperatingSystem.IsWindows())
                File.SetAttributes(file, attributes | FileAttributes.ReadOnly);
            else
                File.SetUnixFileMode(directory, unixMode!.Value & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));

            var failed = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
            Assert(failed.WasMigrated && failed.IssueCode == "settings_save_io_error",
                "failed atomic replacement should leave the usable in-memory mapping and report persistence failure");
            Assert((await File.ReadAllBytesAsync(file).ConfigureAwait(false)).SequenceEqual(original),
                "a failed atomic replacement should preserve the original legacy file bytes");
            Assert(!Directory.EnumerateFiles(directory, "settings.json.*.tmp").Any(),
                "a failed atomic replacement should clean its owned temporary file");
        }
        finally
        {
            if (OperatingSystem.IsWindows())
                File.SetAttributes(file, attributes);
            else
                File.SetUnixFileMode(directory, unixMode!.Value);
        }

        var retried = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
        Assert(retried.WasMigrated && retried.IssueCode is null && retried.Settings.Revision == 32,
            "a later startup should safely retry and commit the migration");
        var stableBytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        var restarted = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
        Assert(!restarted.WasMigrated && restarted.Settings.Revision == 32 &&
               (await File.ReadAllBytesAsync(file).ConfigureAwait(false)).SequenceEqual(stableBytes),
            "the successful retry should be idempotent after restart");
    }

    private static async Task NewerSchemaWithLegacyIdIsUntouched(string root)
    {
        var file = Path.Combine(root, "migration-newer-schema", "settings.json");
        const string document = "{\"schemaVersion\":99,\"revision\":4,\"enabledProviderIds\":[\"codex\",\"antigravity\"],\"futureField\":\"preserve\"}";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, document).ConfigureAwait(false);

        var loaded = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
        Assert(!loaded.WasMigrated && loaded.IssueCode == "settings_newer_schema",
            "legacy IDs inside a newer schema should not be migrated");
        Assert(await File.ReadAllTextAsync(file).ConfigureAwait(false) == document,
            "a newer schema file should remain byte-for-byte unchanged");
    }

    private static async Task CurrentSchemaWithLegacyIdIsUntouched(string root)
    {
        var file = Path.Combine(root, "migration-v2-legacy-id", "settings.json");
        const string document = "{\"schemaVersion\":2,\"revision\":5,\"enabledProviderIds\":[\"codex\",\"antigravity\"]}";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, document).ConfigureAwait(false);

        var loaded = await new SettingsStore(new Paths(file)).LoadAsync().ConfigureAwait(false);
        Assert(!loaded.WasMigrated && loaded.IssueCode == "unknown_provider",
            "a current schema file should not be rewritten through the v1 migration");
        Assert(await File.ReadAllTextAsync(file).ConfigureAwait(false) == document,
            "a current schema file should remain byte-for-byte unchanged");
    }

    private static AppSettings CreateSettings(
        List<string> enabled,
        List<CardLayoutSettings> cards,
        List<DockingEdgeSettings> edges) => new()
    {
        SchemaVersion = 1,
        EnabledProviderIds = enabled,
        ThemeMode = "dark",
        AlwaysOnTop = true,
        SnapToScreenEdge = true,
        StartAtSignIn = false,
        StartupChoiceCompleted = true,
        RefreshMinutes = 9,
        RetentionDays = 90,
        Layout = new LayoutSettings { CardWidth = 900, CardHeight = 650, Cards = cards, Edges = edges },
    };

    private static CardLayoutSettings Card(string id, double width, string monitor, double x, double y, int order) => new()
    {
        ProviderId = id,
        Width = width,
        Height = 550,
        MonitorId = monitor,
        AnchorX = x,
        AnchorY = y,
        GroupOrder = order,
    };

    private static DockingEdgeSettings Edge(string first, string second, CardDockSide side, double gap) => new()
    {
        FirstProviderId = first,
        SecondProviderId = second,
        FirstSide = side,
        Gap = gap,
    };

    private static async Task WriteAsync(string file, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await using var stream = File.Create(file);
        await JsonSerializer.SerializeAsync(stream, settings, Json).ConfigureAwait(false);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Paths(string settingsFile) : IUserPathProvider
    {
        public UserPaths Get()
        {
            var root = Path.GetDirectoryName(settingsFile)!;
            return new UserPaths(settingsFile, root, root, root, root);
        }
    }

    private sealed class RecordingDiagnosticSink : IDiagnosticSink
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public void Record(DiagnosticEvent diagnostic) => Events.Add(diagnostic);
    }
}

using Glideslope.Domain;
using Glideslope.Storage;
using Microsoft.Data.Sqlite;

namespace Glideslope.Storage.Specs;

internal static class Program
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static async Task<int> Main()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"Glideslope.Storage.Specs-{Guid.NewGuid():N}");
        var marker = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(tempRoot);
        var markerPath = Path.Combine(tempRoot, ".owner");
        await File.WriteAllTextAsync(markerPath, marker);
        try
        {
            await PersistsOrderedScopedUtcObservationsAsync(tempRoot);
            await PrunesExpiredRowsOnReopenAsync(tempRoot);
            await StorageWriteFailureIsBoundedAsync(tempRoot);
            await WriteFailureRecoversAfterCooldownAsync(tempRoot);
            await RejectsFutureSchemaAsync(tempRoot);
            await ZeroLengthAccountScopeKeyIsRegeneratedAsync(tempRoot);
            await ListsObservedWindowsOfOneBucketInOrderAsync(tempRoot);
            await RepairsJitteredAndPhantomWindowsOnOpenAsync(tempRoot);
            await MigratesClaudeSemanticsFromSchemaOneAsync(tempRoot);
            await ReadFailureDoesNotStopWritesAsync(tempRoot);
            await ReadFailureKeepsPendingWriteCooldownAsync(tempRoot);
            await PruneFailureDoesNotStopWritesAsync(tempRoot);
            await PruneSuccessEndsWriteCooldownAsync(tempRoot);
            await TryListWindowsReportsAvailabilityAsync(tempRoot);
            CheckpointFoldsWalIntoDatabase(tempRoot);
            CheckpointRefusesWhatItCannotFold(tempRoot);
            CheckpointKeepsWhatSqliteDiscards(tempRoot);
            Console.WriteLine("Glideslope storage specs passed.");
            return 0;
        }
        finally
        {
            var fullTempRoot = Path.GetFullPath(tempRoot);
            var fullSystemTemp = Path.GetFullPath(Path.GetTempPath());
            if (!fullTempRoot.StartsWith(fullSystemTemp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullTempRoot).StartsWith("Glideslope.Storage.Specs-", StringComparison.Ordinal) ||
                await File.ReadAllTextAsync(markerPath) != marker)
                throw new IOException("Refusing to remove a storage spec directory without its ownership marker.");
            Directory.Delete(fullTempRoot, recursive: true);
            if (Directory.Exists(fullTempRoot)) throw new IOException("Storage spec temp directory was not removed.");
        }
    }

    private static async Task PersistsOrderedScopedUtcObservationsAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "series", "history.sqlite");
        var start = Epoch.AddDays(-3);
        var reset = Epoch.AddDays(4);
        var firstAtOffset = Epoch.AddHours(1).ToOffset(TimeSpan.FromHours(2));
        var firstWindow = Window("scope-a", start, reset);
        var otherAccountWindow = Window("scope-b", start, reset);
        var expectedFirstUtc = firstAtOffset.ToUniversalTime();

        await using (var store = await SqliteUsageHistoryStore.OpenAsync(databasePath))
        {
            Assert(store.Health.IsAvailable, "new SQLite history starts available");
            Assert(store.TryAppend(new UsageObservation(firstWindow, firstAtOffset, 0.8)), "first observation queues");
            Assert(store.TryAppend(new UsageObservation(firstWindow, Epoch.AddHours(3), 0.6)), "second observation queues");
            Assert(store.TryAppend(new UsageObservation(firstWindow, firstAtOffset, 0.2)), "duplicate key is handled by SQLite uniqueness");
            Assert(store.TryAppend(new UsageObservation(otherAccountWindow, firstAtOffset, 0.3)), "other account observation queues");
            await store.FlushAsync();

            var scoped = await store.QueryWindowAsync(firstWindow, start, reset, maxSamples: 20_000);
            Assert(scoped.IsAvailable && scoped.Samples.Length == 2, "query returns unique observations for one account/window");
            Assert(scoped.Samples[0].ObservedAtUtc == expectedFirstUtc, "offset input is stored and returned as UTC");
            Assert(scoped.Samples[0].RemainingFraction == 0.8 && scoped.Samples[1].RemainingFraction == 0.6,
                "query orders observations and retains normalized remaining fractions");

            var limited = await store.QueryWindowAsync(firstWindow, start, reset, maxSamples: 1);
            Assert(limited.Samples.Length == 1 && limited.Samples[0].ObservedAtUtc == expectedFirstUtc,
                "query applies the requested bound in chronological order");
            var isolated = await store.QueryWindowAsync(otherAccountWindow, start, reset, maxSamples: 20_000);
            Assert(isolated.Samples.Length == 1 && isolated.Samples[0].RemainingFraction == 0.3,
                "same reset and bucket cannot join another account scope");
        }

        await using (var reopened = await SqliteUsageHistoryStore.OpenAsync(databasePath))
        {
            var restored = await reopened.QueryWindowAsync(firstWindow, start, reset, maxSamples: 20_000);
            Assert(restored.Samples.Length == 2, "accepted observations survive store disposal and reopen");
        }
    }

    private static async Task PrunesExpiredRowsOnReopenAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "retention", "history.sqlite");
        var oldReset = Epoch.AddDays(-8);
        var oldStart = oldReset.AddDays(-7);
        var oldObserved = oldReset.AddDays(-1);
        var oldWindow = Window("scope-old", oldStart, oldReset);
        await using (var store = await SqliteUsageHistoryStore.OpenAsync(databasePath, retentionDays: 60))
        {
            Assert(store.TryAppend(new UsageObservation(oldWindow, oldObserved, 0.45)), "old sample queues before retention sweep");
            await store.FlushAsync();
            await store.SetRetentionDaysAsync(7);
            var pruned = await store.QueryWindowAsync(oldWindow, oldStart, oldReset, maxSamples: 20_000);
            Assert(pruned.Samples.IsEmpty, "runtime retention reduction prunes old samples in a serialized transaction");
        }

        await using (var reopened = await SqliteUsageHistoryStore.OpenAsync(databasePath, retentionDays: 7))
        {
            var expired = await reopened.QueryWindowAsync(oldWindow, oldStart, oldReset, maxSamples: 20_000);
            Assert(expired.Samples.IsEmpty, "reopen prunes samples older than the selected retention period");
        }
    }

    private static async Task RejectsFutureSchemaAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "future", "history.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99;";
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await SqliteUsageHistoryStore.OpenAsync(databasePath);
            throw new InvalidOperationException("A future database schema must not be overwritten.");
        }
        catch (InvalidDataException)
        {
            await using var reopened = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await reopened.OpenAsync();
            await using var verify = reopened.CreateCommand();
            verify.CommandText = "PRAGMA user_version;";
            Assert(Convert.ToInt32(await verify.ExecuteScalarAsync()) == 99,
                "failed migration released its SQLite connection without changing the newer schema");
        }
    }

    private static async Task StorageWriteFailureIsBoundedAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "failed-write", "history.sqlite");
        var window = Window("scope-failure", Epoch.AddDays(-1), Epoch.AddDays(6));
        await using (var store = await SqliteUsageHistoryStore.OpenAsync(databasePath))
        {
            await using (var sabotage = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            }.ToString()))
            {
                await sabotage.OpenAsync();
                await using var drop = sabotage.CreateCommand();
                drop.CommandText = "DROP TABLE usage_observations;";
                await drop.ExecuteNonQueryAsync();
            }

            Assert(store.TryAppend(new UsageObservation(window, Epoch, 0.5)), "one observation enters the bounded queue before the write failure");
            await store.FlushAsync();
            Assert(!store.Health.IsAvailable && store.Health.SafeErrorCode == "history_write_failed",
                "SQLite write failure publishes a sanitized unavailable state");
            Assert(!store.TryAppend(new UsageObservation(window, Epoch.AddMinutes(1), 0.4)),
                "unavailable storage drops new samples without building a retry queue");
        }
    }

    // A failed write schedules a 30-second cooldown; a later successful write records new data and restores health.
    private static async Task WriteFailureRecoversAfterCooldownAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "write-recovery", "history.sqlite");
        var window = Window("scope-recovery", Epoch.AddDays(-1), Epoch.AddDays(6));
        var clock = new ManualTimeProvider(Epoch);
        var diagnostics = new RecordingStorageDiagnostics();
        await using var store = await SqliteUsageHistoryStore.OpenAsync(
            databasePath, SqliteUsageHistoryStore.DefaultRetentionDays, clock, diagnostics.Record);

        await ExecuteOnSideConnectionAsync(databasePath, "DROP TABLE usage_observations;");
        Assert(store.TryAppend(new UsageObservation(window, Epoch, 0.5)), "recovery: the first observation is queued");
        await store.FlushAsync();
        Assert(!store.Health.IsAvailable && store.Health.SafeErrorCode == "history_write_failed",
            "recovery: the failed write publishes history_write_failed");
        Assert(diagnostics.Any("history_write_retry_scheduled", "history_write_failed_SqliteException_code_1_retry_in_30s"),
            "recovery: the failure logs history_write_retry_scheduled with the exception type and cooldown");

        clock.Advance(SqliteUsageHistoryStore.WriteRetryCooldown - TimeSpan.FromSeconds(1));
        Assert(!store.TryAppend(new UsageObservation(window, Epoch.AddSeconds(30), 0.45)),
            "recovery: samples are still dropped before the cooldown ends");

        await ExecuteOnSideConnectionAsync(databasePath, """
            CREATE TABLE usage_observations (
                window_id INTEGER NOT NULL REFERENCES usage_windows(id) ON DELETE CASCADE,
                observed_utc_ticks INTEGER NOT NULL,
                remaining_fraction REAL NOT NULL CHECK(remaining_fraction >= 0 AND remaining_fraction <= 1),
                PRIMARY KEY(window_id, observed_utc_ticks)
            );
            CREATE INDEX usage_observations_by_time ON usage_observations(observed_utc_ticks);
            """);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert(store.TryAppend(new UsageObservation(window, Epoch.AddMinutes(1), 0.4)),
            "recovery: after the cooldown a new observation is accepted");
        await store.FlushAsync();
        Assert(store.Health.IsAvailable && store.Health.SafeErrorCode is null,
            "recovery: the successful write returns health to available");
        Assert(diagnostics.Any("history_write_recovered", "after_history_write_failed"),
            "recovery: the recovery is logged as history_write_recovered");

        var stored = await store.QueryWindowAsync(window, window.NominalStartUtc, window.ResetAtUtc, maxSamples: 100);
        Assert(stored.IsAvailable && stored.Samples.Length == 1 &&
               stored.Samples[0].ObservedAtUtc == Epoch.AddMinutes(1) && stored.Samples[0].RemainingFraction == 0.4,
            "recovery: the second observation is recorded and the dropped ones are not");
    }

    // A zero-length account-scope.key is regenerated so future launches can keep a stable account scope.
    private static async Task ZeroLengthAccountScopeKeyIsRegeneratedAsync(string tempRoot)
    {
        var dataDirectory = Path.Combine(tempRoot, "zero-length-key");
        var identityDirectory = Path.Combine(dataDirectory, "identity");
        var keyPath = Path.Combine(identityDirectory, "account-scope.key");
        Directory.CreateDirectory(identityDirectory);
        await File.WriteAllBytesAsync(keyPath, []);

        var diagnostics = new RecordingStorageDiagnostics();
        var store = new AccountScopeKeyStore(dataDirectory, diagnostics.Record);
        var scope = await store.ResolveAsync(ProviderIds.Claude, "issuer", "subject", CancellationToken.None);
        Assert(scope.Length == 64, "zero-length key: a scope is resolved instead of failing");
        Assert(new FileInfo(keyPath).Length == 32, "zero-length key: a full 32-byte key replaces it");
        Assert(diagnostics.Any("account_scope_key_regenerated", "zero_length_replaced"),
            "zero-length key: the regeneration is logged as account_scope_key_regenerated");

        var reopened = new AccountScopeKeyStore(dataDirectory);
        Assert(await reopened.ResolveAsync(ProviderIds.Claude, "issuer", "subject", CancellationToken.None) == scope,
            "zero-length key: the regenerated key is stable across store instances");

        var freshDirectory = Path.Combine(tempRoot, "fresh-key");
        var freshDiagnostics = new RecordingStorageDiagnostics();
        var fresh = new AccountScopeKeyStore(freshDirectory, freshDiagnostics.Record);
        await fresh.ResolveAsync(ProviderIds.Claude, "issuer", "subject", CancellationToken.None);
        Assert(new FileInfo(Path.Combine(freshDirectory, "identity", "account-scope.key")).Length == 32,
            "new key: the key is published complete");
        Assert(freshDiagnostics.Any("account_scope_key_created", "published"), "new key: the creation is logged");

        Assert(!Directory.EnumerateFiles(identityDirectory, "*.tmp").Any() &&
               !Directory.EnumerateFiles(Path.Combine(freshDirectory, "identity"), "*.tmp").Any(),
            "key writes leave no temporary files behind");
    }

    // Design doc §15.1: the card's previous-week / next-week buttons browse the stored
    // windows of one bucket. Three windows for the weekly bucket (one of them with no observations, inserted
    // directly because the writer only creates a window together with its first observation), plus a window
    // for another bucket: the list holds the two observed weekly windows, oldest first, even when the newer
    // one was written first. A read failure returns an empty list, marks the store unavailable and logs
    // history_list_windows_failed.
    private static async Task ListsObservedWindowsOfOneBucketInOrderAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "list-windows", "history.sqlite");
        var olderStart = Epoch.AddDays(-14);
        var older = Window("scope-list", olderStart, olderStart.AddDays(7));
        var newer = Window("scope-list", olderStart.AddDays(7), olderStart.AddDays(14));
        var emptyStart = olderStart.AddDays(14);
        var otherBucket = new UsageWindowIdentity("scope-list", ProviderIds.Claude, "claude.weekly.fable",
            olderStart, olderStart.AddDays(7), "source.weekly");
        var diagnostics = new RecordingStorageDiagnostics();
        await using var store = await SqliteUsageHistoryStore.OpenAsync(
            databasePath, SqliteUsageHistoryStore.DefaultRetentionDays, timeProvider: null, diagnostics.Record);

        Assert(store.TryAppend(new UsageObservation(newer, newer.NominalStartUtc.AddHours(1), 0.9)), "list: the newer window's observation queues first");
        Assert(store.TryAppend(new UsageObservation(older, older.NominalStartUtc.AddHours(1), 0.7)), "list: the older window's observation queues");
        Assert(store.TryAppend(new UsageObservation(older, older.NominalStartUtc.AddHours(2), 0.6)), "list: a second older observation queues");
        Assert(store.TryAppend(new UsageObservation(otherBucket, otherBucket.NominalStartUtc.AddHours(1), 0.5)), "list: the other bucket's observation queues");
        await store.FlushAsync();
        await ExecuteOnSideConnectionAsync(databasePath, $"""
            INSERT INTO usage_windows (account_scope, provider_id, bucket_id, nominal_start_utc_ticks, reset_utc_ticks, source_semantics)
            VALUES ('scope-list', '{ProviderIds.Claude}', 'claude.weekly.seven_day', {emptyStart.UtcTicks}, {emptyStart.AddDays(7).UtcTicks}, 'source.weekly');
            """);

        var listed = await store.ListWindowsAsync("scope-list", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(listed.Length == 2, $"list: only the two observed weekly windows are listed (got {listed.Length})");
        Assert(listed[0] == older && listed[1] == newer, "list: the windows come oldest first with their full identity");
        var otherListed = await store.ListWindowsAsync("scope-list", ProviderIds.Claude, "claude.weekly.fable");
        Assert(otherListed.Length == 1 && otherListed[0] == otherBucket, "list: another bucket's window is listed only for that bucket");
        var otherScope = await store.ListWindowsAsync("scope-elsewhere", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(otherScope.IsEmpty, "list: another account scope sees none of these windows");

        await ExecuteOnSideConnectionAsync(databasePath, "DROP TABLE usage_observations;");
        var failed = await store.ListWindowsAsync("scope-list", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(failed.IsEmpty, "list: a read failure returns an empty list");
        Assert(!store.Health.IsAvailable && store.Health.SafeErrorCode == "history_read_failed",
            "list: a read failure is handled like QueryWindowAsync's (history_read_failed)");
        Assert(diagnostics.Any("history_list_windows_failed", "SqliteException_code_1"),
            "list: the read failure is logged as history_list_windows_failed with the exception type");
    }

    /// <summary>Window-interaction design §17 rule 5: on open, windows whose reset is off the
    /// minute are merged into the window with the rounded reset, and a window whose only sample is at 100 %
    /// at its own start (a "not started" phantom) is deleted. A real window with two samples is untouched.</summary>
    private static async Task RepairsJitteredAndPhantomWindowsOnOpenAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "repair-windows", "history.sqlite");
        var trueReset = Epoch.AddDays(3);
        var start = trueReset.AddDays(-7);
        var canonical = Window("scope-repair", start, trueReset);
        // Three polls of one week, each with Claude's sub-second jitter on the reset.
        var jitterA = Window("scope-repair", start.AddMilliseconds(-450), trueReset.AddMilliseconds(-450));
        var jitterB = Window("scope-repair", start.AddMilliseconds(400), trueReset.AddMilliseconds(400));
        var jitterC = Window("scope-repair", start.AddMilliseconds(-20), trueReset.AddMilliseconds(-20));
        // A Codex-style phantom: one sample at 100 % taken at the window's own start.
        var phantomStart = Epoch.AddHours(1);
        var phantom = Window("scope-repair", phantomStart, phantomStart.AddDays(7));
        // A real earlier week, on the minute, with two samples: untouched.
        var earlier = Window("scope-repair", start.AddDays(-7), start);
        // A real week whose only sample shows usage 10 s after its start: not a phantom, untouched.
        var usedStart = Epoch.AddDays(-30);
        var used = Window("scope-repair", usedStart, usedStart.AddDays(7));

        var seedDiagnostics = new RecordingStorageDiagnostics();
        await using (var store = await SqliteUsageHistoryStore.OpenAsync(
                         databasePath, SqliteUsageHistoryStore.DefaultRetentionDays, timeProvider: null, seedDiagnostics.Record))
        {
            Assert(store.TryAppend(new UsageObservation(jitterA, start.AddHours(1), 0.9)), "repair: jittered sample A queues");
            Assert(store.TryAppend(new UsageObservation(jitterB, start.AddHours(2), 0.8)), "repair: jittered sample B queues");
            Assert(store.TryAppend(new UsageObservation(jitterC, start.AddHours(3), 0.7)), "repair: jittered sample C queues");
            Assert(store.TryAppend(new UsageObservation(phantom, phantomStart.AddSeconds(1), 1.0)), "repair: the phantom sample queues");
            Assert(store.TryAppend(new UsageObservation(earlier, earlier.NominalStartUtc.AddSeconds(5), 1.0)), "repair: the earlier week's first sample queues");
            Assert(store.TryAppend(new UsageObservation(earlier, earlier.NominalStartUtc.AddHours(4), 0.6)), "repair: the earlier week's second sample queues");
            Assert(store.TryAppend(new UsageObservation(used, usedStart.AddSeconds(10), 0.95)), "repair: the used week's only sample queues");
            await store.FlushAsync();
        }
        Assert(seedDiagnostics.Any("history_windows_repaired", "none"), "repair: an empty database reports nothing to repair");

        var diagnostics = new RecordingStorageDiagnostics();
        await using var reopened = await SqliteUsageHistoryStore.OpenAsync(
            databasePath, SqliteUsageHistoryStore.DefaultRetentionDays, timeProvider: null, diagnostics.Record);
        Assert(diagnostics.Any("history_windows_repaired", "phantoms=1,merged=3"),
            "repair: reopening logs history_windows_repaired with phantoms=1,merged=3");

        var listed = await reopened.ListWindowsAsync("scope-repair", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(listed.Length == 3 && listed[0] == used && listed[1] == earlier && listed[2] == canonical,
            $"repair: only the used week, the earlier week and the one canonical week remain (got {listed.Length})");
        var usedKept = await reopened.QueryWindowAsync(used, used.NominalStartUtc, used.ResetAtUtc, 100);
        Assert(usedKept.Samples.Length == 1 && usedKept.Samples[0].RemainingFraction == 0.95,
            "repair: a single sample that shows usage near the start is not a phantom");
        var merged = await reopened.QueryWindowAsync(canonical, canonical.NominalStartUtc, canonical.ResetAtUtc, 100);
        Assert(merged.IsAvailable && merged.Samples.Length == 3 &&
               merged.Samples.Select(s => s.RemainingFraction).SequenceEqual([0.9, 0.8, 0.7]),
            "repair: the three jittered samples now sit in the canonical window in order");
        var kept = await reopened.QueryWindowAsync(earlier, earlier.NominalStartUtc, earlier.ResetAtUtc, 100);
        Assert(kept.Samples.Length == 2, "repair: a real week with two samples, one at its start, is untouched");
        var gone = await reopened.QueryWindowAsync(phantom, phantom.NominalStartUtc, phantom.ResetAtUtc, 100);
        Assert(gone.Samples.Length == 0, "repair: the phantom window and its sample are gone");

        // A new sample with the stabilized identity lands in the canonical window, not a new one.
        Assert(reopened.TryAppend(new UsageObservation(canonical, start.AddHours(4), 0.65)), "repair: a stabilized sample queues");
        await reopened.FlushAsync();
        var after = await reopened.QueryWindowAsync(canonical, canonical.NominalStartUtc, canonical.ResetAtUtc, 100);
        Assert(after.Samples.Length == 4, "repair: later samples join the canonical window");
    }

    /// <summary>Claude CLI source design §2 rule 5: a schema-1 file's Claude windows carry
    /// `anthropic.oauth.usage/...` semantics. Opening it renames them to `anthropic.claude-cli.usage/...`
    /// (samples intact, other providers untouched), logs the migration, and a schema-2 file is left alone.</summary>
    private static async Task MigratesClaudeSemanticsFromSchemaOneAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "schema-migration", "history.sqlite");
        var start = Epoch.AddDays(-2);
        var oauth = new UsageWindowIdentity("scope-migrate", ProviderIds.Claude, "claude.weekly.seven_day", start, start.AddDays(7), "anthropic.oauth.usage/seven_day");
        var cli = new UsageWindowIdentity("scope-migrate", ProviderIds.Claude, "claude.weekly.seven_day", start, start.AddDays(7), "anthropic.claude-cli.usage/seven_day");
        var codex = new UsageWindowIdentity("scope-migrate", ProviderIds.Codex, "codex.weekly", start, start.AddDays(7), "codex.weekly");
        await using (var seed = await SqliteUsageHistoryStore.OpenAsync(databasePath))
        {
            Assert(seed.TryAppend(new UsageObservation(oauth, start.AddHours(1), 0.9)), "migration: the OAuth-era sample queues");
            Assert(seed.TryAppend(new UsageObservation(oauth, start.AddHours(2), 0.8)), "migration: a second OAuth-era sample queues");
            Assert(seed.TryAppend(new UsageObservation(codex, start.AddHours(1), 0.7)), "migration: a Codex sample queues");
            await seed.FlushAsync();
        }
        Assert(await ReadScalarOnSideConnectionAsync(databasePath, "PRAGMA user_version;") == 2, "migration: a fresh file is created at schema 2");
        // Roll the file back to schema 1, as a pre-switch install would have left it.
        await ExecuteOnSideConnectionAsync(databasePath, "PRAGMA user_version=1;");

        var diagnostics = new RecordingStorageDiagnostics();
        await using (var migrated = await SqliteUsageHistoryStore.OpenAsync(
                         databasePath, SqliteUsageHistoryStore.DefaultRetentionDays, timeProvider: null, diagnostics.Record))
        {
            Assert(diagnostics.Any("history_schema_migrated", "from=1,to=2,rows=1"), "migration: the rename is logged with its row count");
            var renamed = await migrated.QueryWindowAsync(cli, cli.NominalStartUtc, cli.ResetAtUtc, 100);
            Assert(renamed.Samples.Length == 2, "migration: the samples now belong to the CLI-semantics window");
            var old = await migrated.QueryWindowAsync(oauth, oauth.NominalStartUtc, oauth.ResetAtUtc, 100);
            Assert(old.Samples.Length == 0, "migration: nothing is left under the OAuth semantics");
            var kept = await migrated.QueryWindowAsync(codex, codex.NominalStartUtc, codex.ResetAtUtc, 100);
            Assert(kept.Samples.Length == 1, "migration: other providers are untouched");
            // A new CLI-era sample joins the renamed window instead of starting another.
            Assert(migrated.TryAppend(new UsageObservation(cli, start.AddHours(3), 0.75)), "migration: a CLI-era sample queues");
            await migrated.FlushAsync();
            var joined = await migrated.QueryWindowAsync(cli, cli.NominalStartUtc, cli.ResetAtUtc, 100);
            Assert(joined.Samples.Length == 3, "migration: CLI-era samples continue the same week");
        }

        var again = new RecordingStorageDiagnostics();
        await using (var reopened = await SqliteUsageHistoryStore.OpenAsync(
                         databasePath, SqliteUsageHistoryStore.DefaultRetentionDays, timeProvider: null, again.Record))
        {
            Assert(!again.Any("history_schema_migrated", "from=1,to=2,rows=1") && !again.Any("history_schema_migrated", "from=1,to=2,rows=0"),
                "migration: a schema-2 file is not migrated again");
        }
    }

    // Shared DDL for specs that drop and recreate the observations table.
    private const string ObservationsTableSql = """
        CREATE TABLE usage_observations (
            window_id INTEGER NOT NULL REFERENCES usage_windows(id) ON DELETE CASCADE,
            observed_utc_ticks INTEGER NOT NULL,
            remaining_fraction REAL NOT NULL CHECK(remaining_fraction >= 0 AND remaining_fraction <= 1),
            PRIMARY KEY(window_id, observed_utc_ticks)
        );
        CREATE INDEX usage_observations_by_time ON usage_observations(observed_utc_ticks);
        """;

    private const string HideObservationsSql = "ALTER TABLE usage_observations RENAME TO usage_observations_hidden;";
    private const string RestoreObservationsSql = "ALTER TABLE usage_observations_hidden RENAME TO usage_observations;";

    // A failed read schedules recovery; a later successful listing restores health and writes can continue.
    private static async Task ReadFailureDoesNotStopWritesAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "read-failure", "history.sqlite");
        var window = Window("scope-read", Epoch.AddDays(-1), Epoch.AddDays(6));
        var clock = new ManualTimeProvider(Epoch);
        var diagnostics = new RecordingStorageDiagnostics();
        await using var store = await SqliteUsageHistoryStore.OpenAsync(
            databasePath, SqliteUsageHistoryStore.DefaultRetentionDays, clock, diagnostics.Record);
        Assert(store.TryAppend(new UsageObservation(window, Epoch, 0.9)), "read failure: the first observation queues");
        await store.FlushAsync();

        await ExecuteOnSideConnectionAsync(databasePath, HideObservationsSql);
        var failedList = await store.TryListWindowsAsync("scope-read", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(!failedList.IsAvailable && failedList.Windows.IsEmpty && failedList.SafeErrorCode == "history_read_failed",
            "read failure: the listing reports itself unavailable with history_read_failed");
        Assert(!store.Health.IsAvailable && store.Health.SafeErrorCode == "history_read_failed",
            "read failure: health reports history_read_failed");
        Assert(diagnostics.Any("history_list_windows_failed", "SqliteException_code_1"),
            "read failure: the failed listing is logged with the exception type");
        var failedQuery = await store.QueryWindowAsync(window, window.NominalStartUtc, window.ResetAtUtc, 100);
        Assert(!failedQuery.IsAvailable && failedQuery.SafeErrorCode == "history_read_failed",
            "read failure: a failed query reports history_read_failed");
        Assert(diagnostics.Any("history_query_failed", "SqliteException_code_1"),
            "read failure: the failed query is logged with the exception type");
        await ExecuteOnSideConnectionAsync(databasePath, RestoreObservationsSql);

        clock.Advance(TimeSpan.FromHours(6));
        Assert(store.TryAppend(new UsageObservation(window, Epoch.AddHours(6), 0.8)),
            "read failure: six hours later a new observation is accepted");
        await store.FlushAsync();
        Assert(store.Health.IsAvailable && store.Health.SafeErrorCode is null,
            "read failure: the successful write returns health to available");

        // A read failure followed by a successful read restores health without waiting for a write.
        await ExecuteOnSideConnectionAsync(databasePath, HideObservationsSql);
        Assert(!(await store.TryListWindowsAsync("scope-read", ProviderIds.Claude, "claude.weekly.seven_day")).IsAvailable,
            "read failure: a second hidden-table listing fails");
        await ExecuteOnSideConnectionAsync(databasePath, RestoreObservationsSql);
        var listed = await store.TryListWindowsAsync("scope-read", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(listed.IsAvailable && listed.SafeErrorCode is null && listed.Windows.Length == 1 && listed.Windows[0] == window,
            "read failure: the next listing succeeds with the stored window");
        Assert(store.Health.IsAvailable && store.Health.SafeErrorCode is null,
            "read failure: a successful read restores health");
        Assert(diagnostics.Any("history_read_recovered", "after_history_read_failed"),
            "read failure: the recovery is logged as history_read_recovered");
        var stored = await store.QueryWindowAsync(window, window.NominalStartUtc, window.ResetAtUtc, 100);
        Assert(stored.IsAvailable && stored.Samples.Length == 2, "read failure: both observations are stored");
    }

    // A read failure must preserve an active write cooldown until its scheduled retry time.
    private static async Task ReadFailureKeepsPendingWriteCooldownAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "read-during-cooldown", "history.sqlite");
        var window = Window("scope-cooldown", Epoch.AddDays(-1), Epoch.AddDays(6));
        var clock = new ManualTimeProvider(Epoch);
        var diagnostics = new RecordingStorageDiagnostics();
        await using var store = await SqliteUsageHistoryStore.OpenAsync(
            databasePath, SqliteUsageHistoryStore.DefaultRetentionDays, clock, diagnostics.Record);

        await ExecuteOnSideConnectionAsync(databasePath, "DROP TABLE usage_observations;");
        Assert(store.TryAppend(new UsageObservation(window, Epoch, 0.5)), "cooldown read: the first observation queues");
        await store.FlushAsync();
        Assert(store.Health.SafeErrorCode == "history_write_failed", "cooldown read: the write failure starts a cooldown");

        var failedList = await store.TryListWindowsAsync("scope-cooldown", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(!failedList.IsAvailable, "cooldown read: the listing fails while the table is missing");
        Assert(!store.Health.IsAvailable && store.Health.SafeErrorCode == "history_write_failed",
            "cooldown read: the read failure does not replace the pending write failure");

        clock.Advance(SqliteUsageHistoryStore.WriteRetryCooldown - TimeSpan.FromSeconds(1));
        Assert(!store.TryAppend(new UsageObservation(window, Epoch.AddSeconds(29), 0.45)),
            "cooldown read: samples are still refused before the cooldown ends");

        await ExecuteOnSideConnectionAsync(databasePath, ObservationsTableSql);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert(store.TryAppend(new UsageObservation(window, Epoch.AddMinutes(1), 0.4)),
            "cooldown read: the cooldown still ends on schedule after the read failure");
        await store.FlushAsync();
        Assert(store.Health.IsAvailable, "cooldown read: the successful write returns health to available");
        Assert(diagnostics.Any("history_write_recovered", "after_history_write_failed"),
            "cooldown read: the recovery is logged as history_write_recovered");
    }

    // A failed retention prune schedules recovery so later samples can be accepted after the cooldown.
    private static async Task PruneFailureDoesNotStopWritesAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "prune-failure", "history.sqlite");
        var window = Window("scope-prune", Epoch.AddDays(-1), Epoch.AddDays(6));
        var clock = new ManualTimeProvider(Epoch);
        var diagnostics = new RecordingStorageDiagnostics();
        await using var store = await SqliteUsageHistoryStore.OpenAsync(
            databasePath, SqliteUsageHistoryStore.DefaultRetentionDays, clock, diagnostics.Record);

        await ExecuteOnSideConnectionAsync(databasePath, HideObservationsSql);
        await store.SetRetentionDaysAsync(30);
        Assert(!store.Health.IsAvailable && store.Health.SafeErrorCode == "history_prune_failed",
            "prune failure: the failed prune publishes history_prune_failed");
        Assert(diagnostics.Any("history_prune_failed", "SqliteException_code_1"),
            "prune failure: the failed prune is logged with the exception type");
        await ExecuteOnSideConnectionAsync(databasePath, RestoreObservationsSql);

        Assert(store.TryAppend(new UsageObservation(window, Epoch, 0.7)),
            "prune failure: a new observation is accepted at once");
        await store.FlushAsync();
        Assert(store.Health.IsAvailable && store.Health.SafeErrorCode is null,
            "prune failure: the successful write returns health to available");
        var stored = await store.QueryWindowAsync(window, window.NominalStartUtc, window.ResetAtUtc, 100);
        Assert(stored.Samples.Length == 1, "prune failure: the observation is stored");
    }

    // A successful prune ends the pending write cooldown and restores store health.
    private static async Task PruneSuccessEndsWriteCooldownAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "prune-ends-cooldown", "history.sqlite");
        var window = Window("scope-prune-cooldown", Epoch.AddDays(-1), Epoch.AddDays(6));
        var clock = new ManualTimeProvider(Epoch);
        var diagnostics = new RecordingStorageDiagnostics();
        await using var store = await SqliteUsageHistoryStore.OpenAsync(
            databasePath, SqliteUsageHistoryStore.DefaultRetentionDays, clock, diagnostics.Record);

        await ExecuteOnSideConnectionAsync(databasePath, "DROP TABLE usage_observations;");
        Assert(store.TryAppend(new UsageObservation(window, Epoch, 0.5)), "prune recovery: the first observation queues");
        await store.FlushAsync();
        Assert(store.Health.SafeErrorCode == "history_write_failed", "prune recovery: the write failure starts a cooldown");

        await ExecuteOnSideConnectionAsync(databasePath, ObservationsTableSql);
        await store.SetRetentionDaysAsync(SqliteUsageHistoryStore.DefaultRetentionDays);
        Assert(store.Health.IsAvailable && store.Health.SafeErrorCode is null,
            "prune recovery: the successful prune returns health to available");
        Assert(diagnostics.Any("history_write_recovered", "after_history_write_failed_by_prune"),
            "prune recovery: the recovery is logged with _by_prune");
        Assert(store.TryAppend(new UsageObservation(window, Epoch.AddSeconds(1), 0.45)),
            "prune recovery: samples are accepted without waiting out the cooldown");
        await store.FlushAsync();
    }

    // TryListWindowsAsync says whether the store could be read,
    // ListWindowsAsync returns the same windows, and ListWindowsOrThrowAsync (the scheduler's lookup) throws
    // UsageHistoryUnavailableException when it could not.
    private static async Task TryListWindowsReportsAvailabilityAsync(string tempRoot)
    {
        var databasePath = Path.Combine(tempRoot, "try-list", "history.sqlite");
        var window = Window("scope-try-list", Epoch.AddDays(-1), Epoch.AddDays(6));
        await using var store = await SqliteUsageHistoryStore.OpenAsync(databasePath);
        Assert(store.TryAppend(new UsageObservation(window, Epoch, 0.6)), "try list: the observation queues");
        await store.FlushAsync();

        var listed = await store.TryListWindowsAsync("scope-try-list", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(listed.IsAvailable && listed.SafeErrorCode is null && listed.Windows.Length == 1 && listed.Windows[0] == window,
            "try list: an available store lists its window");
        var plain = await store.ListWindowsAsync("scope-try-list", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(plain.SequenceEqual(listed.Windows), "try list: ListWindowsAsync returns the same windows");
        var viaLookup = await store.ListWindowsOrThrowAsync("scope-try-list", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(viaLookup.SequenceEqual(listed.Windows), "try list: the lookup returns the same windows");
        var empty = await store.TryListWindowsAsync("scope-nobody", ProviderIds.Claude, "claude.weekly.seven_day");
        Assert(empty.IsAvailable && empty.Windows.IsEmpty, "try list: nothing stored is available and empty");

        await ExecuteOnSideConnectionAsync(databasePath, HideObservationsSql);
        try
        {
            await store.ListWindowsOrThrowAsync("scope-try-list", ProviderIds.Claude, "claude.weekly.seven_day");
            throw new InvalidOperationException("Storage spec failed: try list: an unreadable store must throw from the lookup.");
        }
        catch (UsageHistoryUnavailableException ex)
        {
            Assert(ex.SafeErrorCode == "history_read_failed", "try list: the lookup's exception carries history_read_failed");
        }
        Assert((await store.ListWindowsAsync("scope-try-list", ProviderIds.Claude, "claude.weekly.seven_day")).IsEmpty,
            "try list: the plain listing still answers an unreadable store with an empty array");
        await ExecuteOnSideConnectionAsync(databasePath, RestoreObservationsSql);
    }

    // A database copied with its -wal while the writer is open may have rows only in the -wal.
    // Checkpointing folds those rows into the main file and leaves no non-empty sidecar.
    private static void CheckpointFoldsWalIntoDatabase(string tempRoot)
    {
        var copyDirectory = Path.Combine(tempRoot, "checkpoint-copy");
        var copyPath = SeedDatabaseWithUncheckpointedRows(Path.Combine(tempRoot, "checkpoint-seed"), copyDirectory, rows: 50);
        Assert(new FileInfo(copyPath + "-wal").Length > 0, "checkpoint: the copied database starts with rows only in its -wal");

        Assert(SqliteHistoryCheckpoint.TryCheckpointInPlace(copyPath, out var status), $"checkpoint: the fold succeeds (status {status})");
        Assert(status.StartsWith("log_", StringComparison.Ordinal), "checkpoint: the status reports the frame counts");
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = new FileInfo(copyPath + suffix);
            Assert(!sidecar.Exists || sidecar.Length == 0, $"checkpoint: no non-empty {suffix} is left");
        }
        Assert(!Directory.EnumerateFiles(copyDirectory, "*" + SqliteHistoryCheckpoint.SidecarCopyInfix + "*").Any(),
            "checkpoint: once every frame is in the main file, the safety copy of the -wal is removed");
        Assert(CountSamples(copyPath) == 50, "checkpoint: every row from the -wal is in the main file");
    }

    // SQLite deletes an unusable -wal on close. Preserve its bytes under the inert copy name while
    // checkpointing the valid database, so the migration does not lose the sidecar.
    private static void CheckpointKeepsWhatSqliteDiscards(string tempRoot)
    {
        var directory = Path.Combine(tempRoot, "checkpoint-discarded-wal");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "usage-history.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = databasePath,
                   Mode = SqliteOpenMode.ReadWriteCreate,
                   Pooling = false
               }.ToString()))
        {
            connection.Open();
            ExecuteOn(connection, "PRAGMA journal_mode=WAL;");
            ExecuteOn(connection, "CREATE TABLE samples (id INTEGER PRIMARY KEY, value REAL NOT NULL);");
            ExecuteOn(connection, "INSERT INTO samples (value) VALUES (1);");
        }
        File.WriteAllText(databasePath + "-wal", "wal-bytes-unusable");

        Assert(SqliteHistoryCheckpoint.TryCheckpointInPlace(databasePath, out var status),
            $"discarded wal: a real database still checkpoints (status {status})");
        Assert(status.EndsWith("_sidecar_copy_kept", StringComparison.Ordinal),
            $"discarded wal: the status says the copy was kept (status {status})");
        var copies = Directory.GetFiles(directory, "usage-history.sqlite-wal" + SqliteHistoryCheckpoint.SidecarCopyInfix + "*");
        Assert(copies.Length == 1 && File.ReadAllText(copies[0]) == "wal-bytes-unusable",
            "discarded wal: the -wal SQLite could not use is kept under the inert copy name");
        Assert(CountSamples(databasePath) == 1, "discarded wal: the database's own rows are intact");
    }

    // If checkpointing cannot fold the database, report the failure without changing files.
    private static void CheckpointRefusesWhatItCannotFold(string tempRoot)
    {
        var directory = Path.Combine(tempRoot, "checkpoint-refusals");
        Directory.CreateDirectory(directory);
        var missing = Path.Combine(directory, "missing.sqlite");
        Assert(!SqliteHistoryCheckpoint.TryCheckpointInPlace(missing, out var missingStatus) && missingStatus == "database_missing",
            "checkpoint: a missing database is refused as database_missing");
        Assert(!File.Exists(missing), "checkpoint: a missing database is not created");

        var garbage = Path.Combine(directory, "garbage.sqlite");
        File.WriteAllText(garbage, "not a sqlite database, long enough to have a header of sorts............................................");
        File.WriteAllText(garbage + "-wal", "wal-bytes-garbage");
        Assert(!SqliteHistoryCheckpoint.TryCheckpointInPlace(garbage, out var garbageStatus) &&
               garbageStatus.StartsWith("SqliteException_code_", StringComparison.Ordinal),
            $"checkpoint: a file that is not a database is refused with the SQLite code (status {garbageStatus})");
        // Found by this spec: SQLite deleted this -wal while closing the failed open. The checkpoint's safety
        // copy puts it back, and the copy itself is then removed.
        Assert(File.Exists(garbage + "-wal") && File.ReadAllText(garbage + "-wal") == "wal-bytes-garbage",
            "checkpoint: a refused database's -wal is back, with its bytes");
        Assert(!Directory.EnumerateFiles(directory, "*" + SqliteHistoryCheckpoint.SidecarCopyInfix + "*").Any(),
            "checkpoint: after a refusal no safety copy is left");
        Assert(File.ReadAllText(garbage).StartsWith("not a sqlite database", StringComparison.Ordinal),
            "checkpoint: a refused database file is unchanged");
    }

    /// <summary>Builds a WAL-mode database whose uncheckpointed rows are copied with the main file into
    /// <paramref name="destinationDirectory"/> while the writer remains open. Returns the copied database path.</summary>
    private static string SeedDatabaseWithUncheckpointedRows(string seedDirectory, string destinationDirectory, int rows)
    {
        Directory.CreateDirectory(seedDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var seedPath = Path.Combine(seedDirectory, "usage-history.sqlite");
        var copyPath = Path.Combine(destinationDirectory, "usage-history.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = seedPath,
                   Mode = SqliteOpenMode.ReadWriteCreate,
                   Pooling = false
               }.ToString()))
        {
            connection.Open();
            ExecuteOn(connection, "PRAGMA journal_mode=WAL;");
            ExecuteOn(connection, "PRAGMA wal_autocheckpoint=0;");
            ExecuteOn(connection, "CREATE TABLE samples (id INTEGER PRIMARY KEY, value REAL NOT NULL);");
            ExecuteOn(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            for (var i = 0; i < rows; i++)
                ExecuteOn(connection, $"INSERT INTO samples (value) VALUES ({i});");
            CopyWhileOpen(seedPath, copyPath);
            CopyWhileOpen(seedPath + "-wal", copyPath + "-wal");
        }
        return copyPath;
    }

    private static void ExecuteOn(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    // SQLite keeps its files open with read and write sharing, so a reader that allows both can copy them.
    private static void CopyWhileOpen(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
    }

    private static long CountSamples(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM samples;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadScalarOnSideConnectionAsync(string databasePath, string commandText)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteOnSideConnectionAsync(string databasePath, string commandText)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A manually advanced clock for testing cooldowns without waiting in real time.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public void Advance(TimeSpan by)
        {
            lock (_gate) _now += by;
        }
    }

    /// <summary>Captures storage diagnostics; the history writer records from its own task.</summary>
    private sealed class RecordingStorageDiagnostics
    {
        private readonly object _gate = new();
        private readonly List<StorageDiagnostic> _events = [];

        public void Record(StorageDiagnostic diagnostic)
        {
            lock (_gate) _events.Add(diagnostic);
        }

        public bool Any(string code, string status)
        {
            lock (_gate) return _events.Any(e => e.Code == code && e.Status == status);
        }
    }

    private static UsageWindowIdentity Window(string scope, DateTimeOffset start, DateTimeOffset reset) =>
        new(scope, ProviderIds.Claude, "claude.weekly.seven_day", start, reset, "source.weekly");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Storage spec failed: {message}.");
    }
}

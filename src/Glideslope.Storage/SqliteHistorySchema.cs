using System.Globalization;
using Glideslope.Domain;
using Microsoft.Data.Sqlite;

namespace Glideslope.Storage;

/// <summary>
/// The history database's schema work, which touches no store state: the pragmas and schema migrations run
/// at open, the one-time window repair of window-interaction design §17 rule 5, and the bounded retention
/// prune. <see cref="SqliteUsageHistoryStore"/> calls these on its writer connection, at open and from its
/// serialized writer.
/// </summary>
internal static class SqliteHistorySchema
{
    // Schema version 2 migrates Claude history to the CLI source semantics.
    private const int SchemaVersion = 2;
    private const int PruneBatchSize = 10_000;

    internal static async Task ConfigureAndMigrateAsync(SqliteConnection connection, Action<StorageDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        await using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new InvalidDataException("History database schema is newer than this application.");
        if (version == 0)
        {
            command.CommandText = """
                CREATE TABLE usage_windows (
                    id INTEGER PRIMARY KEY,
                    account_scope TEXT NOT NULL,
                    provider_id TEXT NOT NULL,
                    bucket_id TEXT NOT NULL,
                    nominal_start_utc_ticks INTEGER NOT NULL,
                    reset_utc_ticks INTEGER NOT NULL,
                    source_semantics TEXT NOT NULL,
                    UNIQUE(account_scope, provider_id, bucket_id, nominal_start_utc_ticks, reset_utc_ticks, source_semantics)
                );
                CREATE TABLE usage_observations (
                    window_id INTEGER NOT NULL REFERENCES usage_windows(id) ON DELETE CASCADE,
                    observed_utc_ticks INTEGER NOT NULL,
                    remaining_fraction REAL NOT NULL CHECK(remaining_fraction >= 0 AND remaining_fraction <= 1),
                    PRIMARY KEY(window_id, observed_utc_ticks)
                );
                CREATE INDEX usage_observations_by_time ON usage_observations(observed_utc_ticks);
                PRAGMA user_version=2;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (version == 1)
        {
            // Preserve recorded samples when the provider switches from OAuth usage to CLI usage semantics.
            command.CommandText = """
                UPDATE usage_windows
                SET source_semantics = 'anthropic.claude-cli.usage' || substr(source_semantics, length('anthropic.oauth.usage') + 1)
                WHERE source_semantics LIKE 'anthropic.oauth.usage/%';
                """;
            var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "PRAGMA user_version=2;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            diagnostics(new StorageDiagnostic("history_schema_migrated", $"from=1,to=2,rows={rows}"));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Repairs previously stored windows once per open, in one transaction: (a) delete every window whose only sample is at
    /// 100 % within <see cref="WindowIdentityPolicy.NotStartedTolerance"/> of its nominal start (a phantom);
    /// (b) move the samples of every window whose reset is not on a minute boundary into the window with the
    /// rounded reset (created if needed) and delete the old row. Logged as history_windows_repaired with the
    /// counts; a failure is logged as history_windows_repair_failed and the store still opens.
    /// </summary>
    internal static async Task RepairWindowsAsync(SqliteConnection connection, Action<StorageDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            int phantoms;
            await using (var deletePhantoms = connection.CreateCommand())
            {
                deletePhantoms.Transaction = (SqliteTransaction)transaction;
                deletePhantoms.CommandText = """
                    DELETE FROM usage_windows
                    WHERE id IN (
                        SELECT w.id FROM usage_windows AS w
                        WHERE (SELECT COUNT(*) FROM usage_observations AS o WHERE o.window_id = w.id) = 1
                          AND EXISTS (
                            SELECT 1 FROM usage_observations AS o
                            WHERE o.window_id = w.id
                              AND o.remaining_fraction >= 1
                              AND abs(o.observed_utc_ticks - w.nominal_start_utc_ticks) <= $tolerance));
                    """;
                deletePhantoms.Parameters.AddWithValue("$tolerance", WindowIdentityPolicy.NotStartedTolerance.Ticks);
                phantoms = await deletePhantoms.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var unrounded = new List<(long Id, string Scope, string Provider, string Bucket, long Start, long Reset, string Semantics)>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = (SqliteTransaction)transaction;
                select.CommandText = """
                    SELECT id, account_scope, provider_id, bucket_id, nominal_start_utc_ticks, reset_utc_ticks, source_semantics
                    FROM usage_windows
                    WHERE reset_utc_ticks % $unit <> 0
                    ORDER BY id ASC;
                    """;
                select.Parameters.AddWithValue("$unit", WindowIdentityPolicy.ResetGranularity.Ticks);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    unrounded.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6)));
            }

            foreach (var window in unrounded)
            {
                var roundedReset = WindowIdentityPolicy.RoundReset(new DateTimeOffset(window.Reset, TimeSpan.Zero)).UtcTicks;
                var roundedStart = window.Start + (roundedReset - window.Reset);
                long canonicalId;
                await using (var ensure = connection.CreateCommand())
                {
                    ensure.Transaction = (SqliteTransaction)transaction;
                    ensure.CommandText = """
                        INSERT INTO usage_windows
                            (account_scope, provider_id, bucket_id, nominal_start_utc_ticks, reset_utc_ticks, source_semantics)
                        VALUES ($scope, $provider, $bucket, $start, $reset, $semantics)
                        ON CONFLICT(account_scope, provider_id, bucket_id, nominal_start_utc_ticks, reset_utc_ticks, source_semantics)
                        DO NOTHING;
                        """;
                    AddCanonicalParameters(ensure, window.Scope, window.Provider, window.Bucket, roundedStart, roundedReset, window.Semantics);
                    await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var find = connection.CreateCommand())
                {
                    find.Transaction = (SqliteTransaction)transaction;
                    find.CommandText = """
                        SELECT id FROM usage_windows
                        WHERE account_scope = $scope AND provider_id = $provider AND bucket_id = $bucket
                          AND nominal_start_utc_ticks = $start AND reset_utc_ticks = $reset AND source_semantics = $semantics;
                        """;
                    AddCanonicalParameters(find, window.Scope, window.Provider, window.Bucket, roundedStart, roundedReset, window.Semantics);
                    canonicalId = (long)(await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Canonical window insert did not return a row."));
                }

                await using var move = connection.CreateCommand();
                move.Transaction = (SqliteTransaction)transaction;
                move.CommandText = """
                    INSERT OR IGNORE INTO usage_observations(window_id, observed_utc_ticks, remaining_fraction)
                    SELECT $canonical, observed_utc_ticks, remaining_fraction FROM usage_observations WHERE window_id = $old;
                    DELETE FROM usage_windows WHERE id = $old;
                    """;
                move.Parameters.AddWithValue("$canonical", canonicalId);
                move.Parameters.AddWithValue("$old", window.Id);
                await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            diagnostics(new StorageDiagnostic("history_windows_repaired",
                phantoms == 0 && unrounded.Count == 0 ? "none" : $"phantoms={phantoms},merged={unrounded.Count}"));
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            diagnostics(new StorageDiagnostic("history_windows_repair_failed", ex.GetType().Name));
        }
    }

    internal static void AddCanonicalParameters(SqliteCommand command, string scope, string provider, string bucket,
        long start, long reset, string semantics)
    {
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$bucket", bucket);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$reset", reset);
        command.Parameters.AddWithValue("$semantics", semantics);
    }

    internal static async Task PruneExpiredAsync(SqliteConnection connection, int retentionDays, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcDateTime.Ticks;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            DELETE FROM usage_observations
            WHERE rowid IN (
                SELECT o.rowid FROM usage_observations AS o
                WHERE o.observed_utc_ticks < $cutoff
                LIMIT $batch
            );
            DELETE FROM usage_windows
            WHERE id IN (
                SELECT w.id FROM usage_windows AS w
                WHERE NOT EXISTS (SELECT 1 FROM usage_observations AS o WHERE o.window_id = w.id)
                LIMIT $batch
            );
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$batch", PruneBatchSize);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

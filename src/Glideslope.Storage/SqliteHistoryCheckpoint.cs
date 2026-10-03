using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Glideslope.Storage;

/// <summary>
/// Folds a history database's write-ahead log into its main file before user-data migration.
/// Glideslope.Core cannot reference SQLite, so Program.cs supplies <see cref="TryCheckpointInPlace"/>
/// as the migration's HistoryDatabaseCheckpoint. Folding keeps committed rows with the database and
/// prevents a separated or stale WAL from being replayed after the move.
/// </summary>
public static class SqliteHistoryCheckpoint
{
    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    // The sidecars that can hold rows. "-shm" is only an index SQLite rebuilds, so it is not copied.
    private static readonly string[] DataSidecarSuffixes = ["-wal", "-journal"];

    /// <summary>Infix for a preserved copy of a non-empty SQLite sidecar. SQLite ignores this name.</summary>
    public const string SidecarCopyInfix = ".pre-checkpoint-";

    /// <summary>
    /// Opens the existing database read-write (never creating it), reads its schema (which rolls back a hot
    /// journal and recovers the WAL), runs PRAGMA wal_checkpoint(FULL) and then (TRUNCATE), and closes it; closing the last
    /// connection lets SQLite delete the -wal and -shm. Returns true only when the checkpoint was not busy
    /// and every sidecar is then absent or empty. <paramref name="status"/> is a safe code: frame counts,
    /// SQLite result codes and exception type names, never a path.
    /// Every non-empty "-wal" and "-journal" is copied beside itself before opening because SQLite may remove
    /// sidecars while closing a database, including after a failed open. After a failure each original is put
    /// back from its copy if SQLite changed or removed it. After a success the copies are removed only when
    /// every WAL frame was checkpointed; otherwise they are kept, and the status ends in
    /// "_sidecar_copy_kept".
    /// </summary>
    public static bool TryCheckpointInPlace(string databasePath, out string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            status = "database_missing";
            return false;
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var copies = new List<SidecarCopy>();
        foreach (var suffix in DataSidecarSuffixes)
        {
            var sidecar = fullPath + suffix;
            var info = new FileInfo(sidecar);
            if (!info.Exists || info.Length == 0)
                continue;
            var copy = sidecar + SidecarCopyInfix + stamp;
            try
            {
                File.Copy(sidecar, copy, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing has been opened yet, so every original is untouched; only the copies taken so far go.
                var kept = RemoveCopies(copies);
                status = $"sidecar_copy_failed_{ex.GetType().Name}{kept}";
                return false;
            }
            copies.Add(new SidecarCopy(sidecar, copy, info.Length));
        }

        CheckpointResult full;
        CheckpointResult truncate = CheckpointResult.None;
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = fullPath,
                       Mode = SqliteOpenMode.ReadWrite,
                       Pooling = false,
                       DefaultTimeout = 5
                   }.ToString()))
            {
                connection.Open();
                using (var touch = connection.CreateCommand())
                {
                    touch.CommandText = "SELECT count(*) FROM sqlite_master;";
                    touch.ExecuteScalar();
                }

                // FULL first preserves frame counts proving every frame reached the main file; TRUNCATE resets
                // the log before reporting, so it cannot provide that proof.
                // TRUNCATE then empties the -wal; closing the last connection removes it and the -shm.
                full = RunCheckpoint(connection, "FULL");
                if (full.ReturnedRow && full.Busy == 0)
                    truncate = RunCheckpoint(connection, "TRUNCATE");
            }
        }
        catch (SqliteException ex)
        {
            status = $"{ex.GetType().Name}_code_{ex.SqliteErrorCode}{RestoreOriginals(copies)}";
            return false;
        }
        catch (IOException ex)
        {
            status = $"{ex.GetType().Name}{RestoreOriginals(copies)}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            status = $"{ex.GetType().Name}{RestoreOriginals(copies)}";
            return false;
        }

        var logFrames = full.LogFrames;
        var checkpointedFrames = full.CheckpointedFrames;
        if (!full.ReturnedRow || !truncate.ReturnedRow)
        {
            status = $"checkpoint_returned_no_row{RestoreOriginals(copies)}";
            return false;
        }

        if (full.Busy != 0 || truncate.Busy != 0)
        {
            status = $"checkpoint_busy_log_{logFrames}_checkpointed_{checkpointedFrames}{RestoreOriginals(copies)}";
            return false;
        }

        foreach (var suffix in SidecarSuffixes)
        {
            var sidecar = new FileInfo(fullPath + suffix);
            if (sidecar.Exists && sidecar.Length > 0)
            {
                status = $"sidecar_{suffix.TrimStart('-')}_not_empty{RestoreOriginals(copies)}";
                return false;
            }
        }

        // Every frame SQLite found was written into the main file, so the copies hold nothing the database
        // lacks. With no frames (a database not in WAL mode reports -1, and a "-wal" SQLite could not use
        // reports 0) the copies are the only record of what was there, and they stay.
        var folded = logFrames > 0 && checkpointedFrames == logFrames;
        var suffixNote = copies.Count == 0 ? "" : folded ? RemoveCopies(copies) : "_sidecar_copy_kept";
        status = $"log_{logFrames}_checkpointed_{checkpointedFrames}{suffixNote}";
        return true;
    }

    private sealed record SidecarCopy(string Sidecar, string Copy, long Length);

    /// <summary>One PRAGMA wal_checkpoint row: busy (0 or 1), frames in the log, frames checkpointed; -1 for
    /// a database not in WAL mode. None stands for a checkpoint that was not run.</summary>
    private sealed record CheckpointResult(bool ReturnedRow, long Busy, long LogFrames, long CheckpointedFrames)
    {
        public static readonly CheckpointResult None = new(false, 0, -1, -1);
    }

    private static CheckpointResult RunCheckpoint(SqliteConnection connection, string mode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA wal_checkpoint({mode});";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new CheckpointResult(true, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2))
            : CheckpointResult.None;
    }

    /// <summary>After a failed checkpoint: puts back any original SQLite removed or changed, and removes the
    /// copies. Returns "" when all went back cleanly, or "_sidecar_copy_kept_&lt;exception type&gt;" when a
    /// copy had to stay (the restore or the removal failed), so the caller's log shows it.</summary>
    private static string RestoreOriginals(List<SidecarCopy> copies)
    {
        string? keptBecause = null;
        foreach (var entry in copies)
        {
            try
            {
                var current = new FileInfo(entry.Sidecar);
                if (!current.Exists || current.Length != entry.Length)
                    File.Copy(entry.Copy, entry.Sidecar, overwrite: true);
                File.Delete(entry.Copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The copy stays under its inert name, so the bytes are still on disk.
                keptBecause ??= ex.GetType().Name;
            }
        }
        return keptBecause is null ? "" : $"_sidecar_copy_kept_{keptBecause}";
    }

    /// <summary>Removes the copies (this method's own files). Returns "" or
    /// "_sidecar_copy_kept_&lt;exception type&gt;" when one could not be removed and stays under its inert
    /// name.</summary>
    private static string RemoveCopies(List<SidecarCopy> copies)
    {
        string? keptBecause = null;
        foreach (var entry in copies)
        {
            try
            {
                File.Delete(entry.Copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                keptBecause ??= ex.GetType().Name;
            }
        }
        return keptBecause is null ? "" : $"_sidecar_copy_kept_{keptBecause}";
    }
}

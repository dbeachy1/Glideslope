using System.Diagnostics;
using System.Globalization;

namespace Glideslope.Core;

/// <summary>
/// Folds a SQLite database's write-ahead log into its main file where the
/// database lies, so the database can afterwards be moved as that one file. Returns true only when the
/// database's "-wal", "-shm" and "-journal" files are then absent or empty. <paramref name="status"/> is a
/// safe code for the log: codes, counts and exception type names, never a path or file content.
/// Glideslope.Core does not reference SQLite, so the app passes Glideslope.Storage's
/// SqliteHistoryCheckpoint.TryCheckpointInPlace (see Program.cs).
/// </summary>
public delegate bool HistoryDatabaseCheckpoint(string databasePath, out string status);

/// <summary>
/// Moves settings and history from the legacy GlidePath per-user directories into the current Glideslope
/// directories. Call this once per launch, after the
/// single-instance owner check succeeds and before any settings or history file is opened, so a
/// concurrent second process can never race this move; a forwarded (non-owner) launch never calls it.
/// </summary>
public static class UserDataMigration
{
    /// <summary>The history database's file name in the data root. The app opens
    /// the store under this name (ProviderMonitoringSession), and the migration moves it only together with
    /// its SQLite sidecar files.</summary>
    public const string HistoryDatabaseFileName = "usage-history.sqlite";

    /// <summary>
    /// Migrates every distinct per-user directory root the old and new layouts share. The config-settings
    /// directory, data directory, and cache directory are independent roots on Linux (under ~/.config,
    /// ~/.local/share, and ~/.cache) but collapse onto the same %LOCALAPPDATA% root on Windows; the
    /// distinct-root check below only migrates each real filesystem root once. The ephemeral runtime/IPC
    /// directory is deliberately excluded: it holds no persisted user data (see InstanceBroker) and is
    /// rebuilt fresh under the new name by whichever process first claims single-instance ownership.
    /// </summary>
    // Compatibility overload for callers that do not provide a history checkpoint. A database with
    // sidecars is held back rather than moved.
    internal static void MigrateIfNeeded(IDiagnosticSink diagnostics) =>
        MigrateIfNeeded(diagnostics, checkpoint: null, forceCopyFallback: false);

    /// <summary>The migration entry point with
    /// <paramref name="checkpoint"/> folding a GlidePath history database's write-ahead log into it before
    /// the database is moved.</summary>
    public static void MigrateIfNeeded(IDiagnosticSink diagnostics, HistoryDatabaseCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        MigrateIfNeeded(diagnostics, checkpoint, forceCopyFallback: false);
    }

    // The optional forced fallback makes the copy-and-verify path testable without requiring a
    // cross-volume filesystem.
    internal static void MigrateIfNeeded(IDiagnosticSink diagnostics, bool forceCopyFallback) =>
        MigrateIfNeeded(diagnostics, checkpoint: null, forceCopyFallback: forceCopyFallback);

    internal static void MigrateIfNeeded(IDiagnosticSink diagnostics, HistoryDatabaseCheckpoint? checkpoint, bool forceCopyFallback)
    {
        var current = DefaultUserPathProvider.BuildPaths(DefaultUserPathProvider.ProductDirectoryName);
        var legacy = DefaultUserPathProvider.BuildPaths(DefaultUserPathProvider.LegacyProductDirectoryName);
        MigrateIfNeeded(diagnostics, legacy, current, checkpoint, forceCopyFallback);
    }

    // Accept explicit layouts so migration behavior can be exercised for both nested Windows paths and
    // separate Linux paths.
    internal static void MigrateIfNeeded(IDiagnosticSink diagnostics, UserPaths legacy, UserPaths current, bool forceCopyFallback) =>
        MigrateIfNeeded(diagnostics, legacy, current, checkpoint: null, forceCopyFallback: forceCopyFallback);

    internal static void MigrateIfNeeded(IDiagnosticSink diagnostics, UserPaths legacy, UserPaths current,
        HistoryDatabaseCheckpoint? checkpoint, bool forceCopyFallback)
    {
        var roots = new (string RoleCode, string Old, string New)[]
        {
            ("user_data_migration_settings", DirectoryOf(legacy.SettingsFile), DirectoryOf(current.SettingsFile)),
            ("user_data_migration_data", legacy.DataDirectory, current.DataDirectory),
            ("user_data_migration_cache", legacy.CacheDirectory, current.CacheDirectory),
        };

        var migratedNewRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (roleCode, oldPath, newPath) in roots)
        {
            if (!migratedNewRoots.Add(newPath))
            {
                // Windows collapses settings/data/cache onto nested paths under one root; once the
                // shared root has been handled once, a later entry naming the same new path is a no-op.
                continue;
            }

            MigrateDirectory(roleCode, oldPath, newPath, diagnostics, checkpoint, forceCopyFallback);
        }
    }

    private static string DirectoryOf(string filePath) => Path.GetDirectoryName(filePath) ?? filePath;

    private static void MigrateDirectory(string roleCode, string oldPath, string newPath, IDiagnosticSink diagnostics,
        HistoryDatabaseCheckpoint? checkpoint, bool forceCopyFallback)
    {
        // A staging directory this migration owns exclusively, used so a crash mid-copy never leaves a
        // half-written directory sitting at newPath (which would otherwise look like "already migrated").
        var stagingPath = newPath + ".glideslope-migrating";

        // Resume an interrupted merge before examining the source and destination roots.
        ResumeLeftoverMerges(roleCode, oldPath, newPath, diagnostics, checkpoint);

        var newExists = Directory.Exists(newPath);
        var oldExists = Directory.Exists(oldPath);

        // A pending marker proves that non-bootstrap files in the destination were created by a deferred
        // session. Set them aside before merging so legacy user data remains authoritative.
        var pending = oldExists && HasPendingMarker(oldPath);
        if (pending)
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: "pending_marker_found"));

        if (newExists && oldExists && (pending || ContainsOnlyBootstrapEntries(roleCode, newPath, diagnostics)))
        {
            // The app may create its log or runtime directory before migration. Merge into a destination
            // that contains only these bootstrap entries.
            MergeIntoBootstrapRoot(roleCode, oldPath, newPath, diagnostics, checkpoint);
            return;
        }

        if (newExists && oldExists)
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: "skipped_both_present"));
            return;
        }

        if (newExists)
        {
            // Steady state after a prior successful migration (or a fresh install with no legacy data).
            return;
        }

        if (!oldExists)
        {
            // Nothing under the old name; nothing to migrate.
            return;
        }

        var started = Stopwatch.GetTimestamp();
        if (!forceCopyFallback)
        {
            try
            {
                Directory.Move(oldPath, newPath);
                diagnostics.Record(new DiagnosticEvent(
                    roleCode, Status: "moved", DurationMilliseconds: ElapsedMilliseconds(started)));
                // The marker is only read in old roots; remove the copy carried into the destination.
                TryRemovePendingMarker(roleCode, newPath, diagnostics);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Copy only after a confirmed cross-volume failure. Other failures leave the source
                // untouched for a later launch.
                if (!IsCrossVolumeMoveFailure(ex, oldPath, newPath))
                {
                    diagnostics.Record(new DiagnosticEvent(
                        roleCode, Status: $"deferred_source_locked_{ex.GetType().Name}",
                        DurationMilliseconds: ElapsedMilliseconds(started)));
                // The marker distinguishes files created by this deferred session on a later launch.
                    WritePendingMarker(roleCode, oldPath, newPath, diagnostics);
                    return;
                }

                diagnostics.Record(new DiagnosticEvent(
                    roleCode, Status: $"move_cross_volume_copying_{ex.GetType().Name}_hresult_{ex.HResult:X8}"));
            }
        }

        // Remove the marker before copying so it cannot later make an old root appear deferred.
        // If it cannot be removed, nothing is copied this launch and the old root stays untouched.
        if (!TryRemovePendingMarker(roleCode, oldPath, diagnostics))
            return;

        try
        {
            if (Directory.Exists(stagingPath))
            {
                // Leftover from a previous failed attempt. This directory is ours alone (the "-glideslope-
                // migrating" suffix is not a real product path), and oldPath still holds the authoritative
                // original, so discarding a stale partial copy here loses nothing.
                Directory.Delete(stagingPath, recursive: true);
            }

            var (fileCount, totalBytes) = CopyDirectoryRecursive(oldPath, stagingPath);
            if (!VerifyCopy(oldPath, stagingPath))
            {
                throw new IOException($"{roleCode}_copy_unverified");
            }

            Directory.Move(stagingPath, newPath);

            try
            {
                Directory.Delete(oldPath, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The verified destination is active; any source files that could not be removed remain
                // available for manual cleanup.
                diagnostics.Record(new DiagnosticEvent(
                    roleCode, Status: $"published_old_not_removed_{ex.GetType().Name}",
                    DurationMilliseconds: ElapsedMilliseconds(started)));
                return;
            }

            diagnostics.Record(new DiagnosticEvent(
                roleCode,
                Status: $"copied_{fileCount}_files_{totalBytes}_bytes",
                DurationMilliseconds: ElapsedMilliseconds(started)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The old directory is left intact on any failure. A partial staging copy is our own
            // artifact (never the user's only copy of anything, since oldPath is untouched), so it is
            // safe to discard; the next launch retries from a clean state.
            try
            {
                if (Directory.Exists(stagingPath))
                    Directory.Delete(stagingPath, recursive: true);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; a leftover staging directory is harmless and retried next launch.
                diagnostics.Record(new DiagnosticEvent(
                    roleCode, Status: $"staging_cleanup_failed_{cleanupEx.GetType().Name}"));
            }

            diagnostics.Record(new DiagnosticEvent(
                roleCode, Status: $"failed_{ex.GetType().Name}", DurationMilliseconds: ElapsedMilliseconds(started)));
            // The source is untouched; mark it deferred while this launch creates the destination.
            WritePendingMarker(roleCode, oldPath, newPath, diagnostics);
        }
    }

    // Windows' ERROR_NOT_SAME_DEVICE (0x11) as an HRESULT: "The system cannot move the file to a
    // different disk drive."
    private const int ErrorNotSameDeviceHResult = unchecked((int)0x80070011);

    // Unix errno EXDEV ("Invalid cross-device link"), 18 on Linux and macOS.
    private const int UnixExdevErrno = 18;

    /// <summary>
    /// True only when the failed rename is provably a cross-volume move, the one
    /// case the copy fallback exists for: either the OS reported ERROR_NOT_SAME_DEVICE, or the two paths
    /// have different roots (Windows drive letters; .NET refuses such a Directory.Move up front with a
    /// generic IOException, so the HResult alone would miss it). A lock, a permission problem, or any
    /// other failure is not cross-volume and must never reach the copy-then-delete path.
    /// </summary>
    private static bool IsCrossVolumeMoveFailure(Exception ex, string oldPath, string newPath)
    {
        if (ex is IOException && ex.HResult == ErrorNotSameDeviceHResult)
            return true;

        // Unix reports a cross-device rename as EXDEV. If the runtime does not expose that code here,
        // defer the move and retry rather than risk copying after an unrelated failure.
        if (!OperatingSystem.IsWindows() && ex is IOException && ex.HResult == UnixExdevErrno)
            return true;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !string.Equals(
            Path.GetPathRoot(Path.GetFullPath(oldPath)),
            Path.GetPathRoot(Path.GetFullPath(newPath)),
            comparison);
    }

    // Folders the app itself may create before migration runs. Anything else in the new root means real
    // data already lives there, and the both-present rule applies.
    // Existing installs may already contain cache from an earlier launcher, so it remains a recognized
    // bootstrap directory.
    private static readonly HashSet<string> BootstrapEntryNames = new(StringComparer.OrdinalIgnoreCase) { "logs", "runtime", "cache" };

    private static bool ContainsOnlyBootstrapEntries(string roleCode, string newPath, IDiagnosticSink diagnostics)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(newPath)
                .All(entry => Directory.Exists(entry) && BootstrapEntryNames.Contains(Path.GetFileName(entry)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"bootstrap_scan_failed_{ex.GetType().Name}"));
            return false;
        }
    }

    // Stored only in a deferred legacy source root; its location identifies which source was deferred.
    internal const string PendingMarkerFileName = "glideslope-migration-pending";

    // Destination for files created by a session while migration was deferred. Its contents are preserved.
    internal const string DeferredSessionFolderPrefix = "deferred-session-";

    // Inert suffix for preserving SQLite sidecars without their main database.
    private const string QuarantineInfix = ".stale-";

    // Keep the database and its SQLite sidecars together; separating them can lose committed rows or replay
    // a sidecar against a different database.
    private static readonly string[] HistoryDatabaseSidecarSuffixes = ["-wal", "-shm", "-journal"];

    private static readonly HashSet<string> HistoryDatabaseFamilyNames = new(StringComparer.Ordinal)
    {
        HistoryDatabaseFileName,
        HistoryDatabaseFileName + "-wal",
        HistoryDatabaseFileName + "-shm",
        HistoryDatabaseFileName + "-journal",
    };

    // Suffix for a claimed source root that a later launch can resume.
    private const string MergeWorkingSuffix = ".migrating-";

    // UTC timestamp used in migration-owned names.
    private static string UtcStamp() =>
        DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

    private static void MergeIntoBootstrapRoot(string roleCode, string oldPath, string newPath, IDiagnosticSink diagnostics,
        HistoryDatabaseCheckpoint? checkpoint)
    {
        var started = Stopwatch.GetTimestamp();

        // Claim the entire source before moving entries so a locked file defers the merge without leaving
        // a partially migrated destination.
        var workingPath = oldPath + MergeWorkingSuffix + UtcStamp();
        try
        {
            Directory.Move(oldPath, workingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"deferred_source_locked_{ex.GetType().Name}",
                DurationMilliseconds: ElapsedMilliseconds(started)));
            // The marker identifies destination files created while migration is deferred.
            WritePendingMarker(roleCode, oldPath, newPath, diagnostics);
            return;
        }

        MergeEntries(roleCode, workingPath, oldPath, newPath, diagnostics, checkpoint, started);
    }

    /// <summary>
    /// Finishes a merge an earlier launch started but did not complete,
    /// found as a "&lt;old root&gt;.migrating-*" sibling. Runs before the both-present check, because the
    /// entries that did move (such as settings.json) make the new root look like real data.
    /// </summary>
    private static void ResumeLeftoverMerges(string roleCode, string oldPath, string newPath, IDiagnosticSink diagnostics,
        HistoryDatabaseCheckpoint? checkpoint)
    {
        List<string> leftovers;
        try
        {
            var parent = Path.GetDirectoryName(oldPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                return;
            leftovers = Directory.EnumerateDirectories(parent, Path.GetFileName(oldPath) + MergeWorkingSuffix + "*")
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"leftover_scan_failed_{ex.GetType().Name}"));
            return;
        }

        foreach (var leftover in leftovers)
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: "resumed_partial_migration"));
            MergeEntries(roleCode, leftover, oldPath, newPath, diagnostics, checkpoint, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Moves every entry of the claimed working root into the new root. Each pass first decides the history
    /// database (checkpoint, hold or
    /// quarantine; PrepareHistoryDatabase), then sets aside what a deferred session created
    /// (SetAsideDeferredSessionEntries), then moves entries. Held entries keep the working root under its
    /// ".migrating-" name for the next launch; the pending marker is removed only when a pass completes.
    /// </summary>
    private static void MergeEntries(string roleCode, string workingPath, string oldPath, string newPath, IDiagnosticSink diagnostics,
        HistoryDatabaseCheckpoint? checkpoint, long started)
    {
        var database = PrepareHistoryDatabase(roleCode, workingPath, diagnostics, checkpoint);
        if (!SetAsideDeferredSessionEntries(roleCode, workingPath, newPath, database.MainMoves, diagnostics))
            return;

        var moved = 0;
        var skipped = 0;
        var held = 0;
        try
        {
            Directory.CreateDirectory(newPath);
            foreach (var entry in Directory.EnumerateFileSystemEntries(workingPath).ToList())
            {
                var name = Path.GetFileName(entry);

                // The pending marker belongs to the migration and is removed when the pass completes.
                if (string.Equals(name, PendingMarkerFileName, StringComparison.Ordinal))
                    continue;

                // Keep held database files and sidecars together in the working root.
                if (database.HeldNames.Contains(name))
                {
                    held++;
                    continue;
                }

                var target = Path.Combine(newPath, name);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    // Preserve any preexisting destination entry; migration never overwrites user data.
                    skipped++;
                    continue;
                }

                if (Directory.Exists(entry)) Directory.Move(entry, target);
                else File.Move(entry, target);
                moved++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Entries move whole. Unmoved files remain in the working root for the next launch.
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"merge_failed_after_{moved}_entries_{ex.GetType().Name}",
                DurationMilliseconds: ElapsedMilliseconds(started)));
            return;
        }

        if (held > 0)
        {
            // Keep the working-root name so the next launch resumes the held entries.
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"merged_{moved}_entries_skipped_{skipped}_held_{held}",
                DurationMilliseconds: ElapsedMilliseconds(started)));
            return;
        }

        // Remove the marker before restoring the source name so it cannot mark merged data as deferred.
        if (!TryRemovePendingMarker(roleCode, workingPath, diagnostics))
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"merged_{moved}_entries_skipped_{skipped}",
                DurationMilliseconds: ElapsedMilliseconds(started)));
            return;
        }

        // Remove an empty working root. Restore the source name when only skipped bootstrap entries remain.
        try
        {
            if (!Directory.EnumerateFileSystemEntries(workingPath).Any())
                Directory.Delete(workingPath);
            else if (!Directory.Exists(oldPath) && !File.Exists(oldPath))
                Directory.Move(workingPath, oldPath);
            else
                diagnostics.Record(new DiagnosticEvent(roleCode, Status: "merge_leftovers_kept_old_root_recreated"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Everything that could be merged is merged; the working root is retried on the next launch.
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"merge_cleanup_failed_{ex.GetType().Name}"));
        }

        diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"merged_{moved}_entries_skipped_{skipped}",
            DurationMilliseconds: ElapsedMilliseconds(started)));
    }

    /// <summary>What one merge pass does with the history database family in its
    /// working root. MainMoves: the main file moves this pass (with any sidecars left, all empty).
    /// HeldNames: entries this pass must leave in the working root.</summary>
    private sealed record HistoryDatabaseDecision(bool MainMoves, HashSet<string> HeldNames);

    /// <summary>
    /// Runs before anything in a working root is moved. A main file with no
    /// sidecar moves as it is. A main file with a sidecar is folded in place by <paramref name="checkpoint"/>
    /// and moves only when every sidecar is then absent or empty; otherwise the whole family is held in the
    /// working root this launch (never split). A sidecar with no main file beside it (its main moved on an
    /// earlier pass) is renamed to an inert ".stale-" name, and so is never placed beside a database.
    /// </summary>
    private static HistoryDatabaseDecision PrepareHistoryDatabase(string roleCode, string workingPath,
        IDiagnosticSink diagnostics, HistoryDatabaseCheckpoint? checkpoint)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        var main = Path.Combine(workingPath, HistoryDatabaseFileName);
        var sidecars = HistoryDatabaseSidecarSuffixes.Where(suffix => File.Exists(main + suffix)).ToList();
        if (!File.Exists(main))
        {
            foreach (var suffix in sidecars)
            {
                if (!QuarantineSidecar(roleCode, main + suffix, suffix, diagnostics))
                    held.Add(HistoryDatabaseFileName + suffix);
            }
            return new HistoryDatabaseDecision(false, held);
        }

        if (sidecars.Count == 0)
            return new HistoryDatabaseDecision(true, held);

        bool folded;
        string status;
        if (checkpoint is null)
        {
            folded = false;
            status = "no_checkpoint";
        }
        else
        {
            try
            {
                folded = checkpoint(main, out status);
            }
            catch (Exception ex)
            {
                // The Storage checkpoint catches its own SQLite and I/O failures; this catches anything else
                // (for example the native SQLite library failing to load) so the rest of the merge still runs.
                folded = false;
                status = ex.GetType().Name;
            }
        }

        if (folded)
        {
            var dirty = HistoryDatabaseSidecarSuffixes.FirstOrDefault(suffix => !IsMissingOrEmpty(roleCode, main + suffix, diagnostics));
            if (dirty is null)
            {
                diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"database_checkpointed_{status}"));
                return new HistoryDatabaseDecision(true, held);
            }
            status = $"sidecar_{dirty.TrimStart('-')}_not_empty";
        }

        foreach (var name in HistoryDatabaseFamilyNames)
        {
            if (File.Exists(Path.Combine(workingPath, name)))
                held.Add(name);
        }
        diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"database_checkpoint_failed_{status}_held"));
        return new HistoryDatabaseDecision(false, held);
    }

    /// <summary>Renames a sidecar that has no main file beside it to an inert
    /// ".stale-&lt;utc&gt;" name in place. The renamed file is then merged like any other entry, so it is
    /// kept, never deleted. False (logged) when the rename failed; the caller then holds it.</summary>
    private static bool QuarantineSidecar(string roleCode, string sidecarPath, string suffix, IDiagnosticSink diagnostics)
    {
        var code = suffix.TrimStart('-');
        try
        {
            File.Move(sidecarPath, sidecarPath + QuarantineInfix + UtcStamp());
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"database_sidecar_quarantined_{code}"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"database_sidecar_quarantine_failed_{code}_{ex.GetType().Name}_held"));
            return false;
        }
    }

    // Treat an unreadable size as non-empty so the caller holds the database.
    private static bool IsMissingOrEmpty(string roleCode, string path, IDiagnosticSink diagnostics)
    {
        try
        {
            var info = new FileInfo(path);
            return !info.Exists || info.Length == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"database_sidecar_size_unreadable_{ex.GetType().Name}"));
            return false;
        }
    }

    /// <summary>
    /// Moves destination entries created during a deferred session into a preserved subdirectory before
    /// merging. The history database and its sidecars move together. Returns false if any entry cannot be
    /// moved, leaving the working root available for a later launch.
    /// </summary>
    private static bool SetAsideDeferredSessionEntries(string roleCode, string workingPath, string newPath,
        bool databaseMoves, IDiagnosticSink diagnostics)
    {
        if (!Directory.Exists(newPath))
            return true;

        var names = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(workingPath))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, PendingMarkerFileName, StringComparison.Ordinal) ||
                    HistoryDatabaseFamilyNames.Contains(name) || BootstrapEntryNames.Contains(name))
                    continue;
                var existing = Path.Combine(newPath, name);
                if (File.Exists(existing) || Directory.Exists(existing))
                    names.Add(name);
            }

            if (databaseMoves)
                names.AddRange(HistoryDatabaseFamilyNames.Where(name => File.Exists(Path.Combine(newPath, name))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"deferred_session_scan_failed_{ex.GetType().Name}"));
            return false;
        }

        if (names.Count == 0)
            return true;

        var asidePath = Path.Combine(newPath, DeferredSessionFolderPrefix + UtcStamp());
        var setAside = 0;
        try
        {
            Directory.CreateDirectory(asidePath);
            foreach (var name in names)
            {
                var source = Path.Combine(newPath, name);
                var destination = Path.Combine(asidePath, name);
                if (Directory.Exists(source)) Directory.Move(source, destination);
                else File.Move(source, destination);
                setAside++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Record(new DiagnosticEvent(roleCode,
                Status: $"deferred_session_set_aside_failed_after_{setAside}_entries_{ex.GetType().Name}"));
            return false;
        }

        diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"deferred_session_set_aside_{setAside}_entries"));
        return true;
    }

    // The marker is consulted only in a legacy source root.
    private static bool HasPendingMarker(string oldPath) => File.Exists(Path.Combine(oldPath, PendingMarkerFileName));

    /// <summary>
    /// Records that migration was deferred while this launch creates the new root. Written only when the
    /// new root is absent or holds nothing but
    /// bootstrap folders (checked here, not assumed), so every non-bootstrap entry that later appears in
    /// the new root while the marker exists came from a deferred session. Never overwritten: a marker from
    /// an earlier deferral is kept as it is.
    /// </summary>
    private static void WritePendingMarker(string roleCode, string oldPath, string newPath, IDiagnosticSink diagnostics)
    {
        var markerPath = Path.Combine(oldPath, PendingMarkerFileName);
        if (File.Exists(markerPath))
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: "pending_marker_kept"));
            return;
        }

        if (Directory.Exists(newPath) && !ContainsOnlyBootstrapEntries(roleCode, newPath, diagnostics))
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: "pending_marker_not_written_new_root_has_data"));
            return;
        }

        try
        {
            using (var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write("Glideslope migration deferred at ");
                writer.Write(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.Write('\n');
            }
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: "pending_marker_written"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a marker, the source remains untouched when both roots contain data.
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"pending_marker_not_written_{ex.GetType().Name}"));
        }
    }

    /// <summary>Deletes the marker in <paramref name="rootPath"/>, the
    /// migration's own file (not user data). True when it is gone, or was never there.</summary>
    private static bool TryRemovePendingMarker(string roleCode, string rootPath, IDiagnosticSink diagnostics)
    {
        var markerPath = Path.Combine(rootPath, PendingMarkerFileName);
        if (!File.Exists(markerPath))
            return true;
        try
        {
            File.Delete(markerPath);
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: "pending_marker_removed"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Record(new DiagnosticEvent(roleCode, Status: $"pending_marker_not_removed_{ex.GetType().Name}"));
            return false;
        }
    }

    private static long ElapsedMilliseconds(long started) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private static (int FileCount, long TotalBytes) CopyDirectoryRecursive(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        var fileCount = 0;
        var totalBytes = 0L;
        foreach (var sourceDirectory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, sourceDirectory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(destinationRoot, relative);
            File.Copy(sourceFile, destinationFile, overwrite: false);
            fileCount++;
            totalBytes += new FileInfo(destinationFile).Length;
        }

        return (fileCount, totalBytes);
    }

    /// <summary>Confirms the copy holds exactly the same relative files with the same sizes as the
    /// source before the source is ever deleted.</summary>
    private static bool VerifyCopy(string sourceRoot, string destinationRoot)
    {
        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(sourceRoot, path), path => new FileInfo(path).Length, StringComparer.Ordinal);
        var destinationFiles = Directory.EnumerateFiles(destinationRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(destinationRoot, path), path => new FileInfo(path).Length, StringComparer.Ordinal);

        if (sourceFiles.Count != destinationFiles.Count)
            return false;

        foreach (var (relativePath, length) in sourceFiles)
        {
            if (!destinationFiles.TryGetValue(relativePath, out var copiedLength) || copiedLength != length)
                return false;
        }

        return true;
    }
}

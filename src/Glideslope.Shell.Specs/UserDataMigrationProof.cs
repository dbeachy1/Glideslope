using System.Globalization;
using Glideslope.Core;
using Glideslope.Storage;
using Microsoft.Data.Sqlite;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Exercises UserDataMigration.MigrateIfNeeded against an owned temp root standing in for the real
/// %LOCALAPPDATA%/XDG per-user directories, using the same GLIDESLOPE_PROOF_ROOT/ProofRoot override
/// the shell already relies on for isolated path resolution (see ProofRootOverridesUserPaths in
/// Program.cs). Never touches the real user profile. Each scenario below gets its own proof-root
/// subdirectory so they cannot interfere with one another through the shared ProofRoot.Current state.
/// </summary>
internal static class UserDataMigrationProof
{
    public static void Run(string tempRoot)
    {
        RunOldOnlyMoves(tempRoot);
        RunNewOnlyIsUntouched(tempRoot);
        RunBothPresentKeepsBoth(tempRoot);
        RunLogOnlyNewRootStillMigrates(tempRoot);
        RunCopyFallbackVerifiedAndOldRemoved(tempRoot);
        RunFailedCopyLeavesOldIntact(tempRoot);
        RunSecondPassIsNoOp(tempRoot);
        RunWindowsInstallLayoutMigrates(tempRoot);
        RunLockedOldRootIsDeferred(tempRoot);
        RunCopyPublishedOldNotRemoved(tempRoot);
        RunWindowsMergeWithLockedFileIsDeferred(tempRoot);
        RunLeftoverMergeRootIsFinished(tempRoot);
        RunLeftoverMergeRootReturnsUnmergeableEntries(tempRoot);
        RunDeferredWindowsLaunchIsSetAsideAndOldDataWins(tempRoot);
        RunDeferredLinuxLayoutIsSetAsideAndOldDataWins(tempRoot);
        RunSetAsideNeverTouchesRealData(tempRoot);
        RunMergeCheckpointsHistoryBeforeMoving(tempRoot);
        RunMergeHoldsDatabaseWhenCheckpointFails(tempRoot);
        RunStaleSidecarBesideMovedDatabaseIsQuarantined(tempRoot);
    }

    private static void RunOldOnlyMoves(string tempRoot)
    {
        var proofRoot = ConfigureProofRoot(tempRoot, "migration-old-only");
        var legacySettingsDir = Path.Combine(proofRoot, "config", "GlidePath");
        var legacyDataDir = Path.Combine(proofRoot, "data", "GlidePath");
        Directory.CreateDirectory(legacySettingsDir);
        File.WriteAllText(Path.Combine(legacySettingsDir, "settings.json"), "{\"schemaVersion\":2}");
        Directory.CreateDirectory(legacyDataDir);
        File.WriteAllText(Path.Combine(legacyDataDir, "usage-history.sqlite"), "sqlite-bytes");

        var diagnostics = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(diagnostics);

        var newSettingsDir = Path.Combine(proofRoot, "config", "Glideslope");
        var newDataDir = Path.Combine(proofRoot, "data", "Glideslope");
        Assert(!Directory.Exists(legacySettingsDir), "old-only settings directory should be gone after a move");
        Assert(!Directory.Exists(legacyDataDir), "old-only data directory should be gone after a move");
        Assert(File.ReadAllText(Path.Combine(newSettingsDir, "settings.json")) == "{\"schemaVersion\":2}", "moved settings content should be intact");
        Assert(File.ReadAllText(Path.Combine(newDataDir, "usage-history.sqlite")) == "sqlite-bytes", "moved history content should be intact");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "moved"), "settings migration should log a moved decision");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_data" && e.Status == "moved"), "data migration should log a moved decision");
    }

    private static void RunNewOnlyIsUntouched(string tempRoot)
    {
        var proofRoot = ConfigureProofRoot(tempRoot, "migration-new-only");
        var newSettingsDir = Path.Combine(proofRoot, "config", "Glideslope");
        Directory.CreateDirectory(newSettingsDir);
        File.WriteAllText(Path.Combine(newSettingsDir, "settings.json"), "{\"schemaVersion\":2,\"already\":\"current\"}");

        var diagnostics = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(diagnostics);

        var legacySettingsDir = Path.Combine(proofRoot, "config", "GlidePath");
        Assert(!Directory.Exists(legacySettingsDir), "a fresh install with no legacy directory should not create one");
        Assert(File.ReadAllText(Path.Combine(newSettingsDir, "settings.json")) == "{\"schemaVersion\":2,\"already\":\"current\"}", "new-only content must not be touched");
        Assert(!diagnostics.Events.Any(e => e.Code == "user_data_migration_settings"), "new-only should not even log a decision; there is nothing to migrate");
    }

    private static void RunBothPresentKeepsBoth(string tempRoot)
    {
        var proofRoot = ConfigureProofRoot(tempRoot, "migration-both-present");
        var legacySettingsDir = Path.Combine(proofRoot, "config", "GlidePath");
        var newSettingsDir = Path.Combine(proofRoot, "config", "Glideslope");
        Directory.CreateDirectory(legacySettingsDir);
        File.WriteAllText(Path.Combine(legacySettingsDir, "settings.json"), "{\"schemaVersion\":1,\"old\":true}");
        Directory.CreateDirectory(newSettingsDir);
        File.WriteAllText(Path.Combine(newSettingsDir, "settings.json"), "{\"schemaVersion\":2,\"new\":true}");

        var diagnostics = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(diagnostics);

        Assert(File.ReadAllText(Path.Combine(legacySettingsDir, "settings.json")) == "{\"schemaVersion\":1,\"old\":true}", "old directory content must survive when both exist");
        Assert(File.ReadAllText(Path.Combine(newSettingsDir, "settings.json")) == "{\"schemaVersion\":2,\"new\":true}", "new directory content must survive when both exist and stays the one in use");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "skipped_both_present"), "both-present should be logged as an explicit skip");
    }

    // On Windows, the file log creates the new root's
    // logs folder before the migration runs, so the new root "exists" while holding no real data.
    private static void RunLogOnlyNewRootStillMigrates(string tempRoot)
    {
        var proofRoot = ConfigureProofRoot(tempRoot, "migration-log-only-new-root");
        var legacySettingsDir = Path.Combine(proofRoot, "config", "GlidePath");
        var newSettingsDir = Path.Combine(proofRoot, "config", "Glideslope");
        Directory.CreateDirectory(Path.Combine(legacySettingsDir, "identity"));
        File.WriteAllText(Path.Combine(legacySettingsDir, "settings.json"), "{\"schemaVersion\":2,\"real\":true}");
        File.WriteAllText(Path.Combine(legacySettingsDir, "identity", "account-scope.key"), "key-bytes");
        Directory.CreateDirectory(Path.Combine(newSettingsDir, "logs"));
        File.WriteAllText(Path.Combine(newSettingsDir, "logs", "glideslope.log"), "early log line");

        var diagnostics = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(diagnostics);

        Assert(File.ReadAllText(Path.Combine(newSettingsDir, "settings.json")) == "{\"schemaVersion\":2,\"real\":true}",
            "real settings move in beside an early log folder");
        Assert(File.ReadAllText(Path.Combine(newSettingsDir, "identity", "account-scope.key")) == "key-bytes",
            "the account-scope key moves too, so history scopes stay the same");
        Assert(File.ReadAllText(Path.Combine(newSettingsDir, "logs", "glideslope.log")) == "early log line",
            "the early log is kept");
        Assert(!Directory.Exists(legacySettingsDir), "the emptied old root is removed");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "merged_2_entries_skipped_0"),
            "the merge is logged with what moved");
        Assert(!diagnostics.Events.Any(e => e.Status == "skipped_both_present"), "a log-only new root is not treated as real data");
    }

    private static void RunCopyFallbackVerifiedAndOldRemoved(string tempRoot)
    {
        var proofRoot = ConfigureProofRoot(tempRoot, "migration-copy-fallback");
        var legacyDataDir = Path.Combine(proofRoot, "data", "GlidePath");
        Directory.CreateDirectory(Path.Combine(legacyDataDir, "nested"));
        File.WriteAllText(Path.Combine(legacyDataDir, "usage-history.sqlite"), "sqlite-bytes-for-copy");
        File.WriteAllText(Path.Combine(legacyDataDir, "nested", "scope-key.bin"), "scope-key-bytes");

        var diagnostics = new RecordingDiagnosticSink();
        // forceCopyFallback stands in for a real cross-volume move, which cannot be manufactured
        // portably inside a single owned temp root; see UserDataMigration's internal overload comment.
        UserDataMigration.MigrateIfNeeded(diagnostics, forceCopyFallback: true);

        var newDataDir = Path.Combine(proofRoot, "data", "Glideslope");
        Assert(!Directory.Exists(legacyDataDir), "old directory should be removed only after its copy is verified");
        Assert(!Directory.Exists(newDataDir + ".glideslope-migrating"), "the staging directory should be published (renamed away), not left behind");
        Assert(File.ReadAllText(Path.Combine(newDataDir, "usage-history.sqlite")) == "sqlite-bytes-for-copy", "copied top-level file content should be intact");
        Assert(File.ReadAllText(Path.Combine(newDataDir, "nested", "scope-key.bin")) == "scope-key-bytes", "copied nested file content should be intact");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_data" && e.Status is not null && e.Status.StartsWith("copied_", StringComparison.Ordinal)), "a forced fallback should log a copied decision with counts");
    }

    private static void RunFailedCopyLeavesOldIntact(string tempRoot)
    {
        var proofRoot = ConfigureProofRoot(tempRoot, "migration-copy-failure");
        var legacyDataDir = Path.Combine(proofRoot, "data", "GlidePath");
        Directory.CreateDirectory(legacyDataDir);
        File.WriteAllText(Path.Combine(legacyDataDir, "usage-history.sqlite"), "sqlite-bytes-must-survive");

        // Pre-create the staging directory's own path as a plain file, so CopyDirectoryRecursive's
        // Directory.CreateDirectory(destinationRoot) throws deterministically, without needing a real
        // cross-volume boundary or a locked file.
        var newDataDir = Path.Combine(proofRoot, "data", "Glideslope");
        var stagingBlocker = newDataDir + ".glideslope-migrating";
        Directory.CreateDirectory(Path.GetDirectoryName(stagingBlocker)!);
        File.WriteAllText(stagingBlocker, "not a directory");

        var diagnostics = new RecordingDiagnosticSink();
        try
        {
            UserDataMigration.MigrateIfNeeded(diagnostics, forceCopyFallback: true);

            Assert(Directory.Exists(legacyDataDir), "old directory must remain after a failed copy");
            Assert(File.ReadAllText(Path.Combine(legacyDataDir, "usage-history.sqlite")) == "sqlite-bytes-must-survive", "old content must be untouched after a failed copy");
            Assert(!Directory.Exists(newDataDir), "no new directory should appear from a failed copy");
            // Creating a directory over a plain file throws IOException, which is included in the failure status.
            Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_data" && e.Status == "failed_IOException"), "a failed copy should be logged as failed, with the exception type");
        }
        finally
        {
            if (File.Exists(stagingBlocker))
                File.Delete(stagingBlocker);
        }
    }

    private static void RunSecondPassIsNoOp(string tempRoot)
    {
        var proofRoot = ConfigureProofRoot(tempRoot, "migration-second-pass");
        var legacySettingsDir = Path.Combine(proofRoot, "config", "GlidePath");
        Directory.CreateDirectory(legacySettingsDir);
        File.WriteAllText(Path.Combine(legacySettingsDir, "settings.json"), "{\"schemaVersion\":2}");

        var diagnostics = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(diagnostics);
        UserDataMigration.MigrateIfNeeded(diagnostics);

        var newSettingsDir = Path.Combine(proofRoot, "config", "Glideslope");
        Assert(File.ReadAllText(Path.Combine(newSettingsDir, "settings.json")) == "{\"schemaVersion\":2}", "a second run must not alter already-migrated content");
        var settingsDecisions = diagnostics.Events.Count(e => e.Code == "user_data_migration_settings" && e.Status == "moved");
        Assert(settingsDecisions == 1, "a second run should not repeat the move decision");
    }

    // Models a Windows install whose launcher and log writer precreate cache\bundle and logs\ in the
    // new root. Migration must still merge the legacy settings, history, and account-scope key.
    private static void RunWindowsInstallLayoutMigrates(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "migration-windows-install", "LocalAppData");
        var legacy = WindowsShapedPaths(localAppData, "GlidePath");
        var current = WindowsShapedPaths(localAppData, "Glideslope");
        var legacyRoot = legacy.DataDirectory;
        var newRoot = current.DataDirectory;

        Directory.CreateDirectory(Path.Combine(legacyRoot, "identity"));
        File.WriteAllText(legacy.SettingsFile, "{\"schemaVersion\":2,\"windows\":true}");
        File.WriteAllText(Path.Combine(legacyRoot, "usage-history.sqlite"), "sqlite-bytes-windows");
        File.WriteAllText(Path.Combine(legacyRoot, "identity", "account-scope.key"), "key-bytes-windows");

        Directory.CreateDirectory(current.LogsDirectory);
        File.WriteAllText(Path.Combine(current.LogsDirectory, "glideslope.log"), "early log line");
        Directory.CreateDirectory(Path.Combine(current.CacheDirectory, "bundle"));
        File.WriteAllText(Path.Combine(current.CacheDirectory, "bundle", "extracted.dll"), "bundle-bytes");

        var diagnostics = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(diagnostics, legacy, current, forceCopyFallback: false);

        Assert(File.ReadAllText(current.SettingsFile) == "{\"schemaVersion\":2,\"windows\":true}",
            "Windows install: settings.json moves under the new root");
        Assert(File.ReadAllText(Path.Combine(newRoot, "usage-history.sqlite")) == "sqlite-bytes-windows",
            "Windows install: usage-history.sqlite moves under the new root");
        Assert(File.ReadAllText(Path.Combine(newRoot, "identity", "account-scope.key")) == "key-bytes-windows",
            "Windows install: identity\\account-scope.key moves under the new root");
        Assert(File.ReadAllText(Path.Combine(current.CacheDirectory, "bundle", "extracted.dll")) == "bundle-bytes",
            "Windows install: the launcher's cache\\bundle is left in place");
        Assert(File.ReadAllText(Path.Combine(current.LogsDirectory, "glideslope.log")) == "early log line",
            "Windows install: the early log is kept");
        Assert(!Directory.Exists(legacyRoot), "Windows install: the emptied GlidePath root is removed");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "merged_3_entries_skipped_0"),
            "Windows install: the shared root is logged as a migration (a merge of three entries)");
        Assert(!diagnostics.Events.Any(e => e.Status == "skipped_both_present"),
            "Windows install: a root holding only logs\\ and cache\\ is not treated as real data");
    }

    // Mirrors DefaultUserPathProvider.BuildPaths' Windows branch (every path nests under
    // <LocalAppData>\<product>) against an owned temp root, so the spec runs on any OS.
    private static UserPaths WindowsShapedPaths(string localAppData, string productDirectoryName)
    {
        var root = Path.Combine(localAppData, productDirectoryName);
        return new UserPaths(
            Path.Combine(root, "settings.json"),
            root,
            Path.Combine(root, "runtime"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "logs"));
    }

    // When a running GlidePath holds a file open, migration must defer without copying or deleting
    // other files from the locked root.
    private static void RunLockedOldRootIsDeferred(string tempRoot)
    {
        var proofRoot = ConfigureProofRoot(tempRoot, "migration-locked-old-root");
        var legacyDataDir = Path.Combine(proofRoot, "data", "GlidePath");
        var newDataDir = Path.Combine(proofRoot, "data", "Glideslope");
        Directory.CreateDirectory(legacyDataDir);
        var lockedFile = Path.Combine(legacyDataDir, "usage-history.sqlite");
        var otherFile = Path.Combine(legacyDataDir, "other.bin");
        File.WriteAllText(lockedFile, "sqlite-bytes-locked");
        File.WriteAllText(otherFile, "other-bytes");

        var diagnostics = new RecordingDiagnosticSink();
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            UserDataMigration.MigrateIfNeeded(diagnostics);
        }

        if (!OperatingSystem.IsWindows())
        {
            // Unix file locks are advisory and do not block a directory rename, so the same layout
            // simply moves there; the point on Linux is that nothing is lost.
            Assert(File.ReadAllText(Path.Combine(newDataDir, "usage-history.sqlite")) == "sqlite-bytes-locked",
                "locked old root (Unix): the rename is not blocked and the content moves intact");
            Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_data" && e.Status == "moved"),
                "locked old root (Unix): the move is logged");
            return;
        }

        Assert(File.ReadAllText(lockedFile) == "sqlite-bytes-locked", "locked old root: the locked file is untouched");
        Assert(File.ReadAllText(otherFile) == "other-bytes", "locked old root: unlocked files are not deleted");
        Assert(!Directory.Exists(newDataDir), "locked old root: no new root is published");
        Assert(!Directory.Exists(newDataDir + ".glideslope-migrating"), "locked old root: no staging copy is made");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_data" && e.Status is not null &&
                e.Status.StartsWith("deferred_source_locked_", StringComparison.Ordinal)),
            "locked old root: the decision is logged as deferred_source_locked with the exception type");
        Assert(!diagnostics.Events.Any(e => e.Status is not null && e.Status.StartsWith("copied_", StringComparison.Ordinal)),
            "locked old root: the copy fallback is not used");
        // the deferral records itself in the old root.
        Assert(File.Exists(Path.Combine(legacyDataDir, "glideslope-migration-pending")),
            "locked old root: the deferral writes the pending marker into the old root");

        // With the lock released, the next launch completes the move.
        var retry = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(retry);
        Assert(File.ReadAllText(Path.Combine(newDataDir, "usage-history.sqlite")) == "sqlite-bytes-locked",
            "locked old root: the next launch moves the data once the lock is gone");
        Assert(!Directory.Exists(legacyDataDir), "locked old root: the old root is gone after the retried move");
        Assert(retry.Events.Any(e => e.Code == "user_data_migration_data" && e.Status == "moved"),
            "locked old root: the retried move is logged");
        // the marker moved with the root and is tidied away.
        Assert(!File.Exists(Path.Combine(newDataDir, "glideslope-migration-pending")),
            "locked old root: the marker that moved with the root is removed");
    }

    // a genuine cross-volume copy that is verified and published, but whose old
    // root cannot be fully removed afterwards, is reported as published_old_not_removed, not "failed".
    // Windows only: holding a file open without FileShare.Delete is what blocks its deletion there;
    // Unix has no equivalent, so the scenario cannot be built on Linux.
    private static void RunCopyPublishedOldNotRemoved(string tempRoot)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var proofRoot = ConfigureProofRoot(tempRoot, "migration-copy-old-not-removed");
        var legacyDataDir = Path.Combine(proofRoot, "data", "GlidePath");
        var newDataDir = Path.Combine(proofRoot, "data", "Glideslope");
        Directory.CreateDirectory(legacyDataDir);
        var heldFile = Path.Combine(legacyDataDir, "usage-history.sqlite");
        File.WriteAllText(heldFile, "sqlite-bytes-held");
        File.WriteAllText(Path.Combine(legacyDataDir, "other.bin"), "other-bytes");

        var diagnostics = new RecordingDiagnosticSink();
        using (new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // forceCopyFallback stands in for a real cross-volume move; see RunCopyFallbackVerifiedAndOldRemoved.
            UserDataMigration.MigrateIfNeeded(diagnostics, forceCopyFallback: true);
        }

        Assert(File.ReadAllText(Path.Combine(newDataDir, "usage-history.sqlite")) == "sqlite-bytes-held",
            "published copy: the held file was copied and published");
        Assert(File.ReadAllText(Path.Combine(newDataDir, "other.bin")) == "other-bytes",
            "published copy: the other file was copied and published");
        Assert(File.ReadAllText(heldFile) == "sqlite-bytes-held", "published copy: the held old file survives");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_data" && e.Status is not null &&
                e.Status.StartsWith("published_old_not_removed_", StringComparison.Ordinal)),
            "published copy: an old root that cannot be removed is logged as published_old_not_removed");
        Assert(!diagnostics.Events.Any(e => e.Status is not null && e.Status.StartsWith("failed", StringComparison.Ordinal)),
            "published copy: a published copy is not reported as failed");
    }

    // The Windows upgrade goes through the merge, which moves
    // entries one by one. With an old GlidePath still holding usage-history.sqlite, identity\ and
    // Claim the legacy root with one rename before merging. If an open file prevents the rename, defer the
    // whole merge so settings and identity cannot move without the held history database.
    private static void RunWindowsMergeWithLockedFileIsDeferred(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "migration-windows-merge-locked", "LocalAppData");
        var legacy = WindowsShapedPaths(localAppData, "GlidePath");
        var current = WindowsShapedPaths(localAppData, "Glideslope");
        var legacyRoot = legacy.DataDirectory;
        var newRoot = current.DataDirectory;
        var historyFile = Path.Combine(legacyRoot, "usage-history.sqlite");

        Directory.CreateDirectory(Path.Combine(legacyRoot, "identity"));
        File.WriteAllText(legacy.SettingsFile, "{\"schemaVersion\":2,\"locked\":true}");
        File.WriteAllText(historyFile, "sqlite-bytes-held");
        File.WriteAllText(Path.Combine(legacyRoot, "identity", "account-scope.key"), "key-bytes-held");
        Directory.CreateDirectory(current.LogsDirectory);
        Directory.CreateDirectory(Path.Combine(current.CacheDirectory, "bundle"));

        var diagnostics = new RecordingDiagnosticSink();
        using (new FileStream(historyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            UserDataMigration.MigrateIfNeeded(diagnostics, legacy, current, forceCopyFallback: false);
        }

        if (OperatingSystem.IsWindows())
        {
            Assert(File.ReadAllText(legacy.SettingsFile) == "{\"schemaVersion\":2,\"locked\":true}",
                "Windows merge, held file: settings.json stays in the old root");
            Assert(File.ReadAllText(Path.Combine(legacyRoot, "identity", "account-scope.key")) == "key-bytes-held",
                "Windows merge, held file: identity\\ stays in the old root");
            Assert(File.ReadAllText(historyFile) == "sqlite-bytes-held", "Windows merge, held file: the held history is untouched");
            Assert(!File.Exists(current.SettingsFile) && !Directory.Exists(Path.Combine(newRoot, "identity")),
                "Windows merge, held file: nothing moves into the new root");
            Assert(!Directory.EnumerateDirectories(localAppData, "GlidePath.migrating-*").Any(),
                "Windows merge, held file: no working root is left behind");
            Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status is not null &&
                    e.Status.StartsWith("deferred_source_locked_", StringComparison.Ordinal)),
                "Windows merge, held file: the merge is logged as deferred_source_locked with the exception type");
            // the deferral records itself in the old root.
            Assert(File.Exists(Path.Combine(legacyRoot, "glideslope-migration-pending")),
                "Windows merge, held file: the deferral writes the pending marker into the old root");
        }
        else
        {
            // Unix locks do not block a rename, so the merge completes there on the first call.
            Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "merged_3_entries_skipped_0"),
                "Windows-shaped merge on Unix: the held file does not block the merge");
            return;
        }

        // With the old app gone, the next launch merges everything.
        var retry = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(retry, legacy, current, forceCopyFallback: false);
        Assert(File.ReadAllText(Path.Combine(newRoot, "usage-history.sqlite")) == "sqlite-bytes-held",
            "Windows merge, retried: the history moves once the file is released");
        Assert(File.ReadAllText(current.SettingsFile) == "{\"schemaVersion\":2,\"locked\":true}",
            "Windows merge, retried: settings.json moves");
        Assert(!Directory.Exists(legacyRoot), "Windows merge, retried: the old root is removed");
        Assert(retry.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "merged_3_entries_skipped_0"),
            "Windows merge, retried: the merge is logged");
        // the marker never reaches the new root and is removed with the pass.
        Assert(retry.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "pending_marker_removed"),
            "Windows merge, retried: the pending marker is removed once the merge completes");
        Assert(!File.Exists(Path.Combine(newRoot, "glideslope-migration-pending")),
            "Windows merge, retried: the pending marker is not merged into the new root");
    }

    // A merge interrupted after its working-root rename (a crash, or an
    // entry that failed to move) is finished on the next launch, even though the new root already holds
    // real data from the entries that did move.
    private static void RunLeftoverMergeRootIsFinished(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "migration-leftover-merge", "LocalAppData");
        var legacy = WindowsShapedPaths(localAppData, "GlidePath");
        var current = WindowsShapedPaths(localAppData, "Glideslope");
        var newRoot = current.DataDirectory;
        var leftover = Path.Combine(localAppData, "GlidePath.migrating-20260926T120000000Z");

        Directory.CreateDirectory(current.LogsDirectory);
        Directory.CreateDirectory(Path.Combine(newRoot, "identity"));
        File.WriteAllText(current.SettingsFile, "{\"schemaVersion\":2,\"already\":\"moved\"}");
        File.WriteAllText(Path.Combine(newRoot, "identity", "account-scope.key"), "key-bytes-moved");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "usage-history.sqlite"), "sqlite-bytes-stranded");

        var diagnostics = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(diagnostics, legacy, current, forceCopyFallback: false);

        Assert(File.ReadAllText(Path.Combine(newRoot, "usage-history.sqlite")) == "sqlite-bytes-stranded",
            "leftover merge: the stranded history is moved into the new root");
        Assert(File.ReadAllText(current.SettingsFile) == "{\"schemaVersion\":2,\"already\":\"moved\"}",
            "leftover merge: already-moved settings are untouched");
        Assert(!Directory.Exists(leftover), "leftover merge: the emptied working root is removed");
        Assert(!Directory.Exists(legacy.DataDirectory), "leftover merge: the old root name is not recreated");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "resumed_partial_migration"),
            "leftover merge: the resume is logged as resumed_partial_migration");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "merged_1_entries_skipped_0"),
            "leftover merge: the finished merge is logged with what moved");
        Assert(!diagnostics.Events.Any(e => e.Status == "skipped_both_present"),
            "leftover merge: real data from the partial merge does not trigger skipped_both_present");

        var second = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(second, legacy, current, forceCopyFallback: false);
        Assert(!second.Events.Any(e => e.Code == "user_data_migration_settings"),
            "leftover merge: once finished, a later launch has nothing to do");
    }

    // Entries that cannot be merged because the target already exists
    // (old bootstrap folders such as logs\) are returned to the old root name when the merge pass
    // completes, so the working root is not resumed on every launch.
    private static void RunLeftoverMergeRootReturnsUnmergeableEntries(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "migration-leftover-returned", "LocalAppData");
        var legacy = WindowsShapedPaths(localAppData, "GlidePath");
        var current = WindowsShapedPaths(localAppData, "Glideslope");
        var leftover = Path.Combine(localAppData, "GlidePath.migrating-20260926T120000000Z");

        Directory.CreateDirectory(current.LogsDirectory);
        File.WriteAllText(current.SettingsFile, "{\"schemaVersion\":2}");
        Directory.CreateDirectory(Path.Combine(leftover, "logs"));
        File.WriteAllText(Path.Combine(leftover, "logs", "glidepath.log"), "old log line");
        File.WriteAllText(Path.Combine(leftover, "usage-history.sqlite"), "sqlite-bytes-returned-case");

        var diagnostics = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(diagnostics, legacy, current, forceCopyFallback: false);

        Assert(File.ReadAllText(Path.Combine(current.DataDirectory, "usage-history.sqlite")) == "sqlite-bytes-returned-case",
            "returned leftovers: the mergeable entry moves");
        Assert(!Directory.Exists(leftover), "returned leftovers: the working root no longer exists");
        Assert(File.ReadAllText(Path.Combine(legacy.DataDirectory, "logs", "glidepath.log")) == "old log line",
            "returned leftovers: the unmergeable old logs\\ is kept under the old root name");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "merged_1_entries_skipped_1"),
            "returned leftovers: the merge logs one moved and one skipped");

        var second = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(second, legacy, current, forceCopyFallback: false);
        Assert(!second.Events.Any(e => e.Status == "resumed_partial_migration"),
            "returned leftovers: a later launch does not resume the merge again");
    }

    // An old GlidePath history database may still be open, so migration defers and the app can create new
    // files in the destination. On a later launch, the legacy data takes precedence and conflicting new
    // files are kept aside.
    private static void RunDeferredWindowsLaunchIsSetAsideAndOldDataWins(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "migration-deferred-windows", "LocalAppData");
        var legacy = WindowsShapedPaths(localAppData, "GlidePath");
        var current = WindowsShapedPaths(localAppData, "Glideslope");
        var legacyRoot = legacy.DataDirectory;
        var newRoot = current.DataDirectory;
        var legacyHistory = Path.Combine(legacyRoot, "usage-history.sqlite");
        var marker = Path.Combine(legacyRoot, "glideslope-migration-pending");

        Directory.CreateDirectory(Path.Combine(legacyRoot, "identity"));
        File.WriteAllText(legacy.SettingsFile, "{\"schemaVersion\":2,\"glidepath\":true}");
        File.WriteAllText(legacyHistory, "sqlite-bytes-glidepath");
        File.WriteAllText(Path.Combine(legacyRoot, "identity", "account-scope.key"), "key-bytes-glidepath");
        Directory.CreateDirectory(current.LogsDirectory);
        File.WriteAllText(Path.Combine(current.LogsDirectory, "glideslope.log"), "early log line");
        Directory.CreateDirectory(Path.Combine(current.CacheDirectory, "bundle"));

        var first = new RecordingDiagnosticSink();
        using (new FileStream(legacyHistory, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            UserDataMigration.MigrateIfNeeded(first, legacy, current, forceCopyFallback: false);
        }

        if (!OperatingSystem.IsWindows())
        {
            // Unix locks do not block the rename, so the merge completes on the first launch: no deferral, no marker.
            Assert(File.ReadAllText(Path.Combine(newRoot, "usage-history.sqlite")) == "sqlite-bytes-glidepath",
                "deferred session (Unix): the history merges on the first launch");
            Assert(!first.Events.Any(e => e.Status == "pending_marker_written"), "deferred session (Unix): no marker is written");
            return;
        }

        Assert(first.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status is not null &&
                e.Status.StartsWith("deferred_source_locked_", StringComparison.Ordinal)),
            "deferred session: the first launch defers on the held history");
        Assert(first.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "pending_marker_written"),
            "deferred session: the deferral writes the pending marker");
        Assert(File.Exists(marker), "deferred session: the marker is in the GlidePath root");
        Assert(!File.Exists(Path.Combine(newRoot, "glideslope-migration-pending")), "deferred session: no marker in the new root");

        // The rest of the first launch, as the app runs it: AccountScopeKeyStore creates identity\, the history
        // store creates the database, SettingsStore saves defaults; the app is then killed, leaving a -wal.
        File.WriteAllText(current.SettingsFile, "{\"schemaVersion\":2,\"defaults\":true}");
        File.WriteAllText(Path.Combine(newRoot, "usage-history.sqlite"), "sqlite-bytes-deferred");
        File.WriteAllText(Path.Combine(newRoot, "usage-history.sqlite-wal"), "wal-bytes-deferred");
        Directory.CreateDirectory(Path.Combine(newRoot, "identity"));
        File.WriteAllText(Path.Combine(newRoot, "identity", "account-scope.key"), "key-bytes-deferred");

        // Second launch: GlidePath still running.
        var second = new RecordingDiagnosticSink();
        using (new FileStream(legacyHistory, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            UserDataMigration.MigrateIfNeeded(second, legacy, current, forceCopyFallback: false);
        }
        Assert(second.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "pending_marker_found"),
            "deferred session, still held: the marker is found");
        Assert(second.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status is not null &&
                e.Status.StartsWith("deferred_source_locked_", StringComparison.Ordinal)),
            "deferred session, still held: the launch defers again");
        Assert(second.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "pending_marker_kept"),
            "deferred session, still held: the marker is kept");
        Assert(File.ReadAllText(current.SettingsFile) == "{\"schemaVersion\":2,\"defaults\":true}",
            "deferred session, still held: nothing is set aside while the old root cannot be claimed");
        Assert(Directory.GetDirectories(newRoot, "deferred-session-*").Length == 0,
            "deferred session, still held: no set-aside folder yet");
        Assert(!second.Events.Any(e => e.Status == "skipped_both_present"), "deferred session, still held: not skipped");

        // Third launch: GlidePath has exited.
        var third = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(third, legacy, current, forceCopyFallback: false);

        Assert(File.ReadAllText(current.SettingsFile) == "{\"schemaVersion\":2,\"glidepath\":true}",
            "deferred session, released: the GlidePath settings win");
        Assert(File.ReadAllText(Path.Combine(newRoot, "usage-history.sqlite")) == "sqlite-bytes-glidepath",
            "deferred session, released: the GlidePath history wins");
        Assert(File.ReadAllText(Path.Combine(newRoot, "identity", "account-scope.key")) == "key-bytes-glidepath",
            "deferred session, released: the GlidePath account-scope key wins");
        Assert(!File.Exists(Path.Combine(newRoot, "usage-history.sqlite-wal")),
            "deferred session, released: the deferred session's -wal is not left beside the GlidePath database");
        var aside = Directory.GetDirectories(newRoot, "deferred-session-*");
        Assert(aside.Length == 1, "deferred session, released: one set-aside folder");
        Assert(File.ReadAllText(Path.Combine(aside[0], "settings.json")) == "{\"schemaVersion\":2,\"defaults\":true}",
            "deferred session, released: the deferred settings are kept aside");
        Assert(File.ReadAllText(Path.Combine(aside[0], "usage-history.sqlite")) == "sqlite-bytes-deferred",
            "deferred session, released: the deferred history is kept aside");
        Assert(File.ReadAllText(Path.Combine(aside[0], "usage-history.sqlite-wal")) == "wal-bytes-deferred",
            "deferred session, released: the deferred -wal is kept aside with its database");
        Assert(File.ReadAllText(Path.Combine(aside[0], "identity", "account-scope.key")) == "key-bytes-deferred",
            "deferred session, released: the deferred key is kept aside");
        Assert(File.ReadAllText(Path.Combine(current.LogsDirectory, "glideslope.log")) == "early log line",
            "deferred session, released: the log is untouched");
        Assert(!Directory.Exists(legacyRoot), "deferred session, released: the GlidePath root is gone");
        Assert(!Directory.EnumerateDirectories(localAppData, "GlidePath.migrating-*").Any(),
            "deferred session, released: no working root is left");
        Assert(third.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "pending_marker_found"),
            "deferred session, released: the marker is found");
        Assert(third.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "deferred_session_set_aside_4_entries"),
            "deferred session, released: four entries are set aside");
        Assert(third.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "merged_3_entries_skipped_0"),
            "deferred session, released: the three GlidePath entries merge");
        Assert(third.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "pending_marker_removed"),
            "deferred session, released: the marker is removed");
        Assert(!third.Events.Any(e => e.Status == "skipped_both_present"), "deferred session, released: not skipped");

        var fourth = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(fourth, legacy, current, forceCopyFallback: false);
        Assert(!fourth.Events.Any(e => e.Code == "user_data_migration_settings"), "deferred session: a later launch has nothing to do");
        Assert(Directory.GetDirectories(newRoot, "deferred-session-*").Length == 1, "deferred session: the set-aside folder is kept");
    }

    // the same sequence on the Linux layout (settings, data and cache in separate
    // roots; the proof root gives that shape). Only the data root is held, so only it defers.
    private static void RunDeferredLinuxLayoutIsSetAsideAndOldDataWins(string tempRoot)
    {
        var proofRoot = ConfigureProofRoot(tempRoot, "migration-deferred-linux-layout");
        var legacySettingsDir = Path.Combine(proofRoot, "config", "GlidePath");
        var legacyDataDir = Path.Combine(proofRoot, "data", "GlidePath");
        var newSettingsDir = Path.Combine(proofRoot, "config", "Glideslope");
        var newDataDir = Path.Combine(proofRoot, "data", "Glideslope");
        var legacyHistory = Path.Combine(legacyDataDir, "usage-history.sqlite");
        Directory.CreateDirectory(legacySettingsDir);
        File.WriteAllText(Path.Combine(legacySettingsDir, "settings.json"), "{\"schemaVersion\":2,\"glidepath\":true}");
        Directory.CreateDirectory(Path.Combine(legacyDataDir, "identity"));
        File.WriteAllText(legacyHistory, "sqlite-bytes-glidepath");
        File.WriteAllText(Path.Combine(legacyDataDir, "identity", "account-scope.key"), "key-bytes-glidepath");

        var first = new RecordingDiagnosticSink();
        using (new FileStream(legacyHistory, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            UserDataMigration.MigrateIfNeeded(first);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert(first.Events.Any(e => e.Code == "user_data_migration_data" && e.Status == "moved"),
                "deferred Linux layout (Unix): the held file does not block the move");
            Assert(File.ReadAllText(Path.Combine(newDataDir, "usage-history.sqlite")) == "sqlite-bytes-glidepath",
                "deferred Linux layout (Unix): the history moves intact");
            return;
        }

        Assert(first.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "moved"),
            "deferred Linux layout: the settings root is not held and moves");
        Assert(first.Events.Any(e => e.Code == "user_data_migration_data" && e.Status is not null &&
                e.Status.StartsWith("deferred_source_locked_", StringComparison.Ordinal)),
            "deferred Linux layout: the data root defers");
        Assert(File.Exists(Path.Combine(legacyDataDir, "glideslope-migration-pending")),
            "deferred Linux layout: the marker is written into the old data root");
        Assert(!Directory.Exists(newDataDir), "deferred Linux layout: no new data root yet");

        // The rest of the first launch: the key store and the history store create the data root.
        Directory.CreateDirectory(Path.Combine(newDataDir, "identity"));
        File.WriteAllText(Path.Combine(newDataDir, "identity", "account-scope.key"), "key-bytes-deferred");
        File.WriteAllText(Path.Combine(newDataDir, "usage-history.sqlite"), "sqlite-bytes-deferred");
        File.WriteAllText(Path.Combine(newDataDir, "usage-history.sqlite-shm"), "shm-bytes-deferred");

        var second = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(second);

        Assert(File.ReadAllText(Path.Combine(newDataDir, "usage-history.sqlite")) == "sqlite-bytes-glidepath",
            "deferred Linux layout: the GlidePath history wins");
        Assert(File.ReadAllText(Path.Combine(newDataDir, "identity", "account-scope.key")) == "key-bytes-glidepath",
            "deferred Linux layout: the GlidePath key wins");
        Assert(!File.Exists(Path.Combine(newDataDir, "usage-history.sqlite-shm")),
            "deferred Linux layout: the deferred -shm is not left beside the GlidePath database");
        var aside = Directory.GetDirectories(newDataDir, "deferred-session-*");
        Assert(aside.Length == 1 &&
               File.ReadAllText(Path.Combine(aside[0], "usage-history.sqlite")) == "sqlite-bytes-deferred" &&
               File.ReadAllText(Path.Combine(aside[0], "usage-history.sqlite-shm")) == "shm-bytes-deferred" &&
               File.ReadAllText(Path.Combine(aside[0], "identity", "account-scope.key")) == "key-bytes-deferred",
            "deferred Linux layout: the deferred files are kept aside in the data root");
        Assert(File.ReadAllText(Path.Combine(newSettingsDir, "settings.json")) == "{\"schemaVersion\":2,\"glidepath\":true}",
            "deferred Linux layout: the settings moved on the first launch are untouched");
        Assert(!Directory.Exists(legacyDataDir), "deferred Linux layout: the old data root is gone");
        Assert(second.Events.Any(e => e.Code == "user_data_migration_data" && e.Status == "deferred_session_set_aside_3_entries"),
            "deferred Linux layout: three entries are set aside");
        Assert(second.Events.Any(e => e.Code == "user_data_migration_data" && e.Status == "merged_2_entries_skipped_0"),
            "deferred Linux layout: the two GlidePath entries merge");
        Assert(!second.Events.Any(e => e.Code == "user_data_migration_settings"),
            "deferred Linux layout: the settings root has nothing more to do");
    }

    // the set-aside never touches real Glideslope data. A user with no GlidePath
    // (even with a stray marker in the new root) and a GlidePath root that reappears after a completed
    // migration (no marker) both leave the Glideslope data exactly where it is.
    private static void RunSetAsideNeverTouchesRealData(string tempRoot)
    {
        var noLegacy = ConfigureProofRoot(tempRoot, "migration-no-legacy-stray-marker");
        var noLegacyData = Path.Combine(noLegacy, "data", "Glideslope");
        Directory.CreateDirectory(Path.Combine(noLegacyData, "identity"));
        File.WriteAllText(Path.Combine(noLegacyData, "usage-history.sqlite"), "sqlite-bytes-real");
        File.WriteAllText(Path.Combine(noLegacyData, "identity", "account-scope.key"), "key-bytes-real");
        File.WriteAllText(Path.Combine(noLegacyData, "glideslope-migration-pending"), "stray marker");
        var none = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(none);
        Assert(File.ReadAllText(Path.Combine(noLegacyData, "usage-history.sqlite")) == "sqlite-bytes-real" &&
               File.ReadAllText(Path.Combine(noLegacyData, "identity", "account-scope.key")) == "key-bytes-real",
            "no GlidePath: real data is untouched");
        Assert(Directory.GetDirectories(noLegacyData, "deferred-session-*").Length == 0, "no GlidePath: nothing is set aside");
        Assert(!none.Events.Any(e => e.Code == "user_data_migration_data"), "no GlidePath: no decision at all");

        var reappeared = ConfigureProofRoot(tempRoot, "migration-reappeared-legacy");
        var realData = Path.Combine(reappeared, "data", "Glideslope");
        var reappearedLegacy = Path.Combine(reappeared, "data", "GlidePath");
        Directory.CreateDirectory(Path.Combine(realData, "identity"));
        File.WriteAllText(Path.Combine(realData, "usage-history.sqlite"), "sqlite-bytes-real");
        File.WriteAllText(Path.Combine(realData, "identity", "account-scope.key"), "key-bytes-real");
        Directory.CreateDirectory(Path.Combine(reappearedLegacy, "identity"));
        File.WriteAllText(Path.Combine(reappearedLegacy, "usage-history.sqlite"), "sqlite-bytes-fresh-glidepath");
        File.WriteAllText(Path.Combine(reappearedLegacy, "identity", "account-scope.key"), "key-bytes-fresh-glidepath");
        var both = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(both);
        Assert(File.ReadAllText(Path.Combine(realData, "usage-history.sqlite")) == "sqlite-bytes-real" &&
               File.ReadAllText(Path.Combine(realData, "identity", "account-scope.key")) == "key-bytes-real",
            "reappeared GlidePath: real data is untouched");
        Assert(File.ReadAllText(Path.Combine(reappearedLegacy, "usage-history.sqlite")) == "sqlite-bytes-fresh-glidepath",
            "reappeared GlidePath: the reappeared root is untouched");
        Assert(Directory.GetDirectories(realData, "deferred-session-*").Length == 0, "reappeared GlidePath: nothing is set aside");
        Assert(both.Events.Any(e => e.Code == "user_data_migration_data" && e.Status == "skipped_both_present"),
            "reappeared GlidePath: without a marker it is skipped as both present");
    }

    // a GlidePath database left with rows only in its -wal (the app was killed)
    // is folded before the merge moves it, with the real Storage checkpoint, so no row is lost and no -wal
    // travels separately.
    private static void RunMergeCheckpointsHistoryBeforeMoving(string tempRoot)
    {
        var scenarioRoot = Path.Combine(tempRoot, "migration-checkpoint");
        var localAppData = Path.Combine(scenarioRoot, "LocalAppData");
        var legacy = WindowsShapedPaths(localAppData, "GlidePath");
        var current = WindowsShapedPaths(localAppData, "Glideslope");
        SeedDatabaseWithUncheckpointedRows(Path.Combine(scenarioRoot, "seed"), legacy.DataDirectory, rows: 50);
        File.WriteAllText(legacy.SettingsFile, "{\"schemaVersion\":2}");
        Assert(new FileInfo(Path.Combine(legacy.DataDirectory, "usage-history.sqlite-wal")).Length > 0,
            "checkpoint merge: the GlidePath database starts with rows only in its -wal");
        Directory.CreateDirectory(current.LogsDirectory);

        var diagnostics = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(diagnostics, legacy, current, SqliteHistoryCheckpoint.TryCheckpointInPlace, forceCopyFallback: false);

        var moved = Path.Combine(current.DataDirectory, "usage-history.sqlite");
        Assert(IsMissingOrEmpty(moved + "-wal") && IsMissingOrEmpty(moved + "-shm") && IsMissingOrEmpty(moved + "-journal"),
            "checkpoint merge: no non-empty sidecar reaches the new root");
        Assert(CountSamples(moved) == 50, "checkpoint merge: every row from the -wal is in the moved database");
        Assert(Directory.GetFiles(current.DataDirectory, "*.pre-checkpoint-*").Length == 0,
            "checkpoint merge: the checkpoint's safety copy is removed once every frame is folded");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status is not null &&
                e.Status.StartsWith("database_checkpointed_log_", StringComparison.Ordinal)),
            "checkpoint merge: the checkpoint is logged with its frame counts");
        Assert(!diagnostics.Events.Any(e => e.Status is not null && e.Status.Contains("_held", StringComparison.Ordinal)),
            "checkpoint merge: nothing is held");
        Assert(!Directory.Exists(legacy.DataDirectory), "checkpoint merge: the GlidePath root is gone");
        Assert(!Directory.EnumerateDirectories(localAppData, "GlidePath.migrating-*").Any(),
            "checkpoint merge: no working root is left");
    }

    // when the checkpoint fails, the database and its -wal stay together in the
    // working root, everything else merges, and the next launch (checkpoint working) moves the database and
    // sets aside the fresh one the in-between launch created.
    private static void RunMergeHoldsDatabaseWhenCheckpointFails(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "migration-checkpoint-held", "LocalAppData");
        var legacy = WindowsShapedPaths(localAppData, "GlidePath");
        var current = WindowsShapedPaths(localAppData, "Glideslope");
        var newRoot = current.DataDirectory;
        Directory.CreateDirectory(Path.Combine(legacy.DataDirectory, "identity"));
        File.WriteAllText(legacy.SettingsFile, "{\"schemaVersion\":2,\"held\":true}");
        File.WriteAllText(Path.Combine(legacy.DataDirectory, "identity", "account-scope.key"), "key-bytes-held-case");
        File.WriteAllText(Path.Combine(legacy.DataDirectory, "usage-history.sqlite"), "sqlite-bytes-held-case");
        File.WriteAllText(Path.Combine(legacy.DataDirectory, "usage-history.sqlite-wal"), "wal-bytes-held-case");
        Directory.CreateDirectory(current.LogsDirectory);

        HistoryDatabaseCheckpoint failing = (string _, out string status) =>
        {
            status = "injected_failure";
            return false;
        };
        var first = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(first, legacy, current, failing, forceCopyFallback: false);

        var working = Directory.GetDirectories(localAppData, "GlidePath.migrating-*");
        Assert(working.Length == 1, "held database: the working root is kept under its .migrating- name");
        Assert(File.ReadAllText(Path.Combine(working[0], "usage-history.sqlite")) == "sqlite-bytes-held-case" &&
               File.ReadAllText(Path.Combine(working[0], "usage-history.sqlite-wal")) == "wal-bytes-held-case",
            "held database: the database and its -wal stay together, unchanged");
        Assert(!File.Exists(Path.Combine(newRoot, "usage-history.sqlite")) && !File.Exists(Path.Combine(newRoot, "usage-history.sqlite-wal")),
            "held database: neither file reaches the new root");
        Assert(File.ReadAllText(current.SettingsFile) == "{\"schemaVersion\":2,\"held\":true}" &&
               File.ReadAllText(Path.Combine(newRoot, "identity", "account-scope.key")) == "key-bytes-held-case",
            "held database: the other entries merge");
        Assert(!Directory.Exists(legacy.DataDirectory), "held database: the old root name is not recreated");
        Assert(first.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "database_checkpoint_failed_injected_failure_held"),
            "held database: the failed checkpoint is logged with its status");
        Assert(first.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "merged_2_entries_skipped_0_held_2"),
            "held database: the merge is logged with what was held");

        // The rest of that launch: the history store creates a fresh database in the new root.
        File.WriteAllText(Path.Combine(newRoot, "usage-history.sqlite"), "sqlite-bytes-fresh");

        HistoryDatabaseCheckpoint truncating = (string path, out string status) =>
        {
            File.WriteAllBytes(path + "-wal", Array.Empty<byte>());
            status = "injected_truncate";
            return true;
        };
        var second = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(second, legacy, current, truncating, forceCopyFallback: false);

        Assert(File.ReadAllText(Path.Combine(newRoot, "usage-history.sqlite")) == "sqlite-bytes-held-case",
            "held database, resumed: the GlidePath database moves");
        Assert(IsMissingOrEmpty(Path.Combine(newRoot, "usage-history.sqlite-wal")),
            "held database, resumed: only an empty -wal travels with it");
        var aside = Directory.GetDirectories(newRoot, "deferred-session-*");
        Assert(aside.Length == 1 && File.ReadAllText(Path.Combine(aside[0], "usage-history.sqlite")) == "sqlite-bytes-fresh",
            "held database, resumed: the in-between launch's fresh database is kept aside");
        Assert(!Directory.EnumerateDirectories(localAppData, "GlidePath.migrating-*").Any(),
            "held database, resumed: the working root is gone");
        Assert(second.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "resumed_partial_migration"),
            "held database, resumed: the resume is logged");
        Assert(second.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "database_checkpointed_injected_truncate"),
            "held database, resumed: the checkpoint is logged");
        Assert(second.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "deferred_session_set_aside_1_entries"),
            "held database, resumed: the fresh database is set aside");
        Assert(second.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "merged_2_entries_skipped_0"),
            "held database, resumed: the database and its empty -wal merge");
    }

    // If a merge moves the main database but leaves -wal and -shm sidecars behind, quarantine the stale
    // sidecars so SQLite cannot replay them against the moved database.
    private static void RunStaleSidecarBesideMovedDatabaseIsQuarantined(string tempRoot)
    {
        var localAppData = Path.Combine(tempRoot, "migration-stale-sidecar", "LocalAppData");
        var legacy = WindowsShapedPaths(localAppData, "GlidePath");
        var current = WindowsShapedPaths(localAppData, "Glideslope");
        var newRoot = current.DataDirectory;
        var leftover = Path.Combine(localAppData, "GlidePath.migrating-20260926T120000000Z");
        Directory.CreateDirectory(current.LogsDirectory);
        File.WriteAllText(Path.Combine(newRoot, "usage-history.sqlite"), "sqlite-bytes-live");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "usage-history.sqlite-wal"), "wal-bytes-stale");
        File.WriteAllText(Path.Combine(leftover, "usage-history.sqlite-shm"), "shm-bytes-stale");

        var diagnostics = new RecordingDiagnosticSink();
        UserDataMigration.MigrateIfNeeded(diagnostics, legacy, current, forceCopyFallback: false);

        Assert(File.ReadAllText(Path.Combine(newRoot, "usage-history.sqlite")) == "sqlite-bytes-live",
            "stale sidecar: the live database is untouched");
        Assert(!File.Exists(Path.Combine(newRoot, "usage-history.sqlite-wal")) && !File.Exists(Path.Combine(newRoot, "usage-history.sqlite-shm")),
            "stale sidecar: no -wal or -shm is placed beside the live database");
        var staleWal = Directory.GetFiles(newRoot, "usage-history.sqlite-wal.stale-*");
        var staleShm = Directory.GetFiles(newRoot, "usage-history.sqlite-shm.stale-*");
        Assert(staleWal.Length == 1 && File.ReadAllText(staleWal[0]) == "wal-bytes-stale",
            "stale sidecar: the stale -wal is kept under an inert name");
        Assert(staleShm.Length == 1 && File.ReadAllText(staleShm[0]) == "shm-bytes-stale",
            "stale sidecar: the stale -shm is kept under an inert name");
        Assert(Directory.GetDirectories(newRoot, "deferred-session-*").Length == 0,
            "stale sidecar: nothing of the live database is set aside");
        Assert(!Directory.Exists(leftover), "stale sidecar: the working root is gone");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "database_sidecar_quarantined_wal") &&
               diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "database_sidecar_quarantined_shm"),
            "stale sidecar: each quarantine is logged");
        Assert(diagnostics.Events.Any(e => e.Code == "user_data_migration_settings" && e.Status == "merged_2_entries_skipped_0"),
            "stale sidecar: the two inert files merge");
    }

    // helpers for the checkpoint scenarios (owned temp paths only).
    private static bool IsMissingOrEmpty(string path)
    {
        var info = new FileInfo(path);
        return !info.Exists || info.Length == 0;
    }

    /// <summary>Builds a WAL-mode database with uncheckpointed rows and copies its main file and -wal into
    /// <paramref name="destinationDirectory"/> while the writer remains open.</summary>
    private static void SeedDatabaseWithUncheckpointedRows(string seedDirectory, string destinationDirectory, int rows)
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
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ConfigureProofRoot(string tempRoot, string scenarioName)
    {
        var proofRoot = Path.Combine(tempRoot, scenarioName);
        Directory.CreateDirectory(proofRoot);
        Assert(ProofRoot.Configure([ProofRoot.ArgumentName, proofRoot]).Succeeded, "scenario proof root should be accepted");
        return proofRoot;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL: {message}");
    }

    private sealed class RecordingDiagnosticSink : IDiagnosticSink
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public void Record(DiagnosticEvent diagnostic) => Events.Add(diagnostic);
    }
}

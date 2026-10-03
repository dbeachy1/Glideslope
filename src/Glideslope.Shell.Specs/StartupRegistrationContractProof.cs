using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Exercises ownership, stale-target, removal, and published-launcher contracts
/// using only a task-owned temporary root. The same artifact rules run on both
/// Windows and Linux; the current host selects the platform entry format.
/// </summary>
/// <remarks>Platform rules are selected through <see cref="StartupRegistrationOptions"/> and run on either
/// host. Windows registry behavior uses a file-backed store under the task-owned root; only installed-launcher
/// resolution depends on the host.</remarks>
internal static class StartupRegistrationContractProof
{
    public static async Task RunAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"Glideslope.StartupProof-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            await RunLauncherResolutionProofAsync(tempRoot).ConfigureAwait(false);
            await RunRegistrationOwnershipProofAsync(tempRoot).ConfigureAwait(false);
            await RunLegacyReconciliationProofAsync(tempRoot).ConfigureAwait(false);
            await RunLinuxDevBuildProofAsync(tempRoot).ConfigureAwait(false);
            await RunWindowsRunValueProofAsync(tempRoot).ConfigureAwait(false);
            await RunWindowsMigrationProofAsync(tempRoot).ConfigureAwait(false);
            await RunWindowsStartupFolderDisabledProofAsync(tempRoot).ConfigureAwait(false);
            await RunWindowsDevBuildProofAsync(tempRoot).ConfigureAwait(false);
            await RunWindowsApprovalProofAsync(tempRoot).ConfigureAwait(false);
            await RunQuotingAndIoFailureProofAsync(tempRoot).ConfigureAwait(false);
            RunValueParsingProof();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
                if (Directory.Exists(tempRoot))
                    throw new IOException("owned_startup_proof_temp_root_survived_cleanup");
            }
        }
    }

    // ---- Shared helpers (also used by the startup specs in Program.cs) ------------------------------

    /// <summary>The Windows rules with a file-backed Run value and Startup folder under <paramref name="root"/>.</summary>
    internal static PlatformStartupRegistration WindowsFlavor(string root, IDiagnosticSink? sink = null, string? selfLauncherPath = null)
    {
        var startup = Path.Combine(root, "startup");
        return new PlatformStartupRegistration(sink, new StartupRegistrationOptions(
            Windows: true,
            StartupFolder: startup,
            RunValues: new FileWindowsRunValueStore(startup),
            LinuxArtifactPath: string.Empty,
            LinuxLegacyArtifactPath: string.Empty,
            SelfLauncherPath: selfLauncherPath));
    }

    /// <summary>The Linux rules with the autostart folder under <paramref name="root"/>.</summary>
    internal static PlatformStartupRegistration LinuxFlavor(string root, IDiagnosticSink? sink = null)
    {
        var autostart = Path.Combine(root, "autostart");
        return new PlatformStartupRegistration(sink, new StartupRegistrationOptions(
            Windows: false,
            StartupFolder: string.Empty,
            RunValues: null,
            LinuxArtifactPath: Path.Combine(autostart, "Glideslope.desktop"),
            LinuxLegacyArtifactPath: Path.Combine(autostart, "GlidePath.desktop"),
            SelfLauncherPath: null));
    }

    internal static PlatformStartupRegistration HostFlavor(string root, IDiagnosticSink? sink = null) =>
        OperatingSystem.IsWindows() ? WindowsFlavor(root, sink) : LinuxFlavor(root, sink);

    /// <summary>An installed launcher: Glideslope.App.exe with the installer's marker (Windows rules), or
    /// the packaged glideslope-launcher.sh (Linux rules).</summary>
    internal static string CreateInstalledLauncher(string directory, bool windows)
    {
        Directory.CreateDirectory(directory);
        if (windows)
        {
            File.WriteAllText(Path.Combine(directory, PlatformStartupRegistration.WindowsInstalledMarkerFileName), "installed");
            var exe = Path.Combine(directory, PlatformStartupRegistration.WindowsAppFileName);
            File.WriteAllText(exe, "installed app");
            return exe;
        }

        var launcher = Path.Combine(directory, "glideslope-launcher.sh");
        File.WriteAllText(launcher, "installed launcher");
        return launcher;
    }

    /// <summary>A build run from its output folder: the apphost with no installer marker.</summary>
    internal static string CreateDevApp(string directory, bool windows)
    {
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, windows ? PlatformStartupRegistration.WindowsAppFileName : "Glideslope.App");
        File.WriteAllText(exe, "development build");
        return exe;
    }

    internal static string RunValuePath(string root) => Path.Combine(root, "startup", FileWindowsRunValueStore.RunDirectoryName, "Glideslope.txt");

    private static string ApprovalPath(string root) => Path.Combine(root, "startup", FileWindowsRunValueStore.ApprovalDirectoryName, "Glideslope.bin");

    // The StartupApproved\StartupFolder approval file for a retired
    // Startup-folder .cmd, keyed by its own file name rather than the Run value name.
    private static string StartupFolderApprovalPath(string root, string fileName) =>
        Path.Combine(root, "startup", FileWindowsRunValueStore.StartupFolderApprovalDirectoryName, fileName + ".bin");

    // ---- Installed launcher (host-dependent) ------------------------------------------------------

    private static Task RunLauncherResolutionProofAsync(string tempRoot)
    {
        var installDirectory = Path.Combine(tempRoot, "published");
        Directory.CreateDirectory(installDirectory);
        var processPath = Path.Combine(installDirectory, OperatingSystem.IsWindows() ? "Glideslope.App.exe" : "Glideslope.App");
        File.WriteAllText(processPath, "published app");

        if (OperatingSystem.IsWindows())
        {
            // the installed Windows launcher is the exe itself, recognized by the
            // installer's marker beside it; the retired glideslope-launcher.cmd does not count.
            File.WriteAllText(Path.Combine(installDirectory, "glideslope-launcher.cmd"), "retired launcher");
            var withoutMarker = InstalledLauncherPath.TryResolve(processPath);
            Assert(!withoutMarker.Succeeded && withoutMarker.IssueCode == "startup_launcher_missing",
                "the retired launcher .cmd does not make a build an installed copy");

            var markerPath = Path.Combine(installDirectory, PlatformStartupRegistration.WindowsInstalledMarkerFileName);
            File.WriteAllText(markerPath, "installed");
            var resolved = InstalledLauncherPath.TryResolve(processPath);
            Assert(resolved.Succeeded && resolved.Path == processPath, "the installed exe (with its marker) is its own launcher");

            var renamed = Path.Combine(installDirectory, "Other.exe");
            File.WriteAllText(renamed, "another exe");
            Assert(!InstalledLauncherPath.TryResolve(renamed).Succeeded, "only Glideslope.App.exe is an installed launcher");

            File.Delete(markerPath);
            var missing = InstalledLauncherPath.TryResolve(processPath);
            Assert(!missing.Succeeded && missing.IssueCode == "startup_launcher_missing", "missing marker should be typed");
            return Task.CompletedTask;
        }

        var launcherPath = Path.Combine(installDirectory, "glideslope-launcher.sh");
        File.WriteAllText(launcherPath, "published launcher");
        var linuxResolved = InstalledLauncherPath.TryResolve(processPath);
        Assert(linuxResolved.Succeeded && linuxResolved.Path == launcherPath, "published launcher should be selected");

        File.Delete(launcherPath);
        var linuxMissing = InstalledLauncherPath.TryResolve(processPath);
        Assert(!linuxMissing.Succeeded && linuxMissing.IssueCode == "startup_launcher_missing", "missing launcher should be typed");
        return Task.CompletedTask;
    }

    // ---- Linux rules ------------------------------------------------------------------------------

    private static async Task RunRegistrationOwnershipProofAsync(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "linux-ownership");
        var installDirectory = Path.Combine(root, "registration-install");
        var launcherPath = CreateInstalledLauncher(installDirectory, windows: false);

        var startup = LinuxFlavor(root);
        var artifact = startup.ResolveArtifactPath();

        var enabled = await startup.SetAsync(true, launcherPath).ConfigureAwait(false);
        Assert(enabled.IsRegistered && enabled.IssueCode is null, "owned startup entry should register");
        Assert((await startup.ReadAsync().ConfigureAwait(false)).IsRegistered, "owned startup entry should read as registered");

        var foreign = "foreign startup content\n";
        await File.WriteAllTextAsync(artifact, foreign).ConfigureAwait(false);
        var foreignRead = await startup.ReadAsync().ConfigureAwait(false);
        Assert(!foreignRead.IsRegistered && foreignRead.IssueCode == "startup_registration_not_owned", "foreign startup entry should be rejected");
        var foreignDisable = await startup.SetAsync(false, launcherPath).ConfigureAwait(false);
        Assert(!foreignDisable.IsRegistered && foreignDisable.IssueCode == "startup_registration_not_owned", "foreign startup entry should be preserved on disable");
        Assert(await File.ReadAllTextAsync(artifact).ConfigureAwait(false) == foreign, "foreign startup entry must remain untouched");

        var missingLauncher = Path.Combine(installDirectory, "missing-launcher");
        var foreignEnable = await startup.SetAsync(true, missingLauncher).ConfigureAwait(false);
        Assert(!foreignEnable.IsRegistered && foreignEnable.IssueCode == "startup_registration_not_owned", "foreign startup entry must win over missing launcher");
        Assert(await File.ReadAllTextAsync(artifact).ConfigureAwait(false) == foreign, "foreign startup entry must not be overwritten");
        File.Delete(artifact);
        var rejected = await startup.SetAsync(true, missingLauncher).ConfigureAwait(false);
        Assert(!rejected.IsRegistered && rejected.IssueCode == "startup_launcher_missing", "missing launcher must prevent enable");

        var registered = await startup.SetAsync(true, launcherPath).ConfigureAwait(false);
        Assert(registered.IsRegistered, "owned startup entry should be recreated");
        File.Delete(launcherPath);
        var stale = await startup.ReadAsync().ConfigureAwait(false);
        Assert(!stale.IsRegistered && stale.IssueCode == "startup_registration_stale", "missing launcher should make registration stale");

        var removed = await startup.SetAsync(false, launcherPath).ConfigureAwait(false);
        Assert(!removed.IsRegistered && removed.IssueCode is null && !File.Exists(artifact), "exact owned stale entry should be removable");
    }

    /// <summary>
    /// Proves ReconcileLegacyArtifactAsync: an owned pre-rename artifact is removed and replaced by
    /// exactly one new-named entry, a second pass is a no-op, and a foreign file at the legacy path is
    /// left completely alone. The legacy-shaped content below is a frozen historical fixture (the exact
    /// bytes PlatformStartupRegistration wrote before the rename), reproduced here rather than through
    /// the current writer, which can only ever write the current shape.
    /// </summary>
    private static async Task RunLegacyReconciliationProofAsync(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "linux-legacy");
        var launcherPath = CreateInstalledLauncher(Path.Combine(root, "legacy-reconcile-install"), windows: false);
        var startup = LinuxFlavor(root);
        var newArtifact = startup.ResolveArtifactPath();
        var legacyArtifact = Path.Combine(root, "autostart", "GlidePath.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyArtifact)!);
        await File.WriteAllTextAsync(legacyArtifact, BuildLegacyOwnedLinuxContent(launcherPath)).ConfigureAwait(false);

        // With no installed launcher (a development build), the old entry must be kept: deleting it first
        // had turned off an existing start-at-sign-in registration.
        var noLauncher = await startup.ReconcileLegacyArtifactAsync(Path.Combine(root, "missing-launcher")).ConfigureAwait(false);
        Assert(!noLauncher.IsRegistered, "no new entry can be written without a launcher");
        Assert(File.Exists(legacyArtifact), "the owned old entry is kept when the new one can't be written");
        Assert(!File.Exists(newArtifact), "no new entry was written without a launcher");

        var reconciled = await startup.ReconcileLegacyArtifactAsync(launcherPath).ConfigureAwait(false);
        Assert(reconciled.IsRegistered && reconciled.IssueCode is null, "reconciliation should register the new-named artifact from an owned legacy one");
        Assert(!File.Exists(legacyArtifact), "the owned legacy artifact should be removed once reconciled");
        Assert(File.Exists(newArtifact), "exactly one new-named artifact should exist after reconciliation");

        // A second pass finds nothing left at the legacy path: idempotent, no change.
        var secondPass = await startup.ReconcileLegacyArtifactAsync(launcherPath).ConfigureAwait(false);
        Assert(secondPass.IsRegistered && !File.Exists(legacyArtifact), "a second reconciliation pass should be a no-op");

        // disable removes an owned legacy entry too, so the next launch's
        // reconcile cannot turn start at sign-in back on against the user's choice.
        await File.WriteAllTextAsync(legacyArtifact, BuildLegacyOwnedLinuxContent(launcherPath)).ConfigureAwait(false);
        var disabled = await startup.SetAsync(false, launcherPath).ConfigureAwait(false);
        Assert(!disabled.IsRegistered && disabled.IssueCode is null, "disable should succeed");
        Assert(!File.Exists(newArtifact) && !File.Exists(legacyArtifact), "disable removes the owned new and legacy entries");
        var afterDisable = await startup.ReconcileLegacyArtifactAsync(launcherPath).ConfigureAwait(false);
        Assert(!afterDisable.IsRegistered && !File.Exists(newArtifact), "the next launch's reconcile must not re-enable after disable");

        // A foreign file at the legacy path (one this app never wrote) must be left completely alone,
        // and must never cause a new-named artifact to be written on its behalf.
        const string foreignContent = "not ours\n";
        await File.WriteAllTextAsync(legacyArtifact, foreignContent).ConfigureAwait(false);
        var foreignResult = await startup.ReconcileLegacyArtifactAsync(launcherPath).ConfigureAwait(false);
        Assert(!foreignResult.IsRegistered, "a foreign legacy file must not be treated as registered");
        Assert(await File.ReadAllTextAsync(legacyArtifact).ConfigureAwait(false) == foreignContent, "a foreign legacy file must remain byte-for-byte untouched");
        Assert(!File.Exists(newArtifact), "a foreign legacy file must not cause a new-named artifact to be written");
        var foreignDisable = await startup.SetAsync(false, launcherPath).ConfigureAwait(false);
        Assert(foreignDisable.IssueCode is null && await File.ReadAllTextAsync(legacyArtifact).ConfigureAwait(false) == foreignContent,
            "disable leaves a foreign legacy file untouched");
    }

    /// <summary>A development build does not redirect an entry that starts the installed launcher.</summary>
    private static async Task RunLinuxDevBuildProofAsync(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "linux-dev");
        var installed = CreateInstalledLauncher(Path.Combine(root, "usr-lib-glideslope"), windows: false);
        var dev = CreateDevApp(Path.Combine(root, "bin"), windows: false);
        var sink = new RecordingSink();
        var startup = LinuxFlavor(root, sink);
        Assert((await startup.SetAsync(true, installed).ConfigureAwait(false)).IsRegistered, "installed entry registers");
        var before = await File.ReadAllTextAsync(startup.ResolveArtifactPath()).ConfigureAwait(false);
        var kept = await startup.SetAsync(true, dev).ConfigureAwait(false);
        Assert(kept.IsRegistered && kept.IssueCode is null, "the installed entry still counts as registered");
        Assert(await File.ReadAllTextAsync(startup.ResolveArtifactPath()).ConfigureAwait(false) == before, "a dev build must not re-point the installed entry");
        Assert(sink.Has("startup_registration_kept_installed_entry"), "keeping the installed entry is logged");
    }

    // ---- Windows rules: the Run value -------------------------------------------------------------

    private static async Task RunWindowsRunValueProofAsync(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "windows-run-value");
        // a space, a non-ASCII letter and a percent sign, which broke the
        // retired batch entry (OEM code page, %-expansion), are written into the Run value unchanged.
        var exe = CreateInstalledLauncher(Path.Combine(root, "Programs Zo\u00EB 100% Glideslope"), windows: true);
        var sink = new RecordingSink();
        var startup = WindowsFlavor(root, sink);
        var runValue = RunValuePath(root);
        Assert(startup.ResolveArtifactPath() == runValue, "the Windows rules register the Run value");
        Assert(startup.ResolveFileLocations().All(location => location.StartsWith(root, StringComparison.Ordinal)),
            "every file the Windows rules may touch is under the owned root");

        var enabled = await startup.SetAsync(true, exe).ConfigureAwait(false);
        Assert(enabled.IsRegistered && enabled.IssueCode is null, "the Run value registers");
        Assert(await File.ReadAllTextAsync(runValue).ConfigureAwait(false) == $"\"{exe}\" --autostart",
            "the Run value is exactly the quoted exe with --autostart");
        Assert(!Directory.EnumerateFiles(root, "*.cmd", SearchOption.AllDirectories).Any(), "no batch file is written");
        Assert(sink.Has("startup_run_value_written"), "writing the Run value is logged");
        Assert((await startup.ReadAsync().ConfigureAwait(false)).IsRegistered, "the Run value reads as registered");
        var again = await startup.SetAsync(true, exe).ConfigureAwait(false);
        Assert(again.IsRegistered && sink.Has("startup_run_value_unchanged"), "enabling twice leaves the value unchanged");

        const string foreign = "\"C:\\Tools\\Other.exe\" --autostart";
        await File.WriteAllTextAsync(runValue, foreign).ConfigureAwait(false);
        var foreignRead = await startup.ReadAsync().ConfigureAwait(false);
        Assert(!foreignRead.IsRegistered && foreignRead.IssueCode == "startup_registration_not_owned", "a Run value we did not write is not ours");
        var foreignDisable = await startup.SetAsync(false, exe).ConfigureAwait(false);
        Assert(foreignDisable.IssueCode == "startup_registration_not_owned", "disable reports a foreign Run value");
        var foreignEnable = await startup.SetAsync(true, exe).ConfigureAwait(false);
        Assert(foreignEnable.IssueCode == "startup_registration_not_owned", "enable refuses to overwrite a foreign Run value");
        Assert(await File.ReadAllTextAsync(runValue).ConfigureAwait(false) == foreign, "a foreign Run value stays byte for byte");
        File.Delete(runValue);

        var missing = await startup.SetAsync(true, Path.Combine(root, "missing", PlatformStartupRegistration.WindowsAppFileName)).ConfigureAwait(false);
        Assert(!missing.IsRegistered && missing.IssueCode == "startup_launcher_missing", "a missing exe cannot be registered");
        var other = Path.Combine(root, "Other.exe");
        await File.WriteAllTextAsync(other, "another exe").ConfigureAwait(false);
        var wrongName = await startup.SetAsync(true, other).ConfigureAwait(false);
        Assert(!wrongName.IsRegistered && wrongName.IssueCode == "startup_launcher_missing", "only Glideslope.App.exe can be registered");
        Assert(!File.Exists(runValue), "a refused enable writes nothing");

        Assert((await startup.SetAsync(true, exe).ConfigureAwait(false)).IsRegistered, "re-registers");
        File.Delete(exe);
        var stale = await startup.ReadAsync().ConfigureAwait(false);
        Assert(!stale.IsRegistered && stale.IssueCode == "startup_registration_stale", "a Run value whose exe is gone is stale");
        var removed = await startup.SetAsync(false, exe).ConfigureAwait(false);
        Assert(!removed.IsRegistered && removed.IssueCode is null && !File.Exists(runValue), "an owned stale Run value is removable");
    }

    /// <summary>An installed copy migrates retired Startup-folder entries to the Run value; without an installed
    /// copy they are preserved, and disable removes both owned names.</summary>
    private static async Task RunWindowsMigrationProofAsync(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "windows-migration");
        var installDir = Path.Combine(root, "Programs", "Glideslope");
        var exe = CreateInstalledLauncher(installDir, windows: true);
        var startupFolder = Path.Combine(root, "startup");
        var currentCmd = Path.Combine(startupFolder, "Glideslope.startup.cmd");
        var legacyCmd = Path.Combine(startupFolder, "GlidePath.startup.cmd");
        var runValue = RunValuePath(root);

        async Task WriteOwnedCmdsAsync()
        {
            Directory.CreateDirectory(startupFolder);
            // Both stale, as after an upgrade: the retired launcher and the old GlidePath folder are gone.
            await File.WriteAllTextAsync(currentCmd, BuildOwnedCmdContent("Glideslope", Path.Combine(installDir, "glideslope-launcher.cmd"))).ConfigureAwait(false);
            await File.WriteAllTextAsync(legacyCmd, BuildOwnedCmdContent("GlidePath", Path.Combine(root, "old GlidePath", "glideslope-launcher.cmd"))).ConfigureAwait(false);
        }

        await WriteOwnedCmdsAsync().ConfigureAwait(false);
        var sink = new RecordingSink();
        var startup = WindowsFlavor(root, sink);

        // Without an installed launcher, preserve the existing entries because there is no replacement target.
        var noLauncher = await startup.ReconcileLegacyArtifactAsync(null).ConfigureAwait(false);
        Assert(!noLauncher.IsRegistered && File.Exists(currentCmd) && File.Exists(legacyCmd) && !File.Exists(runValue),
            "without an installed launcher nothing is migrated or removed");
        Assert(sink.Has("startup_registration_legacy_kept"), "keeping the old entries is logged");

        var migrated = await startup.ReconcileLegacyArtifactAsync(exe).ConfigureAwait(false);
        Assert(migrated.IsRegistered && migrated.IssueCode is null, "an installed copy migrates the batch entries to the Run value");
        Assert(await File.ReadAllTextAsync(runValue).ConfigureAwait(false) == $"\"{exe}\" --autostart", "the migrated Run value starts the installed exe");
        Assert(!File.Exists(currentCmd) && !File.Exists(legacyCmd), "both owned batch entries are removed once the Run value is confirmed");
        Assert(sink.Count("startup_cmd_removed") == 2, "each removal is logged");

        var secondPass = await startup.ReconcileLegacyArtifactAsync(exe).ConfigureAwait(false);
        Assert(secondPass.IsRegistered, "a second reconcile is a no-op");

        // E7: the installer's "start at sign-in" unchecked runs disable. It removes the Run value and the
        // owned batch entries under both names; before, the GlidePath one survived and the first launch
        // turned start at sign-in back on.
        await WriteOwnedCmdsAsync().ConfigureAwait(false);
        var disabled = await startup.SetAsync(false, exe).ConfigureAwait(false);
        Assert(!disabled.IsRegistered && disabled.IssueCode is null, "disable succeeds");
        Assert(!File.Exists(runValue) && !File.Exists(currentCmd) && !File.Exists(legacyCmd), "disable removes the Run value and both batch entries");
        var firstLaunch = await startup.ReconcileLegacyArtifactAsync(exe).ConfigureAwait(false);
        Assert(!firstLaunch.IsRegistered && !File.Exists(runValue), "the first launch after disable stays disabled");

        // Foreign content at a retired name, and a directory there, are left alone.
        const string foreign = "@echo off\r\nrem someone else's file\r\n";
        await File.WriteAllTextAsync(legacyCmd, foreign).ConfigureAwait(false);
        Directory.CreateDirectory(currentCmd);
        var foreignPass = await startup.ReconcileLegacyArtifactAsync(exe).ConfigureAwait(false);
        Assert(!foreignPass.IsRegistered && !File.Exists(runValue), "foreign entries never cause a Run value to be written");
        Assert(await File.ReadAllTextAsync(legacyCmd).ConfigureAwait(false) == foreign && Directory.Exists(currentCmd),
            "foreign entries stay untouched");
        Assert((await startup.SetAsync(true, exe).ConfigureAwait(false)).IsRegistered, "enable works beside foreign entries");
        Assert((await startup.SetAsync(false, exe).ConfigureAwait(false)).IssueCode is null, "disable works beside foreign entries");
        Assert(await File.ReadAllTextAsync(legacyCmd).ConfigureAwait(false) == foreign && Directory.Exists(currentCmd),
            "enable and disable leave foreign entries untouched");
    }

    /// <summary>
    /// Migration of a retired .cmd entry to the Run value must preserve its approval state.
    /// that the user may have already turned that entry off in Task Manager or Settings > Apps > Startup;
    /// Windows records that choice in StartupApproved\StartupFolder, one binary value per file name, the
    /// same low-bit shape already proven for the Run value's own approval in
    /// <see cref="RunWindowsApprovalProofAsync"/>. This proof covers: (a) an owned current-name .cmd
    /// recorded off is migrated to nothing (no Run value, the leftover .cmd is still removed, not
    /// registered, logged once); (b) the same .cmd recorded on, and with no approval value at all, both
    /// still migrate as before; (c) a current- and a legacy-name .cmd that both start the same exe, one off
    /// and one on, still migrate, since not every artifact the Run value would replace is off.
    /// </summary>
    private static async Task RunWindowsStartupFolderDisabledProofAsync(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "windows-startup-folder-disabled");
        var installDir = Path.Combine(root, "Programs", "Glideslope");
        var exe = CreateInstalledLauncher(installDir, windows: true);
        var startupFolder = Path.Combine(root, "startup");
        var currentCmd = Path.Combine(startupFolder, "Glideslope.startup.cmd");
        var legacyCmd = Path.Combine(startupFolder, "GlidePath.startup.cmd");
        var runValue = RunValuePath(root);
        var currentApproval = StartupFolderApprovalPath(root, "Glideslope.startup.cmd");
        var legacyApproval = StartupFolderApprovalPath(root, "GlidePath.startup.cmd");
        var launcherInCmd = Path.Combine(installDir, "glideslope-launcher.cmd");

        async Task WriteOwnedCurrentCmdAsync()
        {
            Directory.CreateDirectory(startupFolder);
            await File.WriteAllTextAsync(currentCmd, BuildOwnedCmdContent("Glideslope", launcherInCmd)).ConfigureAwait(false);
        }

        // (a) The current-name .cmd is owned but Windows recorded it as off (first byte 0x03, same shape as
        // RunWindowsApprovalProofAsync's Run-value approval bytes): the reconcile must not write the Run
        // value, must still remove the leftover .cmd, must report not registered, and must log the outcome
        // exactly once with the writer, artifact and disabled counts.
        await WriteOwnedCurrentCmdAsync().ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(currentApproval)!);
        await File.WriteAllBytesAsync(currentApproval, [0x03, 0, 0, 0]).ConfigureAwait(false);
        var offSink = new RecordingSink();
        var offStartup = WindowsFlavor(root, offSink);
        var offResult = await offStartup.ReconcileLegacyArtifactAsync(exe).ConfigureAwait(false);
        Assert(!offResult.IsRegistered && offResult.IssueCode is null,
            "a .cmd entry Windows recorded as off must read as not registered after reconcile");
        Assert(!File.Exists(runValue), "a disabled .cmd entry must never cause the Run value to be written");
        Assert(!File.Exists(currentCmd), "the disabled owned .cmd is still removed as a leftover");
        Assert(offSink.Count("startup_registration_legacy_disabled_by_windows") == 1,
            "the disabled-by-Windows outcome is logged exactly once");
        Assert(offSink.Events.Single(e => e.Code == "startup_registration_legacy_disabled_by_windows").Status
               == "writer=installed,artifacts=1,disabled=1",
            "the disabled-by-Windows log line carries the writer, artifact and disabled counts");
        File.Delete(currentApproval);

        // (b) The same .cmd, but Windows recorded it as on (first byte 0x02): migrates exactly as today.
        await WriteOwnedCurrentCmdAsync().ConfigureAwait(false);
        await File.WriteAllBytesAsync(currentApproval, [0x02, 0, 0, 0]).ConfigureAwait(false);
        var onStartup = WindowsFlavor(root);
        var onResult = await onStartup.ReconcileLegacyArtifactAsync(exe).ConfigureAwait(false);
        Assert(onResult.IsRegistered && onResult.IssueCode is null,
            "a .cmd entry Windows recorded as on must still migrate to the Run value");
        Assert(await File.ReadAllTextAsync(runValue).ConfigureAwait(false) == $"\"{exe}\" --autostart",
            "the migrated Run value starts the installed exe");
        Assert(!File.Exists(currentCmd), "the migrated .cmd is removed");
        File.Delete(runValue);
        File.Delete(currentApproval);

        // (b, continued) No StartupApproved\StartupFolder value at all (never disabled by the user) also
        // migrates: an absent approval value means on, the same rule the Run value's own approval uses.
        await WriteOwnedCurrentCmdAsync().ConfigureAwait(false);
        var noApprovalStartup = WindowsFlavor(root);
        var noApprovalResult = await noApprovalStartup.ReconcileLegacyArtifactAsync(exe).ConfigureAwait(false);
        Assert(noApprovalResult.IsRegistered && noApprovalResult.IssueCode is null,
            "a .cmd entry with no StartupFolder approval value at all still migrates");
        Assert(await File.ReadAllTextAsync(runValue).ConfigureAwait(false) == $"\"{exe}\" --autostart",
            "the migrated Run value starts the installed exe when no approval value exists");
        File.Delete(runValue);

        // (c) Both the current- and legacy-name .cmd start the same exe, one off and one on: still
        // migrates, because not every artifact the Run value would replace is off.
        await WriteOwnedCurrentCmdAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(legacyCmd, BuildOwnedCmdContent("GlidePath", launcherInCmd)).ConfigureAwait(false);
        await File.WriteAllBytesAsync(currentApproval, [0x03, 0, 0, 0]).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyApproval)!);
        await File.WriteAllBytesAsync(legacyApproval, [0x02, 0, 0, 0]).ConfigureAwait(false);
        var mixedSink = new RecordingSink();
        var mixedStartup = WindowsFlavor(root, mixedSink);
        var mixedResult = await mixedStartup.ReconcileLegacyArtifactAsync(exe).ConfigureAwait(false);
        Assert(mixedResult.IsRegistered && mixedResult.IssueCode is null,
            "one owned entry off and one on still migrates, since not all replaced artifacts are off");
        Assert(await File.ReadAllTextAsync(runValue).ConfigureAwait(false) == $"\"{exe}\" --autostart",
            "the migrated Run value starts the installed exe");
        Assert(!File.Exists(currentCmd) && !File.Exists(legacyCmd), "both owned .cmd entries are removed once migrated");
        Assert(!mixedSink.Has("startup_registration_legacy_disabled_by_windows"),
            "a mixed on/off set is not treated as fully disabled");
        File.Delete(runValue);
        File.Delete(currentApproval);
        File.Delete(legacyApproval);
    }

    /// <summary>A development build does not redirect an installed entry and migrates only a batch entry that
    /// starts its own executable.</summary>
    private static async Task RunWindowsDevBuildProofAsync(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "windows-dev");
        var installed = CreateInstalledLauncher(Path.Combine(root, "Programs", "Glideslope"), windows: true);
        var installed2 = CreateInstalledLauncher(Path.Combine(root, "Programs", "Glideslope 2"), windows: true);
        var dev = CreateDevApp(Path.Combine(root, "src", "bin", "Debug"), windows: true);
        var runValue = RunValuePath(root);
        var sink = new RecordingSink();
        var startup = WindowsFlavor(root, sink);

        Assert((await startup.SetAsync(true, installed).ConfigureAwait(false)).IsRegistered, "installed entry registers");
        var kept = await startup.SetAsync(true, dev).ConfigureAwait(false);
        Assert(kept.IsRegistered && kept.IssueCode is null, "the installed entry still counts as registered for the dev build");
        Assert(await File.ReadAllTextAsync(runValue).ConfigureAwait(false) == $"\"{installed}\" --autostart", "a dev build must not re-point the installed entry");
        Assert(sink.Has("startup_registration_kept_installed_entry"), "keeping the installed entry is logged");

        Assert((await startup.SetAsync(true, installed2).ConfigureAwait(false)).IsRegistered, "another installed copy may take the entry");
        Assert(await File.ReadAllTextAsync(runValue).ConfigureAwait(false) == $"\"{installed2}\" --autostart", "the entry follows the installed copy");

        File.Delete(runValue);
        Assert((await startup.SetAsync(true, dev).ConfigureAwait(false)).IsRegistered, "with no entry, a dev build may register itself (design doc §16.1)");
        Assert((await startup.SetAsync(true, installed).ConfigureAwait(false)).IsRegistered, "an installed copy replaces a dev build's entry");
        Assert(await File.ReadAllTextAsync(runValue).ConfigureAwait(false) == $"\"{installed}\" --autostart", "the installed copy owns the entry again");
        File.Delete(runValue);

        // A dev build's own §16.1 batch entry (it starts this very exe) is moved to the Run value by that
        // build; an entry that starts anything else stays.
        var startupFolder = Path.Combine(root, "startup");
        Directory.CreateDirectory(startupFolder);
        var selfCmd = Path.Combine(startupFolder, "Glideslope.startup.cmd");
        var otherCmd = Path.Combine(startupFolder, "GlidePath.startup.cmd");
        await File.WriteAllTextAsync(selfCmd, BuildOwnedCmdContent("Glideslope", dev)).ConfigureAwait(false);
        await File.WriteAllTextAsync(otherCmd, BuildOwnedCmdContent("GlidePath", Path.Combine(root, "old GlidePath", "glideslope-launcher.cmd"))).ConfigureAwait(false);
        var selfSink = new RecordingSink();
        var selfStartup = WindowsFlavor(root, selfSink, selfLauncherPath: dev);
        var moved = await selfStartup.ReconcileLegacyArtifactAsync(null).ConfigureAwait(false);
        Assert(moved.IsRegistered, "a dev build moves its own batch entry to the Run value");
        Assert(await File.ReadAllTextAsync(runValue).ConfigureAwait(false) == $"\"{dev}\" --autostart", "the Run value starts the same dev exe the batch entry did");
        Assert(!File.Exists(selfCmd), "the dev build's own batch entry is removed");
        Assert(File.Exists(otherCmd) && selfSink.Has("startup_cmd_kept"), "a batch entry that starts something else is kept");

        File.Delete(runValue);
        var onlyOther = await selfStartup.ReconcileLegacyArtifactAsync(null).ConfigureAwait(false);
        Assert(!onlyOther.IsRegistered && !File.Exists(runValue) && File.Exists(otherCmd),
            "a dev build never migrates a batch entry that does not start its own exe");
        File.Delete(otherCmd);
    }

    /// <summary>An entry disabled in Windows reads as unregistered. Explicit enable and disable clear only this
    /// app's approval value.</summary>
    private static async Task RunWindowsApprovalProofAsync(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "windows-approval");
        var exe = CreateInstalledLauncher(Path.Combine(root, "Programs", "Glideslope"), windows: true);
        var approval = ApprovalPath(root);
        var sink = new RecordingSink();
        var startup = WindowsFlavor(root, sink);
        Assert((await startup.SetAsync(true, exe).ConfigureAwait(false)).IsRegistered, "registers");

        Directory.CreateDirectory(Path.GetDirectoryName(approval)!);
        foreach (var (first, on) in new[] { ((byte)0x02, true), ((byte)0x03, false), ((byte)0x06, true), ((byte)0x07, false) })
        {
            await File.WriteAllBytesAsync(approval, [first, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]).ConfigureAwait(false);
            var state = await startup.ReadAsync().ConfigureAwait(false);
            Assert(state.IsRegistered == on && state.IssueCode is null, $"approval byte 0x{first:X2} reads as {(on ? "on" : "off")}");
        }

        Assert(sink.Has("startup_registration_disabled_in_windows"), "an entry turned off in Windows is logged");
        var reenabled = await startup.SetAsync(true, exe).ConfigureAwait(false);
        Assert(reenabled.IsRegistered && !File.Exists(approval), "enable clears this app's approval value");
        Assert(sink.Has("startup_approval_cleared"), "clearing the approval value is logged");

        await File.WriteAllBytesAsync(approval, [0x03, 0, 0, 0]).ConfigureAwait(false);
        var disabled = await startup.SetAsync(false, exe).ConfigureAwait(false);
        Assert(!disabled.IsRegistered && !File.Exists(approval) && !File.Exists(RunValuePath(root)), "disable removes the Run value and its approval value");
    }

    // ---- Quoting and storage failures (moved here from Program.SettingsRevisionAndFutureSchema) ------

    private static async Task RunQuotingAndIoFailureProofAsync(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "quoting");
        var launcherPath = CreateInstalledLauncher(Path.Combine(root, "Program Files", "Glideslope"), windows: false);
        var linux = LinuxFlavor(root);
        var linuxResult = await linux.SetAsync(true, launcherPath).ConfigureAwait(false);
        Assert(linuxResult.IsRegistered, "startup registration should succeed for a launcher path with spaces");
        var startupText = await File.ReadAllTextAsync(linux.ResolveArtifactPath()).ConfigureAwait(false);
        Assert(startupText.Contains($"\"{launcherPath.Replace("\\", "\\\\", StringComparison.Ordinal)}\"", StringComparison.Ordinal),
            "startup content should quote launcher paths with spaces");
        var tryExec = launcherPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        Assert(startupText.Contains($"TryExec={tryExec}\n", StringComparison.Ordinal), "TryExec should be an unquoted desktop string path");
        Assert(!startupText.Contains("TryExec=\"", StringComparison.Ordinal), "TryExec should not use Exec quoting");

        var blocker = Path.Combine(root, "startup-blocker");
        await File.WriteAllTextAsync(blocker, "owned blocker").ConfigureAwait(false);
        var failingLinux = LinuxFlavor(blocker);
        var linuxFailed = await failingLinux.SetAsync(true, launcherPath).ConfigureAwait(false);
        Assert(!linuxFailed.IsRegistered && linuxFailed.IssueCode == "startup_registration_io_error", "startup IO failure should remain typed");

        var exe = CreateInstalledLauncher(Path.Combine(root, "Programs", "Glideslope"), windows: true);
        var sink = new RecordingSink();
        var failingWindows = WindowsFlavor(blocker, sink);
        var windowsFailed = await failingWindows.SetAsync(true, exe).ConfigureAwait(false);
        Assert(!windowsFailed.IsRegistered && windowsFailed.IssueCode == "startup_registration_io_error", "Run value storage failure should remain typed");
        Assert(sink.Events.Any(e => e.Code == "startup_registration_io_error" && e.Status!.Contains("type=", StringComparison.Ordinal)),
            "the storage failure is logged with its exception type");
        Assert(await File.ReadAllTextAsync(blocker).ConfigureAwait(false) == "owned blocker", "startup failure should preserve its blocker");
    }

    private static void RunValueParsingProof()
    {
        var exe = Path.Combine(Path.GetTempPath(), "Glideslope", PlatformStartupRegistration.WindowsAppFileName);
        Assert(PlatformStartupRegistration.ParseRunValueData(PlatformStartupRegistration.FormatRunValueData(exe)) == exe, "the written shape parses back");
        Assert(PlatformStartupRegistration.ParseRunValueData($"\"{exe}\" --autostart --other") is null, "extra arguments are not ours");
        Assert(PlatformStartupRegistration.ParseRunValueData($"{exe} --autostart") is null, "an unquoted path is not ours");
        Assert(PlatformStartupRegistration.ParseRunValueData("\"Glideslope.App.exe\" --autostart") is null, "a relative path is not ours");
        Assert(PlatformStartupRegistration.ParseRunValueData($"\"{Path.Combine(Path.GetTempPath(), "Other.exe")}\" --autostart") is null, "another exe is not ours");
        Assert(PlatformStartupRegistration.ParseRunValueData("\"\" --autostart") is null, "an empty path is not ours");
    }

    /// <summary>The exact four-line batch entry 1.0 wrote (frozen historical fixture), for the current or the
    /// pre-rename product name.</summary>
    private static string BuildOwnedCmdContent(string product, string launcherPath) =>
        $"@echo off\r\nrem X-{product}-Managed=true\r\nset \"DOTNET_BUNDLE_EXTRACT_BASE_DIR=%LOCALAPPDATA%\\{product}\\cache\\bundle\"\r\n\"{launcherPath}\" --autostart\r\n";

    private static string BuildLegacyOwnedLinuxContent(string launcherPath)
    {
        var escapedExec = launcherPath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var escapedTryExec = launcherPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        return string.Join('\n', [
            "[Desktop Entry]",
            "Type=Application",
            "Name=Glide Path",
            $"Exec=\"{escapedExec}\" --autostart",
            $"TryExec={escapedTryExec}",
            "Terminal=false",
            "X-GlidePath-Managed=true"
        ]) + '\n';
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL: {message}");
    }

    private sealed class RecordingSink : IDiagnosticSink
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public void Record(DiagnosticEvent diagnostic) => Events.Add(diagnostic);
        public bool Has(string code) => Events.Any(e => e.Code == code);
        public int Count(string code) => Events.Count(e => e.Code == code);
    }
}

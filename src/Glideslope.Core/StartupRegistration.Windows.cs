namespace Glideslope.Core;

// PlatformStartupRegistration, the Windows side: the Run value's data format, reading and setting it,
// the StartupApproved on/off values Windows keeps, and the one-time move of the retired Startup-folder
// .cmd entries to the Run value. The Run value store itself is in WindowsRunValueStores.cs; the
// ownership rules both platforms share stay in StartupRegistration.cs.
public sealed partial class PlatformStartupRegistration
{
    /// <summary>The exact Run value data this app writes for <paramref name="exePath"/>.</summary>
    internal static string FormatRunValueData(string exePath) => $"\"{exePath}\" --autostart";

    /// <summary>Returns the exe path when <paramref name="data"/> has exactly the shape
    /// <see cref="FormatRunValueData"/> writes, otherwise null (not ours).</summary>
    internal static string? ParseRunValueData(string data)
    {
        if (!data.StartsWith('"') || !data.EndsWith(RunValueSuffix, StringComparison.Ordinal))
            return null;

        var pathLength = data.Length - RunValueSuffix.Length - 1;
        if (pathLength <= 0)
            return null;

        var path = data.Substring(1, pathLength);
        if (path.Contains('"', StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            return null;

        return string.Equals(Path.GetFileName(path), WindowsAppFileName, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    // ---- Windows: the Run value -------------------------------------------------------------------

    private StartupRegistrationState ReadWindows()
    {
        var run = InspectRunValue();
        switch (run.Kind)
        {
            case ArtifactKind.Missing:
                return new StartupRegistrationState(false);
            case ArtifactKind.NotOwned:
                return new StartupRegistrationState(false, "startup_registration_not_owned");
            case ArtifactKind.Stale:
                return new StartupRegistrationState(false, "startup_registration_stale");
        }

        if (IsDisabledByWindows())
        {
            // Not an issue code: the Settings checkbox shows the real state (off), and checking it and
            // saving turns the entry back on through SetAsync, which clears the approval value.
            _diagnostics.Record(new DiagnosticEvent("startup_registration_disabled_in_windows", Status: "startup_approved_off"));
            return new StartupRegistrationState(false);
        }

        return new StartupRegistrationState(true);
    }

    private ArtifactInspection InspectRunValue()
    {
        var value = RunValues.ReadRunValue(WindowsRunValueName);
        if (!value.Exists)
            return new ArtifactInspection(ArtifactKind.Missing, null);

        var target = value.Data is null ? null : ParseRunValueData(value.Data);
        if (target is null)
            return new ArtifactInspection(ArtifactKind.NotOwned, null);

        return new ArtifactInspection(IsExistingFile(target) ? ArtifactKind.Owned : ArtifactKind.Stale, target);
    }

    /// <summary>
    /// The Settings checkbox reflects the actual registration, including whether Windows disabled the Run
    /// entry in Task Manager or Settings > Apps > Startup. Explorer keeps one binary value per Run entry under
    /// StartupApproved\Run: first byte 0x02 or 0x06
    /// means on, 0x03 or 0x07 off, followed by a FILETIME. The format is undocumented, so only the low bit
    /// of the first byte is read, and the app never writes the value; it only deletes its own, which
    /// Windows treats as on.
    /// </summary>
    private bool IsDisabledByWindows()
    {
        var approval = RunValues.ReadApprovalValue(WindowsRunValueName);
        return approval is { Length: > 0 } && (approval[0] & 0x01) != 0;
    }

    /// <summary>
    /// Same low-bit rule as <see cref="IsDisabledByWindows"/>, applied to
    /// a retired Startup-folder artifact's own approval value under StartupApproved\StartupFolder, keyed by
    /// the artifact's file name rather than the Run value name. An absent value means Windows never disabled
    /// the entry. Reconciliation uses this state to preserve the user's choice when migrating an owned .cmd.
    /// </summary>
    private bool IsStartupFolderArtifactDisabledByWindows(string fileName)
    {
        var approval = RunValues.ReadStartupFolderApprovalValue(fileName);
        return approval is { Length: > 0 } && (approval[0] & 0x01) != 0;
    }

    private void ClearStartupApproval(string reason)
    {
        if (RunValues.DeleteApprovalValue(WindowsRunValueName))
            _diagnostics.Record(new DiagnosticEvent("startup_approval_cleared", Status: reason));
    }

    private async Task<StartupRegistrationState> SetWindowsAsync(bool enabled, string launcherPath, CancellationToken cancellationToken)
    {
        var existing = InspectRunValue();
        if (!enabled)
        {
            if (existing.Kind is ArtifactKind.Owned or ArtifactKind.Stale)
            {
                RunValues.DeleteRunValue(WindowsRunValueName);
                _diagnostics.Record(new DiagnosticEvent("startup_run_value_removed", Status: KindName(existing.Kind)));
            }

            if (existing.Kind != ArtifactKind.NotOwned)
                ClearStartupApproval("disable");

            // Remove every owned retired .cmd entry under both names so reconciliation cannot re-enable it.
            await RemoveStartupFolderArtifactsAsync(replacementLauncher: null, "disable", cancellationToken).ConfigureAwait(false);
            if (existing.Kind == ArtifactKind.NotOwned)
            {
                _diagnostics.Record(new DiagnosticEvent("startup_registration_not_owned", Status: "run_value_left_alone"));
                return new StartupRegistrationState(false, "startup_registration_not_owned");
            }

            return ReadWindows();
        }

        if (existing.Kind == ArtifactKind.NotOwned)
        {
            _diagnostics.Record(new DiagnosticEvent("startup_registration_not_owned", Status: "run_value_left_alone"));
            return new StartupRegistrationState(false, "startup_registration_not_owned");
        }

        var launcherIssue = CheckWindowsLauncher(launcherPath);
        if (launcherIssue is not null)
        {
            _diagnostics.Record(new DiagnosticEvent("startup_launcher_missing", Status: $"startup_registration_rejected,{launcherIssue}"));
            return new StartupRegistrationState(false, "startup_launcher_missing");
        }

        if (KeepsInstalledEntry(existing, launcherPath))
            return ReadWindows();

        var data = FormatRunValueData(launcherPath);
        var current = RunValues.ReadRunValue(WindowsRunValueName);
        if (current.Exists && string.Equals(current.Data, data, StringComparison.Ordinal))
        {
            _diagnostics.Record(new DiagnosticEvent("startup_run_value_unchanged", Status: $"installed={IsInstalledLauncher(launcherPath)}"));
        }
        else
        {
            RunValues.WriteRunValue(WindowsRunValueName, data);
            _diagnostics.Record(new DiagnosticEvent("startup_run_value_written",
                Status: $"previous={KindName(existing.Kind)},installed={IsInstalledLauncher(launcherPath)}"));
        }

        ClearStartupApproval("enable");
        var state = ReadWindows();
        if (!state.IsRegistered)
        {
            _diagnostics.Record(new DiagnosticEvent("startup_run_value_unconfirmed", Status: state.IssueCode ?? "not_registered"));
            return state;
        }

        // Write the new entry before removing the old one. Until removal, both entries may start the app.
        await RemoveStartupFolderArtifactsAsync(launcherPath, "enable", cancellationToken).ConfigureAwait(false);
        return state;
    }

    private async Task<StartupRegistrationState> ReconcileWindowsAsync(string? installedLauncherPath, CancellationToken cancellationToken)
    {
        var artifacts = await FindStartupFolderArtifactsAsync(cancellationToken).ConfigureAwait(false);
        if (artifacts.Count == 0)
            return ReadWindows();

        // Write the new entry before removing the old one so a failed write preserves startup registration.
        string? writer = null;
        var writerKind = "none";
        if (IsExistingFile(installedLauncherPath))
        {
            writer = installedLauncherPath;
            writerKind = "installed";
        }
        else if (_options.SelfLauncherPath is { } self && IsExistingFile(self) &&
                 artifacts.Any(artifact => TargetsSameApp(artifact.LauncherPath, self)))
        {
            writer = self;
            writerKind = "self";
        }

        if (writer is null)
        {
            _diagnostics.Record(new DiagnosticEvent("startup_registration_legacy_kept", Status: $"launcher_unavailable,artifacts={artifacts.Count}"));
            return ReadWindows();
        }

        // Which owned artifacts the new Run value would replace: every
        // one of them when the writer is the installed launcher, otherwise only the one(s) that already
        // start this build's own exe — the same partition RemoveStartupFolderArtifactsAsync uses below to
        // decide what a replacement removes.
        var replaced = writerKind == "installed"
            ? artifacts
            : artifacts.Where(artifact => TargetsSameApp(artifact.LauncherPath, writer)).ToList();
        var disabledCount = replaced.Count(artifact => IsStartupFolderArtifactDisabledByWindows(Path.GetFileName(artifact.FilePath)));

        // Preserve disabled startup choices when migrating retired entries. If every entry being replaced is
        // disabled, remove the retired files without writing the Run value. Approval values remain untouched.
        if (replaced.Count > 0 && disabledCount == replaced.Count)
        {
            await RemoveStartupFolderArtifactsAsync(writer, "legacy_disabled_by_windows", cancellationToken).ConfigureAwait(false);
            _diagnostics.Record(new DiagnosticEvent("startup_registration_legacy_disabled_by_windows",
                Status: $"writer={writerKind},artifacts={artifacts.Count},disabled={disabledCount}"));
            return ReadWindows();
        }

        _diagnostics.Record(new DiagnosticEvent("startup_registration_legacy_migrating", Status: $"writer={writerKind},artifacts={artifacts.Count}"));
        var written = await SetWindowsAsync(true, writer, cancellationToken).ConfigureAwait(false);
        if (!written.IsRegistered)
            _diagnostics.Record(new DiagnosticEvent("startup_registration_legacy_kept", Status: $"new_entry_not_written,{written.IssueCode ?? "not_registered"}"));
        return written;
    }

    /// <summary>Removes the retired .cmd entries this app owns. With <paramref name="replacementLauncher"/>
    /// null (disable) every owned one goes; otherwise only those the new Run value replaces: all of them when
    /// the writer is an installed copy, else only one that starts the same exe.</summary>
    private async Task RemoveStartupFolderArtifactsAsync(string? replacementLauncher, string reason, CancellationToken cancellationToken)
    {
        foreach (var artifact in await FindStartupFolderArtifactsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (replacementLauncher is not null &&
                !IsInstalledLauncher(replacementLauncher) &&
                !TargetsSameApp(artifact.LauncherPath, replacementLauncher))
            {
                _diagnostics.Record(new DiagnosticEvent("startup_cmd_kept", Status: $"{artifact.NameKind},other_target,{reason}"));
                continue;
            }

            File.Delete(artifact.FilePath);
            _diagnostics.Record(new DiagnosticEvent("startup_cmd_removed", Status: $"{artifact.NameKind},{KindName(artifact.Kind)},{reason}"));
        }
    }

    /// <summary>The retired .cmd entries in the Startup folder that this app owns (or owned, if their
    /// launcher is gone), under both names. Anything else there is logged and left alone.</summary>
    private async Task<List<StartupFolderArtifact>> FindStartupFolderArtifactsAsync(CancellationToken cancellationToken)
    {
        var found = new List<StartupFolderArtifact>();
        if (string.IsNullOrEmpty(_options.StartupFolder))
            return found;

        foreach (var (name, legacy) in new[] { (WindowsArtifactName, false), (LegacyWindowsArtifactName, true) })
        {
            var path = Path.Combine(_options.StartupFolder, name);
            var nameKind = legacy ? "legacy_name" : "current_name";
            if (Directory.Exists(path))
            {
                _diagnostics.Record(new DiagnosticEvent("startup_cmd_left_alone", Status: $"{nameKind},directory"));
                continue;
            }

            if (!File.Exists(path))
                continue;

            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var inspection = legacy ? InspectLegacyArtifact(content, windows: true) : InspectArtifact(content, windows: true);
            if (inspection.Kind is not (ArtifactKind.Owned or ArtifactKind.Stale) || inspection.LauncherPath is null)
            {
                _diagnostics.Record(new DiagnosticEvent("startup_cmd_left_alone", Status: $"{nameKind},not_owned"));
                continue;
            }

            found.Add(new StartupFolderArtifact(path, nameKind, inspection.Kind, inspection.LauncherPath));
        }

        return found;
    }

    /// <summary>A retired .cmd entry starts the same app as <paramref name="exePath"/> when it started that
    /// exe itself (the §16.1 fallback) or the retired launcher beside it.</summary>
    private bool TargetsSameApp(string cmdTarget, string exePath)
    {
        if (SamePath(cmdTarget, exePath))
            return true;

        var directory = Path.GetDirectoryName(exePath);
        return !string.IsNullOrEmpty(directory) && SamePath(cmdTarget, Path.Combine(directory, RetiredWindowsLauncherName));
    }

    private static string? CheckWindowsLauncher(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return "launcher_empty";
        if (!IsExistingFile(launcherPath))
            return "launcher_not_file";
        if (!Path.IsPathFullyQualified(launcherPath))
            return "launcher_not_absolute";
        if (launcherPath.Contains('"', StringComparison.Ordinal))
            return "launcher_has_quote";
        return string.Equals(Path.GetFileName(launcherPath), WindowsAppFileName, StringComparison.OrdinalIgnoreCase)
            ? null
            : "launcher_not_app_exe";
    }

    private static string? ParseWindowsArtifact(string[] lines, string managedMarker, string cacheBundleSegment)
    {
        if (lines.Length != 4 ||
            !string.Equals(lines[0], "@echo off", StringComparison.Ordinal) ||
            !string.Equals(lines[1], $"rem {managedMarker}", StringComparison.Ordinal) ||
            !string.Equals(lines[2], $"set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR=%LOCALAPPDATA%\\{cacheBundleSegment}\"", StringComparison.Ordinal))
            return null;

        const string suffix = "\" --autostart";
        if (!lines[3].StartsWith("\"", StringComparison.Ordinal) || !lines[3].EndsWith(suffix, StringComparison.Ordinal))
            return null;

        var pathLength = lines[3].Length - suffix.Length - 1;
        return pathLength > 0 ? lines[3].Substring(1, pathLength) : null;
    }
}

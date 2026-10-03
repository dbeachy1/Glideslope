using System.Text;

namespace Glideslope.Core;

// PlatformStartupRegistration, the Linux side: the XDG autostart .desktop entry (read, set, the
// pre-rename GlidePath entry's migration) and its exact file format, written and parsed here.
// The ownership rules both platforms share stay in StartupRegistration.cs.
public sealed partial class PlatformStartupRegistration
{
    // ---- Linux: the XDG autostart .desktop entry (unchanged format) --------------------------------

    private async Task<StartupRegistrationState> ReadLinuxAsync(CancellationToken cancellationToken)
    {
        var path = _options.LinuxArtifactPath;
        if (Directory.Exists(path))
            return new StartupRegistrationState(false, "startup_registration_not_owned");
        if (!File.Exists(path))
            return new StartupRegistrationState(false);

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var inspection = InspectArtifact(content, windows: false);
        return inspection.Kind switch
        {
            ArtifactKind.Owned => new StartupRegistrationState(true),
            ArtifactKind.Stale => new StartupRegistrationState(false, "startup_registration_stale"),
            _ => new StartupRegistrationState(false, "startup_registration_not_owned"),
        };
    }

    private async Task<StartupRegistrationState> ReconcileLinuxAsync(string? launcherPath, CancellationToken cancellationToken)
    {
        var legacyPath = _options.LinuxLegacyArtifactPath;
        if (Directory.Exists(legacyPath) || !File.Exists(legacyPath))
        {
            // A directory there is never ours, and a missing file means either nothing was ever
            // registered under the old name or a prior launch already reconciled it (idempotent).
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        var content = await File.ReadAllTextAsync(legacyPath, cancellationToken).ConfigureAwait(false);
        var inspection = InspectLegacyArtifact(content, windows: false);
        if (inspection.Kind is not (ArtifactKind.Owned or ArtifactKind.Stale))
        {
            _diagnostics.Record(new DiagnosticEvent("startup_registration_legacy_not_owned", Status: "legacy_artifact_left_alone"));
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        // Write the new entry first and remove the old one only once the new one is confirmed.
        // Write the replacement first; a development build with no installed
        // launcher then couldn't write the new one, which silently turned off start at sign-in.
        if (!IsExistingFile(launcherPath))
        {
            _diagnostics.Record(new DiagnosticEvent("startup_registration_legacy_kept", Status: "launcher_unavailable"));
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        var written = await SetAsync(true, launcherPath!, cancellationToken).ConfigureAwait(false);
        if (!written.IsRegistered)
        {
            _diagnostics.Record(new DiagnosticEvent("startup_registration_legacy_kept", Status: "new_entry_not_written"));
            return written;
        }

        File.Delete(legacyPath);
        _diagnostics.Record(new DiagnosticEvent(
            "startup_registration_legacy_removed",
            Status: inspection.Kind == ArtifactKind.Owned ? "legacy_owned_removed" : "legacy_stale_removed"));
        return written;
    }

    private async Task<StartupRegistrationState> SetLinuxAsync(bool enabled, string launcherPath, CancellationToken cancellationToken)
    {
        var path = _options.LinuxArtifactPath;
        if (!enabled)
        {
            var existing = await InspectExistingArtifactAsync(path, cancellationToken).ConfigureAwait(false);

            // Disable also removes an owned pre-rename GlidePath entry;
            // otherwise the next launch's ReconcileLegacyArtifactAsync found it and turned start at sign-in
            // back on against the user's choice.
            await RemoveOwnedLinuxLegacyArtifactAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Kind == ArtifactKind.Missing)
                return new StartupRegistrationState(false);
            if (existing.Kind == ArtifactKind.NotOwned)
                return new StartupRegistrationState(false, "startup_registration_not_owned");

            File.Delete(path);
            _diagnostics.Record(new DiagnosticEvent("startup_desktop_entry_removed", Status: KindName(existing.Kind)));
            return await ReadLinuxAsync(cancellationToken).ConfigureAwait(false);
        }

        var existingForWrite = await InspectExistingArtifactAsync(path, cancellationToken).ConfigureAwait(false);
        if (existingForWrite.Kind == ArtifactKind.NotOwned)
            return new StartupRegistrationState(false, "startup_registration_not_owned");

        if (!IsExistingFile(launcherPath))
        {
            _diagnostics.Record(new DiagnosticEvent("startup_launcher_missing", Status: "startup_registration_rejected"));
            return new StartupRegistrationState(false, "startup_launcher_missing");
        }

        if (KeepsInstalledEntry(existingForWrite, launcherPath))
            return await ReadLinuxAsync(cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("startup_path_invalid");
        Directory.CreateDirectory(directory);
        var content = LinuxContent(launcherPath);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Record(new DiagnosticEvent("startup_temp_cleanup_failed", Status: $"startup_registration_failed,type={ex.GetType().Name}"));
            }
        }

        _diagnostics.Record(new DiagnosticEvent("startup_desktop_entry_written",
            Status: $"previous={KindName(existingForWrite.Kind)},installed={IsInstalledLauncher(launcherPath)}"));
        return await ReadLinuxAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveOwnedLinuxLegacyArtifactAsync(CancellationToken cancellationToken)
    {
        var legacyPath = _options.LinuxLegacyArtifactPath;
        if (string.IsNullOrEmpty(legacyPath) || Directory.Exists(legacyPath) || !File.Exists(legacyPath))
            return;

        var inspection = InspectLegacyArtifact(await File.ReadAllTextAsync(legacyPath, cancellationToken).ConfigureAwait(false), windows: false);
        if (inspection.Kind is not (ArtifactKind.Owned or ArtifactKind.Stale))
        {
            _diagnostics.Record(new DiagnosticEvent("startup_registration_legacy_not_owned", Status: "legacy_artifact_left_alone"));
            return;
        }

        File.Delete(legacyPath);
        _diagnostics.Record(new DiagnosticEvent("startup_registration_legacy_removed",
            Status: inspection.Kind == ArtifactKind.Owned ? "legacy_owned_removed,disable" : "legacy_stale_removed,disable"));
    }

    private static string? ParseLinuxArtifact(string[] lines, string managedMarker, string desktopEntryName)
    {
        if (lines.Length != 7 ||
            !string.Equals(lines[0], "[Desktop Entry]", StringComparison.Ordinal) ||
            !string.Equals(lines[1], "Type=Application", StringComparison.Ordinal) ||
            !string.Equals(lines[2], $"Name={desktopEntryName}", StringComparison.Ordinal) ||
            !string.Equals(lines[5], "Terminal=false", StringComparison.Ordinal) ||
            !string.Equals(lines[6], managedMarker, StringComparison.Ordinal))
            return null;

        const string execPrefix = "Exec=\"";
        const string execSuffix = "\" --autostart";
        const string tryExecPrefix = "TryExec=";
        if (!lines[3].StartsWith(execPrefix, StringComparison.Ordinal) ||
            !lines[3].EndsWith(execSuffix, StringComparison.Ordinal) ||
            !lines[4].StartsWith(tryExecPrefix, StringComparison.Ordinal))
            return null;

        var escapedExecLength = lines[3].Length - execPrefix.Length - execSuffix.Length;
        if (escapedExecLength <= 0)
            return null;

        var escapedExecPath = lines[3].Substring(execPrefix.Length, escapedExecLength);
        var escapedTryExecPath = lines[4].Substring(tryExecPrefix.Length);
        if (!string.Equals(escapedExecPath, escapedTryExecPath, StringComparison.Ordinal))
            return null;

        return UnescapeDesktopString(escapedExecPath);
    }

    private static string UnescapeDesktopString(string value)
    {
        var result = new StringBuilder(value.Length);
        var escaping = false;
        foreach (var character in value)
        {
            if (escaping)
            {
                result.Append(character);
                escaping = false;
            }
            else if (character == '\\')
            {
                escaping = true;
            }
            else
            {
                result.Append(character);
            }
        }

        if (escaping)
            result.Append('\\');
        return result.ToString();
    }

    private static string LinuxContent(string launcherPath)
    {
        var executableArgument = EscapeDesktopExecArgument(launcherPath);
        var tryExecPath = EscapeDesktopString(launcherPath);
        return string.Join('\n', [
            "[Desktop Entry]",
            "Type=Application",
            $"Name={DesktopEntryName}",
            $"Exec={executableArgument} --autostart",
            $"TryExec={tryExecPath}",
            "Terminal=false",
            ManagedMarker
        ]) + '\n';
    }

    private static string EscapeDesktopExecArgument(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string EscapeDesktopString(string value)
    {
        return value.Replace("\\", "\\\\");
    }
}

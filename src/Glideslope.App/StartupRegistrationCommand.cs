using Glideslope.Core;

namespace Glideslope.App;

internal sealed record StartupRegistrationCommandResult(bool Handled, int ExitCode, string? IssueCode);

/// <summary>
/// Headless installer entry point. It intentionally runs before single-instance and Avalonia setup,
/// while delegating ownership and file-format decisions to the same startup service as Settings.
/// </summary>
internal static class StartupRegistrationCommand
{
    private const string ArgumentName = "--startup-registration";

    public static async Task<StartupRegistrationCommandResult> TryRunAsync(
        IReadOnlyList<string> args,
        string? processPath,
        IDiagnosticSink diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var commandIndices = Enumerable.Range(0, args.Count)
            .Where(index => string.Equals(args[index], ArgumentName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var proofRootIndices = Enumerable.Range(0, args.Count)
            .Where(index => string.Equals(args[index], ProofRoot.ArgumentName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var malformedFlag = args.Any(argument => argument.StartsWith($"{ArgumentName}=", StringComparison.OrdinalIgnoreCase));
        if (commandIndices.Length == 0 && !malformedFlag)
            return new StartupRegistrationCommandResult(false, 0, null);
        if (commandIndices.Length != 1 || proofRootIndices.Length > 1 || malformedFlag || commandIndices[0] + 1 >= args.Count)
            return Reject(diagnostics, "startup_command_invalid", 2);

        var commandIndex = commandIndices[0];
        if (proofRootIndices.Any(index => index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal)) ||
            (proofRootIndices.Length == 1 && ProofRoot.Current is null))
            return Reject(diagnostics, "startup_proof_root_invalid", 2);
        var action = args[commandIndex + 1];
        var enabled = action.Equals("enable", StringComparison.OrdinalIgnoreCase);
        if (!enabled && !action.Equals("disable", StringComparison.OrdinalIgnoreCase))
            return Reject(diagnostics, "startup_command_invalid", 2);

        for (var index = 0; index < args.Count; index++)
        {
            if (index == commandIndex || index == commandIndex + 1) continue;
            if (string.Equals(args[index], ProofRoot.ArgumentName, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                index++;
                continue;
            }
            return Reject(diagnostics, "startup_command_invalid", 2);
        }

        diagnostics.Record(new DiagnosticEvent("startup_registration_command_started", Status: enabled ? "enable" : "disable"));

        // On Windows the installed launcher is Glideslope.App.exe, marked by the installer.
        var launcher = InstalledLauncherPath.TryResolve(processPath);
        if (!launcher.Succeeded)
            return Reject(diagnostics, launcher.IssueCode ?? "startup_launcher_missing", 2);

        var startup = new PlatformStartupRegistration(diagnostics);
        var proofRoot = ProofRoot.Current ?? Environment.GetEnvironmentVariable("GLIDESLOPE_PROOF_ROOT");
        // Check every file the service may touch, including the Run value and legacy Startup-folder paths.
        if (!string.IsNullOrWhiteSpace(proofRoot) &&
            !startup.ResolveFileLocations().All(location => IsWithinProofRoot(location, proofRoot)))
            return Reject(diagnostics, "startup_proof_path_escape", 2);

        var result = await startup.SetAsync(enabled, launcher.Path!, cancellationToken).ConfigureAwait(false);
        if (result.IssueCode is not null || result.IsRegistered != enabled)
            return Reject(diagnostics, result.IssueCode ?? "startup_registration_failed", 1);

        diagnostics.Record(new DiagnosticEvent("startup_registration_command_complete",
            Status: enabled ? "registered" : "unregistered"));
        return new StartupRegistrationCommandResult(true, 0, null);
    }

    private static StartupRegistrationCommandResult Reject(IDiagnosticSink diagnostics, string issueCode, int exitCode)
    {
        diagnostics.Record(new DiagnosticEvent("startup_registration_command_failed", Status: issueCode));
        return new StartupRegistrationCommandResult(true, exitCode, issueCode);
    }

    private static bool IsWithinProofRoot(string artifactPath, string proofRoot)
    {
        var root = Path.GetFullPath(proofRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var artifact = Path.GetFullPath(artifactPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return artifact.StartsWith(root, comparison);
    }
}

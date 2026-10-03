using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Chooses the installed launcher or, for an apphost process, the current apphost as a fallback. Diagnostics
/// record status codes only and never include launcher paths.
/// </summary>
/// <remarks>
/// The fallback is accepted only when the process path matches the expected apphost; a dotnet host or other
/// executable must never be registered in its place. Installed launcher discovery is platform-specific.
/// </remarks>
internal static class StartupLauncherPath
{
    public static string? Choose(InstalledLauncherPathResult installed, string? processPath, string appHostPath, IDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (installed.Succeeded)
        {
            diagnostics.Record(new DiagnosticEvent("startup_launcher_installed", Status: "installed_launcher"));
            return installed.Path;
        }

        var installedIssue = installed.IssueCode ?? "startup_launcher_missing";
        var processIssue = CheckProcessPath(processPath, appHostPath);
        if (processIssue is null)
        {
            diagnostics.Record(new DiagnosticEvent("startup_launcher_fallback_process_exe", Status: installedIssue));
            return processPath;
        }

        diagnostics.Record(new DiagnosticEvent("startup_launcher_missing", Status: $"startup_registration_unavailable_{processIssue}"));
        return null;
    }

    /// <summary>The apphost file name for the entry assembly: <c>Glideslope.App.exe</c> on Windows,
    /// <c>Glideslope.App</c> elsewhere.</summary>
    public static string AppHostFileName(string entryAssemblyName) =>
        OperatingSystem.IsWindows() ? entryAssemblyName + ".exe" : entryAssemblyName;

    /// <summary>Returns null when the process path is the expected apphost and an existing file that can be
    /// opened for reading, otherwise a short reason code.</summary>
    private static string? CheckProcessPath(string? processPath, string appHostPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return "process_path_missing";
        if (!IsSamePath(processPath, appHostPath))
            return "process_path_not_app";
        if (Directory.Exists(processPath) || !File.Exists(processPath))
            return "process_path_not_file";

        try
        {
            // The running exe is open by the loader, so share everything; this only proves read access.
            using var stream = new FileStream(processPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return null;
        }
        catch (IOException)
        {
            return "process_path_unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            return "process_path_unreadable";
        }
    }

    private static bool IsSamePath(string left, string right)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }
}

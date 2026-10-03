using Glideslope.Core;

namespace Glideslope.App;

internal sealed record InstalledLauncherPathResult(string? Path, string? IssueCode)
{
    public bool Succeeded => Path is not null && IssueCode is null;
}

/// <summary>
/// The launcher an installed copy registers for start at sign-in. Linux: the packaged
/// glideslope-launcher.sh beside the app. Windows uses Glideslope.App.exe itself when the installer's
/// glideslope-installed.marker sits beside it. A build run from its output folder has no marker, so it resolves
/// nothing here and falls back per design doc §16.1 (<see cref="StartupLauncherPath"/>).
/// </summary>
internal static class InstalledLauncherPath
{
    private const string LinuxLauncherName = "glideslope-launcher.sh";

    public static string Resolve(string? processPath)
    {
        var result = TryResolve(processPath);
        return result.Path ?? throw new InvalidOperationException(result.IssueCode ?? "startup_launcher_missing");
    }

    public static InstalledLauncherPathResult TryResolve(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return new InstalledLauncherPathResult(null, "startup_launcher_missing");

        string fullProcessPath;
        try
        {
            fullProcessPath = Path.GetFullPath(processPath);
        }
        catch (ArgumentException)
        {
            return new InstalledLauncherPathResult(null, "startup_launcher_missing");
        }
        catch (NotSupportedException)
        {
            return new InstalledLauncherPathResult(null, "startup_launcher_missing");
        }

        if (!File.Exists(fullProcessPath) || Directory.Exists(fullProcessPath))
            return new InstalledLauncherPathResult(null, "startup_launcher_missing");

        var directory = Path.GetDirectoryName(fullProcessPath);
        if (string.IsNullOrWhiteSpace(directory))
            return new InstalledLauncherPathResult(null, "startup_launcher_missing");

        if (OperatingSystem.IsWindows())
        {
            if (!string.Equals(Path.GetFileName(fullProcessPath), PlatformStartupRegistration.WindowsAppFileName, StringComparison.OrdinalIgnoreCase))
                return new InstalledLauncherPathResult(null, "startup_launcher_missing");

            var marker = Path.Combine(directory, PlatformStartupRegistration.WindowsInstalledMarkerFileName);
            return File.Exists(marker) && !Directory.Exists(marker)
                ? new InstalledLauncherPathResult(fullProcessPath, null)
                : new InstalledLauncherPathResult(null, "startup_launcher_missing");
        }

        var launcherPath = Path.Combine(directory, LinuxLauncherName);
        return File.Exists(launcherPath) && !Directory.Exists(launcherPath)
            ? new InstalledLauncherPathResult(launcherPath, null)
            : new InstalledLauncherPathResult(null, "startup_launcher_missing");
    }
}

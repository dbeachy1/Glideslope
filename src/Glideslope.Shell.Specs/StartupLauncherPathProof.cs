using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Design doc §16.1: StartupLauncherPath.Choose returns the installed launcher when it
/// resolved, falls back to the running exe when it did not, and returns null only when neither exists.
/// Each branch records its decision with a status and never the path. the
/// fallback is only the app's own apphost; the dotnet host (or any other exe) is refused.
/// </summary>
internal static class StartupLauncherPathProof
{
    public static void Run(string tempRoot)
    {
        var directory = Path.Combine(tempRoot, "startup launcher fallback");
        Directory.CreateDirectory(directory);
        var processPath = Path.Combine(directory, StartupLauncherPath.AppHostFileName("Glideslope.App"));
        File.WriteAllText(processPath, "owned executable");
        // on Windows the installed launcher is an installed Glideslope.App.exe (with
        // the installer's marker), no longer glideslope-launcher.cmd; any path distinct from the process works here.
        var installedPath = OperatingSystem.IsWindows()
            ? Path.Combine(directory, "installed", "Glideslope.App.exe")
            : Path.Combine(directory, "glideslope-launcher.sh");
        var noInstalled = new InstalledLauncherPathResult(null, "startup_launcher_missing");

        // Branch 1: the installed launcher resolved, so it wins over the process path.
        var installedSink = new RecordingDiagnosticSink();
        var installed = StartupLauncherPath.Choose(new InstalledLauncherPathResult(installedPath, null), processPath, processPath, installedSink);
        Assert(installed == installedPath, "a resolved installed launcher is used as the startup launcher");
        Assert(installedSink.Events.Count == 1 && installedSink.Events[0].Code == "startup_launcher_installed",
            "choosing the installed launcher records startup_launcher_installed");
        AssertNoPath(installedSink, processPath, installedPath);

        // Branch 2: no installed launcher (a build run from its output folder), but the apphost exists.
        var fallbackSink = new RecordingDiagnosticSink();
        var fallback = StartupLauncherPath.Choose(InstalledLauncherPath.TryResolve(processPath), processPath, processPath, fallbackSink);
        Assert(fallback == processPath, "a missing installed launcher falls back to the running exe");
        Assert(fallbackSink.Events.Count == 1 &&
               fallbackSink.Events[0].Code == "startup_launcher_fallback_process_exe" &&
               fallbackSink.Events[0].Status == "startup_launcher_missing",
            "the fallback records startup_launcher_fallback_process_exe with the installed launcher's issue as status");
        AssertNoPath(fallbackSink, processPath, installedPath);

        // The apphost path is compared as a path, not as text: a different spelling of the same file still matches.
        var respelled = Path.Combine(directory, ".", Path.GetFileName(processPath));
        var respelledSink = new RecordingDiagnosticSink();
        Assert(StartupLauncherPath.Choose(noInstalled, respelled, processPath, respelledSink) == respelled,
            "a differently spelled path to the same apphost is still the fallback");

        // started as `dotnet Glideslope.App.dll`, the process path is the dotnet host. Registering
        // it would start nothing at sign-in, so it is refused with process_path_not_app.
        var dotnetHost = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(dotnetHost, "dotnet host");
        var dotnetSink = new RecordingDiagnosticSink();
        var dotnet = StartupLauncherPath.Choose(noInstalled, dotnetHost, processPath, dotnetSink);
        Assert(dotnet is null, "the dotnet host is never accepted as the startup launcher");
        Assert(dotnetSink.Events.Count == 1 && dotnetSink.Events[0].Code == "startup_launcher_missing" &&
               dotnetSink.Events[0].Status == "startup_registration_unavailable_process_path_not_app",
            "refusing the dotnet host records process_path_not_app");
        AssertNoPath(dotnetSink, dotnetHost, processPath, installedPath);

        // Branch 3: neither an installed launcher nor an existing apphost file.
        var missingProcess = Path.Combine(directory, "missing", StartupLauncherPath.AppHostFileName("Glideslope.App"));
        var missingSink = new RecordingDiagnosticSink();
        var missing = StartupLauncherPath.Choose(noInstalled, missingProcess, missingProcess, missingSink);
        Assert(missing is null, "no installed launcher and no process file leaves startup registration unavailable");
        Assert(missingSink.Events.Count == 1 && missingSink.Events[0].Code == "startup_launcher_missing" &&
               missingSink.Events[0].Status == "startup_registration_unavailable_process_path_not_file",
            "the unavailable case records startup_launcher_missing with process_path_not_file");
        AssertNoPath(missingSink, missingProcess, installedPath);

        var nullSink = new RecordingDiagnosticSink();
        Assert(StartupLauncherPath.Choose(noInstalled, null, processPath, nullSink) is null &&
               nullSink.Events.Count == 1 && nullSink.Events[0].Code == "startup_launcher_missing",
            "a null process path is treated as missing, not as a fallback");

        var directorySink = new RecordingDiagnosticSink();
        Assert(StartupLauncherPath.Choose(noInstalled, directory, directory, directorySink) is null &&
               directorySink.Events.Count == 1 && directorySink.Events[0].Code == "startup_launcher_missing",
            "a directory is never accepted as the process-exe fallback");
        AssertNoPath(directorySink, directory, installedPath);
    }

    private static void AssertNoPath(RecordingDiagnosticSink sink, params string[] paths)
    {
        foreach (var diagnostic in sink.Events)
        {
            foreach (var path in paths)
            {
                Assert(diagnostic.Status is null || !diagnostic.Status.Contains(path, StringComparison.OrdinalIgnoreCase),
                    $"startup launcher diagnostic {diagnostic.Code} must not carry a path");
                Assert(diagnostic.ProviderId is null, $"startup launcher diagnostic {diagnostic.Code} has no provider");
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    private sealed class RecordingDiagnosticSink : IDiagnosticSink
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public void Record(DiagnosticEvent diagnostic) => Events.Add(diagnostic);
    }
}

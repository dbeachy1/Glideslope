using System.Diagnostics;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

internal static class RestartLifecycleProof
{
    public static async Task RunAsync()
    {
        var events = new List<string>();
        ProcessStartInfo? requestedStart = null;
        var diagnostics = new RecordingDiagnosticSink();
        await RestartLifecycle.RunAsync(
            restart: true,
            launcherPath: "glideslope-launcher.sh",
            launch: startInfo =>
            {
                requestedStart = startInfo;
                events.Add("launch");
                return Process.GetCurrentProcess();
            },
            exit: () =>
            {
                events.Add("old_instance_exit");
                return Task.CompletedTask;
            },
            diagnostics).ConfigureAwait(false);

        Assert(requestedStart is { FileName: "glideslope-launcher.sh", UseShellExecute: false },
            "restart starts the configured launcher without shell execution");
        Assert(requestedStart!.ArgumentList.Count == 2 &&
               requestedStart.ArgumentList[0] == RestartLifecycle.WaitForParentArgument &&
               requestedStart.ArgumentList[1] == Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "restart child receives the old process id handoff");
        Assert(events.SequenceEqual(["launch", "old_instance_exit"]),
            "new process starts before the old instance begins teardown");
        Assert(diagnostics.Events.Any(item => item.Code == "shell_restart_launched"),
            "successful process creation is recorded");

        var exitRan = false;
        var failureDiagnostics = new RecordingDiagnosticSink();
        try
        {
            await RestartLifecycle.RunAsync(
                restart: true,
                launcherPath: "glideslope-launcher.sh",
                launch: _ => throw new InvalidOperationException("simulated process start failure"),
                exit: () =>
                {
                    exitRan = true;
                    return Task.CompletedTask;
                },
                failureDiagnostics).ConfigureAwait(false);
            throw new InvalidOperationException("restart launch failure was not propagated");
        }
        catch (InvalidOperationException ex) when (ex.Message == "simulated process start failure")
        {
        }

        Assert(!exitRan, "launch failure leaves the existing coordinator running");
        Assert(failureDiagnostics.Events.Any(item => item.Code == "shell_restart_failed"),
            "restart launch failure is recorded for diagnostics");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"restart lifecycle proof failed: {message}");
    }

    private sealed class RecordingDiagnosticSink : IDiagnosticSink
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public void Record(DiagnosticEvent diagnostic) => Events.Add(diagnostic);
    }
}

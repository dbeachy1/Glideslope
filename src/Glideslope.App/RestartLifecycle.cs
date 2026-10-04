using System.Diagnostics;
using Glideslope.Core;

namespace Glideslope.App;

internal static class RestartLifecycle
{
    internal const string WaitForParentArgument = "--restart-after-pid";
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromMinutes(2);

    internal static async Task RunAsync(
        bool restart,
        string? launcherPath,
        Func<ProcessStartInfo, Process?> launch,
        Func<Task> exit,
        IDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Process? child = null;
        if (restart)
        {
            try
            {
                child = StartChild(launcherPath, launch);
                diagnostics.Record(new DiagnosticEvent("shell_restart_launched", Status: "new_process_started"));
            }
            catch (Exception ex)
            {
                diagnostics.Record(new DiagnosticEvent("shell_restart_failed", Status: ex.GetType().Name));
                throw;
            }
        }

        using (child)
        {
            await exit().ConfigureAwait(true);
        }
    }

    internal static int WaitForParentExit(IReadOnlyList<string> args, IDiagnosticSink diagnostics)
    {
        var flagIndexes = Enumerable.Range(0, args.Count)
            .Where(index => string.Equals(args[index], WaitForParentArgument, StringComparison.Ordinal))
            .ToArray();
        if (flagIndexes.Length == 0) return 0;
        if (flagIndexes.Length != 1 || flagIndexes[0] + 2 != args.Count ||
            !int.TryParse(args[flagIndexes[0] + 1], out var parentId) || parentId <= 0)
        {
            diagnostics.Record(new DiagnosticEvent("shell_restart_handoff_failed", Status: "invalid_parent_process_id"));
            return 1;
        }

        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (!parent.WaitForExit((int)ParentExitTimeout.TotalMilliseconds))
            {
                diagnostics.Record(new DiagnosticEvent("shell_restart_handoff_failed", Status: "parent_exit_timeout"));
                return 1;
            }
        }
        catch (ArgumentException)
        {
            // The parent may exit between the restart process starting and this lookup.
        }

        diagnostics.Record(new DiagnosticEvent("shell_restart_handoff_complete", Status: "parent_exited"));
        return 0;
    }

    private static Process StartChild(string? launcherPath, Func<ProcessStartInfo, Process?> launch)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            throw new InvalidOperationException("restart_launcher_missing");

        var startInfo = new ProcessStartInfo(launcherPath) { UseShellExecute = false };
        startInfo.ArgumentList.Add(WaitForParentArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return launch(startInfo) ?? throw new InvalidOperationException("restart_process_not_started");
    }
}

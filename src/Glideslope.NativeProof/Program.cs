using System.IO;
using Glideslope.Core;

namespace Glideslope.NativeProof;

/// <summary>
/// The native window proof. Not a default test suite: run only via
/// scripts/Invoke-NativeWindowProof.ps1, or directly with --app &lt;path to Glideslope.App.exe&gt; --root
/// &lt;an owned, empty temp folder&gt;.
///
/// Each scenario runs in an isolated app session with a fresh settings seed and hidden desktop. This keeps
/// scenarios independent and permits verified cleanup after each run. The sessions cover move/dock/raise,
/// card-mode behavior, and Windows session end; Main aggregates all results.
///
/// Exit codes:
///   0 = every session's hidden desktop was set up and every scenario committed
///       as the design expects, every session's cleanup verified.
///   1 = every session stood up and cleaned up, but at least one scenario failed somewhere (see this
///       proof's own final report).
///   2 = bad command-line arguments.
///   3 = native_desktop_unavailable in at least one session: its hidden desktop or the app on it could
///       not be stood up at all.
///   4 = every session stood up, but at least one session's cleanup could not verify its process, desktop
///       and proof folder are all gone.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var appPath = ArgumentValue(args, "--app");
        var root = ArgumentValue(args, "--root");
        if (appPath is null || root is null)
        {
            Console.Error.WriteLine("Usage: Glideslope.NativeProof --app <path to Glideslope.App.exe> --root <owned empty temp folder>");
            return 2;
        }

        root = Path.GetFullPath(root);
        void Log(string message) => Console.WriteLine($"{DateTime.UtcNow:O} {message}");

        Directory.CreateDirectory(root);
        Log($"proof_start app={appPath} root={root}");

        var outcomes = new List<SessionOutcome>
        {
            // Run the core interaction scenarios in one isolated session before the independent mode proofs.
            RunSession(appPath, Path.Combine(root, "session-original"), "original",
                ProofSettingsSeeder.Seed, createHelperWindow: true,
                (context, desktopHandle, helper) =>
                [
                    MoveCommitScenario.Run(context),
                    WorkflowScenario.Run(context),
                    ScreenEdgeSnapScenario.Run(context),
                    BringToFrontScenario.Run(context, desktopHandle, helper!),
                ],
                Log),
            // 2.0 mini mode (2.0 design §8.3): each of these three gets its own fresh session from the one
            // mode seed (ProofSettingsSeeder.SeedModeScenario) - Claude+Codex already docked full, Gemini
            // detached full - so none of them depends on another scenario's end state, and none needs the
            // z-order helper window (that is BringToFrontScenario's alone).
            RunSession(appPath, Path.Combine(root, "session-toggle-group"), "toggle_group",
                ProofSettingsSeeder.SeedModeScenario, createHelperWindow: false,
                (context, _, _) => [CardModeToggleScenario.Run(context)],
                Log),
            RunSession(appPath, Path.Combine(root, "session-mini-onto-full"), "mini_onto_full",
                ProofSettingsSeeder.SeedModeScenario, createHelperWindow: false,
                (context, _, _) => [MiniOntoFullDockScenario.Run(context)],
                Log),
            RunSession(appPath, Path.Combine(root, "session-full-onto-mini"), "full_onto_mini",
                ProofSettingsSeeder.SeedModeScenario, createHelperWindow: false,
                (context, _, _) => [FullOntoMiniDockScenario.Run(context)],
                Log),
            // A Windows restart must not turn providers off. Last, and in its own
            // session, because the app exits during it.
            RunSession(appPath, Path.Combine(root, "session-end"), "session_end",
                ProofSettingsSeeder.Seed, createHelperWindow: false,
                (context, desktopHandle, _) => [SessionEndScenario.Run(context, desktopHandle)],
                Log),
        };

        Log("=== scenario results ===");
        foreach (var outcome in outcomes)
        {
            Log($"session={outcome.Label} stoodUp={outcome.StoodUp} cleanupVerified={outcome.CleanupOk}");
            foreach (var result in outcome.Results)
            {
                Log($"scenario={result.Name} passed={result.Passed} reason=\"{result.Reason}\"");
                foreach (var line in result.Evidence) Log($"  evidence: {line}");
            }
        }

        var allStoodUp = outcomes.All(outcome => outcome.StoodUp);
        var allCleanupOk = outcomes.All(outcome => outcome.CleanupOk);
        var allScenariosPassed = allStoodUp && outcomes.SelectMany(outcome => outcome.Results).All(result => result.Passed);
        Log($"proof_end allScenariosPassed={allScenariosPassed} cleanupVerified={allCleanupOk}");

        if (!allStoodUp) return 3;
        if (!allCleanupOk) return 4;
        return allScenariosPassed ? 0 : 1;
    }

    /// <summary>One session's outcome: whether it stood up far enough to run its scenarios, whether its
    /// cleanup verified, and whatever scenario results it produced (empty when it never stood up).</summary>
    private sealed record SessionOutcome(string Label, bool StoodUp, bool CleanupOk, IReadOnlyList<ScenarioResult> Results);

    /// <summary>
    /// Runs one whole app session against <paramref name="sessionRoot"/> (an owned, empty subfolder of
    /// the proof's own --root): seeds settings.json with <paramref name="seed"/>, creates a hidden
    /// desktop, launches the app on it with --proof-root, verifies it is really hidden, locates the three
    /// card windows, optionally starts the z-order helper window, runs <paramref name="runScenarios"/>
    /// against the resulting ScenarioContext, then cleans up and verifies.
    /// </summary>
    private static SessionOutcome RunSession(string appPath, string sessionRoot, string label,
        Func<string, Action<string>, SeededLayout> seed, bool createHelperWindow,
        Func<ScenarioContext, nint, HelperMarkerWindow?, IReadOnlyList<ScenarioResult>> runScenarios,
        Action<string> log)
    {
        log($"session_start label={label} root={sessionRoot}");
        Directory.CreateDirectory(sessionRoot);

        SeededLayout seeded;
        try
        {
            seeded = seed(sessionRoot, log);
        }
        catch (Exception exception)
        {
            log($"native_desktop_unavailable reason=settings_seed_failed detail={exception.Message} session={label}");
            return new SessionOutcome(label, false, true, []);
        }

        var desktopName = $"GlideslopeProof-{Guid.NewGuid():N}"[..32];
        using var session = new HiddenDesktopSession(desktopName);
        var desktopIssue = session.CreateDesktop();
        if (desktopIssue is not null)
        {
            log($"native_desktop_unavailable reason={desktopIssue} session={label}");
            return new SessionOutcome(label, false, true, []);
        }

        // Window design "Level 3 waits: signals, not timers" item 1: the harness creates or opens
        // the named log event for this session's own proof root before it launches the app, so the app's
        // very first afterWrite (Glideslope.App/Program.cs) always finds the kernel object already there
        // instead of racing its own creation. ProofRoot.LogEventName is computed only from sessionRoot's
        // full path, which is exactly what the app will normalize the same --proof-root value to, so both
        // sides land on the identical name.
        var logEventName = ProofRoot.LogEventName(sessionRoot);
        using var logEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, logEventName);
        log($"proof_log_event_ready name={logEventName} session={label}");

        var launchIssue = session.LaunchProcess(appPath, $"--proof-root \"{sessionRoot}\"");
        if (launchIssue is not null)
        {
            log($"native_desktop_unavailable reason={launchIssue} session={label}");
            var cleanupAfterLaunchFailure = CleanupAndReport(session, sessionRoot, log);
            return new SessionOutcome(label, false, cleanupAfterLaunchFailure, []);
        }

        log($"app_launched pid={session.ProcessId} mainThreadId={session.MainThreadId} desktop={desktopName} session={label}");

        var verification = session.VerifyChildIsHidden();
        log($"desktop_verification hiddenDesktopName={verification.HiddenDesktopName} " +
            $"childThreadDesktopName={verification.ChildThreadDesktopName} inputDesktopName={verification.InputDesktopName} " +
            $"childIsOnHiddenDesktop={verification.ChildIsOnHiddenDesktop} hiddenDesktopIsNotInputDesktop={verification.HiddenDesktopIsNotInputDesktop} " +
            $"childBecameInputIdle={verification.ChildBecameInputIdle} session={label}");
        if (!verification.ChildIsOnHiddenDesktop || !verification.HiddenDesktopIsNotInputDesktop)
        {
            log($"native_desktop_unavailable reason=child_process_not_confirmed_hidden session={label}");
            var cleanupAfterVerifyFailure = CleanupAndReport(session, sessionRoot, log);
            return new SessionOutcome(label, false, cleanupAfterVerifyFailure, []);
        }

        var paths = ProofPaths.Resolve(sessionRoot);
        var logFile = ProofPaths.LogFile(paths);
        var proofLog = new ProofLog(logFile, logEvent, log);

        // Card windows exist before layout restoration applies their saved sizes. Wait for the restore
        // completion or failure event before enumerating them, so scenarios see the restored geometry.
        var restoreLine = proofLog.WaitForLine(0, line =>
            (line.Status?.Contains("source=layout_restored", StringComparison.Ordinal) ?? false) ||
            line.Code is "layout_restore_rejected" or "layout_restore_failed");
        log(restoreLine is null
            ? $"layout_restore_not_seen session={label}"
            : $"layout_restore_seen code={restoreLine.Code} status={restoreLine.Status} session={label}");

        var cardWindows = CardWindowLocator.FindCardWindows(session.DesktopHandle, session.ProcessId, log);
        if (cardWindows.Count != CardWindowLocator.ExpectedProviderIds.Count)
        {
            log($"native_desktop_unavailable reason=card_windows_not_found found={cardWindows.Count} " +
                $"expected={CardWindowLocator.ExpectedProviderIds.Count} session={label}");
            var cleanupAfterCardsMissing = CleanupAndReport(session, sessionRoot, log);
            return new SessionOutcome(label, false, cleanupAfterCardsMissing, []);
        }

        log("card_windows_found " + string.Join(" ", cardWindows.Select(pair => $"{pair.Key}=0x{pair.Value:X}")) + $" session={label}");

        HelperMarkerWindow? helper = null;
        if (createHelperWindow)
        {
            helper = new HelperMarkerWindow(session.DesktopHandle, log);
            if (helper.StartupFailure is not null)
                log($"helper_marker_window_failed reason={helper.StartupFailure} session={label}");
        }

        var context = new ScenarioContext(cardWindows, seeded, seeded.SettingsFile, proofLog, log);
        var results = runScenarios(context, session.DesktopHandle, helper);

        // The helper's thread (when created) is attached to the hidden desktop; CloseDesktop fails
        // (desktop_closed=False) while any thread in this process still uses that desktop, so the helper
        // must be gone before cleanup. Dispose is idempotent and a no-op on a null helper.
        helper?.Dispose();
        var cleanupOk = CleanupAndReport(session, sessionRoot, log);
        return new SessionOutcome(label, true, cleanupOk, results);
    }

    private static bool CleanupAndReport(HiddenDesktopSession session, string root, Action<string> log)
    {
        var (processGone, desktopClosed) = session.Cleanup(TimeSpan.FromSeconds(10));
        var folderDeleted = TryDeleteFolder(root, log);
        log($"cleanup process_gone={processGone} desktop_closed={desktopClosed} folder_deleted={folderDeleted}");
        return processGone && desktopClosed && folderDeleted;
    }

    /// <summary>Deletes the session root after process cleanup has completed. A deletion failure is logged
    /// and reported.</summary>
    private static bool TryDeleteFolder(string root, Action<string> log)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            var deleted = !Directory.Exists(root);
            log($"proof_folder_delete_result root={root} deleted={deleted}");
            return deleted;
        }
        catch (IOException exception)
        {
            log($"proof_folder_delete_failed root={root} reason={exception.GetType().Name}");
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            log($"proof_folder_delete_failed root={root} reason={exception.GetType().Name}");
            return false;
        }
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }
}

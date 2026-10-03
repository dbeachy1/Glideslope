using System.Reflection;
using System.Globalization;
using System.Threading;
using Avalonia;
using Glideslope.Core;
using Glideslope.Storage;

namespace Glideslope.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Persist diagnostics because a normal launch has no visible console.
        var paths = new DefaultUserPathProvider();
        var proof = ProofRoot.Configure(args);

        // Native proof uses a named event to wait for log writes without polling. Named EventWaitHandle is
        // Windows-only, so other platforms run without the signal.
        var proofLogEventWanted = proof.Succeeded && ProofRoot.Current is not null;
        using var proofLogEvent = proofLogEventWanted && OperatingSystem.IsWindows()
            ? new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ProofRoot.LogEventName(ProofRoot.Current!))
            : null;

        var diagnostics = new CompositeDiagnosticSink(
            new ConsoleDiagnosticSink(),
            new FileDiagnosticSink(AppLogFile.PathFor(paths.Get()), proofLogEvent is null ? null : () => proofLogEvent.Set()));
        if (!proof.Succeeded)
        {
            diagnostics.Record(new DiagnosticEvent(proof.IssueCode ?? "proof_root_invalid", Status: "launch_rejected"));
            return 2;
        }

        if (proofLogEvent is not null)
        {
            diagnostics.Record(new DiagnosticEvent("proof_log_event_ready", Status: "created_or_opened"));
        }
        else if (proofLogEventWanted)
        {
            diagnostics.Record(new DiagnosticEvent("proof_log_event_skipped", Status: "named_events_unsupported_on_platform"));
        }

        var startupCommand = StartupRegistrationCommand.TryRunAsync(args, Environment.ProcessPath, diagnostics)
            .GetAwaiter().GetResult();
        if (startupCommand.Handled)
            return startupCommand.ExitCode;

        var broker = new InstanceBroker(paths, diagnostics);
        var intent = ActivationIntentParser.Parse(args);
        var activationRouter = new StartupActivationRouter();
        var result = broker.AcquireAsync(intent, activationRouter.DispatchAsync).GetAwaiter().GetResult();
        if (result.Role != InstanceRole.Owner)
        {
            diagnostics.Record(new DiagnosticEvent("instance_activation_complete", Status: result.Role.ToString()));
            if (result.Role == InstanceRole.ActivationFailed)
            {
                diagnostics.Record(new DiagnosticEvent("instance_activation_failed", Status: result.IssueCode ?? "unknown"));
            }

            broker.DisposeAsync().GetAwaiter().GetResult();
            return result.Role == InstanceRole.Forwarded ? 0 : 1;
        }

        // Only the confirmed single-instance owner migrates, so a second concurrently launched process
        // can never race this move; it runs before anything below opens settings or history.
        // Checkpoint the source database before migration so SQLite sidecar data is included in the move.
        UserDataMigration.MigrateIfNeeded(diagnostics, SqliteHistoryCheckpoint.TryCheckpointInPlace);

        // Keep installed-launcher discovery separate from the current apphost: startup reconciliation may only
        // migrate an entry that already launches this same executable.
        var installedLauncher = InstalledLauncherPath.TryResolve(Environment.ProcessPath);
        var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name ?? "Glideslope.App";
        var appHostPath = Path.Combine(AppContext.BaseDirectory, StartupLauncherPath.AppHostFileName(entryAssemblyName));
        var launcherPath = StartupLauncherPath.Choose(installedLauncher, Environment.ProcessPath, appHostPath, diagnostics);

        var context = new AppLaunchContext
        {
            Broker = broker,
            Intent = intent,
            ActivationRouter = activationRouter,
            Paths = paths,
            Diagnostics = diagnostics,
            CapturedUiCulture = CultureInfo.CurrentUICulture,
            CapturedFormatCulture = CultureInfo.CurrentCulture,
            LocaleEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["LANGUAGE"] = Environment.GetEnvironmentVariable("LANGUAGE"),
                ["LC_ALL"] = Environment.GetEnvironmentVariable("LC_ALL"),
                ["LC_MESSAGES"] = Environment.GetEnvironmentVariable("LC_MESSAGES"),
                ["LANG"] = Environment.GetEnvironmentVariable("LANG"),
            },
            LauncherPath = launcherPath,
            InstalledLauncherPath = installedLauncher.Path,
        };

        try
        {
            return BuildAvaloniaApp(context).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // A normal lifetime exit disposes the broker through WindowCoordinator.
            // This fallback covers platform setup failures before that coordinator exists.
            broker.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    private static AppBuilder BuildAvaloniaApp(AppLaunchContext context) =>
        AppBuilder.Configure(() => new GlideslopeApplication(context))
            .UsePlatformDetect()
            .LogToTrace();
}

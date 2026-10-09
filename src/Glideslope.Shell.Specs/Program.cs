using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

internal static class Program
{
    private static volatile string _currentSpec = "(none)";

    /// <summary>Prints the spec name, as before, and remembers it for the watchdog.</summary>
    private static void Announce(string name)
    {
        _currentSpec = name;
        Console.WriteLine($"spec {name}");
    }

    /// <summary>Design doc §5.4: a hung suite must fail instead of waiting indefinitely.</summary>
    private static void StartWatchdog(TimeSpan limit)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(limit);
            Console.Error.WriteLine($"spec timed out at {_currentSpec} after {limit.TotalMinutes:0} minutes");
            Console.Error.Flush();
            Environment.Exit(3);
        }) { IsBackground = true, Name = "spec-watchdog" };
        watchdog.Start();
    }

    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunEntryPointAsync(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Glideslope shell specs failed during '{_currentSpec}': {exception}");
            Console.Error.Flush();
            return 1;
        }
    }

    private static async Task<int> RunEntryPointAsync(string[] args)
    {
        if (args.Length > 0)
            _currentSpec = $"helper mode {args[0]}";

        if (IntPtr.Size != 8)
        {
            Console.Error.WriteLine("Glideslope shell specs require a 64-bit runtime (win-x64 and linux-x64 are the supported targets).");
            return 2;
        }

        if (args.Length > 0 && string.Equals(args[0], "--desktop-inventory", StringComparison.Ordinal))
            return DesktopInventoryProof.Run();
        if (args.Length > 0 && string.Equals(args[0], "--startup-visibility", StringComparison.Ordinal))
        {
            StartupVisibilitySpecs.Run();
            return 0;
        }
        if (args.Length > 0 && string.Equals(args[0], "--settings-provider-id-migration-proof", StringComparison.Ordinal))
            return await SettingsProviderIdMigrationProof.RunStandaloneAsync().ConfigureAwait(false);
        if (args.Length > 0 && string.Equals(args[0], "--linux-tray-lifecycle-proof", StringComparison.Ordinal))
            return await LinuxTrayLifecycleProof.RunAsync().ConfigureAwait(false);
        if (args.Length > 0 && string.Equals(args[0], "--localized-layout-proof", StringComparison.Ordinal))
        {
            CardPresentationProof.RunLocalizedLayoutAcceptanceProof();
            return 0;
        }
        if (args.Length > 0 && string.Equals(args[0], "--history-browse-proof", StringComparison.Ordinal))
        {
            HistoryBrowseSpecs.Run();
            return 0;
        }
        if (args.Length > 0 && string.Equals(args[0], "--history-reset-proof", StringComparison.Ordinal))
        {
            CardPresentationProof.RunHistoryResetProof();
            return 0;
        }
        if (args.Length == 2 && string.Equals(args[0], "--header-visual-proof", StringComparison.Ordinal))
        {
            StartWatchdog(TimeSpan.FromMinutes(2));
            CardPresentationProof.CaptureHeaderScreenshots(Path.GetFullPath(args[1]));
            return 0;
        }
        if (args.Length == 2 && string.Equals(args[0], "--usage-projection-proof", StringComparison.Ordinal))
        {
            StartWatchdog(TimeSpan.FromMinutes(2));
            CardPresentationProof.CaptureUsageProjectionScreenshots(Path.GetFullPath(args[1]));
            return 0;
        }
        if (args.Length == 2 && string.Equals(args[0], "--settings-projection-note-proof", StringComparison.Ordinal))
        {
            StartWatchdog(TimeSpan.FromMinutes(2));
            CardPresentationProof.CaptureSettingsProjectionNoteScreenshots(Path.GetFullPath(args[1]));
            return 0;
        }
        if (args.Length > 0 && string.Equals(args[0], "--language-settings-proof", StringComparison.Ordinal))
        {
            await RestartLifecycleProof.RunAsync().ConfigureAwait(false);
            LocalizationResourceProof.Run();
            CardPresentationProof.Run();
            LocalizationResourceProof.RunSatelliteResourceValidation();
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "--instance-child", StringComparison.Ordinal))
            return await RunInstanceChildWorkerAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && string.Equals(args[0], "--instance-owner-child", StringComparison.Ordinal))
            return await RunInstanceOwnerWorkerAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && string.Equals(args[0], "--instance-hung-client", StringComparison.Ordinal))
            return await RunHungClientWorkerAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && string.Equals(args[0], InstanceBrokerRecoveryProof.ExpectUnreachableChildMode, StringComparison.Ordinal))
            return await InstanceBrokerRecoveryProof.RunExpectUnreachableChildAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && string.Equals(args[0], InstanceBrokerRecoveryProof.ClosingChildMode, StringComparison.Ordinal))
            return await InstanceBrokerRecoveryProof.RunClosingChildAsync(args).ConfigureAwait(false);

        StartWatchdog(TimeSpan.FromMinutes(10));
        var tempRoot = Path.Combine(Path.GetTempPath(), $"Glideslope.Shell.Specs-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var paths = new TestPaths(tempRoot);
            Announce("settings");
            await SettingsDefaultsAndRoundTrip(paths);
            Announce("settings revision");
            await SettingsRevisionAndFutureSchema(tempRoot);
            Announce("settings fallback revision");
            await SettingsFallbackKeepsOnDiskRevision(tempRoot);
            Announce("settings provider ID migration");
            await SettingsProviderIdMigrationProof.RunAsync(tempRoot);
            Announce("installed launcher");
            InstalledLauncherPathContract(tempRoot);
            Announce("startup launcher fallback");
            StartupLauncherPathProof.Run(tempRoot);
            Announce("settings failure");
            SettingsSaveFailureIsTyped(tempRoot);
            Announce("corrupt");
            await CorruptSettingsArePreserved(paths);
            Announce("settings shape and card scale");
            await SettingsStoreShapeProof.RunAsync(tempRoot);
            Announce("startup");
            await StartupArtifactIsIsolated(paths);
            Announce("startup ownership");
            await StartupRegistrationContractProof.RunAsync();
            Announce("proof root");
            await ProofRootOverridesUserPaths(tempRoot);
            Announce("user data migration");
            UserDataMigrationProof.Run(tempRoot);
            Announce("intents");
            InstanceIntentAndClosePolicy();
            Announce("startup activation queue");
            await StartupActivationQueue();
            Announce("startup visibility");
            StartupVisibilitySpecs.Run();
            Announce("serialized save exit");
            await SerializedSaveExit();
            Announce("settings window revision");
            SettingsWindowRevisionProof.Run();
            Announce("history browse arithmetic");
            HistoryBrowseSpecs.Run();
            Announce("snap setting unsnaps");
            SnapSettingSpecs.Run();
            Announce("instance");
            await SingleInstanceOwnerForwardingAndTeardown(tempRoot);
            Announce("instance deadline and recovery");
            await InstanceDeadlineAndRecovery(tempRoot);
            Announce("unix socket path rejection cleanup");
            await UnixSocketPathRejectionReleasesLock(tempRoot);
            Announce("instance recovery");
            await InstanceBrokerRecoveryProof.RunAsync(tempRoot);
            Announce("linux tray monitor");
            LinuxTrayMonitorContract();
            Announce("linux tray protocol");
            LinuxTrayProtocolContractProof.Run();
            Announce("tray receiver");
            await NativeTrayReceiverProofAsync();
            Announce("windows tray native contract");
            WindowsTrayNativeContractProof.Run();
            Announce("localization resources");
            LocalizationResourceProof.Run();
            Announce("live card presentation");
            CardLayoutPlatformProof.Run();
            CardPresentationProof.Run();
            Announce("pending placements");
            PendingPlacementsSpecs.Run();
            Announce("card accessibility");
            CardAccessibilityProof.Run();
            Announce("layout gesture completion");
            LayoutGestureCompletionProof.Run();
            Announce("layout gesture tracker");
            LayoutGestureTrackerSpecs.Run();
            Announce("layout commit decision");
            LayoutCommitDecisionSpecs.Run();
            Announce("docked sides");
            DockedSidesSpecs.Run();
            Announce("bring to front order");
            BringToFrontOrderSpecs.Run();
            Announce("card mode applier order");
            CardModeApplierSpecs.Run();
            Announce("card peek");
            CardPeekSpecs.Run();
            Announce("file diagnostic sink");
            FileDiagnosticSinkProof.Run();
            Announce("localized satellite resource validation");
            LocalizationResourceProof.RunSatelliteResourceValidation();
            Console.WriteLine("Glideslope shell specs passed.");
            return 0;
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
                if (Directory.Exists(tempRoot)) throw new IOException("owned_test_temp_root_survived_cleanup");
            }
        }
    }

    private static void LinuxTrayMonitorContract()
    {
        Assert(
            LinuxTrayMonitor.TryParseOwnedRegistration(
                "org.kde.StatusNotifierItem-42-0/StatusNotifierItem",
                42,
                out var service,
                out var itemPath),
            "linux monitor should recognize the Avalonia item naming convention");
        Assert(service == "org.kde.StatusNotifierItem-42-0", "linux monitor should preserve the owned service name");
        Assert(itemPath == "/StatusNotifierItem", "linux monitor should use the Avalonia item path");
        Assert(
            !LinuxTrayMonitor.TryParseOwnedRegistration(
                "org.kde.StatusNotifierItem-41-0/StatusNotifierItem",
                42,
                out _,
                out _),
            "linux monitor should reject another process item");
        Assert(
            LinuxTrayMonitor.IsMenuVerified(
                "<interface name=\"com.canonical.dbusmenu\"><method name=\"GetLayout\"/></interface>"),
            "linux monitor should require the exported dbusmenu interface");
        Assert(!LinuxTrayMonitor.IsMenuVerified("<node><interface name=\"other\"/></node>"), "unrelated menu XML must stay unverified");

        var state = new TrayViabilityState();
        Assert(!state.Update(false), "fallback should remain quiet while viability is unproven");
        Assert(state.Update(true), "verified registration should transition to usable");
        Assert(!state.Update(true), "repeated verification should not duplicate a transition");
        Assert(state.Update(false), "watcher or item loss should transition back to visible fallback");
    }

    private static async Task SettingsDefaultsAndRoundTrip(TestPaths paths)
    {
        var store = new SettingsStore(paths);
        var loaded = await store.LoadAsync();
        Assert(loaded.WasCreated, "default settings should be created");
        Assert(loaded.Settings.LanguageChoice == "auto", "language choice defaults to automatic selection");
        Assert(loaded.Settings.EnabledSet.SetEquals(ProviderCatalog.DefaultEnabledProviderIds), "all providers should be enabled by default");
        var candidate = loaded.Settings.Clone(); candidate.EnabledProviderIds = [ProviderCatalog.Codex, ProviderCatalog.Gemini];
        candidate.LanguageChoice = "pt-BR";
        Assert((await store.SaveAsync(candidate)).Succeeded, "two-provider settings should save");
        var roundTrip = await store.LoadAsync();
        Assert(roundTrip.Settings.EnabledSet.SetEquals([ProviderCatalog.Codex, ProviderCatalog.Gemini]), "selection should round-trip");
        Assert(roundTrip.Settings.LanguageChoice == "pt-BR", "manual language choice round-trips through the revision-checked store");
        var invalidLanguage = roundTrip.Settings.Clone();
        invalidLanguage.LanguageChoice = "pt-PT";
        var invalidSave = await store.SaveAsync(invalidLanguage, roundTrip.Settings.Revision);
        Assert(!invalidSave.Succeeded && invalidSave.IssueCode == "invalid_language_choice",
            "a new unsupported language choice is rejected without commit");
        var oldInstallPaths = new TestPaths(Path.Combine(Path.GetDirectoryName(paths.Get().SettingsFile)!, "old-install"));
        Directory.CreateDirectory(Path.GetDirectoryName(oldInstallPaths.Get().SettingsFile)!);
        await File.WriteAllTextAsync(oldInstallPaths.Get().SettingsFile, "{\"schemaVersion\":2,\"revision\":0}");
        var oldInstall = await new SettingsStore(oldInstallPaths).LoadAsync();
        Assert(oldInstall.Settings.LanguageChoice == "auto", "a settings file missing the additive language field defaults to auto");
        var invalidDiskPaths = new TestPaths(Path.Combine(Path.GetDirectoryName(paths.Get().SettingsFile)!, "invalid-disk-language"));
        Directory.CreateDirectory(Path.GetDirectoryName(invalidDiskPaths.Get().SettingsFile)!);
        await File.WriteAllTextAsync(invalidDiskPaths.Get().SettingsFile,
            "{\"schemaVersion\":2,\"revision\":3,\"languageChoice\":\"xx-secret-value\"}");
        var languageDiagnostics = new RecordingDiagnosticSink();
        var normalizedDisk = await new SettingsStore(invalidDiskPaths, languageDiagnostics).LoadAsync();
        Assert(normalizedDisk.IssueCode is null && normalizedDisk.Settings.LanguageChoice == "auto",
            "an unsupported on-disk language normalizes without rejecting other settings");
        Assert(languageDiagnostics.Events.Any(item => item.Code == "settings_language_normalized" && item.Status == "invalid_choice_to_auto") &&
               languageDiagnostics.Events.All(item => item.Status?.Contains("secret", StringComparison.Ordinal) != true),
            "the on-disk language diagnostic is sanitized");
        candidate.EnabledProviderIds.Clear();
        Assert(!(await store.SaveAsync(candidate)).Succeeded, "empty selection should be rejected");
    }

    private static async Task CorruptSettingsArePreserved(TestPaths paths)
    {
        await File.WriteAllTextAsync(paths.Get().SettingsFile, "{ not valid json");
        var result = await new SettingsStore(paths).LoadAsync();
        Assert(result.IssueCode == "settings_corrupt_preserved", "corrupt settings should be classified and preserved");
        Assert(Directory.EnumerateFiles(Path.GetDirectoryName(paths.Get().SettingsFile)!, "settings.json.corrupt-*").Any(), "corrupt copy should exist");
    }

    private static async Task SettingsRevisionAndFutureSchema(string tempRoot)
    {
        var paths = new TestPaths(Path.Combine(tempRoot, "settings-review"));
        var store = new SettingsStore(paths);
        var loaded = await store.LoadAsync().ConfigureAwait(false);
        var candidate = loaded.Settings.Clone();
        candidate.EnabledProviderIds = [ProviderCatalog.Codex, ProviderCatalog.Claude];

        var first = store.SaveAsync(candidate, loaded.Settings.Revision);
        var second = store.SaveAsync(candidate, loaded.Settings.Revision);
        var results = await Task.WhenAll(first, second).ConfigureAwait(false);
        Assert(results.Count(result => result.Succeeded) == 1, "concurrent settings writes should commit one revision");
        Assert(results.Any(result => result.IssueCode == "settings_revision_conflict"), "stale settings write should be rejected");

        var futurePath = paths.Get().SettingsFile;
        const string futureDocument = "{\n  \"schemaVersion\": 99,\n  \"futureField\": \"preserve me\"\n}\n";
        await File.WriteAllTextAsync(futurePath, futureDocument).ConfigureAwait(false);
        var futureLoad = await store.LoadAsync().ConfigureAwait(false);
        Assert(futureLoad.IssueCode == "settings_newer_schema", "newer settings schema should be reported");
        var futureSave = await store.SaveAsync(AppSettings.CreateDefault()).ConfigureAwait(false);
        Assert(!futureSave.Succeeded && futureSave.IssueCode == "settings_newer_schema", "newer settings schema must block overwrite");
        Assert(await File.ReadAllTextAsync(futurePath).ConfigureAwait(false) == futureDocument, "newer settings file must remain byte-for-byte intact");

        // the startup quoting and IO-failure checks that followed here moved to
        // StartupRegistrationContractProof.RunQuotingAndIoFailureProofAsync, which runs them against both
        // the Linux .desktop rules and the Windows Run-value rules through explicit options, so no spec can
        // reach the real registry or Startup folder.
    }

    // A readable but invalid settings file retains its on-disk revision in the fallback, allowing
    // subsequent saves to pass revision checks.
    private static async Task SettingsFallbackKeepsOnDiskRevision(string tempRoot)
    {
        var paths = new TestPaths(Path.Combine(tempRoot, "settings-fallback-revision"));
        var settingsFile = paths.Get().SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        // Well-formed JSON at the current schema whose theme fails Validate.
        const string invalidDocument = "{\n  \"schemaVersion\": 2,\n  \"revision\": 7,\n  \"themeMode\": \"purple\"\n}\n";
        await File.WriteAllTextAsync(settingsFile, invalidDocument).ConfigureAwait(false);

        var diagnostics = new RecordingDiagnosticSink();
        var store = new SettingsStore(paths, diagnostics);
        var loaded = await store.LoadAsync().ConfigureAwait(false);
        Assert(loaded.IssueCode == "invalid_theme", "an invalid-but-readable settings file should report its validation issue");
        Assert(loaded.Settings.ThemeMode == "system", "the fallback should be the defaults");
        Assert(loaded.Settings.Revision == 7, "the fallback defaults should start at the file's revision, not 0");
        Assert(diagnostics.Events.Any(e => e.Code == "settings_fallback_revision" && e.Status == "adopted_7"),
            "the adopted fallback revision should be logged");

        var saved = await store.SaveAsync(loaded.Settings).ConfigureAwait(false);
        Assert(saved.Succeeded && saved.IssueCode is null, "saving after a fallback to defaults should succeed");
        Assert(saved.CommittedRevision == 8, "the save should commit the next revision after the file's");
        var reloaded = await store.LoadAsync().ConfigureAwait(false);
        Assert(reloaded.IssueCode is null && reloaded.Settings.Revision == 8 && reloaded.Settings.ThemeMode == "system",
            "the file on disk should now hold the valid defaults at revision 8");

        // The save replaced the user's file with defaults, so the file as
        // it was read must have been kept beside it first, and the copy's name logged.
        var preservedCopies = Directory.EnumerateFiles(Path.GetDirectoryName(settingsFile)!, "settings.json.invalid-*").ToList();
        Assert(preservedCopies.Count == 1, "the invalid settings file should be preserved exactly once");
        Assert(await File.ReadAllTextAsync(preservedCopies[0]).ConfigureAwait(false) == invalidDocument,
            "the preserved copy should hold the original content byte for byte");
        Assert(diagnostics.Events.Any(e => e.Code == "settings_invalid_preserved" && e.Status == Path.GetFileName(preservedCopies[0])),
            "the preservation should be logged as settings_invalid_preserved with the copy's file name");
    }

    private static void InstalledLauncherPathContract(string tempRoot)
    {
        var installDirectory = Path.Combine(tempRoot, "Program Files", "Glideslope");
        Directory.CreateDirectory(installDirectory);
        var executable = Path.Combine(installDirectory, OperatingSystem.IsWindows() ? "Glideslope.App.exe" : "Glideslope.App");
        File.WriteAllText(executable, "owned executable");
        if (OperatingSystem.IsWindows())
        {
            // the installed Windows launcher is the exe itself, with the installer's
            // marker beside it (glideslope-launcher.cmd was retired).
            File.WriteAllText(Path.Combine(installDirectory, PlatformStartupRegistration.WindowsInstalledMarkerFileName), "installed");
            Assert(InstalledLauncherPath.Resolve(executable) == executable, "the installed exe is its own startup launcher");
            return;
        }

        var launcher = Path.Combine(installDirectory, "glideslope-launcher.sh");
        File.WriteAllText(launcher, "owned launcher");
        Assert(InstalledLauncherPath.Resolve(executable) == launcher, "installed startup path should resolve beside the app executable");
    }

    private static void SettingsSaveFailureIsTyped(string tempRoot)
    {
        var blocked = Path.Combine(tempRoot, "settings-blocked");
        File.WriteAllText(blocked, "owned-test-file");
        var paths = new FixedPaths(new UserPaths(
            Path.Combine(blocked, "settings.json"),
            Path.Combine(tempRoot, "data-failure"),
            Path.Combine(tempRoot, "runtime-failure"),
            Path.Combine(tempRoot, "cache-failure"),
            Path.Combine(tempRoot, "logs-failure")));

        var result = new SettingsStore(paths).SaveAsync(AppSettings.CreateDefault()).GetAwaiter().GetResult();
        Assert(!result.Succeeded && result.IssueCode == "settings_save_io_error", "settings IO failure should remain typed");
        Assert(File.ReadAllText(blocked) == "owned-test-file", "settings failure should preserve the blocking file");
    }

    private static async Task StartupArtifactIsIsolated(TestPaths paths)
    {
        // explicit options for the host's rules (on Windows a file-backed Run value),
        // so this spec can never reach the real registry or Startup folder.
        var root = Path.Combine(paths.Get().RuntimeDirectory, "startup-isolated");
        var launcherPath = StartupRegistrationContractProof.CreateInstalledLauncher(Path.Combine(root, "owned launcher"), OperatingSystem.IsWindows());
        var startup = StartupRegistrationContractProof.HostFlavor(root);
        var artifact = startup.ResolveArtifactPath();
        var enabled = await startup.SetAsync(true, launcherPath);
        Assert(enabled.IsRegistered && File.Exists(artifact), "startup artifact should be created in the isolated root");
        Assert((await startup.ReadAsync()).IsRegistered, "startup read should observe the owned artifact");
        var content = await File.ReadAllTextAsync(artifact);
        Assert(content.Contains(OperatingSystem.IsWindows() ? "--autostart" : "TryExec=", StringComparison.Ordinal), "startup artifact should target the installed launcher");
        var disabled = await startup.SetAsync(false, launcherPath);
        Assert(!disabled.IsRegistered && !File.Exists(artifact), "startup artifact should be removed by its owner");
        Assert(!(await startup.ReadAsync()).IsRegistered, "startup read should observe removal");
    }

    private static async Task ProofRootOverridesUserPaths(string tempRoot)
    {
        var proofRoot = Path.Combine(tempRoot, "proof-root");
        var previous = Environment.GetEnvironmentVariable("GLIDESLOPE_PROOF_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("GLIDESLOPE_PROOF_ROOT", proofRoot);
            Assert(ProofRoot.Configure([ProofRoot.ArgumentName, proofRoot]).Succeeded, "absolute proof root should be accepted");
            Assert(!ProofRoot.Configure([ProofRoot.ArgumentName]).Succeeded, "missing proof root should be rejected");
            Assert(!ProofRoot.Configure([ProofRoot.ArgumentName, "relative-proof-root"]).Succeeded, "relative proof root should be rejected");
            var paths = new DefaultUserPathProvider().Get();
            Assert(paths.SettingsFile.StartsWith(proofRoot, StringComparison.Ordinal), "proof root should own settings");
            // The public constructor under a proof root: on Windows the Run value is a file there, never the
            // registry configuration is explicit so the spec can supply the platform rules.
            var startup = new PlatformStartupRegistration();
            var launcherPath = StartupRegistrationContractProof.CreateInstalledLauncher(Path.Combine(proofRoot, "launcher"), OperatingSystem.IsWindows());
            Assert((await startup.SetAsync(true, launcherPath)).IsRegistered, "proof startup should register");
            var expected = OperatingSystem.IsWindows()
                ? Path.Combine(proofRoot, "startup", "HKCU-Run", "Glideslope.txt")
                : Path.Combine(proofRoot, "startup", "Glideslope.desktop");
            Assert(startup.ResolveArtifactPath() == expected && File.Exists(expected), "proof startup should avoid user startup (Run key or autostart folder)");
            Assert(startup.ResolveFileLocations().All(location => location.StartsWith(proofRoot, StringComparison.Ordinal)),
                "every file the startup service may touch stays under the proof root");
            await startup.SetAsync(false, launcherPath);
            Assert(!File.Exists(expected), "proof startup should be removable");
        }
        finally { Environment.SetEnvironmentVariable("GLIDESLOPE_PROOF_ROOT", previous); }
    }

    private static void InstanceIntentAndClosePolicy()
    {
        Assert(ActivationIntentParser.Parse([]) == ActivationIntent.Manual, "manual launch should be the default intent");
        Assert(ActivationIntentParser.Parse(["--autostart"]) == ActivationIntent.Autostart, "autostart flag should be parsed");
        Assert(UserClosePolicy.Decide(2) == UserCloseDecision.DisableProvider, "closing a nonfinal card should disable that provider");
        Assert(UserClosePolicy.Decide(1) == UserCloseDecision.ExitKeepSelection, "closing the final card should exit and keep selection");
    }

    private static async Task StartupActivationQueue()
    {
        var router = new StartupActivationRouter();
        var received = new List<ActivationIntent>();
        var queued = router.DispatchAsync(ActivationIntent.Manual);
        Assert(!queued.IsCompleted, "activation received before coordinator startup should remain pending");

        router.Attach(intent =>
        {
            received.Add(intent);
            return Task.CompletedTask;
        });

        await queued.ConfigureAwait(false);
        await router.DispatchAsync(ActivationIntent.Autostart).ConfigureAwait(false);
        Assert(received.SequenceEqual([ActivationIntent.Manual, ActivationIntent.Autostart]), "startup activation queue should preserve early and direct intents");
    }

    private static async Task SerializedSaveExit()
    {
        var paths = new TestPaths(Path.Combine(Path.GetTempPath(), $"Glideslope.GateProof-{Guid.NewGuid():N}"));
        var store = new SettingsStore(paths);
        var loaded = await store.LoadAsync().ConfigureAwait(false);
        var candidate = loaded.Settings.Clone();
        candidate.EnabledProviderIds = [ProviderCatalog.Codex, ProviderCatalog.Claude];
        var gate = new CoordinatorIntentGate();
        var saveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var saveTask = SaveAfterControlledBlockAsync();
            await saveStarted.Task.ConfigureAwait(false);
            var exitTask = ReadAfterExitWaitsAsync();
            Assert(!exitTask.IsCompleted, "exit intent should wait behind a blocked settings save");
            releaseSave.SetResult(true);
            var save = await saveTask.ConfigureAwait(false);
            Assert(save.Succeeded && save.CommittedRevision == loaded.Settings.Revision + 1, "blocked settings save should commit the latest revision");
            var latest = await exitTask.ConfigureAwait(false);
            Assert(latest.Settings.Revision == save.CommittedRevision, "exit intent should observe the committed revision");
            await gate.DisposeAsync().ConfigureAwait(false);
            try
            {
                await gate.EnterAsync().ConfigureAwait(false);
                throw new InvalidOperationException("FAIL: post-dispose intent should be rejected");
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            releaseSave.TrySetResult(true);
            await gate.DisposeAsync().ConfigureAwait(false);
            var root = Path.GetFullPath(paths.Get().SettingsFile);
            var ownedRoot = Path.GetDirectoryName(root)!;
            if (Directory.Exists(ownedRoot))
                Directory.Delete(ownedRoot, recursive: true);
            if (Directory.Exists(ownedRoot))
                throw new IOException("owned_gate_proof_temp_root_survived_cleanup");
        }

        async Task<SettingsSaveResult> SaveAfterControlledBlockAsync()
        {
            await using var lease = await gate.EnterAsync().ConfigureAwait(false);
            saveStarted.SetResult(true);
            await releaseSave.Task.ConfigureAwait(false);
            return await store.SaveAsync(candidate, loaded.Settings.Revision).ConfigureAwait(false);
        }

        async Task<SettingsLoadResult> ReadAfterExitWaitsAsync()
        {
            await using var lease = await gate.EnterAsync().ConfigureAwait(false);
            return await store.LoadAsync().ConfigureAwait(false);
        }
    }

    private static async Task SingleInstanceOwnerForwardingAndTeardown(string tempRoot)
    {
        var identity = $"spec-{Guid.NewGuid():N}";
        var paths = new TestPaths(tempRoot);
        var callbacks = new List<ActivationIntent>();
        Console.WriteLine("instance owner create");
        var owner = new InstanceBroker(paths, identity: identity);
        Console.WriteLine("instance owner acquire");
        var ownerResult = await owner.AcquireAsync(ActivationIntent.Manual, intent =>
        {
            callbacks.Add(intent);
            return Task.CompletedTask;
        });
        Assert(ownerResult.Role == InstanceRole.Owner, "first instance should own the broker");
        await RunInstanceChildProcessAsync(tempRoot, identity, ActivationIntent.Manual);
        await RunInstanceChildProcessAsync(tempRoot, identity, ActivationIntent.Autostart);
        Assert(callbacks.SequenceEqual([ActivationIntent.Manual, ActivationIntent.Autostart]), "second launches should forward manual and autostart intents");
        await owner.DisposeAsync();

        var reacquired = new InstanceBroker(paths, identity: identity);
        var reacquiredResult = await reacquired.AcquireAsync(ActivationIntent.Manual, _ => Task.CompletedTask);
        Assert(reacquiredResult.Role == InstanceRole.Owner, "owner should be reacquirable after disposal");
        await reacquired.DisposeAsync();
    }

    private static async Task InstanceDeadlineAndRecovery(string tempRoot)
    {
        var identity = $"deadline-{Guid.NewGuid():N}";
        var paths = new TestPaths(tempRoot);
        // Wait for the diagnostic that confirms the malformed client was rejected and the listener is ready
        // for the next connection; the client closes its pipe or socket to produce end-of-stream.
        var listenerFreedSink = new SignalDiagnosticSink(e =>
            (e.Code == "instance_pipe_error" || e.Code == "instance_socket_error") &&
            (e.Status == "activation_payload_rejected" || e.Status == "activation_deadline_expired"));
        var owner = new InstanceBroker(paths, listenerFreedSink, identity: identity);
        var ownerResult = await owner.AcquireAsync(ActivationIntent.Manual, _ => Task.CompletedTask);
        Assert(ownerResult.Role == InstanceRole.Owner, "deadline test should acquire the owner");

        using (var hungClient = StartInstanceModeProcess("--instance-hung-client", tempRoot, identity))
        {
            await WaitForReadyAsync(hungClient).ConfigureAwait(false);
            var rejected = await listenerFreedSink.Signal.ConfigureAwait(false);
            Console.WriteLine($"instance owner listener freed after hung client: code={rejected.Code} status={rejected.Status}");
            await RunInstanceChildProcessAsync(tempRoot, identity, ActivationIntent.Manual).ConfigureAwait(false);
            await StopOwnedProcessAsync(hungClient).ConfigureAwait(false);
        }

        await owner.DisposeAsync().ConfigureAwait(false);

        using var abandonedOwner = StartInstanceModeProcess("--instance-owner-child", tempRoot, identity);
        await WaitForReadyAsync(abandonedOwner).ConfigureAwait(false);
        await StopOwnedProcessAsync(abandonedOwner).ConfigureAwait(false);

        var recovered = new InstanceBroker(paths, identity: identity);
        var recoveredResult = await recovered.AcquireAsync(ActivationIntent.Manual, _ => Task.CompletedTask).ConfigureAwait(false);
        Assert(recoveredResult.Role == InstanceRole.Owner, "abandoned owner should be recoverable");
        await recovered.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task UnixSocketPathRejectionReleasesLock(string tempRoot)
    {
        if (OperatingSystem.IsWindows()) return;

        var identity = $"long-runtime-{Guid.NewGuid():N}";
        var runtimeDirectory = Path.Combine(tempRoot, "socket-path-limit", new string('r', 80));
        var paths = new FixedPaths(new UserPaths(
            Path.Combine(tempRoot, "socket-path-limit", "settings.json"),
            Path.Combine(tempRoot, "socket-path-limit", "data"),
            runtimeDirectory,
            Path.Combine(tempRoot, "socket-path-limit", "cache"),
            Path.Combine(tempRoot, "socket-path-limit", "logs")));
        var instanceName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 8));
        var lockPath = Path.Combine(runtimeDirectory, $"instance-{instanceName}.lock");
        var socketPath = Path.Combine(runtimeDirectory, $"instance-{instanceName}.sock");
        Assert(Encoding.UTF8.GetByteCount(socketPath) > 108, "unix socket path rejection fixture should exceed sockaddr_un capacity");

        await using (var broker = new InstanceBroker(paths, identity: identity))
        {
            var result = await broker.AcquireAsync(ActivationIntent.Manual, _ => Task.CompletedTask).ConfigureAwait(false);
            Assert(result.Role == InstanceRole.ActivationFailed, "overlong unix socket path should return typed acquisition failure");
            Assert(result.IssueCode == "socket_path_too_long", "overlong unix socket path should return a safe stable issue code");
        }

        Assert(!File.Exists(socketPath), "rejected unix endpoint must not leave a socket behind");
        using var lockProbe = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task<int> RunInstanceOwnerWorkerAsync(string[] args)
    {
        Assert(args.Length == 3, "owner child arguments should be bounded");
        var paths = new TestPaths(args[1]);
        var broker = new InstanceBroker(paths, identity: args[2]);
        try
        {
            var result = await broker.AcquireAsync(ActivationIntent.Manual, _ => Task.CompletedTask).ConfigureAwait(false);
            Assert(result.Role == InstanceRole.Owner, "owner child should acquire the broker");
            Console.WriteLine("ready");
            await Console.Out.FlushAsync().ConfigureAwait(false);
            // The parent terminates this helper after the ready signal; it does not need to wake on its own.
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await broker.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> RunHungClientWorkerAsync(string[] args)
    {
        Assert(args.Length == 3, "hung client arguments should be bounded");
        var identity = args[2];
        if (OperatingSystem.IsWindows())
        {
            // The pipe name is scoped to the current user and session.
            using var client = new NamedPipeClientStream(".", InstanceBroker.WindowsPipeNameFor(identity), PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000).ConfigureAwait(false);
            await client.WriteAsync(Encoding.UTF8.GetBytes("Manual")).ConfigureAwait(false);
            await client.FlushAsync().ConfigureAwait(false);
        }
        else
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(new TestPaths(args[1]).Get().RuntimeDirectory, InstanceBroker.UnixSocketFileNameFor(identity)))).ConfigureAwait(false);
            await using var stream = new NetworkStream(client, ownsSocket: false);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("Manual")).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        Console.WriteLine("ready");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        // same idiom as the owner-child worker above -- this process only
        // needs to stay alive (with the connection open but no line terminator sent) until the parent
        // spec kills it, so it waits to be canceled rather than for a fixed 5-minute delay.
        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    private static async Task WaitForReadyAsync(Process process)
    {
        try
        {
            // The child's ready line signals that the helper has initialized.
            var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
            Console.WriteLine($"child process reported readiness line: {line ?? "(none, stream ended)"}");
            Assert(line == "ready", "child process should report readiness");
        }
        catch
        {
            if (!process.HasExited)
            {
                await StopOwnedProcessAsync(process).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            var processId = process.Id;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            Assert(process.Id == processId, "owned child process identity changed during teardown");
        }
    }

    private static Process StartInstanceModeProcess(string mode, string tempRoot, string identity)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("spec_process_path_missing");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(tempRoot);
        startInfo.ArgumentList.Add(identity);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("spec_child_start_failed");
    }

    private static async Task<int> RunInstanceChildWorkerAsync(string[] args)
    {
        Assert(args.Length == 4, "instance child arguments should be bounded");
        var tempRoot = args[1];
        var identity = args[2];
        Assert(Enum.TryParse<ActivationIntent>(args[3], ignoreCase: true, out var intent), "instance child intent should parse");
        var paths = new TestPaths(tempRoot);
        var broker = new InstanceBroker(paths, identity: identity);
        try
        {
            var result = await broker.AcquireAsync(intent, _ => Task.CompletedTask).ConfigureAwait(false);
            Assert(result.Role == InstanceRole.Forwarded, "second instance should receive an acknowledgement");
            return 0;
        }
        finally
        {
            await broker.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunInstanceChildProcessAsync(string tempRoot, string identity, ActivationIntent intent)
    {
        using var process = StartInstanceChildProcess(tempRoot, identity, intent);
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            Console.WriteLine($"forwarding child process exited with code {process.ExitCode}");
            Assert(process.ExitCode == 0, $"forwarding child exited with {process.ExitCode}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }

    private static Process StartInstanceChildProcess(string tempRoot, string identity, ActivationIntent intent)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("spec_process_path_missing");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        startInfo.ArgumentList.Add("--instance-child");
        startInfo.ArgumentList.Add(tempRoot);
        startInfo.ArgumentList.Add(identity);
        startInfo.ArgumentList.Add(intent.ToString());
        return Process.Start(startInfo) ?? throw new InvalidOperationException("spec_child_start_failed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    private static async Task NativeTrayReceiverProofAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // a recording sink, so the proof can see the re-registration decisions.
        var sink = new RecordingDiagnosticSink();
        var tray = TrayServiceFactory.Create(sink);
        try
        {
            Assert(tray is WindowsTrayService, "windows should use the native tray service");
            WindowsTrayReceiverProof.Run((WindowsTrayService)tray, () => sink.Events);
        }
        finally
        {
            await tray.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class TestPaths(string root) : IUserPathProvider
    {
        private readonly UserPaths _paths = new(Path.Combine(root, "config", "Glideslope", "settings.json"), Path.Combine(root, "data"), Path.Combine(root, "runtime"), Path.Combine(root, "cache"), Path.Combine(root, "logs"));
        public UserPaths Get() => _paths;
    }

    private sealed class FixedPaths(UserPaths paths) : IUserPathProvider
    {
        public UserPaths Get() => paths;
    }

    /// <summary>Signals when a diagnostic matching <paramref name="predicate"/> is recorded.</summary>
    private sealed class SignalDiagnosticSink(Func<DiagnosticEvent, bool> predicate) : IDiagnosticSink
    {
        private readonly TaskCompletionSource<DiagnosticEvent> _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DiagnosticEvent> Signal => _signal.Task;

        public void Record(DiagnosticEvent diagnostic)
        {
            if (predicate(diagnostic))
                _signal.TrySetResult(diagnostic);
        }
    }

    /// <summary>Captures SettingsStore decisions for the fallback-revision spec.</summary>
    private sealed class RecordingDiagnosticSink : IDiagnosticSink
    {
        public List<DiagnosticEvent> Events { get; } = [];
        public void Record(DiagnosticEvent diagnostic) => Events.Add(diagnostic);
    }
}

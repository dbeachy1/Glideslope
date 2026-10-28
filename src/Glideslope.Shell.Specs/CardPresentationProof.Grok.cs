using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.Shell.Specs;

internal static partial class CardPresentationProof
{
    private static void AssertGrokPresentation()
    {
        var settings = new SettingsWindow(AppSettings.CreateDefault(), new StartupRegistrationState(false),
            (_, _, _) => Task.FromResult(new SettingsSaveResult(true)));
        try
        {
            Assert(settings.ProviderCheckbox(ProviderCatalog.Grok).IsChecked == true,
                "Grok is checked in Settings for a new installation");
            Assert(settings.ProviderCheckbox(ProviderCatalog.Grok).Content as string == "Grok",
                "Settings labels the provider with its localized Grok brand");
        }
        finally { settings.Close(); }

        var now = DateTimeOffset.UtcNow;
        var card = new ProviderUsageCardWindow(ProviderIds.Grok, showMark: false, _ => { });
        try
        {
            ProviderDisplayState Failed(ProviderStatus status, string code) => new(
                ProviderIds.Grok, 1, true, false, status, null, SnapshotFreshness.Fresh,
                null, [], code, TimeSpan.Zero, now);

            card.UpdateState(Failed(ProviderStatus.NeedsSignIn, "grok_signed_out"));
            card.Show();
            Assert(card.NoticeVisible && card.NoticeText == "SIGN IN REQUIRED Run grok login to sign in.",
                $"Grok's sign-in notice names the documented command (got '{card.NoticeText}')");
            card.UpdateState(Failed(ProviderStatus.MissingApplication, "grok_cli_missing"));
            Assert(card.NoticeText == "CLI NOT FOUND Install the official Grok CLI.",
                $"Grok's missing CLI notice uses the official installation guidance (got '{card.NoticeText}')");
            var snapshot = Snapshot(now, [], providerId: ProviderIds.Grok);
            card.UpdateState(State(snapshot));
            Assert(!card.NoticeVisible && snapshot.Buckets.Single().Role == QuotaBucketRole.Weekly,
                "a ready Grok snapshot uses the existing weekly card presentation");
        }
        finally { card.CloseProgrammatically(); }
    }

    public static void CaptureLiveGrokScreenshots(string outputDirectory)
    {
        AppBuilder.Configure<PresentationTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        using var english = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-US"));
        Directory.CreateDirectory(outputDirectory);
        var outputRoot = Path.GetFullPath(outputDirectory);
        var version = typeof(ProviderUsageCardWindow).Assembly.GetName().Version!.ToString(3);
        var expectedFiles = new List<string> { "grok-settings-dark.png", "grok-settings-light.png" };
        foreach (var theme in new[] { "dark", "light" })
        {
            expectedFiles.Add($"grok-notice-sign-in-{theme}.png");
            expectedFiles.Add($"grok-notice-cli-missing-{theme}.png");
            foreach (var percent in new[] { 70, 100, 150 })
            {
                expectedFiles.Add($"grok-card-full-{theme}-{percent}-v{version}.png");
                expectedFiles.Add($"grok-card-mini-{theme}-{percent}-v{version}.png");
            }
        }
        if (expectedFiles.Any(name => File.Exists(Path.Combine(outputRoot, name))))
            throw new IOException("grok_visual_proof_output_already_contains_expected_images");

        var sessionRoot = Path.Combine(outputRoot, $".grok-proof-session-{Guid.NewGuid():N}");
        var paths = new UserPaths(
            Path.Combine(sessionRoot, "config", "settings.json"),
            Path.Combine(sessionRoot, "data"),
            Path.Combine(sessionRoot, "runtime"),
            Path.Combine(sessionRoot, "cache"),
            Path.Combine(sessionRoot, "logs"));
        ProviderMonitoringSession? session = null;
        try
        {
            var settings = AppSettings.CreateDefault();
            settings.EnabledProviderIds = [ProviderCatalog.Grok];
            session = ProviderMonitoringSession.CreateAsync(paths, settings, new NullDiagnosticSink())
                .GetAwaiter().GetResult();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            session.StartAsync(settings, deadline.Token).GetAwaiter().GetResult();
            ProviderDisplayState? state;
            while (true)
            {
                state = session.Scheduler.GetStates().GetValueOrDefault(ProviderIds.Grok);
                if (state is { IsReading: false, Status: not null }) break;
                Thread.Sleep(100);
                deadline.Token.ThrowIfCancellationRequested();
            }

            if (state.Status != ProviderStatus.Ready || state.Snapshot is not { } snapshot || state.Freshness != SnapshotFreshness.Fresh)
                throw new InvalidDataException($"grok_live_snapshot_not_ready:{state.Status}:{state.SafeErrorCode ?? "none"}");
            var weekly = snapshot.Buckets.SingleOrDefault(bucket => bucket.Role == QuotaBucketRole.Weekly)
                ?? throw new InvalidDataException("grok_live_weekly_bucket_missing");
            if (snapshot.ProviderId != ProviderIds.Grok || !double.IsFinite(weekly.RemainingFraction))
                throw new InvalidDataException("grok_live_snapshot_contract_invalid");

            var history = session.History ?? throw new IOException("grok_proof_history_unavailable");
            var identity = UsageWindowIdentityFactory.From(snapshot, weekly)
                ?? throw new InvalidDataException("grok_proof_weekly_identity_missing");
            history.FlushAsync(deadline.Token).GetAwaiter().GetResult();
            var stored = history.QueryWindowAsync(identity, identity.NominalStartUtc,
                DateTimeOffset.UtcNow.AddMinutes(1), maxSamples: 10, cancellationToken: deadline.Token)
                .GetAwaiter().GetResult();
            if (!stored.IsAvailable || stored.Samples.IsDefaultOrEmpty ||
                stored.Samples.Any(sample => sample.Window.ProviderId != ProviderIds.Grok ||
                                             sample.Window.BucketId != weekly.Id ||
                                             !double.IsFinite(sample.RemainingFraction)))
                throw new InvalidDataException($"grok_proof_history_observation_unavailable:{stored.SafeErrorCode ?? "none"}");

            Console.WriteLine($"Grok live status: Ready; weekly remaining {weekly.RemainingFraction:P0}; reset {weekly.ResetAtUtc:O}; source {weekly.SourceSemantics}; isolated history samples {stored.Samples.Length}.");
            var liveState = state;
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                var themeName = theme == ThemeVariant.Dark ? "dark" : "light";
                foreach (var percent in new[] { 70, 100, 150 })
                {
                    CaptureLiveCard(liveState, theme, CardMode.Full, percent,
                        Path.Combine(outputRoot, $"grok-card-full-{themeName}-{percent}-v{version}.png"));
                    CaptureLiveCard(liveState, theme, CardMode.Mini, percent,
                        Path.Combine(outputRoot, $"grok-card-mini-{themeName}-{percent}-v{version}.png"));
                }
                CaptureNotice(ProviderStatus.NeedsSignIn, "grok_signed_out", theme,
                    Path.Combine(outputRoot, $"grok-notice-sign-in-{themeName}.png"));
                CaptureNotice(ProviderStatus.MissingApplication, "grok_cli_missing", theme,
                    Path.Combine(outputRoot, $"grok-notice-cli-missing-{themeName}.png"));

                var settingsWindow = new SettingsWindow(AppSettings.CreateDefault(), new StartupRegistrationState(false),
                    (_, _, _) => Task.FromResult(new SettingsSaveResult(true)))
                {
                    RequestedThemeVariant = theme
                };
                try
                {
                    if (settingsWindow.ProviderCheckbox(ProviderCatalog.Grok).IsChecked != true)
                        throw new InvalidOperationException("grok_visual_settings_default_unchecked");
                    settingsWindow.Show();
                    settingsWindow.FitToWorkingArea(new PixelRect(0, 0, 1920, 1400), 1.0);
                    SaveScreenshot(settingsWindow, Path.Combine(outputRoot, $"grok-settings-{themeName}.png"));
                }
                finally { settingsWindow.Close(); }
            }
            Console.WriteLine($"Captured production Grok controls in {outputRoot}");
        }
        finally
        {
            if (session is not null) session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var canonicalOutput = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var canonicalSession = Path.GetFullPath(sessionRoot);
            if (!canonicalSession.StartsWith(canonicalOutput, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(canonicalSession).StartsWith(".grok-proof-session-", StringComparison.Ordinal))
                throw new InvalidOperationException("grok_visual_proof_session_root_invalid");
            if (Directory.Exists(canonicalSession))
            {
                Directory.Delete(canonicalSession, recursive: true);
                if (Directory.Exists(canonicalSession)) throw new IOException("grok_visual_proof_session_cleanup_failed");
            }
        }
    }

    public static void CaptureGrokRestartSettingsScreenshots(string outputDirectory)
    {
        AppBuilder.Configure<PresentationTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        using var english = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-US"));
        Directory.CreateDirectory(outputDirectory);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var themeName = theme == ThemeVariant.Dark ? "dark" : "light";
            var path = Path.Combine(outputDirectory, $"grok-settings-restart-{themeName}.png");
            if (File.Exists(path)) throw new IOException("grok_restart_settings_image_already_exists");
            var settings = AppSettings.CreateDefault();
            settings.LanguageChoice = "es";
            var window = new SettingsWindow(settings, new StartupRegistrationState(false),
                (_, _, _) => Task.FromResult(new SettingsSaveResult(true)),
                restart: () => Task.CompletedTask, activeLanguageChoice: "auto")
            {
                RequestedThemeVariant = theme
            };
            try
            {
                window.Show();
                window.FitToWorkingArea(new PixelRect(0, 0, 1920, 1040), 1.0);
                using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException("grok_restart_settings_layout_failed")) { }
                window.UpdateLayout();

                var client = new Rect(0, 0, window.ClientSize.Width, window.ClientSize.Height);
                var restart = BoundsInWindow(window.RestartControl, window);
                var messageArea = BoundsInWindow(window.MessageArea, window);
                var savedLanguageMessage = window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(block => block.Text == LocalizedText.SettingsLanguageSavedRestart);
                var status = BoundsInWindow(savedLanguageMessage, window);
                Assert(window.RestartControl.IsVisible && client.Contains(restart),
                    $"{themeName}: Restart now is visible inside the Settings client ({restart}, client {client})");
                Assert(client.Contains(messageArea) && client.Contains(status),
                    $"{themeName}: the saved-language message and footer fit inside Settings ({messageArea}, status {status}, client {client})");

                var scroll = window.ScrollControl;
                var maximumScroll = scroll.Extent.Height - scroll.Viewport.Height;
                Assert(scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && scroll.Viewport.Height > 0 && maximumScroll > 0.5,
                    $"{themeName}: Settings rows overflow into an enabled scroll viewport (extent {scroll.Extent.Height:0.0}, viewport {scroll.Viewport.Height:0.0})");
                scroll.Offset = new Vector(scroll.Offset.X, maximumScroll);
                window.UpdateLayout();
                Assert(scroll.Offset.Y > 0.5, $"{themeName}: Settings rows can scroll within the viewport (offset {scroll.Offset.Y:0.0})");
                scroll.Offset = new Vector(scroll.Offset.X, 0);
                window.UpdateLayout();

                var viewport = BoundsInWindow(scroll, window);
                foreach (var providerId in ProviderCatalog.DefaultEnabledProviderIds)
                {
                    var checkbox = window.ProviderCheckbox(providerId);
                    var bounds = BoundsInWindow(checkbox, window);
                    Assert(checkbox.IsChecked == true && viewport.Contains(bounds),
                        $"{themeName}: default provider row '{providerId}' is checked and visible ({bounds}, viewport {viewport})");
                }

                SaveScreenshot(window, path);
            }
            finally { window.Close(); }
        }
    }

    private static Rect BoundsInWindow(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException($"grok_restart_settings_control_not_in_window:{control.GetType().Name}");
        return new Rect(origin, control.Bounds.Size);
    }

    private static void CaptureLiveCard(ProviderDisplayState state, ThemeVariant theme, CardMode mode, int percent, string path)
    {
        var scale = percent / 100.0;
        var window = new ProviderUsageCardWindow(ProviderIds.Grok, showMark: true, _ => { })
        {
            RequestedThemeVariant = theme,
            MinWidth = (mode == CardMode.Full ? CardLayoutTiers.MinWidth : CardLayoutTiers.MiniMinWidth) * scale,
            MinHeight = (mode == CardMode.Full ? CardLayoutTiers.MinHeight : CardLayoutTiers.MiniMinHeight) * scale
        };
        try
        {
            window.UpdateState(state);
            window.ApplyMode(mode);
            window.ApplyCardScale(percent);
            window.Width = (mode == CardMode.Full ? ProviderUsageCardWindow.DefaultCardWidth : CardLayoutTiers.MiniDefaultWidth) * scale;
            window.Height = (mode == CardMode.Full ? ProviderUsageCardWindow.DefaultCardHeight : CardLayoutTiers.MiniDefaultHeight) * scale;
            window.Show();
            SaveScreenshot(window, path);
        }
        finally { window.CloseProgrammatically(); }
    }

    private static void CaptureNotice(ProviderStatus status, string code, ThemeVariant theme, string path)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new ProviderDisplayState(ProviderIds.Grok, 1, true, false, status, null,
            SnapshotFreshness.Fresh, null, [], code, TimeSpan.Zero, now);
        var window = new ProviderUsageCardWindow(ProviderIds.Grok, showMark: true, _ => { })
        {
            RequestedThemeVariant = theme
        };
        try
        {
            window.UpdateState(state);
            window.Show();
            SaveScreenshot(window, path);
        }
        finally { window.CloseProgrammatically(); }
    }

    private static void SaveScreenshot(Window window, string path)
    {
        using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException("grok_visual_screenshot_render_failed")) { }
        var scale = window.RenderScaling;
        using var screenshot = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(window.ClientSize.Width * scale), (int)Math.Ceiling(window.ClientSize.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        screenshot.Render(window);
        screenshot.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"Captured {Path.GetFileName(path)}");
    }
}

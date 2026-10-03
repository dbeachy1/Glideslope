using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Glideslope.Core;

namespace Glideslope.App;

internal sealed class GlideslopeApplication : Application
{
    private readonly AppLaunchContext _launchContext;
    private WindowCoordinator? _coordinator;

    public GlideslopeApplication(AppLaunchContext launchContext)
    {
        _launchContext = launchContext;
    }

    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var diagnostics = _launchContext.Diagnostics;
        var settings = new SettingsStore(_launchContext.Paths, diagnostics);
        // Reconciliation may migrate a legacy Startup-folder command only when it launches this executable.
        var startup = new PlatformStartupRegistration(diagnostics, _launchContext.SelfLauncherPath);
        var tray = TrayServiceFactory.Create(diagnostics);
        _coordinator = new WindowCoordinator(
            desktop,
            settings,
            startup,
            tray,
            _launchContext.Broker,
            _launchContext.LauncherPath,
            _launchContext.InstalledLauncherPath,
            diagnostics,
            appSettings => ProviderMonitoringSession.CreateAsync(_launchContext.Paths.Get(), appSettings, diagnostics),
            logFilePath: AppLogFile.PathFor(_launchContext.Paths.Get()),
            applyLocale: appSettings =>
            {
                var locale = LocaleResolver.Resolve(appSettings.LanguageChoice, OperatingSystem.IsLinux(),
                    _launchContext.LocaleEnvironment, _launchContext.CapturedUiCulture, _launchContext.CapturedFormatCulture);
                LocalizedText.SetCultures(locale.UiCulture, locale.FormatCulture);
            });
        _launchContext.ActivationRouter.Attach(_coordinator.HandleActivationAsync);
        base.OnFrameworkInitializationCompleted();
        _ = StartCoordinatorAsync(_coordinator, _launchContext.Intent, desktop, diagnostics);
    }

    private static async Task StartCoordinatorAsync(
        WindowCoordinator coordinator,
        ActivationIntent intent,
        IClassicDesktopStyleApplicationLifetime desktop,
        IDiagnosticSink diagnostics)
    {
        try
        {
            await coordinator.StartAsync(intent).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Include the exception type to distinguish startup failures in diagnostics.
            diagnostics.Record(new DiagnosticEvent("shell_startup_failed", Status: $"startup_aborted,type={ex.GetType().Name}"));
            desktop.Shutdown(1);
        }
    }
}

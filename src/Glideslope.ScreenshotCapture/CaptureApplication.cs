using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;

namespace Glideslope.ScreenshotCapture;

internal sealed class CaptureApplication(CaptureOptions options) : Application
{
    public const int NoHistoryDatabase = 10;
    public const int MissingProviderHistory = 11;
    public const int HistoryReadFailed = 12;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private IReadOnlyDictionary<string, CaptureProviderData> _providers =
        new Dictionary<string, CaptureProviderData>(StringComparer.Ordinal);

    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
            ?? throw new InvalidOperationException("Classic desktop lifetime was not initialized.");
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnFrameworkInitializationCompleted();
        Dispatcher.UIThread.Post(() => _ = CaptureAllAsync());
    }

    private async Task CaptureAllAsync()
    {
        var captures = new[]
        {
            new CaptureSpec("Codex", ProviderIds.Codex, CardMode.Full, ThemeVariant.Dark, 940, 680),
            new CaptureSpec("Claude", ProviderIds.Claude, CardMode.Mini, ThemeVariant.Dark, 530, 270),
            new CaptureSpec("Claude", ProviderIds.Claude, CardMode.Full, ThemeVariant.Light, 940, 680),
            new CaptureSpec("Gemini", ProviderIds.Gemini, CardMode.Full, ThemeVariant.Dark, 940, 680),
        };
        try
        {
            HiddenDesktopCaptureSession.VerifyCaptureDesktop();
            _providers = options.UseLiveReads
                ? await new LiveUsageReader().ReadAllAsync(options.UseRecordedHistory, options.SessionRoot)
                : await Task.Run(() => new RecordedUsageReader().ReadAll());
            foreach (var capture in captures)
                await CaptureOneAsync(capture);
            Console.WriteLine(options.UseLiveReads
                ? "Captured four production cards from fresh provider reads."
                : "Captured four production cards from recorded history; historical pace is intentionally unavailable.");
            _desktop!.Shutdown(0);
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine("Capture data is unavailable: no usage-history database exists.");
            _desktop!.Shutdown(NoHistoryDatabase);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Capture data is unavailable: {ex.Message}");
            _desktop!.Shutdown(MissingProviderHistory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Capture failed ({ex.GetType().Name}).");
            _desktop!.Shutdown(1);
        }
    }

    private async Task CaptureOneAsync(CaptureSpec spec)
    {
        var data = _providers.GetValueOrDefault(spec.ProviderId);
        var state = data?.State;
        if (state?.Status != ProviderStatus.Ready || state.Snapshot is null)
            throw new InvalidDataException($"No ready usage snapshot is available for {spec.Label}.");

        var window = new ProviderUsageCardWindow(spec.ProviderId, showMark: true, _ => { });
        window.RequestedThemeVariant = spec.Theme;
        CardModeApplier.Apply([(window, spec.Mode, 100)]);
        window.Width = spec.Width;
        window.Height = spec.Height;
        window.UpdateState(state);
        if (data?.WeeklyWindow is { } identity)
        {
            window.SetHistory(identity, data.WeeklySamples, data.SamplingInterval,
                historyAvailable: true, windowCount: 1);
        }

        _desktop!.MainWindow = window;
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        var clientSize = window.ClientSize;
        if (clientSize.Width + 0.5 < spec.Width || clientSize.Height + 0.5 < spec.Height)
            throw new InvalidOperationException($"The {spec.Label} {spec.Mode} client area is smaller than requested.");
        var renderScale = window.RenderScaling;
        var pixelSize = new PixelSize((int)Math.Ceiling(clientSize.Width * renderScale),
            (int)Math.Ceiling(clientSize.Height * renderScale));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * renderScale, 96 * renderScale));
        bitmap.Render(window);
        var theme = spec.Theme == ThemeVariant.Light ? "Light" : "Dark";
        var fileName = $"{spec.Label}-{spec.Mode}-{theme}-v{options.AppVersion}.png";
        await using (var stream = new FileStream(Path.Combine(options.OutputDirectory, fileName), FileMode.Create, FileAccess.Write, FileShare.None))
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);

        window.CloseProgrammatically();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private sealed record CaptureSpec(string Label, string ProviderId, CardMode Mode, ThemeVariant Theme, int Width, int Height);
}

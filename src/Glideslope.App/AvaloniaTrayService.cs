using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Glideslope.Core;

namespace Glideslope.App;

internal interface ITrayService : IAsyncDisposable
{
    bool IsUsable { get; }
    event EventHandler? ShowRequested;
    event EventHandler? ExitRequested;
    event EventHandler<TrayViabilityChangedEventArgs>? ViabilityChanged;
    void RefreshLocalizedText();
    void SetVisible(bool visible);
}

internal sealed class AvaloniaTrayService : ITrayService
{
    private readonly IDiagnosticSink _diagnostics;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _showItem;
    private readonly NativeMenuItem _exitItem;
    private readonly LinuxTrayMonitor? _linuxMonitor;
    private bool _visible;
    private bool _isUsable;

    public AvaloniaTrayService(IDiagnosticSink diagnostics)
    {
        _diagnostics = diagnostics;
        var menu = new NativeMenu();
        _showItem = new NativeMenuItem(LocalizedText.TrayShow);
        _showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _exitItem = new NativeMenuItem(LocalizedText.TrayExit);
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_showItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_exitItem);
        WindowIcon? icon = null;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "glideslope-icon.png");
        if (File.Exists(iconPath))
        {
            using var stream = File.OpenRead(iconPath);
            icon = new WindowIcon(stream);
        }
        _icon = new TrayIcon { ToolTipText = LocalizedText.TrayToolTip, Menu = menu, Icon = icon, IsVisible = false };
        var icons = TrayIcon.GetIcons(Application.Current!) ?? new TrayIcons();
        icons.Add(_icon);
        TrayIcon.SetIcons(Application.Current!, icons);
        // Avalonia owns the icon and menu. The monitor only observes that ownership through
        // the session bus, so there is no second icon competing with the real tray item.
        if (OperatingSystem.IsLinux())
        {
            _linuxMonitor = new LinuxTrayMonitor(diagnostics, OnViabilityChanged);
            _linuxMonitor.Start();
        }

        // Registration is asynchronous and remains unproven until the watcher reports our
        // item and its exported menu. Keep the visible-card fallback active in the meantime.
        _diagnostics.Record(new DiagnosticEvent("tray_viability_unproven", Status: "visible_fallback"));
    }

    public bool IsUsable => _isUsable;
    public event EventHandler? ShowRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<TrayViabilityChangedEventArgs>? ViabilityChanged;

    public void RefreshLocalizedText()
    {
        _showItem.Header = LocalizedText.TrayShow;
        _exitItem.Header = LocalizedText.TrayExit;
        _icon.ToolTipText = LocalizedText.TrayToolTip;
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        _icon.IsVisible = visible;
    }

    public ValueTask DisposeAsync()
    {
        _icon.IsVisible = false;
        var icons = TrayIcon.GetIcons(Application.Current!);
        icons?.Remove(_icon);
        return DisposeMonitorAsync();
    }

    private async ValueTask DisposeMonitorAsync()
    {
        if (_linuxMonitor is not null)
        {
            await _linuxMonitor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnViabilityChanged(bool isUsable, string reason)
    {
        _isUsable = isUsable;
        ViabilityChanged?.Invoke(this, new TrayViabilityChangedEventArgs(isUsable, reason));
    }
}

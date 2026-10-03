using System.Runtime.InteropServices;
using Glideslope.Core;

namespace Glideslope.App;

internal static class TrayServiceFactory
{
    public static ITrayService Create(IDiagnosticSink diagnostics)
    {
        if (!OperatingSystem.IsWindows())
            return new AvaloniaTrayService(diagnostics);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "glideslope-icon.ico");
        var iconHandle = NativeTrayIconLoader.Load(iconPath);
        return new WindowsTrayService(diagnostics, iconHandle, ownsIconHandle: iconHandle != 0);
    }
}

internal static class NativeTrayIconLoader
{
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const uint DefaultSize = 0x00000040;

    public static nint Load(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
            return 0;

        return LoadImage(0, path, ImageIcon, 0, 0, LoadFromFile | DefaultSize);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint imageType, int width, int height, uint flags);
}

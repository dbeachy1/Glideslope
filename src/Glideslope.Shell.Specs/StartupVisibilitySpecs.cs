using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

internal static class StartupVisibilitySpecs
{
    public static void Run()
    {
        Assert(!WindowCoordinator.ShouldStartHidden(ActivationIntent.Autostart, trayIsUsable: true, startInMiniMode: true),
            "mini-mode autostart shows cards immediately when the tray is usable");
        Assert(WindowCoordinator.ShouldStartHidden(ActivationIntent.Autostart, trayIsUsable: true, startInMiniMode: false),
            "full-mode autostart remains hidden when the tray is usable");
        Assert(!WindowCoordinator.ShouldStartHidden(ActivationIntent.Autostart, trayIsUsable: false, startInMiniMode: false),
            "autostart shows cards when tray usability is unproven");
        Assert(!WindowCoordinator.ShouldStartHidden(ActivationIntent.Manual, trayIsUsable: true, startInMiniMode: false),
            "manual launch shows cards even when the tray is usable");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"startup visibility spec failed: {message}");
    }
}

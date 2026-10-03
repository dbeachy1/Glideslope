using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Glideslope.ScreenshotCapture;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Readme screenshot capture currently requires Windows.");
            var options = CaptureOptions.Parse(args);
            if (options.IsChild)
            {
                Directory.CreateDirectory(options.OutputDirectory);
                return AppBuilder.Configure(() => new CaptureApplication(options))
                    .UsePlatformDetect()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            return new HiddenDesktopCaptureSession().Run(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Screenshot capture failed ({ex.GetType().Name}): {ex.Message}");
            return 1;
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace Glideslope.Mockup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0].StartsWith("--render", StringComparison.Ordinal))
        {
            AppBuilder.Configure<MockupApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            var mode = args[0];
            var provider = mode.Contains("claude", StringComparison.Ordinal) ? ProviderPreview.Claude
                : mode.Contains("gemini", StringComparison.Ordinal) ? ProviderPreview.Gemini
                : ProviderPreview.Codex;
            Window window = mode == "--render-icon"
                ? new Window { Width = 256, Height = 256, Content = new GlideslopeMark { Width = 256, Height = 256 } }
                : mode == "--render-palette" ? new PaceSwatchWindow()
                : new MockupWindow(
                    light: mode.Contains("system", StringComparison.Ordinal) ? null
                        : mode.Contains("light", StringComparison.Ordinal),
                    provider: provider,
                    docked: mode.Contains("docked", StringComparison.Ordinal),
                    showMark: mode.Contains("owner", StringComparison.Ordinal) ? true : null);
            if (window is MockupWindow && mode.Contains("small", StringComparison.Ordinal))
            {
                window.Width = 780;
                window.Height = 550;
            }
            else if (window is MockupWindow && mode.Contains("large", StringComparison.Ordinal))
            {
                window.Width = 1128;
                window.Height = 796;
            }
            try
            {
                window.Show();
                using var frame = window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException("The preview frame could not be rendered.");
                frame.Save(args[1], PngBitmapEncoderOptions.Default);
            }
            finally
            {
                window.Close();
            }
            return;
        }

        AppBuilder.Configure<MockupApp>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}

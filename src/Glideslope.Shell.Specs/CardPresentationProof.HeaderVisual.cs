using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;

namespace Glideslope.Shell.Specs;

internal static partial class CardPresentationProof
{
    public static void CaptureHeaderScreenshots(string outputDirectory)
    {
        AppBuilder.Configure<PresentationTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        using var english = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-US"));
        Directory.CreateDirectory(outputDirectory);
        var version = typeof(ProviderUsageCardWindow).Assembly.GetName().Version!.ToString(3);
        var cases = new[]
        {
            (ProviderIds.Claude, 100), (ProviderIds.Codex, 100), (ProviderIds.Gemini, 100),
            (ProviderIds.Claude, CardScale.MinimumPercent), (ProviderIds.Claude, CardScale.MaximumPercent)
        };
        foreach (var (providerId, percent) in cases)
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var scale = percent / 100.0;
            var window = new ProviderUsageCardWindow(providerId, showMark: true, _ => { })
            {
                MinWidth = CardLayoutTiers.MinWidth * scale,
                MinHeight = CardLayoutTiers.MinHeight * scale,
                RequestedThemeVariant = theme
            };
            try
            {
                window.ApplyCardScale(percent);
                window.Width = ProviderUsageCardWindow.DefaultCardWidth * scale;
                window.Height = ProviderUsageCardWindow.DefaultCardHeight * scale;
                window.Show();
                using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException("header_screenshot_render_failed")) { }
                // Render the production window, clipping only the bitmap to its header. Font ink and
                // optical offsets remain visible; control bounds alone cannot establish visual alignment.
                var renderScale = window.RenderScaling;
                using var screenshot = new RenderTargetBitmap(
                    new PixelSize((int)Math.Ceiling(window.ClientSize.Width * renderScale), (int)Math.Ceiling(84 * scale * renderScale)),
                    new Vector(96 * renderScale, 96 * renderScale));
                screenshot.Render(window);
                var filename = $"header-{providerId}-{theme}-{percent}-v{version}.png";
                screenshot.Save(Path.Combine(outputDirectory, filename), PngBitmapEncoderOptions.Default);
                Console.WriteLine($"Captured {filename}");
            }
            finally { window.CloseProgrammatically(); }
        }
        Console.WriteLine("Inspect the header PNGs for visible dot/text alignment before approving a UI release.");
    }
}

using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

internal static partial class CardPresentationProof
{
    public static void CaptureSettingsProjectionNoteScreenshots(string outputDirectory)
    {
        AppBuilder.Configure<PresentationTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        using var english = LocalizedText.OverrideForSpecs(CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-US"));
        Directory.CreateDirectory(outputDirectory);

        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var normal = NewSettingsWindow(theme);
            try
            {
                normal.Show();
                normal.FitToWorkingArea(new PixelRect(0, 0, 1920, 1400), 1.0);
                SaveWindow(normal, Path.Combine(outputDirectory, $"settings-note-{theme}-normal.png"));
            }
            finally { normal.Close(); }

            var constrained = NewSettingsWindow(theme);
            try
            {
                constrained.Show();
                constrained.FitToWorkingArea(new PixelRect(0, 0, 1366, 728), 1.25);
                using (constrained.CaptureRenderedFrame() ?? throw new InvalidOperationException("settings_note_constrained_layout_failed")) { }
                var rows = (Avalonia.Controls.StackPanel)constrained.ScrollControl.Content!;
                var note = constrained.ShiftProjectionNoteControl;
                if (rows.Children.IndexOf(note) + 1 != rows.Children.IndexOf(constrained.StartInMiniModeControl))
                    throw new InvalidOperationException("settings_projection_note_order_invalid");
                var notePosition = note.TranslatePoint(new Point(0, 0), rows)
                    ?? throw new InvalidOperationException("settings_projection_note_not_in_rows");
                constrained.ScrollControl.Offset = new Vector(0, Math.Max(0, notePosition.Y - 48));
                SaveWindow(constrained, Path.Combine(outputDirectory, $"settings-note-{theme}-constrained.png"));
            }
            finally { constrained.Close(); }
        }
    }

    private static SettingsWindow NewSettingsWindow(ThemeVariant theme)
    {
        var window = new SettingsWindow(AppSettings.CreateDefault(), new StartupRegistrationState(false),
            (_, _, _) => Task.FromResult(new SettingsSaveResult(true, null)))
        {
            RequestedThemeVariant = theme
        };
        if (window.ShiftProjectionNoteControl.Text !=
            "Note: Hold Shift while a full card is visible to see projected usage for the rest of your week.")
            throw new InvalidOperationException("settings_projection_note_copy_invalid");
        return window;
    }

    private static void SaveWindow(SettingsWindow window, string path)
    {
        using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException("settings_note_screenshot_render_failed")) { }
        var scale = window.RenderScaling;
        using var screenshot = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(window.ClientSize.Width * scale), (int)Math.Ceiling(window.ClientSize.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        screenshot.Render(window);
        screenshot.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"Captured {Path.GetFileName(path)}");
    }
}

using Avalonia.Controls;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>Loads the shared application icon once. Missing or unreadable files leave the icon null so windows
/// can use the default glyph without failing.</summary>
internal static class AppIcon
{
    private static readonly object Gate = new();
    private static bool _attempted;
    private static WindowIcon? _icon;

    /// <summary>The shared icon, loaded on first use.</summary>
    public static WindowIcon? Get(IDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        lock (Gate)
        {
            if (_attempted) return _icon;
            _attempted = true;
            _icon = Load(diagnostics);
            return _icon;
        }
    }

    private static WindowIcon? Load(IDiagnosticSink diagnostics)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "glideslope-icon.ico");
        try
        {
            if (!File.Exists(path))
            {
                diagnostics.Record(new DiagnosticEvent("app_icon_missing", Status: "default_glyph"));
                return null;
            }
            using var stream = File.OpenRead(path);
            var icon = new WindowIcon(stream);
            diagnostics.Record(new DiagnosticEvent("app_icon_loaded", Status: $"bytes={stream.Length}"));
            return icon;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Swallowed so a bad icon file never stops a window from opening; the window shows the default glyph.
            diagnostics.Record(new DiagnosticEvent("app_icon_load_failed", Status: ex.GetType().Name));
            return null;
        }
    }
}

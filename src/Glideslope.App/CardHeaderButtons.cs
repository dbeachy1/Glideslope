using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Glideslope.App;

/// <summary>
/// Shared square buttons for the card header, including settings, reset size, close, minimize, and plain actions.
/// </summary>
internal static class CardHeaderButtons
{
    internal static Button CreateSettingsButton()
    {
        var button = ActionButton("⚙", 29, 19);
        ToolTip.SetTip(button, LocalizedText.CardSettingsTooltip);
        AutomationProperties.SetName(button, LocalizedText.CardSettingsTooltip);
        return button;
    }

    /// <summary>Creates the reset-size button.</summary>
    internal static Button CreateResetSizeButton()
    {
        var button = ActionButton("↺", 29, 19);
        ToolTip.SetTip(button, LocalizedText.CardResetSizeTooltip);
        AutomationProperties.SetName(button, LocalizedText.CardResetSizeTooltip);
        return button;
    }

    internal static Button CreateCloseButton(string providerId)
    {
        // Draw the close mark as centered strokes; the font glyph is not vertically centered on all platforms.
        var button = IconButton("M1,1 L11,11 M11,1 L1,11", 32);
        var name = LocalizedText.CardCloseTooltip(providerId);
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>Creates the minimize button. Cards have no window frame, so the app supplies this control.</summary>
    internal static Button CreateMinimizeButton()
    {
        var button = IconButton("M1,6 L11,6", 29);
        ToolTip.SetTip(button, LocalizedText.CardMinimizeTooltip);
        AutomationProperties.SetName(button, LocalizedText.CardMinimizeTooltip);
        return button;
    }

    /// <summary>A header button whose mark is drawn (a path in a 12 x 12 box) rather than a font glyph, so it
    /// is centered exactly. Same size and colors as <see cref="ActionButton"/>; the stroke takes the Foreground of the
    /// button's content presenter, which is where the theme's hover and pressed colors land, as they do for ⚙ and ↺.</summary>
    internal static Button IconButton(string pathData, double width)
    {
        var button = ActionButton(string.Empty, width, 16);
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(pathData),
            Width = 12,
            Height = 12,
            StrokeThickness = 1.75,
            StrokeLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new Avalonia.Data.Binding(nameof(Avalonia.Controls.Presenters.ContentPresenter.Foreground))
            {
                RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor)
                {
                    AncestorType = typeof(Avalonia.Controls.Presenters.ContentPresenter),
                },
            },
        };
        button.Content = path;
        return button;
    }

    // Shared factory for plain header actions.
    internal static Button ActionButton(string content, double size, double fontSize)
    {
        return new Button
        {
            Content = content,
            Width = size,
            Height = 26,
            MinWidth = size,
            MaxWidth = size,
            MinHeight = 26,
            MaxHeight = 26,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brush("#94A1B7"),
            FontSize = fontSize,
        };
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Glideslope.Mockup;

internal sealed class PaceSwatchWindow : Window
{
    private static readonly (PaceBand Band, string Name, string Threshold)[] Bands =
    [
        (PaceBand.StrongCushion, "Bright green", "12+ hours under"),
        (PaceBand.OnPace, "Light green", "0 to under 12 hours under"),
        (PaceBand.SlightlyOver, "Light yellow", "Up to 6 hours over"),
        (PaceBand.Over, "Bright yellow", "Over 6 to 12 hours over"),
        (PaceBand.SeverelyOver, "Coral / orange", "Over 12 to under 24 hours over"),
        (PaceBand.CriticalOver, "Bright red", "24+ hours over")
    ];

    internal PaceSwatchWindow()
    {
        Width = 1100;
        Height = 500;
        WindowDecorations = WindowDecorations.None;
        Background = new SolidColorBrush(Color.Parse("#D9E0E8"));
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(12)
        };
        grid.Children.Add(Panel(dark: true));
        var light = Panel(dark: false);
        Grid.SetColumn(light, 1);
        grid.Children.Add(light);
        Content = grid;
    }

    private static Control Panel(bool dark)
    {
        var text = new SolidColorBrush(Color.Parse(dark ? "#F5F7FB" : "#172438"));
        var muted = new SolidColorBrush(Color.Parse(dark ? "#94A1B7" : "#596B80"));
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = dark ? "Dark theme" : "Light theme",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 23, FontWeight = FontWeight.SemiBold,
            Foreground = text
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Weekly pace colors · five-hour thresholds scale proportionally",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            Foreground = muted,
            Margin = new Thickness(0, -6, 0, 12)
        });

        foreach (var (band, name, threshold) in Bands)
        {
            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("28,115,*,70"),
                Height = 45,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(new Border
            {
                Width = 19, Height = 19, CornerRadius = new CornerRadius(4),
                Background = PacePreviewPolicy.Brush(dark, band),
                VerticalAlignment = VerticalAlignment.Center
            });
            var nameLabel = Label(name, 13, text, FontWeight.SemiBold);
            Grid.SetColumn(nameLabel, 1);
            row.Children.Add(nameLabel);
            var thresholdLabel = Label(threshold, 11, muted);
            Grid.SetColumn(thresholdLabel, 2);
            row.Children.Add(thresholdLabel);
            var hex = Label(PacePreviewPolicy.Hex(dark, band), 11, muted);
            Grid.SetColumn(hex, 3);
            row.Children.Add(hex);
            stack.Children.Add(row);
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(dark ? "#171D29" : "#F6F8FC")),
            BorderBrush = new SolidColorBrush(Color.Parse(dark ? "#69758A" : "#8A9BB0")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(24),
            Child = stack
        };
    }

    private static TextBlock Label(string value, double size, IBrush color, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = value,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        FontWeight = weight,
        Foreground = color,
        VerticalAlignment = VerticalAlignment.Center
    };
}

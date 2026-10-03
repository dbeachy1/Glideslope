using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System.Globalization;

namespace Glideslope.Mockup;

internal sealed class MockupWindow : Window
{
    internal const double DefaultCardWidth = 940;
    internal const double DefaultCardHeight = 663;
    private bool _dark;
    private readonly ProviderPreview _provider;
    private readonly bool _showMark;
    private IBrush Surface => Brush(_dark ? "#171D29" : "#F6F8FC");
    private IBrush Text => Brush(_dark ? "#F5F7FB" : "#172438");
    private IBrush Muted => Brush(_dark ? "#94A1B7" : "#596B80");
    private IBrush Faint => Brush(_dark ? "#65748B" : "#708198");
    private IBrush Blue => Brush(_dark ? "#61A9F5" : "#2268BB");
    private IBrush WeeklyPaceColor => PacePreviewPolicy.Brush(_dark, _provider.WeeklyBand);
    private IBrush FiveHourPaceColor => PacePreviewPolicy.Brush(_dark, _provider.FiveHourBand);
    private IBrush Guide => Brush(_dark ? "#AEB8C9" : "#8995A7");

    public MockupWindow(bool? light = null, ProviderPreview? provider = null, bool docked = false,
        bool? showMark = null)
    {
        _provider = provider ?? ProviderPreview.Codex;
        _showMark = showMark ?? !docked;
        if (Application.Current is { } app)
            app.RequestedThemeVariant = light switch
            {
                true => ThemeVariant.Light,
                false => ThemeVariant.Dark,
                null => ThemeVariant.Default
            };
        _dark = light switch
        {
            true => false,
            false => true,
            null => Application.Current?.ActualThemeVariant != ThemeVariant.Light
        };
        Title = $"Glideslope — {_provider.Name} card mock-up";
        Width = DefaultCardWidth;
        Height = DefaultCardHeight;
        MinWidth = 780;
        MinHeight = 550;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CornerRadius = new CornerRadius(18);
        Content = BuildScaledCard();
        ActualThemeVariantChanged += (_, _) => SyncThemeWithSystem();
        Opened += (_, _) => SyncThemeWithSystem();
    }

    private Control BuildScaledCard() => new Viewbox
    {
        // Keep the instrument's proportions and scale text with the bars and chart.
        Stretch = Stretch.Uniform,
        Child = BuildCard()
    };

    private Control BuildCard()
    {
        var outer = new Border
        {
            Width = DefaultCardWidth,
            Height = DefaultCardHeight,
            Background = Surface,
            BorderBrush = Brush(_dark ? "#69758A" : "#8A9BB0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(5),
            ClipToBounds = true
        };
        var inner = new Border
        {
            Background = Surface,
            BorderBrush = Brush(_dark ? "#354154" : "#D0DBE7"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(30, 23, 30, 23)
        };
        outer.Child = inner;

        var root = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto,Auto,Auto") };
        inner.Child = root;

        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse(_showMark ? "Auto,*,Auto" : "*,Auto")
        };
        if (_showMark)
            header.Children.Add(new GlideslopeMark { Width = 35, Height = 35 });

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(_showMark ? 12 : 0, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        brand.Children.Add(Label(_provider.Name, 29, Text, FontWeight.SemiBold));
        brand.Children.Add(Label($"Plan type: {_provider.Plan}", 12, Muted,
            margin: new Thickness(0, 5, 0, 0)));
        Grid.SetColumn(brand, _showMark ? 1 : 0);
        header.Children.Add(brand);

        var headerRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var settings = new Button
        {
            Content = "⚙",
            Width = 29,
            Height = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Muted,
            FontSize = 19,
            FontFamily = new FontFamily("Segoe UI")
        };
        ToolTip.SetTip(settings, "Appearance settings");
        var systemItem = new MenuItem { Header = "Follow system theme" };
        var darkItem = new MenuItem { Header = "Dark theme" };
        var lightItem = new MenuItem { Header = "Light theme" };
        systemItem.Click += (_, _) => SetTheme(ThemeVariant.Default);
        darkItem.Click += (_, _) => SetTheme(ThemeVariant.Dark);
        lightItem.Click += (_, _) => SetTheme(ThemeVariant.Light);
        var appearance = new MenuFlyout { ItemsSource = new[] { systemItem, darkItem, lightItem } };
        settings.Click += (_, _) => appearance.ShowAt(settings);
        headerRight.Children.Add(settings);
        var close = new Button
        {
            Content = "×",
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Muted,
            FontSize = 20,
            FontFamily = new FontFamily("Segoe UI")
        };
        ToolTip.SetTip(close, $"Close {_provider.Name} card");
        close.Click += (_, _) => Close();
        headerRight.Children.Add(close);
        Grid.SetColumn(headerRight, _showMark ? 2 : 1);
        header.Children.Add(headerRight);
        header.PointerPressed += (_, e) =>
        {
            if (e.Source is Button || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                return;
            BeginMoveDrag(e);
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var rule = new Border { Height = 1, Background = Brush(_dark ? "#344052" : "#D5DFEA"), Margin = new Thickness(0, 22, 0, 0) };
        Grid.SetRow(rule, 1);
        root.Children.Add(rule);

        var middle = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("270,*"),
            ColumnSpacing = 25,
            Margin = new Thickness(0, 29, 0, 18)
        };
        Grid.SetRow(middle, 2);
        root.Children.Add(middle);

        var summary = new StackPanel { Spacing = 0 };
        summary.Children.Add(Label(_provider.WeeklyReset, 11, Muted, FontWeight.SemiBold));
        // Synthetic dates preserve the sample's spacing and an accurate weekday/countdown.
        if (_provider.HasResetCredits)
        {
            var firstExpiry = DateOnly.FromDateTime(DateTime.Today).AddDays(27);
            var earnedReset = new Border
            {
                Background = PacePreviewPolicy.Brush(_dark, PaceBand.StrongCushion),
                CornerRadius = new CornerRadius(0),
                Margin = new Thickness(0, 11, 0, 0),
                Padding = new Thickness(8, 5, 8, 5)
            };
            var earnedResetContent = new StackPanel { Spacing = 2 };
            var reverseText = Brush(_dark ? "#171D29" : "#FFFFFF");
            earnedResetContent.Children.Add(Label("USAGE RESET EXPIRES", 9, reverseText, FontWeight.SemiBold));
            var dateAndCount = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            dateAndCount.Children.Add(Label($"{FormatSampleDate(firstExpiry)} (27 days left)", 11,
                reverseText, FontWeight.SemiBold));
            var resetCount = new Button
            {
                Content = "3",
                Width = 22,
                Height = 19,
                Padding = new Thickness(0),
                Background = Brush(_dark ? "#B7FFD0" : "#08713D"),
                BorderBrush = Brush(_dark ? "#247747" : "#D4F4E5"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                Foreground = reverseText,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI")
            };
            ToolTip.SetTip(resetCount, "You have 3 usage resets total. Click to see the remaining 2.");
            var otherResets = new MenuFlyout
            {
                ItemsSource = new[]
                {
                    new MenuItem { Header = $"Usage reset number 2 expires {FormatSampleDate(firstExpiry.AddDays(14))}" },
                    new MenuItem { Header = $"Usage reset number 3 expires {FormatSampleDate(firstExpiry.AddDays(21))}" }
                }
            };
            resetCount.Click += (_, _) => otherResets.ShowAt(resetCount);
            dateAndCount.Children.Add(resetCount);
            earnedResetContent.Children.Add(dateAndCount);
            earnedReset.Child = earnedResetContent;
            summary.Children.Add(earnedReset);
        }
        summary.Children.Add(new TextBlock
        {
            Text = $"{_provider.WeeklyRemaining:P0}",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 76,
            FontWeight = FontWeight.SemiBold,
            Foreground = WeeklyPaceColor,
            Margin = new Thickness(-4, 0, 0, -4)
        });
        summary.Children.Add(Label("weekly remaining", 14, Muted));

        // The short-window label and shade use that window's own pace calculation.
        summary.Children.Add(Label("5-HOUR DELTA", 11, Muted, FontWeight.SemiBold,
            margin: new Thickness(0, 11, 0, 8)));
        summary.Children.Add(new DeltaBar(_provider.FiveHourDelta, compact: true, _dark, _provider.FiveHourBand) { Height = 14 });
        summary.Children.Add(Label(_provider.FiveHourDeltaLabel, 12, FiveHourPaceColor, FontWeight.SemiBold,
            HorizontalAlignment.Center, margin: new Thickness(0, 8, 0, 0)));

        var shortRemainingTitle = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"),
            Margin = new Thickness(0, 10, 0, 8)
        };
        shortRemainingTitle.Children.Add(Label(_provider.FableWeeklyRemaining is null
            ? "5-HOUR REMAINING" : "5-HOUR REMAINING · ALL MODELS", 11, Muted, FontWeight.SemiBold));
        var shortRemainingValue = Label($"{_provider.FiveHourRemaining:P0}", 12, FiveHourPaceColor, FontWeight.SemiBold);
        Grid.SetColumn(shortRemainingValue, 1);
        shortRemainingTitle.Children.Add(shortRemainingValue);
        summary.Children.Add(shortRemainingTitle);
        summary.Children.Add(new RemainingBar(_provider.FiveHourRemaining, _dark, _provider.FiveHourBand) { Height = 12 });
        Grid.SetColumn(summary, 0);
        middle.Children.Add(summary);

        var graphGroup = new Grid { RowDefinitions = RowDefinitions.Parse("Auto,*,Auto") };
        var graphHeading = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        graphHeading.Children.Add(Label("WEEKLY GLIDESLOPE", 11, Muted, FontWeight.SemiBold));
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        legend.Children.Add(LegendItem(Blue, "Observed"));
        legend.Children.Add(LegendItem(Guide, "Even pace"));
        Grid.SetColumn(legend, 1);
        graphHeading.Children.Add(legend);
        graphGroup.Children.Add(graphHeading);

        var chart = new WeeklyChart(_dark, _provider.WeeklyRemaining) { Margin = new Thickness(0, 16, 0, 0) };
        Grid.SetRow(chart, 1);
        graphGroup.Children.Add(chart);
        var graphFooter = Label("Seven days from the reported window start  ·  Blue = recorded samples", 11, Faint,
            margin: new Thickness(0, 5, 0, 0));
        Grid.SetRow(graphFooter, 2);
        graphGroup.Children.Add(graphFooter);
        Grid.SetColumn(graphGroup, 1);
        middle.Children.Add(graphGroup);

        var deltaTitle = Label("DELTA FROM GLIDESLOPE", 11, Muted, FontWeight.SemiBold,
            margin: new Thickness(0, 0, 0, 10));
        Grid.SetRow(deltaTitle, 3);
        root.Children.Add(deltaTitle);

        var deltaPanel = new StackPanel { Spacing = 8 };
        deltaPanel.Children.Add(new DeltaBar(_provider.WeeklyDelta, compact: false, _dark, _provider.WeeklyBand) { Height = 20 });
        deltaPanel.Children.Add(Label(_provider.WeeklyDeltaLabel, 14, WeeklyPaceColor, FontWeight.SemiBold,
            HorizontalAlignment.Center));
        Grid.SetRow(deltaPanel, 4);
        root.Children.Add(deltaPanel);

        var remaining = new StackPanel { Spacing = 10, Margin = new Thickness(0, 24, 0, 0) };
        var remainingHeading = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        remainingHeading.Children.Add(Label("WEEKLY REMAINING", 11, Muted, FontWeight.SemiBold));
        var remainingNumber = Label($"{_provider.WeeklyRemaining:P0}", 13, WeeklyPaceColor, FontWeight.Bold);
        Grid.SetColumn(remainingNumber, 1);
        remainingHeading.Children.Add(remainingNumber);
        remaining.Children.Add(remainingHeading);
        remaining.Children.Add(new RemainingBar(_provider.WeeklyRemaining, _dark, _provider.WeeklyBand) { Height = 18 });
        if (_provider.FableWeeklyRemaining is { } fableRemaining &&
            _provider.FableWeeklyDelta is { } fableDelta &&
            _provider.FableWeeklyBand is { } fableBand)
        {
            var fableRow = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("2*,*"),
                ColumnSpacing = 18,
                Margin = new Thickness(0, 13, 0, 0)
            };
            var fableRemainingGroup = new StackPanel { Spacing = 9 };
            var fableHeading = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,Auto")
            };
            fableHeading.Children.Add(Label("FABLE THIS WEEK", 11, Muted, FontWeight.SemiBold));
            var fableValue = Label($"{fableRemaining:P0} remaining", 13,
                PacePreviewPolicy.Brush(_dark, fableBand), FontWeight.Bold);
            Grid.SetColumn(fableValue, 1);
            fableHeading.Children.Add(fableValue);
            fableRemainingGroup.Children.Add(fableHeading);
            fableRemainingGroup.Children.Add(new RemainingBar(fableRemaining, _dark, fableBand) { Height = 14 });
            fableRow.Children.Add(fableRemainingGroup);

            var fableDeltaGroup = new StackPanel { Spacing = 9 };
            fableDeltaGroup.Children.Add(Label("FABLE WEEKLY DELTA", 11, Muted, FontWeight.SemiBold));
            fableDeltaGroup.Children.Add(new DeltaBar(fableDelta, compact: false, _dark, fableBand) { Height = 14 });
            fableDeltaGroup.Children.Add(Label(_provider.FableWeeklyDeltaLabel!, 11,
                PacePreviewPolicy.Brush(_dark, fableBand), FontWeight.SemiBold,
                HorizontalAlignment.Center));
            Grid.SetColumn(fableDeltaGroup, 1);
            fableRow.Children.Add(fableDeltaGroup);
            remaining.Children.Add(fableRow);
        }
        Grid.SetRow(remaining, 5);
        root.Children.Add(remaining);
        var frame = new Grid { Width = DefaultCardWidth, Height = DefaultCardHeight };
        frame.Children.Add(outer);
        AddCornerGrip(frame, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner,
            HorizontalAlignment.Left, VerticalAlignment.Top);
        AddCornerGrip(frame, WindowEdge.NorthEast, StandardCursorType.TopRightCorner,
            HorizontalAlignment.Right, VerticalAlignment.Top);
        AddCornerGrip(frame, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner,
            HorizontalAlignment.Left, VerticalAlignment.Bottom);
        AddCornerGrip(frame, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner,
            HorizontalAlignment.Right, VerticalAlignment.Bottom);
        return frame;
    }

    private void AddCornerGrip(Grid frame, WindowEdge edge, StandardCursorType cursor,
        HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        var grip = new Border
        {
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Cursor = new Cursor(cursor)
        };
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            BeginResizeDrag(edge, e);
            e.Handled = true;
        };
        frame.Children.Add(grip);
    }

    private void SetTheme(ThemeVariant variant)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = variant;
        var dark = variant == ThemeVariant.Dark ||
            (variant == ThemeVariant.Default && Application.Current?.ActualThemeVariant != ThemeVariant.Light);
        if (_dark == dark) return;
        _dark = dark;
        Content = BuildScaledCard();
    }

    private void SyncThemeWithSystem()
    {
        var dark = ActualThemeVariant != ThemeVariant.Light;
        if (_dark == dark) return;
        _dark = dark;
        Content = BuildScaledCard();
    }

    private StackPanel LegendItem(IBrush color, string name)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        item.Children.Add(new Border { Width = 13, Height = 3, Background = color, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center });
        item.Children.Add(Label(name, 11, Muted));
        return item;
    }

    private static TextBlock Label(string text, double size, IBrush color, FontWeight weight = FontWeight.Normal,
        HorizontalAlignment horizontal = HorizontalAlignment.Left, VerticalAlignment vertical = VerticalAlignment.Center,
        Thickness? margin = null) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        FontWeight = weight,
        Foreground = color,
        HorizontalAlignment = horizontal,
        VerticalAlignment = vertical,
        Margin = margin ?? new Thickness(0)
    };

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    private static string FormatSampleDate(DateOnly date)
    {
        var day = date.Day;
        var suffix = day % 100 is >= 11 and <= 13 ? "th" : (day % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th"
        };
        var weekday = date.ToDateTime(TimeOnly.MinValue)
            .ToString("ddd", CultureInfo.GetCultureInfo("en-US"))
            .ToUpperInvariant();
        var month = date.ToDateTime(TimeOnly.MinValue)
            .ToString("MMMM", CultureInfo.GetCultureInfo("en-US"));
        return $"{weekday}, {month} {day}{suffix}";
    }
}

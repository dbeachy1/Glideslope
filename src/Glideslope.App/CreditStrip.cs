using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.App;

/// <summary>
/// Displays reset credits, a sign-in or missing-CLI notice, or a blank strip. Full and mini bodies can host it
/// at different text sizes. Call <see cref="Update"/> before <see cref="ApplyTheme"/>; the host controls layout.
/// </summary>
internal sealed class CreditStrip
{
    private readonly Border _strip;
    private readonly StackPanel _noticeContent;
    private readonly TextBlock _noticeHeading;
    private readonly TextBlock _noticeLine;
    private readonly StackPanel _creditContent;
    private readonly TextBlock _creditExpiration;
    private readonly Button _creditCount;

    public CreditStrip(double textSize)
    {
        TextSize = textSize;
        _creditExpiration = Label(string.Empty, textSize, bold: true, wrap: true);
        _creditCount = new Button
        {
            Height = 30,
            MinWidth = 34,
            MinHeight = 30,
            Padding = new Avalonia.Thickness(6, 2),
            CornerRadius = new Avalonia.CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            FontSize = textSize,
            FontWeight = FontWeight.SemiBold
        };
        _creditContent = new StackPanel { Spacing = 2, IsVisible = false };
        _creditContent.Children.Add(Label(LocalizedText.CardCreditExpires, textSize, bold: true, wrap: true));
        var creditLine = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8 };
        creditLine.Children.Add(_creditExpiration);
        Grid.SetColumn(_creditCount, 1);
        creditLine.Children.Add(_creditCount);
        _creditContent.Children.Add(creditLine);
        _noticeHeading = Label(string.Empty, textSize, bold: true, wrap: true);
        _noticeLine = Label(string.Empty, textSize, bold: true, wrap: true);
        _noticeContent = new StackPanel { Spacing = 2, IsVisible = false };
        _noticeContent.Children.Add(_noticeHeading);
        _noticeContent.Children.Add(_noticeLine);
        var stripContent = new Grid();
        stripContent.Children.Add(_creditContent);
        stripContent.Children.Add(_noticeContent);
        // Use a minimum height so longer translated expiration text can wrap without clipping.
        _strip = new Border { MinHeight = 70, Margin = new Avalonia.Thickness(0, 4, 0, 0), Padding = new Avalonia.Thickness(8, 4), Child = stripContent };
    }

    /// <summary>The font size every label and value in the strip was built at (16 for the full body, 14 for
    /// the mini body per design §4.3).</summary>
    internal double TextSize { get; }

    /// <summary>The strip's own control, a Border reserving <see cref="ReservedHeight"/>. A host adds this to
    /// its own layout; the strip never adds itself anywhere.</summary>
    internal Control Control => _strip;

    internal double ReservedHeight => _strip.MinHeight;
    internal bool NoticeVisible => _noticeContent.IsVisible;
    internal Control NoticeContentControl => _noticeContent;
    internal string NoticeText => $"{_noticeHeading.Text} {_noticeLine.Text}";
    internal bool CreditBoxVisible => _creditContent.IsVisible;
    internal Control CreditContentControl => _creditContent;
    internal bool IsCreditInventoryVisible => _creditContent.IsVisible;
    internal bool IsVisuallyPainted =>
        _strip.Background is SolidColorBrush background && background.Color.A > 0 ||
        _strip.BorderBrush is SolidColorBrush border && border.Color.A > 0;
    internal string CreditCountText => _creditCount.Content?.ToString() ?? string.Empty;
    internal Button CreditCountControl => _creditCount;

    /// <summary>The strip shows credits, a sign-in or missing-CLI notice, or remains blank. A stale snapshot may
    /// still provide credits. The notice and credit box never overlap. Sets content and visibility only; call
    /// <see cref="ApplyTheme"/> afterward to color the result.</summary>
    internal void Update(ProviderDisplayState? state)
    {
        switch (state?.Status)
        {
            case ProviderStatus.NeedsSignIn:
            case ProviderStatus.AuthenticationExpired:
                UpdateCredits(null);
                // A case that only matches a non-null enum value implies state is not null here.
                ShowNotice(LocalizedText.CardNoticeSignInHeading, LocalizedText.CardNoticeSignIn(state!.ProviderId));
                break;
            case ProviderStatus.MissingApplication:
                UpdateCredits(null);
                ShowNotice(LocalizedText.CardNoticeMissingHeading, LocalizedText.CardNoticeMissing(state!.ProviderId));
                break;
            default:
                _noticeContent.IsVisible = false;
                UpdateCredits(state?.Snapshot?.ResetCredits);
                break;
        }
    }

    private void ShowNotice(string heading, string line)
    {
        _noticeHeading.Text = heading;
        _noticeLine.Text = line;
        _noticeContent.IsVisible = true;
    }

    private void UpdateCredits(ResetCreditInventory? inventory)
    {
        var hasCredits = inventory is { HasKnownZero: false, AvailableCount: > 0 };
        _creditContent.IsVisible = hasCredits;
        if (!hasCredits || inventory is null)
        {
            _creditCount.Content = null;
            _creditCount.Tag = null;
            ToolTip.SetTip(_creditCount, null);
            return;
        }

        var first = inventory.Details.FirstOrDefault();
        var localExpiration = first?.ExpiresAtUtc?.ToLocalTime();
        _creditExpiration.Text = localExpiration is { } expiration
            ? LocalizedText.CreditExpiration(expiration, DateTimeOffset.Now)
            : LocalizedText.CardCreditExpirationUnknown;
        _creditCount.Content = LocalizedText.Count(inventory.AvailableCount);
        ToolTip.SetTip(_creditCount, LocalizedText.CreditTooltip(inventory.AvailableCount));
        _creditCount.Click -= OnCreditCountClick;
        _creditCount.Click += OnCreditCountClick;
        _creditCount.Tag = inventory;
    }

    private void OnCreditCountClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_creditCount.Tag is not ResetCreditInventory inventory || inventory.AvailableCount <= 1) return;
        CreateCreditDetailsFlyout(inventory)?.ShowAt(_creditCount);
    }

    internal MenuFlyout? CreateCreditDetailsFlyoutForCurrentInventory() => _creditCount.Tag is ResetCreditInventory inventory
        ? CreateCreditDetailsFlyout(inventory)
        : null;

    private static MenuFlyout? CreateCreditDetailsFlyout(ResetCreditInventory inventory)
    {
        if (inventory.AvailableCount <= 1) return null;
        var flyout = new MenuFlyout();
        foreach (var detail in inventory.AdditionalDetails)
        {
            var text = detail.ExpiresAtUtc is { } expires
                ? LocalizedText.CreditExpiration(expires.ToLocalTime(), DateTimeOffset.Now)
                : LocalizedText.CardCreditExpirationUnknown;
            flyout.Items.Add(new MenuItem { Header = text, IsEnabled = false });
        }
        if (inventory.UndisclosedCount > 0)
            flyout.Items.Add(new MenuItem { Header = LocalizedText.CreditUndisclosed(inventory.UndisclosedCount), IsEnabled = false });
        return flyout;
    }

    /// <summary>Colors the strip (background, border and text) for the notice, the credit box or the blank
    /// state left by <see cref="Update"/>, and the credit-count button's fill. Call after every update.</summary>
    internal void ApplyTheme(bool dark)
    {
        _creditCount.Background = Brush(dark ? "#2D8060" : "#B8E8CB");
        _creditCount.Foreground = Brush(dark ? "#FFFFFF" : "#174D36");
        if (_noticeContent.IsVisible)
        {
            // Design §2 rule 7: "reverse orange": light text on orange in the dark theme, dark text on a
            // pale orange in the light theme, the same shape as the green credit box.
            _strip.Background = Brush(dark ? "#8A4B00" : "#FFE3BF");
            _strip.BorderBrush = Brush(dark ? "#C77400" : "#E39A2E");
            foreach (var text in _noticeContent.GetVisualDescendants().OfType<TextBlock>())
                text.Foreground = Brush(dark ? "#FFF1DC" : "#5A3300");
            return;
        }
        if (!_creditContent.IsVisible)
        {
            _strip.Background = Brushes.Transparent;
            _strip.BorderBrush = Brushes.Transparent;
            return;
        }

        _strip.Background = Brush(dark ? "#1D5B45" : "#DDF4E8");
        _strip.BorderBrush = Brush(dark ? "#2D8060" : "#97D6B2");
        foreach (var text in _creditContent.GetVisualDescendants().OfType<TextBlock>())
            text.Foreground = Brush(dark ? "#E6FFF0" : "#174D36");
    }

    private static TextBlock Label(string text, double size, bool bold = false, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        TextWrapping = wrap ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static SolidColorBrush Brush(string hex) => new(Avalonia.Media.Color.Parse(hex));
}

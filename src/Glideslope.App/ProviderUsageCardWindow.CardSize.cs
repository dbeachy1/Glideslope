using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Glideslope.Core;

namespace Glideslope.App;

// Applies coordinator-owned card scaling and raises requests from the title-row size flyout.
// Card scale requests are applied by the coordinator.
internal sealed partial class ProviderUsageCardWindow
{
    /// <summary>Scales the card content and synchronizes the size slider without raising
    /// <see cref="CardScaleRequested"/>. The coordinator owns the window size and minimum size.</summary>
    // Public because ICardModeWindow exposes this method to the coordinator.
    public void ApplyCardScale(int percent)
    {
        var snapped = CardScale.Snap(percent);
        var previous = _scalePercent;
        _scalePercent = snapped;
        _lastRequestedScalePercent = snapped;
        var factor = snapped / 100.0;
        // A new transform each time, so the LayoutTransformControl sees a property change and lays out again.
        _scaleHost.LayoutTransform = new ScaleTransform(factor, factor);
        SetScaleSliderQuietly(snapped);
        _diagnostics.Record(new DiagnosticEvent("card_scale_applied", _providerId, $"mode={_mode},percent={snapped},previous={previous},requested={percent}"));
    }

    /// <summary>Moves the size slider and its value text without raising <see cref="CardScaleRequested"/>.</summary>
    private void SetScaleSliderQuietly(int percent)
    {
        _applyingScale = true;
        try
        {
            _scaleSlider.Value = percent;
        }
        finally
        {
            _applyingScale = false;
        }
        _scaleValue.Text = LocalizedText.Percentage(percent / 100.0);
    }

    /// <summary>A slider step (drag, arrow key, or a value set in code) snaps to the
    /// nearest 10 % step (the control itself leaves a value set in code where it is), shows the value, and asks
    /// the coordinator for it. The card does not scale itself here; it waits for <see cref="ApplyCardScale"/>.</summary>
    private void OnScaleSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_applyingScale) return;
        var percent = CardScale.Snap(e.NewValue);
        if (Math.Abs(_scaleSlider.Value - percent) > 0.001)
            SetScaleSliderQuietly(percent);
        _scaleValue.Text = LocalizedText.Percentage(percent / 100.0);
        if (percent == _lastRequestedScalePercent) return;
        var from = _lastRequestedScalePercent;
        _lastRequestedScalePercent = percent;
        _scaleChangedWhileOpen = true;
        _diagnostics.Record(new DiagnosticEvent("card_scale_requested", _providerId, $"mode={_mode},percent={percent},from={from},applied={_scalePercent}"));
        CardScaleRequested?.Invoke(this, percent);
    }

    private void OnCardSizeFlyoutOpened(object? sender, EventArgs e)
    {
        _lastRequestedScalePercent = _scalePercent;
        _scaleChangedWhileOpen = false;
        SetScaleSliderQuietly(_scalePercent);
        // Match the flyout to the card surface and current theme.
        if ((_cardSizeFlyout.Content as Control)?.Parent is FlyoutPresenter presenter)
        {
            var dark = ActualThemeVariant != ThemeVariant.Light;
            presenter.Background = Brush(dark ? "#1D2635" : "#FFFFFF");
            presenter.BorderBrush = Brush(dark ? "#354154" : "#D0DBE7");
            presenter.BorderThickness = new Thickness(1);
            presenter.CornerRadius = new CornerRadius(8);
        }
        // Arrow keys step 10 % as soon as the flyout opens.
        _scaleSlider.Focus();
        _diagnostics.Record(new DiagnosticEvent("card_scale_flyout", _providerId, $"opened,percent={_scalePercent}"));
    }

    /// <summary>Closing the flyout ends the change: the coordinator saves once, only if something was asked for.</summary>
    private void OnCardSizeFlyoutClosed(object? sender, EventArgs e)
    {
        var changed = _scaleChangedWhileOpen;
        _scaleChangedWhileOpen = false;
        _diagnostics.Record(new DiagnosticEvent("card_scale_flyout", _providerId,
            $"closed,percent={_lastRequestedScalePercent},applied={_scalePercent},changed={changed}"));
        if (changed) CardScaleChangeEnded?.Invoke(this, EventArgs.Empty);
    }
}

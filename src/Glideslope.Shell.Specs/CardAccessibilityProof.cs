using Avalonia.Automation;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;

namespace Glideslope.Shell.Specs;

/// <summary>
/// Verifies the production action-button factory supplies explicit automation
/// names alongside its localized tooltips without constructing a desktop window.
/// </summary>
internal static class CardAccessibilityProof
{
    public static void Run()
    {
        var settings = CardHeaderButtons.CreateSettingsButton();
        var close = CardHeaderButtons.CreateCloseButton("codex");
        var resetSize = CardHeaderButtons.CreateResetSizeButton();
        Assert(AutomationProperties.GetName(settings) == LocalizedText.CardSettingsTooltip, "settings button automation name");
        Assert(AutomationProperties.GetName(close) == LocalizedText.CardCloseTooltip("codex"), "provider close button automation name");
        // The minimize button and drawn marks use buttons as tall as the other header controls.
        var minimize = CardHeaderButtons.CreateMinimizeButton();
        Assert(AutomationProperties.GetName(minimize) == LocalizedText.CardMinimizeTooltip, "minimize button automation name");
        Assert(close.Height == settings.Height && minimize.Height == settings.Height,
            "the close and minimize buttons are as tall as the settings button, so their centered marks line up with it");
        // The reset-size button matches the gear's style and size.
        Assert(AutomationProperties.GetName(resetSize) == LocalizedText.CardResetSizeTooltip, "reset-size button automation name");
        Assert(resetSize.Width == settings.Width && resetSize.Height == settings.Height && resetSize.FontSize == settings.FontSize,
            "reset-size button matches the settings gear's size and style");
        AssertModeToggleAndMiniRefreshNames();
    }

    /// <summary>2.0 mini mode design §8.2: the toggle and the mini Refresh have accessible names, and the
    /// toggle's name follows the mode (it names the mode a click would switch <em>to</em>).</summary>
    private static void AssertModeToggleAndMiniRefreshNames()
    {
        var window = new ProviderUsageCardWindow(ProviderIds.Codex, showMark: true, _ => { });
        try
        {
            Assert(AutomationProperties.GetName(window.ModeToggleControl) == LocalizedText.CardMiniModeTooltip,
                "the toggle's name is 'Mini mode' while the card is full");
            Assert(AutomationProperties.GetName(window.MiniRefreshControl) == LocalizedText.CardRefresh,
                "the mini Refresh button's accessible name is Card_Refresh");
            window.ApplyMode(CardMode.Mini);
            Assert(AutomationProperties.GetName(window.ModeToggleControl) == LocalizedText.CardFullModeTooltip,
                "the toggle's name is 'Full mode' after switching to mini");
            window.ApplyMode(CardMode.Full);
            Assert(AutomationProperties.GetName(window.ModeToggleControl) == LocalizedText.CardMiniModeTooltip,
                "the toggle's name follows the mode back to 'Mini mode' after returning to full");
        }
        finally
        {
            window.CloseProgrammatically();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL: {message}");
    }
}

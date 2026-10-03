using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Glideslope.App;
using Glideslope.Core;
using Glideslope.Domain;
using Glideslope.Monitoring;

namespace Glideslope.Shell.Specs;

// The card-size control: the "Aa" button, its flyout slider, the requests it raises, and
// ApplyCardScale's effect on the slider. Governed by window design §18.
internal static partial class CardPresentationProof
{
    /// <summary>
    /// The "Aa" button sits between ↺ and ⚙ at the gear's size with the
    /// "Card size" tooltip and accessible name; its flyout has a vertical 70–150 % slider in 10 % ticks with up
    /// bigger; a step asks the coordinator (CardScaleRequested) and shows the value; ApplyCardScale moves the slider
    /// without asking again and never touches the window's minimum; an off-step value snaps; arrow keys step 10 %; Esc
    /// closes the flyout, and closing after a change raises CardScaleChangeEnded once.
    /// </summary>
    private static void AssertCardSizeControl()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new ProviderUsageCardWindow(ProviderIds.Claude, showMark: true, _ => { });
        var requested = new List<int>();
        var ended = 0;
        void OnRequested(ProviderUsageCardWindow sender, int percent) => requested.Add(percent);
        void OnEnded(object? sender, EventArgs e) => ended++;
        window.CardScaleRequested += OnRequested;
        window.CardScaleChangeEnded += OnEnded;
        try
        {
            window.UpdateState(State(Snapshot(now, [])));
            window.Show();
            using (window.CaptureRenderedFrame() ?? throw new InvalidOperationException("card_size_render_failed")) { }
            var reset = window.BoundsWithinCard(window.ResetSizeControl);
            var size = window.BoundsWithinCard(window.CardSizeControl);
            var gear = window.BoundsWithinCard(window.SettingsControl);
            Assert(reset.Right <= size.Left + 0.5 && size.Right <= gear.Left + 0.5,
                $"the card size button sits between reset size and the gear ({reset} / {size} / {gear})");
            Assert(Math.Abs(size.Width - gear.Width) < 0.5 && Math.Abs(size.Height - gear.Height) < 0.5, "the card size button is the gear's size");
            Assert(ToolTip.GetTip(window.CardSizeControl) as string == "Card size" &&
                   AutomationProperties.GetName(window.CardSizeControl) == "Card size" &&
                   window.CardSizeControl.Content as string == ProviderUsageCardWindow.CardSizeGlyph,
                "the card size button reads Aa with the tooltip and accessible name 'Card size'");
            Assert(!window.CanBeginMoveDragFrom(window.CardSizeControl), "a press on the card size button never starts a card drag");

            var flyout = window.CardSizeFlyout;
            Assert(ReferenceEquals(window.CardSizeControl.Flyout, flyout), "the card size button opens the size flyout");
            flyout.ShowAt(window.CardSizeControl);
            var slider = window.CardSizeSlider;
            Assert(flyout.IsOpen && slider.Orientation == Orientation.Vertical && slider.Minimum == 70 && slider.Maximum == 150 &&
                   slider.TickFrequency == 10 && slider.IsSnapToTickEnabled && slider.Value == 100 && window.CardSizeValueText == "100%",
                $"the flyout shows a vertical 70-150 % slider in 10 % ticks at 100 % (value {slider.Value}, text '{window.CardSizeValueText}')");

            // Up is bigger: the thumb is higher at 150 than at 70.
            double ThumbY()
            {
                (TopLevel.GetTopLevel(slider) as Layoutable)?.UpdateLayout();
                var thumb = slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault()
                    ?? throw new InvalidOperationException("card_size_slider_has_no_thumb");
                return thumb.TranslatePoint(new Point(0, 0), slider)?.Y ?? throw new InvalidOperationException("card_size_thumb_not_connected");
            }
            slider.Value = 150;
            var top = ThumbY();
            slider.Value = 70;
            var bottom = ThumbY();
            Assert(top < bottom, $"dragging up makes the card bigger (thumb y {top:0} at 150 %, {bottom:0} at 70 %)");
            slider.Value = 100;
            Assert(requested.SequenceEqual([150, 70, 100]), $"each step asks the coordinator once (got {string.Join(",", requested)})");
            requested.Clear();

            slider.Value = 110;
            Assert(requested.SequenceEqual([110]) && window.CardSizeValueText == "110%" && window.CardScalePercent == 100,
                "a step asks for 110 % and shows it; the card waits for the coordinator to apply it");
            var minWidth = window.MinWidth;
            var minHeight = window.MinHeight;
            window.ApplyCardScale(110);
            Assert(window.CardScalePercent == 110 && requested.Count == 1 && slider.Value == 110,
                "applying 110 % moves nothing that asks again");
            Assert(window.MinWidth == minWidth && window.MinHeight == minHeight, "the card never changes its own minimum size");

            slider.Value = 93;
            Assert(slider.Value == 90 && requested.SequenceEqual([110, 90]) && window.CardSizeValueText == "90%",
                $"an off-step value snaps to 90 % (value {slider.Value}, asked {string.Join(",", requested)})");
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up, Source = slider });
            Assert(slider.Value == 100 && requested[^1] == 100, $"the Up arrow steps 10 % bigger (value {slider.Value})");
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = slider });
            Assert(slider.Value == 90 && requested[^1] == 90, $"the Down arrow steps 10 % smaller (value {slider.Value})");
            Assert(ended == 0, "nothing ends the change while the flyout is open");
            slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = slider });
            Assert(!flyout.IsOpen && ended == 1, $"Esc closes the flyout, which ends the change once (open {flyout.IsOpen}, ended {ended})");

            flyout.ShowAt(window.CardSizeControl);
            flyout.Hide();
            Assert(ended == 1, "opening and closing the flyout without a change ends nothing");
            window.ApplyCardScale(CardScale.DefaultPercent);
        }
        finally
        {
            window.CardScaleRequested -= OnRequested;
            window.CardScaleChangeEnded -= OnEnded;
            window.CloseProgrammatically();
        }
    }
}

using Avalonia;
using Glideslope.App;
using Glideslope.Core;

namespace Glideslope.Shell.Specs;

/// <summary>Verifies mode application through CardLayoutPlatform with a fake window that enforces minimum sizes.
/// ApplyMode and the new mode's scale must be applied before setting its minimum, or the old minimum can clamp
/// the card and prevent it from reaching the requested mode size.</summary>
internal static class CardModeApplierSpecs
{
    public static void Run()
    {
        FullToMiniEndsAtTheProposedMiniSize();
        MiniToFullEndsAtTheProposedFullSize();
        SplitApplyNeverShowsAnInBetweenSize();
    }

    /// <summary>Applying mode, geometry, then minimum avoids an intermediate size while preserving the final
    /// state produced by CardModeApplier.Apply.</summary>
    private static void SplitApplyNeverShowsAnInBetweenSize()
    {
        var miniSize = CardScale.DefaultSize(CardMode.Mini, 100);
        var fullSize = CardScale.DefaultSize(CardMode.Full, 100);

        var window = new FakeCardModeWindow(miniSize.Width, miniSize.Height, CardScale.MinimumSize(CardMode.Mini, 100));
        window.Sizes.Clear();
        ApplySplitAndGeometry(window, CardMode.Full, 100, fullSize);
        Assert(window.Sizes.SequenceEqual([fullSize]), $"mini to full goes straight to the full size (sizes: {string.Join(" ", window.Sizes)})");
        var fullMinimum = CardScale.MinimumSize(CardMode.Full, 100);
        Assert(window.MinWidth == fullMinimum.Width && window.MinHeight == fullMinimum.Height, "the full minimum is in place afterward");

        window.Sizes.Clear();
        ApplySplitAndGeometry(window, CardMode.Mini, 100, miniSize);
        Assert(window.Sizes.SequenceEqual([miniSize]), $"full to mini goes straight to the mini size (sizes: {string.Join(" ", window.Sizes)})");
        var miniMinimum = CardScale.MinimumSize(CardMode.Mini, 100);
        Assert(window.MinWidth == miniMinimum.Width && window.MinHeight == miniMinimum.Height, "the mini minimum is in place afterward");
    }

    private static void ApplySplitAndGeometry(FakeCardModeWindow window, CardMode mode, int percent, LogicalSize proposedSize)
    {
        var backend = new SingleMonitorGeometryBackend();
        var platform = new CardLayoutPlatform(backend);
        IReadOnlyDictionary<string, ICardLayoutWindow> windows =
            new Dictionary<string, ICardLayoutWindow>(StringComparer.Ordinal) { [window.ProviderId] = window };
        var geometry = platform.Capture(windows);
        var settings = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = window.ProviderId, MonitorId = "primary" }] };
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [window.ProviderId] = new LogicalRect(geometry.Rects[window.ProviderId].X, geometry.Rects[window.ProviderId].Y,
                proposedSize.Width, proposedSize.Height),
        };
        var target = new[] { ((ICardModeWindow)window, mode, percent) };
        CardModeApplier.ApplyBeforeGeometry(target);
        platform.Apply(windows, settings, rects, geometry);
        CardModeApplier.ApplyMinimum(target);
    }

    private static void FullToMiniEndsAtTheProposedMiniSize()
    {
        // A full card initialized at its nominal 940 x 680 geometry with the localized full minimum in place,
        // before its group is switched to mini.
        var window = new FakeCardModeWindow(940, 680, CardScale.MinimumSize(CardMode.Full, 100));
        var miniSize = CardScale.DefaultSize(CardMode.Mini, 100);
        ApplyModeScaleAndGeometry(window, CardMode.Mini, 100, miniSize);

        Assert(window.Calls.SequenceEqual(["mode=Mini", "scale=100"]),
            $"ApplyMode runs before ApplyCardScale (calls: {string.Join(",", window.Calls)})");
        var expectedMinimum = CardScale.MinimumSize(CardMode.Mini, 100);
        Assert(window.MinWidth == expectedMinimum.Width && window.MinHeight == expectedMinimum.Height,
            $"the window's minimum becomes the mini minimum at the new scale (got {window.MinWidth}x{window.MinHeight})");
        Assert(window.Size == miniSize,
            $"a full card going mini ends at exactly the mini default size (got {window.Size}, wanted {miniSize})");
    }

    private static void MiniToFullEndsAtTheProposedFullSize()
    {
        // A mini card at its mini default size, 530 x 270 at 100%, with the mini minimum in place.
        var window = new FakeCardModeWindow(CardLayoutTiers.MiniDefaultWidth, CardLayoutTiers.MiniDefaultHeight,
            CardScale.MinimumSize(CardMode.Mini, 100));
        window.ApplyMode(CardMode.Mini);
        window.Calls.Clear();
        var fullSize = CardScale.DefaultSize(CardMode.Full, 100);
        ApplyModeScaleAndGeometry(window, CardMode.Full, 100, fullSize);

        Assert(window.Calls.SequenceEqual(["mode=Full", "scale=100"]),
            $"ApplyMode runs before ApplyCardScale (calls: {string.Join(",", window.Calls)})");
        var expectedMinimum = CardScale.MinimumSize(CardMode.Full, 100);
        Assert(window.MinWidth == expectedMinimum.Width && window.MinHeight == expectedMinimum.Height,
            $"the window's minimum becomes the full minimum at the new scale (got {window.MinWidth}x{window.MinHeight})");
        Assert(window.Size == fullSize,
            $"a mini card going full ends at exactly the full default size (got {window.Size}, wanted {fullSize})");
    }

    /// <summary>CardModeApplier.Apply (mode, scale, minimum), then the real CardLayoutPlatform.Apply against a
    /// fake single-monitor backend - the coordinator's own next step (WindowCoordinator.ApplyModesAndGeometry
    /// calls CardModeApplier.Apply and then ApplyCardGeometry) - so the size checked at the end is the one the
    /// coordinator would actually leave on screen, not just what CardModeApplier itself touched.</summary>
    private static void ApplyModeScaleAndGeometry(FakeCardModeWindow window, CardMode mode, int percent, LogicalSize proposedSize)
    {
        var backend = new SingleMonitorGeometryBackend();
        var platform = new CardLayoutPlatform(backend);
        IReadOnlyDictionary<string, ICardLayoutWindow> windows =
            new Dictionary<string, ICardLayoutWindow>(StringComparer.Ordinal) { [window.ProviderId] = window };
        var geometry = platform.Capture(windows);
        var settings = new LayoutSettings { Cards = [new CardLayoutSettings { ProviderId = window.ProviderId, MonitorId = "primary" }] };
        var rects = new Dictionary<string, LogicalRect>(StringComparer.Ordinal)
        {
            [window.ProviderId] = new LogicalRect(geometry.Rects[window.ProviderId].X, geometry.Rects[window.ProviderId].Y,
                proposedSize.Width, proposedSize.Height),
        };
        CardModeApplier.Apply([((ICardModeWindow)window, mode, percent)]);
        platform.Apply(windows, settings, rects, geometry);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {message}");
    }

    /// <summary>
    /// A fake that behaves like a real window for the order rule: setting MinWidth/MinHeight above the current
    /// size grows the window at once (the exact behavior CardModeApplier.Apply's comment relies on), and
    /// SetSize (called by CardLayoutPlatform.Apply, standing in for the coordinator's own geometry apply)
    /// clamps a size below the current minimum up to it. Every instance shares the id "card": only one window
    /// is ever needed per spec here.
    /// </summary>
    private sealed class FakeCardModeWindow : ICardModeWindow, ICardLayoutWindow
    {
        private double _width;
        private double _height;
        private double _minWidth;
        private double _minHeight;

        public FakeCardModeWindow(double width, double height, LogicalSize startingMinimum)
        {
            _minWidth = startingMinimum.Width;
            _minHeight = startingMinimum.Height;
            _width = Math.Max(width, _minWidth);
            _height = Math.Max(height, _minHeight);
        }

        public List<string> Calls { get; } = [];
        /// <summary>Every size the window actually took, in order: proves no in-between frame).</summary>
        public List<LogicalSize> Sizes { get; } = [];
        public string ProviderId => "card";
        public object NativeHandle => this;
        public LogicalSize Size => new(_width, _height);

        public double MinWidth
        {
            get => _minWidth;
            set
            {
                _minWidth = value;
                // Models a real window: raising the minimum above the current size grows the window at once.
                if (_width < value) { _width = value; Sizes.Add(Size); }
            }
        }

        public double MinHeight
        {
            get => _minHeight;
            set
            {
                _minHeight = value;
                if (_height < value) { _height = value; Sizes.Add(Size); }
            }
        }

        public void ApplyMode(CardMode mode) => Calls.Add($"mode={mode}");
        public void ApplyCardScale(int percent) => Calls.Add($"scale={percent}");

        public void SetSize(LogicalSize size)
        {
            // Models a real window: a size set below the current minimum is clamped up to it.
            var before = Size;
            _width = Math.Max(size.Width, _minWidth);
            _height = Math.Max(size.Height, _minHeight);
            if (Size != before) Sizes.Add(Size);
        }
    }

    /// <summary>A single 1920 x 1080 monitor at 100%, for driving the real CardLayoutPlatform.Apply.</summary>
    private sealed class SingleMonitorGeometryBackend : ICardWindowGeometryBackend
    {
        private static readonly CardLayoutScreen Screen =
            new("primary", new PhysicalRect(0, 0, 1920, 1080), new PhysicalRect(0, 0, 1920, 1040), 1, true);
        private PixelPoint _position = new(100, 100);

        public IReadOnlyList<CardLayoutScreen> GetScreens(ICardLayoutWindow window) => [Screen];
        public string? GetMonitorId(ICardLayoutWindow window, IReadOnlyList<CardLayoutScreen> screens) => "primary";
        public PixelPoint GetPosition(ICardLayoutWindow window) => _position;
        public LogicalSize GetSize(ICardLayoutWindow window) => ((FakeCardModeWindow)window).Size;
        public void SetPosition(ICardLayoutWindow window, PixelPoint position) => _position = position;
        public void SetSize(ICardLayoutWindow window, LogicalSize size) => ((FakeCardModeWindow)window).SetSize(size);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Glideslope.Core;

namespace Glideslope.App;

// The card's window chrome and pointer handling: docked sides, the magnet bars, which presses may start a
// move-drag, the peek-aware press handling, the Undock button, the resize grips and their cursors.
// Governed by window design §3.3, §5.1, §5.6 and §6, and 2.1 Ctrl peek design §4.1 and §10.4.
internal sealed partial class ProviderUsageCardWindow
{
    /// <summary>Window design §5.6: which of this card's sides are docked, from the coordinator after every
    /// layout change. Non-empty means "in a snapped group" (Ctrl + body-drag releases; Undock button shows).</summary>
    internal void SetDockedSides(IReadOnlySet<CardDockSide> sides)
    {
        _dockedSides = sides;
        if (_undockSide is { } shown && !sides.Contains(shown)) HideUndock();
    }
    private bool InSnappedGroup => _dockedSides.Count > 0;

    /// <summary>The side currently showing a magnet bar, or null when none is shown.</summary>
    internal CardDockSide? CurrentMagnetSide => _magnetSide;
    /// <summary>The magnet bar control for a given side, exposed so a spec can measure its coverage
    /// of the middle 25% of that side (window design §13 Level 2, CardPresentationProof).</summary>
    internal Control MagnetBarFor(CardDockSide side) => side switch
    {
        CardDockSide.Top => _magnetTop,
        CardDockSide.Bottom => _magnetBottom,
        CardDockSide.Left => _magnetLeft,
        _ => _magnetRight,
    };
    /// <summary>Shows the magnet bar on the given side and hides any other side's bar. Window design
    /// §6: the lead shows it on the side facing the candidate, the candidate shows it on its own
    /// facing side - WindowCoordinator calls this once per card, with each card's own facing side.</summary>
    internal void ShowMagnet(CardDockSide side)
    {
        UpdateMagnetBarLengths();
        _magnetSide = side;
        _magnetTop.IsVisible = side == CardDockSide.Top;
        _magnetBottom.IsVisible = side == CardDockSide.Bottom;
        _magnetLeft.IsVisible = side == CardDockSide.Left;
        _magnetRight.IsVisible = side == CardDockSide.Right;
    }
    /// <summary>Hides every magnet bar. Window design §6: clears on a candidate change, on commit, and
    /// on cancel.</summary>
    internal void ClearMagnet()
    {
        _magnetSide = null;
        _magnetTop.IsVisible = false;
        _magnetBottom.IsVisible = false;
        _magnetLeft.IsVisible = false;
        _magnetRight.IsVisible = false;
    }
    private static Border CreateMagnetBar(HorizontalAlignment horizontal, VerticalAlignment vertical) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#FF71D9FF")),
        HorizontalAlignment = horizontal,
        VerticalAlignment = vertical,
        Margin = new Thickness(3),
        IsVisible = false,
        IsHitTestVisible = false,
        Focusable = false,
        ZIndex = GripZIndex + 1,
    };
    /// <summary>Keeps each magnet bar at 4 px thick and the middle 25% of its own side's current
    /// length, since the window's shape is free (window design §3.2) and can change size at any time.</summary>
    private void UpdateMagnetBarLengths()
    {
        const double barThickness = 4;
        const double barCoverage = 0.25;
        var width = _moveDragSurface.Bounds.Width;
        var height = _moveDragSurface.Bounds.Height;
        _magnetTop.Width = width * barCoverage;
        _magnetTop.Height = barThickness;
        _magnetBottom.Width = width * barCoverage;
        _magnetBottom.Height = barThickness;
        _magnetLeft.Width = barThickness;
        _magnetLeft.Height = height * barCoverage;
        _magnetRight.Width = barThickness;
        _magnetRight.Height = height * barCoverage;
    }
    internal bool CanBeginMoveDragFrom(object? source)
    {
        if (source is not Visual visual) return false;
        for (var current = visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Control control &&
                (_resizeGrips.Contains(control) || control is Button or WeeklyHistoryChart or TextBox or ComboBox or ListBox or ScrollViewer))
                return false;
            if (ReferenceEquals(current, _moveDragSurface)) return true;
        }

        return false;
    }

    /// <summary>
    /// Handles the peek-aware part of a body-drag press. Raises PeekInputObserved(anyButton:
    /// true) first - the coordinator's handler can end an active peek synchronously inside that Invoke
    /// (CardPeekController.End sets IsPeeking = false), so <see cref="IsPeeking"/> is captured before raising
    /// it, not after. Returns true when the card was peeking at the moment of the press: a press while peeking
    /// only ends the peek, never starts a move-drag or a Ctrl-detach, so the caller must start neither; the
    /// user presses again, on the now-restored mini card, to drag or detach. Internal so the headless proof can
    /// drive it directly, the same way <see cref="UpdateUndockButton"/> is.
    /// </summary>
    internal bool HandlePeekAwarePress(KeyModifiers modifiers)
    {
        var wasPeeking = IsPeeking;
        PeekInputObserved?.Invoke(this, modifiers, true);
        return wasPeeking;
    }

    /// <summary>
    /// A press anywhere on a peeking card ends the peek and is handled before child controls receive it.
    /// The drag-surface handler also checks peek state for input that reaches it directly.
    /// </summary>
    private void OnWindowTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsPeeking) return;
        HandlePeekAwarePress(e.KeyModifiers);
        e.Handled = true;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (HandlePeekAwarePress(e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CanBeginMoveDragFrom(e.Source)) return;
        if (InSnappedGroup && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            DetachDragStarted?.Invoke(this, EventArgs.Empty);
        else
            MoveDragStarted?.Invoke(this, EventArgs.Empty);
        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        // Report current modifiers and button state so the coordinator can update peek state.
        var properties = e.GetCurrentPoint(_moveDragSurface).Properties;
        var anyButton = properties.IsLeftButtonPressed || properties.IsRightButtonPressed || properties.IsMiddleButtonPressed;
        PeekInputObserved?.Invoke(this, e.KeyModifiers, anyButton);
        var detach = InSnappedGroup && e.KeyModifiers.HasFlag(KeyModifiers.Control) && CanBeginMoveDragFrom(e.Source);
        _moveDragSurface.Cursor = detach ? DragMoveCursor : null;
        UpdateUndockButton(e.GetPosition(_moveDragSurface));
    }

    /// <summary>Window design §5.6. Shows the Undock button when the pointer is near the middle of a docked
    /// edge (or on the button itself), hides it otherwise. Internal so the headless proof can drive it.</summary>
    internal void UpdateUndockButton(Point p)
    {
        // The Undock button is hidden while peeking.
        if (IsPeeking) { HideUndock(); return; }
        var size = _moveDragSurface.Bounds.Size;
        if (_undockSide is { } shown && _undock.IsVisible && _undock.Bounds.Contains(p)) return;
        foreach (var side in _dockedSides)
        {
            var nearEdge = side switch
            {
                CardDockSide.Top => p.Y <= UndockHoverDepth,
                CardDockSide.Bottom => p.Y >= size.Height - UndockHoverDepth,
                CardDockSide.Left => p.X <= UndockHoverDepth,
                _ => p.X >= size.Width - UndockHoverDepth,
            };
            var alongMiddle = side is CardDockSide.Top or CardDockSide.Bottom
                ? Math.Abs(p.X - size.Width / 2) <= size.Width * UndockHoverSpan / 2
                : Math.Abs(p.Y - size.Height / 2) <= size.Height * UndockHoverSpan / 2;
            if (nearEdge && alongMiddle) { ShowUndock(side); return; }
        }
        HideUndock();
    }

    private void ShowUndock(CardDockSide side)
    {
        _undockSide = side;
        _undock.HorizontalAlignment = side switch
        {
            CardDockSide.Left => HorizontalAlignment.Left,
            CardDockSide.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        _undock.VerticalAlignment = side switch
        {
            CardDockSide.Top => VerticalAlignment.Top,
            CardDockSide.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center,
        };
        _undock.Margin = new Thickness(UndockButtonInset);
        _undock.IsVisible = true;
    }

    private void HideUndock()
    {
        _undockSide = null;
        _undock.IsVisible = false;
    }

    private void AddResizeGrip(Grid surface, WindowEdge edge)
    {
        var corner = edge is WindowEdge.NorthWest or WindowEdge.NorthEast or WindowEdge.SouthWest or WindowEdge.SouthEast;
        var horizontalEdge = edge is WindowEdge.North or WindowEdge.South;
        var grip = new Border
        {
            Background = Brushes.Transparent,
            Cursor = CursorForEdge(edge),
            // Explicit z-order keeps resize grips above card content regardless of insertion order.
            ZIndex = GripZIndex,
            Width = corner ? CornerHitSize : horizontalEdge ? double.NaN : EdgeHitSize,
            Height = corner ? CornerHitSize : horizontalEdge ? EdgeHitSize : double.NaN,
            // Edges run the full side between the two corners, so a press anywhere along a side resizes.
            Margin = corner ? default : horizontalEdge ? new Thickness(CornerHitSize, 0) : new Thickness(0, CornerHitSize),
            HorizontalAlignment = edge switch
            {
                WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest => HorizontalAlignment.Left,
                WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Stretch
            },
            VerticalAlignment = edge switch
            {
                WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast => VerticalAlignment.Top,
                WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Stretch
            }
        };
        _resizeGrips.Add(grip);
        _resizeGripEdges.Add(grip, edge);
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            ResizeDragStarted?.Invoke(this, edge);
            BeginResizeDrag(edge, e);
            e.Handled = true;
        };
        surface.Children.Add(grip);
    }

    // One shared Cursor instance per standard type: Cursor does not expose the StandardCursorType it
    // was built from, so production code and CardPresentationProof both read the exact same cached
    // instance from CursorForEdge, letting the spec verify the hit-test-to-cursor mapping by
    // reference instead of duplicating a private lookup.
    private static readonly Cursor TopLeftCornerCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRightCornerCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor BottomLeftCornerCursor = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor BottomRightCornerCursor = new(StandardCursorType.BottomRightCorner);
    private static readonly Cursor SizeNorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor SizeWestEastCursor = new(StandardCursorType.SizeWestEast);

    /// <summary>The standard OS resize cursor for each edge/corner hit zone, kept alongside the hit test it mirrors.</summary>
    internal static Cursor CursorForEdge(WindowEdge edge) => edge switch
    {
        WindowEdge.NorthWest => TopLeftCornerCursor,
        WindowEdge.SouthEast => BottomRightCornerCursor,
        WindowEdge.NorthEast => TopRightCornerCursor,
        WindowEdge.SouthWest => BottomLeftCornerCursor,
        WindowEdge.North or WindowEdge.South => SizeNorthSouthCursor,
        _ => SizeWestEastCursor,
    };
}

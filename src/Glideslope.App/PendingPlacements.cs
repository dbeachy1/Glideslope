using Avalonia;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Tracks requested X11 positions until the window manager reports an echo or adjustment. This lets geometry
/// capture use the requested position while readback is pending. Requests expire because the window manager may
/// leave an already-positioned window unchanged or ignore a move while the user is dragging it.
/// </summary>
internal sealed class PendingPlacements(TimeProvider time)
{
    /// <summary>How long to wait for a window-manager position report before using native geometry.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, (PixelPoint Position, long At)> _requested = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (LogicalSize Size, long At)> _requestedSizes = new(StringComparer.Ordinal);

    /// <summary>Allows half a logical pixel of size difference for DPI rounding.</summary>
    private const double SizeTolerance = 0.5;

    public void RecordSize(string providerId, LogicalSize requested) => _requestedSizes[providerId] = (requested, time.GetTimestamp());

    /// <summary>Classifies a reported size while a request is pending. Equal sizes are echoes and clear the request;
    /// different sizes are adjustments and keep it pending because Avalonia may report width and height separately.
    /// Returns None when no request is pending.</summary>
    public PlacementConfirmation ConfirmSize(string providerId, LogicalSize reported)
    {
        if (!_requestedSizes.TryGetValue(providerId, out var entry)) return PlacementConfirmation.None;
        if (time.GetElapsedTime(entry.At) >= Lifetime)
        {
            _requestedSizes.Remove(providerId);
            return PlacementConfirmation.None;
        }
        if (Math.Abs(entry.Size.Width - reported.Width) <= SizeTolerance &&
            Math.Abs(entry.Size.Height - reported.Height) <= SizeTolerance)
        {
            _requestedSizes.Remove(providerId);
            return PlacementConfirmation.Echo;
        }
        return PlacementConfirmation.Adjusted;
    }

    public void Record(string providerId, PixelPoint requested) => _requested[providerId] = (requested, time.GetTimestamp());

    public bool TryGet(string providerId, out PixelPoint requested)
    {
        requested = default;
        if (!TakeLive(providerId, remove: false, out var entry)) return false;
        requested = entry.Position;
        return true;
    }

    public PlacementConfirmation Confirm(string providerId, PixelPoint reported, out PixelPoint requested)
    {
        requested = reported;
        if (!TakeLive(providerId, remove: true, out var entry)) return PlacementConfirmation.None;
        requested = entry.Position;
        return requested == reported ? PlacementConfirmation.Echo : PlacementConfirmation.Adjusted;
    }

    /// <summary>Finds the card's request if it has not expired; an expired one is forgotten here.</summary>
    private bool TakeLive(string providerId, bool remove, out (PixelPoint Position, long At) entry)
    {
        if (!_requested.TryGetValue(providerId, out entry)) return false;
        var live = time.GetElapsedTime(entry.At) < Lifetime;
        if (remove || !live) _requested.Remove(providerId);
        return live;
    }
}

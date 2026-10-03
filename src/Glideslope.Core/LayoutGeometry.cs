namespace Glideslope.Core;

/// <summary>A positive logical window size supplied or accepted by the desktop host.</summary>
public readonly record struct LogicalSize
{
    public LogicalSize(double width, double height)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
    }

    public double Width { get; }
    public double Height { get; }
}

/// <summary>Logical desktop coordinates and size, independent of Avalonia or a native window API.</summary>
public readonly record struct LogicalRect
{
    public LogicalRect(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        _ = new LogicalSize(width, height);
        if (!double.IsFinite(x + width) || !double.IsFinite(y + height))
            throw new ArgumentOutOfRangeException(nameof(width), "Rectangle edges must remain finite.");
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public LogicalSize Size => new(Width, Height);

    public LogicalRect Translate(double deltaX, double deltaY) => new(X + deltaX, Y + deltaY, Width, Height);
}

public readonly record struct LogicalPoint
{
    public LogicalPoint(double x, double y)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }
}

/// <summary>One monitor's logical work area; native code owns the monitor identity and scale.</summary>
public sealed record LogicalWorkArea
{
    public LogicalWorkArea(string monitorId, LogicalRect bounds, bool isPrimary = false)
    {
        MonitorId = ValidateMonitorId(monitorId);
        Bounds = bounds;
        IsPrimary = isPrimary;
    }

    public string MonitorId { get; }
    public LogicalRect Bounds { get; }
    public bool IsPrimary { get; }

    private static string ValidateMonitorId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Monitor identity must be bounded and contain no control characters.", nameof(value));
        return value;
    }
}

/// <summary>Pixel rectangle as reported by a native platform API.</summary>
public readonly record struct PhysicalRect
{
    public PhysicalRect(int x, int y, int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
}

public readonly record struct PhysicalPoint(int X, int Y);

public static class LayoutDpiTransform
{
    public static LogicalRect ToLogical(PhysicalRect rect, double scale)
    {
        return ToLogical(rect, new PhysicalPoint(0, 0), new LogicalPoint(0, 0), scale);
    }

    /// <summary>Converts monitor-local physical coordinates into logical desktop coordinates.</summary>
    public static LogicalRect ToLogical(PhysicalRect rect, PhysicalPoint physicalMonitorOrigin,
        LogicalPoint logicalMonitorOrigin, double scale)
    {
        ValidateScale(scale);
        return new LogicalRect(
            logicalMonitorOrigin.X + ((double)rect.X - physicalMonitorOrigin.X) / scale,
            logicalMonitorOrigin.Y + ((double)rect.Y - physicalMonitorOrigin.Y) / scale,
            rect.Width / scale,
            rect.Height / scale);
    }

    /// <summary>Rounds the two edges independently so adjacent logical windows share a pixel boundary.</summary>
    public static PhysicalRect ToPhysical(LogicalRect rect, double scale)
    {
        return ToPhysical(rect, new PhysicalPoint(0, 0), new LogicalPoint(0, 0), scale);
    }

    /// <summary>Converts logical desktop coordinates using the selected monitor's origin and scale.</summary>
    public static PhysicalRect ToPhysical(LogicalRect rect, PhysicalPoint physicalMonitorOrigin,
        LogicalPoint logicalMonitorOrigin, double scale)
    {
        ValidateScale(scale);
        var left = Round(physicalMonitorOrigin.X + (rect.X - logicalMonitorOrigin.X) * scale);
        var top = Round(physicalMonitorOrigin.Y + (rect.Y - logicalMonitorOrigin.Y) * scale);
        var right = Round(physicalMonitorOrigin.X + (rect.Right - logicalMonitorOrigin.X) * scale);
        var bottom = Round(physicalMonitorOrigin.Y + (rect.Bottom - logicalMonitorOrigin.Y) * scale);
        return new PhysicalRect(left, top, checked(right - left), checked(bottom - top));
    }

    private static int Round(double value)
    {
        if (!double.IsFinite(value) || value is < int.MinValue or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private static void ValidateScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
    }
}

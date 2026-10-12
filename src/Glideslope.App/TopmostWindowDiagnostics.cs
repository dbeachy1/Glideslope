using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>Observes native topmost style and z-order changes on provider cards.</summary>
internal sealed class TopmostWindowDiagnostics
{
    private const int GWL_EXSTYLE = -20;
    private const uint GW_OWNER = 4;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint WM_WINDOWPOSCHANGED = 0x0047;
    private const uint WM_STYLECHANGING = 0x007C;
    private const uint WM_STYLECHANGED = 0x007D;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const int MaximumStackMethods = 12;

    private readonly Window _window;
    private readonly string _providerId;
    private readonly IDiagnosticSink _diagnostics;
    private uint? _lastObservedStyle;
    private string _nativeSizeState = "unknown";

    internal TopmostWindowDiagnostics(Window window, string providerId, IDiagnosticSink diagnostics)
    {
        _window = window;
        _providerId = providerId;
        _diagnostics = diagnostics;
    }

    /// <summary>Attaches before Show so native creation and taskbar style messages are observed.</summary>
    public static void Attach(Window window, string providerId, IDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!OperatingSystem.IsWindows()) return;

        var observer = new TopmostWindowDiagnostics(window, providerId, diagnostics);
        Win32Properties.AddWndProcHookCallback(window, observer.OnWindowMessage);
    }

    internal IntPtr OnWindowMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0005) _nativeSizeState = SizeState(wParam.ToInt32());

        if (message == WM_STYLECHANGING && wParam.ToInt32() == GWL_EXSTYLE)
        {
            var (oldStyle, newStyle) = ReadStyleChangeOrCurrent(lParam, hWnd);
            ObserveStyleChangeRequest(oldStyle, newStyle, hWnd);
            ObserveStyle(oldStyle, "message=WM_STYLECHANGING", hWnd);
        }
        else if (message == WM_STYLECHANGED && wParam.ToInt32() == GWL_EXSTYLE)
        {
            var (_, newStyle) = ReadStyleChangeOrCurrent(lParam, hWnd);
            ObserveStyle(newStyle, "message=WM_STYLECHANGED", hWnd);
        }
        else if (message is WM_WINDOWPOSCHANGING or WM_WINDOWPOSCHANGED)
        {
            ObserveStyle(ReadExtendedStyle(hWnd), WindowPositionContext(message, lParam), hWnd);
        }

        // This callback is observational. Avalonia and other hooks must receive every native message.
        return IntPtr.Zero;
    }

    internal void ObserveStyle(uint style, string transitionContext, IntPtr hWnd = default)
    {
        if (_lastObservedStyle is not uint previous)
        {
            _lastObservedStyle = style;
            Record("window_topmost_native_snapshot", $"{transitionContext} {Context(style, hWnd)}");
            return;
        }

        _lastObservedStyle = style;
        var wasTopmost = (previous & WS_EX_TOPMOST) != 0;
        var isTopmost = (style & WS_EX_TOPMOST) != 0;
        if (wasTopmost == isTopmost) return;

        var stack = isTopmost ? "" : $" stack={ManagedStack()}";
        Record(isTopmost ? "window_topmost_native_gained" : "window_topmost_native_lost",
            $"{transitionContext} oldExStyle=0x{previous:X8} newExStyle=0x{style:X8} {Context(style, hWnd)}{stack}");
    }

    internal void ObserveStyleChangeRequest(uint oldStyle, uint newStyle, IntPtr hWnd = default)
    {
        if ((oldStyle & WS_EX_TOPMOST) == 0 || (newStyle & WS_EX_TOPMOST) != 0) return;
        Record("window_topmost_style_clear_requested",
            $"message=WM_STYLECHANGING oldExStyle=0x{oldStyle:X8} newExStyle=0x{newStyle:X8} {Context(oldStyle, hWnd)} stack={ManagedStack()}");
    }

    private string Context(uint style, IntPtr hWnd)
    {
        var owner = _window.Owner;
        var ownerType = owner is null ? "none" : owner.GetType().Name;
        var nativeOwner = NativeOwnerContext(hWnd);
        var nativeHandle = hWnd == IntPtr.Zero ? "unavailable" : $"0x{hWnd.ToInt64():X}";
        return $"hwnd={nativeHandle} managedTopmost={_window.Topmost} nativeTopmost={(style & WS_EX_TOPMOST) != 0} " +
            $"taskbarRole={(_window.ShowInTaskbar ? "representative" : "secondary")} owner={ownerType} " +
            $"nativeOwner={nativeOwner} showInTaskbar={_window.ShowInTaskbar} windowState={_window.WindowState} " +
            $"nativeSizeState={_nativeSizeState} exStyle=0x{style:X8}";
    }

    private static string NativeOwnerContext(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "unavailable";
        var owner = GetWindow(hWnd, GW_OWNER);
        return owner == IntPtr.Zero
            ? "none"
            : $"hwnd=0x{owner.ToInt64():X},topmost={(ReadExtendedStyle(owner) & WS_EX_TOPMOST) != 0}";
    }

    private void Record(string code, string status) =>
        _diagnostics.Record(new DiagnosticEvent(code, _providerId, status));

    private static uint ReadExtendedStyle(IntPtr hWnd)
    {
        var value = IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE) : new IntPtr(GetWindowLong32(hWnd, GWL_EXSTYLE));
        return unchecked((uint)value.ToInt64());
    }

    private static (uint OldStyle, uint NewStyle) ReadStyleChange(IntPtr styleStruct) =>
        (unchecked((uint)Marshal.ReadInt32(styleStruct)), unchecked((uint)Marshal.ReadInt32(styleStruct, sizeof(uint))));

    private static (uint OldStyle, uint NewStyle) ReadStyleChangeOrCurrent(IntPtr styleStruct, IntPtr hWnd)
    {
        if (styleStruct != IntPtr.Zero) return ReadStyleChange(styleStruct);
        var currentStyle = ReadExtendedStyle(hWnd);
        return (currentStyle, currentStyle);
    }

    private static string WindowPositionContext(uint message, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return $"message={MessageName(message)}";
        var position = Marshal.PtrToStructure<WindowPosition>(lParam);
        return $"message={MessageName(message)} insertAfter=0x{position.InsertAfter.ToInt64():X} " +
            $"flags={FormatPositionFlags(position.Flags)}";
    }

    private static string FormatPositionFlags(uint flags)
    {
        var names = new List<string>(8);
        if ((flags & SWP_NOSIZE) != 0) names.Add("NOSIZE");
        if ((flags & SWP_NOMOVE) != 0) names.Add("NOMOVE");
        if ((flags & SWP_NOZORDER) != 0) names.Add("NOZORDER");
        if ((flags & SWP_NOACTIVATE) != 0) names.Add("NOACTIVATE");
        if ((flags & SWP_FRAMECHANGED) != 0) names.Add("FRAMECHANGED");
        if ((flags & SWP_SHOWWINDOW) != 0) names.Add("SHOWWINDOW");
        if ((flags & SWP_HIDEWINDOW) != 0) names.Add("HIDEWINDOW");
        if ((flags & SWP_NOOWNERZORDER) != 0) names.Add("NOOWNERZORDER");
        if ((flags & SWP_ASYNCWINDOWPOS) != 0) names.Add("ASYNCWINDOWPOS");
        return names.Count == 0 ? $"0x{flags:X8}" : string.Join('|', names);
    }

    private static string MessageName(uint message) => message switch
    {
        WM_STYLECHANGING => "WM_STYLECHANGING",
        WM_STYLECHANGED => "WM_STYLECHANGED",
        WM_WINDOWPOSCHANGING => "WM_WINDOWPOSCHANGING",
        WM_WINDOWPOSCHANGED => "WM_WINDOWPOSCHANGED",
        _ => $"0x{message:X4}"
    };

    private static string SizeState(int state) => state switch
    {
        0 => "restored",
        1 => "minimized",
        2 => "maximized",
        3 => "maximized_showing",
        4 => "maximized_hiding",
        _ => $"unknown_{state}"
    };

    private static string ManagedStack()
    {
        var methods = new StackTrace(1, fNeedFileInfo: false).GetFrames()
            ?.Select(frame => frame.GetMethod())
            .Where(method => method is not null && method.DeclaringType != typeof(TopmostWindowDiagnostics) &&
                method.DeclaringType?.Namespace?.StartsWith("System", StringComparison.Ordinal) != true)
            .Take(MaximumStackMethods)
            .Select(method => $"{method!.DeclaringType?.Name ?? "?"}.{method.Name}")
            .ToArray();
        return methods is { Length: > 0 } ? string.Join('|', methods) : "unavailable";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);
}

using System.Runtime.InteropServices;
using Avalonia.Threading;
using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Reports the result of the real notification-area registration owned by the app.
/// The event is deliberately separate from <see cref="ITrayService"/> so the
/// shell coordinator can adopt it without making the fallback implementation
/// claim a platform-specific registration guarantee.
/// </summary>
internal sealed class TrayViabilityChangedEventArgs : EventArgs
{
    public TrayViabilityChangedEventArgs(bool isUsable, string reason)
    {
        IsUsable = isUsable;
        Reason = reason;
    }

    public bool IsUsable { get; }
    public string Reason { get; }
}

/// <summary>
/// Narrow Win32 notification-area backend for the one application-owned icon.
/// The caller supplies the icon handle and chooses whether this service owns it.
/// No second icon is created for probing. The receiver is a hidden, unowned
/// top-level tool window because message-only windows do not receive broadcasts
/// such as TaskbarCreated.
/// </summary>
internal sealed class WindowsTrayService : ITrayService
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_STATE = 0x00000008;

    private const uint NIS_HIDDEN = 0x00000001;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint WM_APP = 0x8000;
    private const uint WM_NCCREATE = 0x0081;
    private const uint WM_NCDESTROY = 0x0082;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int GWLP_USERDATA = -21;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WM_NULL = 0x0000;

    // NOTIFYICON_VERSION_4 packs the icon ID into the high 16 bits of the
    // callback lParam, so the registered ID itself must remain ushort-sized.
    private static readonly nint IconWindowId = new(0x4750); // Stable per process class identity.
    private const uint ShowCommand = 1;
    private const uint ExitCommand = 2;

    private readonly IDiagnosticSink _diagnostics;
    private readonly nint _iconHandle;
    private readonly bool _ownsIconHandle;
    private readonly string _className;
    private readonly NativeWindowProcedure _windowProcedure;
    private readonly uint _callbackMessage;
    private readonly uint _taskbarCreatedMessage;
    private readonly nint _moduleHandle;
    private readonly nint _selfHandle;

    private nint _windowHandle;
    private nint _menuHandle;
    private ushort _classAtom;
    private bool _classRegistered;
    private bool _iconAdded;
    private bool _visible;
    private bool _isUsable;
    private bool _disposed;
    private bool _selfHandleReleased;

    public WindowsTrayService(IDiagnosticSink diagnostics, nint iconHandle, bool ownsIconHandle = false)
    {
        _diagnostics = diagnostics;
        _iconHandle = iconHandle;
        _ownsIconHandle = ownsIconHandle;
        _className = $"Glideslope.WindowsTray.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _callbackMessage = WM_APP + 0x371;
        _windowProcedure = WindowProcedure;
        _moduleHandle = OperatingSystem.IsWindows() ? GetModuleHandle(null) : 0;

        if (!OperatingSystem.IsWindows())
        {
            _selfHandle = 0;
            RecordUnknown("platform_unsupported");
            return;
        }

        _selfHandle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_iconHandle == 0)
        {
            RecordUnknown("icon_load_failed");
            ReleaseNativeResources();
            return;
        }

        if (_moduleHandle == 0)
        {
            RecordUnknown("module_handle_failed");
            ReleaseNativeResources();
            return;
        }

        if (_taskbarCreatedMessage == 0)
        {
            RecordUnknown("taskbar_message_failed");
            ReleaseNativeResources();
            return;
        }

        if (!CreateNativeWindow())
        {
            RecordUnknown("window_setup_failed");
            ReleaseNativeResources();
            return;
        }

        TryRegister("initial_register");
    }

    public bool IsUsable => _isUsable;
    internal nint NativeWindowHandle => _windowHandle;
    internal uint TaskbarCreatedMessageForSpecs => _taskbarCreatedMessage;

    public event EventHandler? ShowRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<TrayViabilityChangedEventArgs>? ViabilityChanged;

    public void RefreshLocalizedText()
    {
        // Native labels are created from LocalizedText when the shell icon is registered.
    }

    public void SetVisible(bool visible)
    {
        if (_disposed || !OperatingSystem.IsWindows()) return;
        _visible = visible;

        if (!_iconAdded)
        {
            if (visible) TryRegister("visible_register");
            return;
        }

        var data = CreateNotifyIconData(NIF_STATE);
        data.dwStateMask = NIS_HIDDEN;
        data.dwState = visible ? 0u : NIS_HIDDEN;
        if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
        {
            _iconAdded = false;
            SetUsable(false, "visibility_update_failed");
            _diagnostics.Record(new DiagnosticEvent("tray_registration_lost", Status: "visibility_update_failed"));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        SetUsable(false, "disposed");
        ReleaseNativeResources();
        _diagnostics.Record(new DiagnosticEvent("tray_disposed", Status: "native_resources_released"));
        return ValueTask.CompletedTask;
    }

    private bool CreateNativeWindow()
    {
        var classInfo = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            hInstance = _moduleHandle,
            lpszClassName = _className,
        };
        _classAtom = RegisterClassEx(ref classInfo);
        if (_classAtom == 0) return false;
        _classRegistered = true;

        _menuHandle = CreatePopupMenu();
        if (_menuHandle == 0 || !AppendMenu(_menuHandle, MF_STRING, ShowCommand, LocalizedText.TrayShow) ||
            !AppendMenu(_menuHandle, MF_SEPARATOR, 0, null) ||
            !AppendMenu(_menuHandle, MF_STRING, ExitCommand, LocalizedText.TrayExit))
        {
            return false;
        }

        _windowHandle = CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            _className,
            LocalizedText.TrayToolTip,
            WS_POPUP,
            0,
            0,
            0,
            0,
            0,
            0,
            _moduleHandle,
            _selfHandle);
        return _windowHandle != 0;
    }

    private void TryRegister(string reason)
    {
        if (_disposed || _windowHandle == 0 || _iconHandle == 0 || _iconAdded) return;

        var data = CreateNotifyIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_STATE);
        data.dwStateMask = NIS_HIDDEN;
        data.dwState = _visible ? 0u : NIS_HIDDEN;
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            _diagnostics.Record(new DiagnosticEvent("tray_registration_failed", Status: "shell_rejected_icon"));
            SetUsable(false, reason);
            return;
        }

        _iconAdded = true;
        data.uFlags = 0;
        data.Version.uVersion = NOTIFYICON_VERSION_4;
        var versionSucceeded = Shell_NotifyIcon(NIM_SETVERSION, ref data);
        if (!RegistrationConfirmed(true, versionSucceeded))
        {
            _ = Shell_NotifyIcon(NIM_DELETE, ref data);
            _iconAdded = false;
            SetUsable(false, "version_negotiation_failed");
            _diagnostics.Record(new DiagnosticEvent("tray_registration_failed", Status: "version_negotiation_failed"));
            return;
        }

        SetUsable(true, reason);
        _diagnostics.Record(new DiagnosticEvent("tray_registration_verified", Status: "shell_accepted_icon"));
    }

    private NOTIFYICONDATA CreateNotifyIconData(uint flags)
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _windowHandle,
            uID = unchecked((uint)IconWindowId.ToInt64()),
            uFlags = flags,
            uCallbackMessage = _callbackMessage,
            hIcon = _iconHandle,
            szTip = LocalizedText.TrayToolTip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    private void HandleTaskbarCreated()
    {
        if (_disposed) return;
        // Re-register before reporting loss so an Explorer restart does not reveal cards when the icon returns.
        var wasAdded = _iconAdded;
        if (_iconAdded)
        {
            var data = CreateNotifyIconData(0);
            var deleted = Shell_NotifyIcon(NIM_DELETE, ref data);
            _diagnostics.Record(new DiagnosticEvent("tray_stale_icon_delete", Status: deleted ? "deleted" : "already_gone"));
        }

        _iconAdded = false;
        _diagnostics.Record(new DiagnosticEvent("tray_taskbar_recreated", Status: $"re_registering,was_added={wasAdded},visible={_visible}"));
        TryRegister("taskbar_re_registration");
    }

    private void HandleTrayCallback(nint lParam)
    {
        if (!TryDecodeCallback(unchecked((uint)lParam.ToInt64()), out var callback))
        {
            return;
        }

        switch (callback)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                PostShowRequested();
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
    }

    private static bool TryDecodeCallback(uint packed, out uint callback)
    {
        var iconId = (packed >> 16) & 0xFFFF;
        var expectedIconId = unchecked((uint)IconWindowId.ToInt64()) & 0xFFFF;
        callback = packed & 0xFFFF;
        return iconId == expectedIconId;
    }

    internal static int NotifyIconDataSizeForSpecs => Marshal.SizeOf<NOTIFYICONDATA>();
    internal static int NotifyIconVersionOffsetForSpecs => (int)Marshal.OffsetOf<NOTIFYICONDATA>(nameof(NOTIFYICONDATA.Version));
    internal static uint IconIdForSpecs => unchecked((uint)IconWindowId.ToInt64());
    internal static bool DecodeCallbackForSpecs(uint packed, out uint callback) => TryDecodeCallback(packed, out callback);
    internal static bool RegistrationConfirmedForSpecs(bool addSucceeded, bool versionSucceeded) => RegistrationConfirmed(addSucceeded, versionSucceeded);

    private static bool RegistrationConfirmed(bool addSucceeded, bool versionSucceeded) => addSucceeded && versionSucceeded;

    private void ShowMenu()
    {
        if (_menuHandle == 0 || _windowHandle == 0 || _disposed) return;
        if (!GetCursorPos(out var point)) return;
        _ = SetForegroundWindow(_windowHandle);
        _ = TrackPopupMenuEx(_menuHandle, TPM_RIGHTBUTTON, point.X, point.Y, _windowHandle, 0);
        _ = PostMessage(_windowHandle, WM_NULL, 0, 0);
    }

    private void PostShowRequested()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) ShowRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private void PostExitRequested()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) ExitRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private void SetUsable(bool usable, string reason)
    {
        if (_isUsable == usable) return;
        _isUsable = usable;
        try
        {
            ViabilityChanged?.Invoke(this, new TrayViabilityChangedEventArgs(usable, reason));
        }
        catch (Exception)
        {
            _diagnostics.Record(new DiagnosticEvent("tray_viability_handler_failed", Status: "handler_exception"));
        }
    }

    private void RecordUnknown(string status)
    {
        _diagnostics.Record(new DiagnosticEvent("tray_viability_unproven", Status: status));
    }

    private nint WindowProcedure(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (message == WM_NCCREATE)
        {
            var create = Marshal.PtrToStructure<CREATESTRUCT>(lParam);
            SetWindowLongPtr(hWnd, GWLP_USERDATA, create.lpCreateParams);
        }

        var userData = GetWindowLongPtr(hWnd, GWLP_USERDATA);
        if (userData != 0 && message != WM_NCDESTROY)
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is WindowsTrayService service)
            {
                if (message == service._taskbarCreatedMessage)
                {
                    service.HandleTaskbarCreated();
                    return 0;
                }

                if (message == service._callbackMessage)
                {
                    service.HandleTrayCallback(lParam);
                    return 0;
                }

                if (message == WM_COMMAND)
                {
                    var command = unchecked((uint)wParam.ToInt64()) & 0xFFFF;
                    if (command == ShowCommand) service.PostShowRequested();
                    else if (command == ExitCommand) service.PostExitRequested();
                    return 0;
                }
            }
        }

        if (message == WM_NCDESTROY)
        {
            var handle = GetWindowLongPtr(hWnd, GWLP_USERDATA);
            if (handle != 0)
            {
                SetWindowLongPtr(hWnd, GWLP_USERDATA, 0);
                var self = GCHandle.FromIntPtr(handle);
                var service = self.Target as WindowsTrayService;
                self.Free();
                if (service is not null)
                    service._selfHandleReleased = true;
            }
        }

        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void ReleaseNativeResources()
    {
        if (!OperatingSystem.IsWindows()) return;

        if (_iconAdded && _windowHandle != 0)
        {
            var data = CreateNotifyIconData(0);
            _ = Shell_NotifyIcon(NIM_DELETE, ref data);
            _iconAdded = false;
        }

        if (_windowHandle != 0)
        {
            var window = _windowHandle;
            _windowHandle = 0;
            _ = DestroyWindow(window);
        }

        if (_menuHandle != 0)
        {
            _ = DestroyMenu(_menuHandle);
            _menuHandle = 0;
        }

        if (_classRegistered)
        {
            _ = UnregisterClass(_className, _moduleHandle);
            _classRegistered = false;
        }

        if (_selfHandle != 0 && !_selfHandleReleased)
        {
            var self = GCHandle.FromIntPtr(_selfHandle);
            if (self.IsAllocated) self.Free();
            _selfHandleReleased = true;
        }

        if (_ownsIconHandle && _iconHandle != 0)
            _ = DestroyIcon(_iconHandle);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint NativeWindowProcedure(nint hWnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCT
    {
        public nint lpCreateParams;
        public nint hInstance;
        public nint hMenu;
        public nint hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public nint lpszName;
        public nint lpszClass;
        public uint dwExStyle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public NotifyIconVersion Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NotifyIconVersion
    {
        [FieldOffset(0)] public uint uTimeout;
        [FieldOffset(0)] public uint uVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(nint menu, uint flags, uint identifier, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
}

using System.Runtime.InteropServices;

namespace Glideslope.NativeProof;

/// <summary>
/// Raw Win32 declarations for the native window proof. They cover desktop creation,
/// process launch, window enumeration, and the
/// WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE envelope. The harness targets a hidden desktop; see
/// HiddenDesktopSession for that verification.
/// </summary>
internal static class NativeMethods
{
    internal const uint GENERIC_ALL = 0x10000000;
    internal const uint DESKTOP_CREATEWINDOW = 0x0002;
    internal const uint DESKTOP_ENUMERATE = 0x0040;
    internal const uint DESKTOP_WRITEOBJECTS = 0x0080;
    internal const uint DESKTOP_READOBJECTS = 0x0001;
    internal const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    internal const uint DF_ALLOWOTHERACCOUNTHOOK = 0x0001;
    internal const int UOI_NAME = 2;

    internal const uint STARTF_USESHOWWINDOW = 0x00000001;
    internal const int SW_SHOWNORMAL = 1;
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    internal const uint INFINITE = 0xFFFFFFFF;
    internal const uint STILL_ACTIVE = 259;
    internal const uint WAIT_OBJECT_0 = 0;

    // WaitForInputIdle signals that the child created its message queue and is waiting for input.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint WaitForInputIdle(nint hProcess, uint dwMilliseconds);

    internal const int WM_NULL = 0x0000;
    internal const int WM_ENTERSIZEMOVE = 0x0231;
    internal const int WM_EXITSIZEMOVE = 0x0232;
    internal const int WM_ACTIVATE = 0x0006;
    // WM_ACTIVATE's wParam low word: BringToFrontScenario posts this directly to a card's HWND as a
    // message-based activation signal a hidden, non-input desktop can still deliver, when
    // SetForegroundWindow's OS-level handoff silently does nothing (see that scenario's remarks).
    internal const nint WA_ACTIVE = 1;
    internal const int WM_CLOSE = 0x0010;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal static readonly nint HWND_TOP = 0;
    internal static readonly nint HWND_TOPMOST = -1;

    // Window design "Level 3 waits: signals, not timers" item 7: GestureDriver's real signal for
    // "the card's message queue has drained the WM_WINDOWPOSCHANGED this step just posted", replacing the
    // Thread.Sleep(60) pump between steps.
    internal const uint SMTO_NORMAL = 0x0000;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessageTimeoutW(nint hWnd, int msg, nint wParam, nint lParam, uint fuFlags,
        uint uTimeout, out nint lpdwResult);

    internal const uint SPI_GETWORKAREA = 0x0030;

    /// <summary>Per-monitor-V2 DPI awareness, matching the real app's Avalonia manifest, so
    /// SystemParametersInfo/GetWindowRect coordinates line up with the ones the app itself sees.</summary>
    internal static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(nint value);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateDesktopW(string lpszDesktop, nint lpszDevice, nint pDevmode,
        uint dwFlags, uint dwDesiredAccess, nint lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseDesktop(nint hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetUserObjectInformationW(nint hObj, int nIndex, nint pvInfo, uint nLength, out uint lpnLengthNeeded);

    internal delegate bool EnumDesktopWindowsDelegate(nint hwnd, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumDesktopWindows(nint hDesktop, EnumDesktopWindowsDelegate lpfn, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessW(
        string? lpApplicationName,
        System.Text.StringBuilder lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(nint hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(nint hObject);

    // --- Helper window support (BringToFrontScenario's z-order marker) ---

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetThreadDesktop(nint hDesktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandleW(string? lpModuleName);

    internal delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
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
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool UnregisterClassW(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    internal const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    internal const uint WS_VISIBLE = 0x10000000;
    internal const int WM_DESTROY = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);
}

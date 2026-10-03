using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Glideslope.NativeProof;

/// <summary>Records the child's desktop and whether it differs from the visible input desktop. Also records
/// whether the startup message-queue signal fired before the thread desktop was inspected.</summary>
internal sealed record DesktopVerification(string HiddenDesktopName, string ChildThreadDesktopName, string? InputDesktopName, bool ChildIsOnHiddenDesktop, bool HiddenDesktopIsNotInputDesktop, bool ChildBecameInputIdle);

/// <summary>
/// Owns one hidden Win32 desktop and one child process launched on it for the native window proof.
/// SendInput only reaches the
/// desktop the user is looking at, which is exactly why this harness never uses it; everything here
/// (CreateDesktop, STARTUPINFO.lpDesktop, SendMessage, SetWindowPos) is desktop-handle- or
/// message-based and works against a desktop nobody is displaying.
/// </summary>
internal sealed class HiddenDesktopSession : IDisposable
{
    private nint _desktop;
    private nint _processHandle;
    private nint _threadHandle;
    private bool _processStarted;
    private bool _desktopCreated;

    public string DesktopName { get; }
    public uint ProcessId { get; private set; }
    public uint MainThreadId { get; private set; }

    /// <summary>The raw HDESK, exposed for the EnumDesktopWindows/window-creation calls in
    /// CardWindowLocator and HelperMarkerWindow that need it directly.</summary>
    public nint DesktopHandle => _desktop;

    public HiddenDesktopSession(string desktopName)
    {
        DesktopName = desktopName;
    }

    /// <summary>Creates the hidden desktop. Returns null on success, or a short reason code the caller
    /// reports as part of native_desktop_unavailable.</summary>
    public string? CreateDesktop()
    {
        // Desktop rights the child process needs to create and enumerate its own windows, plus the
        // rights this process needs to enumerate and close it again. No SWITCHDESKTOP right is
        // requested anywhere in this proof: the hidden desktop is never made the visible one.
        const uint access = NativeMethods.GENERIC_ALL;
        _desktop = NativeMethods.CreateDesktopW(DesktopName, 0, 0, 0, access, 0);
        if (_desktop == 0)
        {
            var error = Marshal.GetLastWin32Error();
            return $"create_desktop_failed_win32_{error}";
        }

        _desktopCreated = true;
        return null;
    }

    /// <summary>Launches <paramref name="exePath"/> with <paramref name="arguments"/> on the hidden
    /// desktop by qualifying STARTUPINFO.lpDesktop with the calling process's own window station name
    /// (e.g. "WinSta0\GlideslopeProof-xxxx"), rather than relying on an unqualified desktop name being
    /// resolved against the right window station.</summary>
    public string? LaunchProcess(string exePath, string arguments)
    {
        if (!_desktopCreated) return "launch_before_desktop_created";
        if (!File.Exists(exePath)) return $"app_executable_missing_{exePath}";

        var windowStationName = ReadWindowStationName();
        if (windowStationName is null) return "window_station_name_unavailable";
        var qualifiedDesktop = $"{windowStationName}\\{DesktopName}";

        var startupInfo = new NativeMethods.STARTUPINFOW
        {
            cb = Marshal.SizeOf<NativeMethods.STARTUPINFOW>(),
            lpDesktop = Marshal.StringToHGlobalUni(qualifiedDesktop),
            dwFlags = NativeMethods.STARTF_USESHOWWINDOW,
            wShowWindow = NativeMethods.SW_SHOWNORMAL,
        };

        var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");
        try
        {
            var created = NativeMethods.CreateProcessW(
                exePath,
                commandLine,
                0,
                0,
                bInheritHandles: false,
                dwCreationFlags: NativeMethods.CREATE_NEW_CONSOLE,
                lpEnvironment: 0,
                lpCurrentDirectory: null,
                ref startupInfo,
                out var processInformation);
            if (!created)
            {
                var error = Marshal.GetLastWin32Error();
                return $"create_process_failed_win32_{error}";
            }

            _processHandle = processInformation.hProcess;
            _threadHandle = processInformation.hThread;
            ProcessId = processInformation.dwProcessId;
            MainThreadId = processInformation.dwThreadId;
            _processStarted = true;
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(startupInfo.lpDesktop);
        }
    }

    /// <summary>Verifies by desktop name that the child runs on the hidden desktop and that this desktop is
    /// distinct from the visible input desktop.</summary>
    /// <summary>Hang watchdog for the child's GUI message-queue startup signal.</summary>
    private static readonly TimeSpan ChildInputIdleHangWatchdog = TimeSpan.FromSeconds(60);

    public DesktopVerification VerifyChildIsHidden()
    {
        // GetThreadDesktop can return 0 for a brief window right after CreateProcess returns, before
        // the child's main thread has initialized its GUI message queue (observed against notepad.exe
        // during this proof's own smoke test). WaitForInputIdle is the OS's own signal for exactly that
        // condition (the process has a message queue and is idle, waiting for input), so this waits on it
        // once - a hang watchdog, not a deadline - and then reads GetThreadDesktop exactly once.
        var idleWaitResult = NativeMethods.WaitForInputIdle(_processHandle, (uint)ChildInputIdleHangWatchdog.TotalMilliseconds);
        var childBecameInputIdle = idleWaitResult == NativeMethods.WAIT_OBJECT_0;

        var childDesktopHandle = NativeMethods.GetThreadDesktop(MainThreadId);
        var childDesktopName = childDesktopHandle == 0 ? "<unavailable>" : ReadObjectName(childDesktopHandle) ?? "<unreadable>";

        var inputDesktopHandle = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS);
        string? inputDesktopName = null;
        if (inputDesktopHandle != 0)
        {
            inputDesktopName = ReadObjectName(inputDesktopHandle);
            NativeMethods.CloseDesktop(inputDesktopHandle);
        }

        var childIsHidden = string.Equals(childDesktopName, DesktopName, StringComparison.Ordinal);
        var hiddenIsNotInput = inputDesktopName is null ||
            !string.Equals(inputDesktopName, DesktopName, StringComparison.OrdinalIgnoreCase);
        return new DesktopVerification(DesktopName, childDesktopName, inputDesktopName, childIsHidden, hiddenIsNotInput, childBecameInputIdle);
    }

    /// <summary>Kills only the process this session started, matched by the exact handle/PID captured
    /// at launch (never by image name), waits for exit, closes handles, and closes the desktop. Returns
    /// which of the three cleanup steps succeeded so the caller can assert all three are gone.</summary>
    public (bool ProcessGone, bool DesktopClosed) Cleanup(TimeSpan waitForExit)
    {
        var processGone = true;
        if (_processStarted && _processHandle != 0)
        {
            if (NativeMethods.GetExitCodeProcess(_processHandle, out var exitCode) && exitCode == NativeMethods.STILL_ACTIVE)
                NativeMethods.TerminateProcess(_processHandle, unchecked((uint)-1));
            var waitResult = NativeMethods.WaitForSingleObject(_processHandle, (uint)waitForExit.TotalMilliseconds);
            processGone = waitResult == NativeMethods.WAIT_OBJECT_0;
            NativeMethods.CloseHandle(_processHandle);
            _processHandle = 0;
        }

        if (_threadHandle != 0)
        {
            NativeMethods.CloseHandle(_threadHandle);
            _threadHandle = 0;
        }

        var desktopClosed = true;
        if (_desktopCreated && _desktop != 0)
        {
            desktopClosed = NativeMethods.CloseDesktop(_desktop);
            _desktop = 0;
            _desktopCreated = false;
        }

        return (processGone, desktopClosed);
    }

    private static string? ReadWindowStationName()
    {
        var handle = NativeMethods.GetProcessWindowStation();
        return handle == 0 ? null : ReadObjectName(handle);
    }

    private static string? ReadObjectName(nint handle)
    {
        // First call with a zero-length buffer to discover the required size, matching the standard
        // GetUserObjectInformationW two-call pattern (there is no fixed maximum desktop/window-station
        // name length to guess at).
        NativeMethods.GetUserObjectInformationW(handle, NativeMethods.UOI_NAME, 0, 0, out var neededBytes);
        if (neededBytes == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)neededBytes);
        try
        {
            if (!NativeMethods.GetUserObjectInformationW(handle, NativeMethods.UOI_NAME, buffer, neededBytes, out _))
                return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        Cleanup(TimeSpan.FromSeconds(5));
    }
}

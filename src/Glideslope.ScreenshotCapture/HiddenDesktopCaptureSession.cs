using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Glideslope.ScreenshotCapture;

internal sealed class HiddenDesktopCaptureSession
{
    private const string DesktopName = "Codex-Sandbox";
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopCreateWindow = 0x0002;
    private const uint DesktopEnumerate = 0x0040;
    private const uint DesktopWriteObjects = 0x0080;
    private const uint CaptureDesktopAccess = DesktopReadObjects | DesktopCreateWindow | DesktopEnumerate | DesktopWriteObjects;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseShowWindow = 0x0001;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x0102;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint ChildTimeoutMilliseconds = 120_000;
    private const uint ChildTimeoutExitCode = 124;
    private static readonly EnumDesktopProc EnumDesktopCallback = CollectDesktopName;
    private static List<string>? _namesDuringEnumeration;

    public int Run(CaptureOptions options)
    {
        options.EnsureNoUnapprovedOverwrite();
        EnsureInteractiveWindowStation();
        var before = CodexDesktopNames();
        EnsureAllowedInventory(before, options.AllowExistingRuntimeDesktops);
        var runtimeDesktopBaseline = RuntimeDesktopNames(before);
        var hadTarget = before.Any(name => string.Equals(name, DesktopName, StringComparison.Ordinal));

        IntPtr desktop = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;
        Process? child = null;
        string? childPath = null;
        DateTimeOffset childStart = default;
        uint childPid = 0;
        var createdDesktop = false;
        var sessionRoot = options.UseLiveReads
            ? Path.Combine(AppContext.BaseDirectory, $".capture-session-{Guid.NewGuid():N}")
            : null;
        if (sessionRoot is not null && (Directory.Exists(sessionRoot) || File.Exists(sessionRoot)))
            throw new IOException("The unique capture session path already exists.");
        try
        {
            desktop = OpenDesktop(DesktopName, 0, false, CaptureDesktopAccess);
            if (desktop == IntPtr.Zero)
            {
                if (hadTarget)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The existing Codex-Sandbox desktop could not be opened.");
                desktop = CreateDesktop(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, CaptureDesktopAccess, IntPtr.Zero);
                if (desktop == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create Codex-Sandbox.");
                createdDesktop = true;
            }

            EnsureAllowedInventory(CodexDesktopNames(), options.AllowExistingRuntimeDesktops);
            var executablePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Glideslope.ScreenshotCapture.exe"));
            if (!File.Exists(executablePath)) throw new FileNotFoundException("The compiled screenshot capture apphost is missing.");
            Directory.CreateDirectory(options.OutputDirectory);

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = $"WinSta0\\{DesktopName}",
                dwFlags = StartfUseShowWindow,
                wShowWindow = 0,
            };
            var commandLine = new StringBuilder($"{Quote(executablePath)} --child {Quote(options.OutputDirectory)}{(options.Overwrite ? " --overwrite" : string.Empty)}{(options.UseLiveReads ? " --live" : string.Empty)}{(options.UseRecordedHistory ? " --use-recorded-history" : string.Empty)}{(sessionRoot is null ? string.Empty : $" --session-root {Quote(sessionRoot)}")}");
            if (!CreateProcess(executablePath, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow,
                    IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not launch the capture child on Codex-Sandbox.");

            processHandle = processInfo.hProcess;
            threadHandle = processInfo.hThread;
            childPid = processInfo.dwProcessId;
            child = Process.GetProcessById(checked((int)childPid));
            childPath = Path.GetFullPath(child.MainModule?.FileName
                ?? throw new InvalidOperationException("Could not verify the capture child's executable path."));
            childStart = child.StartTime.ToUniversalTime();
            if (!PathEquals(childPath, executablePath))
                throw new InvalidOperationException("The started child does not match the screenshot capture apphost.");

            var wait = WaitForSingleObject(processHandle, ChildTimeoutMilliseconds);
            if (wait == WaitTimeout)
            {
                StopVerifiedChild(child, childPid, childPath, childStart, processHandle);
                throw new TimeoutException("Capture exceeded the 120-second limit; the verified child was stopped and awaited.");
            }
            if (wait != WaitObject0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Waiting for the screenshot capture child failed.");
            if (!GetExitCodeProcess(processHandle, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the screenshot capture child exit code.");
            if (exitCode != 0)
                throw new CaptureChildException(exitCode);

            Console.WriteLine("Capture child completed; the named desktop will be checked after handle cleanup.");
            return 0;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (processHandle != IntPtr.Zero && WaitForSingleObject(processHandle, 0) == WaitTimeout)
            {
                try
                {
                    if (child is null || childPath is null)
                        throw new InvalidOperationException("The owned capture process is running but its identity could not be verified.");
                    StopVerifiedChild(child, childPid, childPath, childStart, processHandle);
                }
                catch (Exception ex) { cleanupFailure = ex; }
            }

            child?.Dispose();
            if (sessionRoot is not null &&
                (processHandle == IntPtr.Zero || WaitForSingleObject(processHandle, 0) == WaitObject0))
            {
                try { LiveUsageReader.DeleteOwnedSessionDirectory(sessionRoot); }
                catch (Exception ex) { cleanupFailure ??= ex; }
            }
            if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);

            var after = CodexDesktopNames();
            EnsureAllowedInventory(after, options.AllowExistingRuntimeDesktops);
            var runtimeDesktopAfter = RuntimeDesktopNames(after);
            var expectedCount = createdDesktop ? 0 : (hadTarget ? 1 : 0);
            if (after.Count(name => name == DesktopName) != expectedCount && cleanupFailure is null)
                cleanupFailure = new InvalidOperationException($"Codex-Sandbox count changed unexpectedly (before {before.Count(name => name == DesktopName)}, after {after.Count(name => name == DesktopName)}).");
            if (!runtimeDesktopBaseline.SequenceEqual(runtimeDesktopAfter, StringComparer.Ordinal) && cleanupFailure is null)
                cleanupFailure = new InvalidOperationException("The pre-existing Codex runtime-desktop inventory changed during capture.");
            if (cleanupFailure is not null)
                throw new InvalidOperationException("Screenshot capture cleanup did not complete cleanly.", cleanupFailure);
            Console.WriteLine($"Codex desktop inventory verified: before={before.Count}, after={after.Count}.");
        }
    }

    private static void StopVerifiedChild(Process child, uint pid, string executablePath, DateTimeOffset startTime, IntPtr processHandle)
    {
        if (child.Id != checked((int)pid) || !PathEquals(Path.GetFullPath(child.MainModule?.FileName ?? string.Empty), executablePath) ||
            child.StartTime.ToUniversalTime() != startTime)
            throw new InvalidOperationException("The capture process no longer matches its recorded PID, executable path, and start time.");
        if (!TerminateProcess(processHandle, ChildTimeoutExitCode) || WaitForSingleObject(processHandle, Infinite) != WaitObject0)
            throw new InvalidOperationException("The verified capture child did not stop after termination.");
    }

    private static void EnsureInteractiveWindowStation()
    {
        var station = GetProcessWindowStation();
        if (station == IntPtr.Zero || !string.Equals(ReadObjectName(station), "WinSta0", StringComparison.Ordinal))
            throw new InvalidOperationException("Run the screenshot command from the interactive WinSta0 session.");
    }

    private static List<string> CodexDesktopNames()
    {
        var station = GetProcessWindowStation();
        if (station == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not get the current window station.");
        _namesDuringEnumeration = [];
        try
        {
            if (!EnumDesktops(station, EnumDesktopCallback, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate desktops on WinSta0.");
            return _namesDuringEnumeration;
        }
        finally { _namesDuringEnumeration = null; }
    }

    private static bool CollectDesktopName(string name, IntPtr parameter)
    {
        if (name.Contains("Codex", StringComparison.OrdinalIgnoreCase))
            _namesDuringEnumeration?.Add(name);
        return true;
    }

    private static void EnsureAllowedInventory(IReadOnlyList<string> names, bool allowExistingRuntimeDesktops)
    {
        var unexpected = names.Where(name => !string.Equals(name, DesktopName, StringComparison.Ordinal) &&
            !(allowExistingRuntimeDesktops && name.StartsWith("CodexSandboxDesktop-", StringComparison.Ordinal))).ToArray();
        var targetCount = names.Count(name => string.Equals(name, DesktopName, StringComparison.Ordinal));
        if (targetCount > 1 || unexpected.Length > 0)
        {
            var detail = names.Count == 0 ? "none" : string.Join(", ", names);
            throw new InvalidOperationException($"Capture found an unsupported Codex-desktop inventory ({names.Count}): {detail}. Use --allow-existing-runtime-desktops only for known CodexSandboxDesktop-* runtime names, or resolve the inventory before retrying.");
        }
    }

    private static string[] RuntimeDesktopNames(IReadOnlyList<string> names) => names
        .Where(name => name.StartsWith("CodexSandboxDesktop-", StringComparison.Ordinal))
        .OrderBy(name => name, StringComparer.Ordinal).ToArray();

    public static void VerifyCaptureDesktop()
    {
        var current = GetThreadDesktop(GetCurrentThreadId());
        var input = OpenInputDesktop(0, false, DesktopReadObjects);
        try
        {
            var currentName = ReadObjectName(current);
            var inputName = ReadObjectName(input);
            if (!string.Equals(currentName, DesktopName, StringComparison.Ordinal) ||
                string.Equals(currentName, inputName, StringComparison.Ordinal))
                throw new InvalidOperationException("The capture child is not isolated on Codex-Sandbox.");
            var names = CodexDesktopNames();
            if (names.Count(name => string.Equals(name, DesktopName, StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException("Codex-Sandbox is not uniquely registered.");
        }
        finally { CloseDesktop(input); }
    }

    private static string ReadObjectName(IntPtr handle)
    {
        GetUserObjectInformation(handle, 2, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query the window-station name.");
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetUserObjectInformation(handle, 2, buffer, needed, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the window-station name.");
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate bool EnumDesktopProc([MarshalAs(UnmanagedType.LPWStr)] string desktopName, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenDesktop(string desktopName, uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktop(string desktopName, IntPtr device, IntPtr devMode, uint flags, uint desiredAccess, IntPtr securityAttributes);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "CloseDesktop")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumDesktopsW")] private static extern bool EnumDesktops(IntPtr station, EnumDesktopProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetUserObjectInformationW")] private static extern bool GetUserObjectInformation(IntPtr handle, int index, IntPtr information, int length, out int needed);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    private sealed class CaptureChildException(uint exitCode) : Exception(
        exitCode switch
        {
            CaptureApplication.NoHistoryDatabase => "No usage-history database exists. Run Glideslope and refresh the providers first.",
            CaptureApplication.MissingProviderHistory => "Recorded weekly usage is missing for one or more requested providers. Refresh those providers and retry.",
            CaptureApplication.HistoryReadFailed => "The recorded usage database could not be read in read-only mode.",
            _ => $"Capture child failed with code {exitCode}. Check the child exit code and retry.",
        });
}

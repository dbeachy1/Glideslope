using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Glideslope.Shell.Specs;

/// <summary>Read-only count of Codex-named desktops on the interactive window station.</summary>
internal static class DesktopInventoryProof
{
    private const uint WindowStationEnumerateDesktops = 0x0001;
    private static readonly EnumDesktopProcedure Callback = CollectDesktopName;
    private static readonly List<string> CodexDesktopNames = [];

    public static int Run()
    {
        try { return RunCore(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Desktop inventory failed: {ex.GetType().Name}.");
            return 1;
        }
    }

    private static int RunCore()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Desktop inventory is supported on Windows only.");
            return 2;
        }

        CodexDesktopNames.Clear();
        using var station = new WindowStationHandle(OpenWindowStation("WinSta0", inherit: false, WindowStationEnumerateDesktops));
        if (station.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open WinSta0 for desktop enumeration.");
        if (!EnumDesktops(station, Callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate WinSta0 desktops.");

        var unexpected = CodexDesktopNames.Where(name => !string.Equals(name, "Codex-Sandbox", StringComparison.Ordinal)).ToArray();
        var sandboxCount = CodexDesktopNames.Count(name => string.Equals(name, "Codex-Sandbox", StringComparison.Ordinal));
        Console.WriteLine($"Codex desktop count: {CodexDesktopNames.Count}");
        Console.WriteLine($"Codex-Sandbox count: {sandboxCount}");
        foreach (var name in CodexDesktopNames)
            Console.WriteLine($"Codex desktop: {name}");
        if (unexpected.Length > 0 || sandboxCount > 1)
        {
            Console.Error.WriteLine("Unexpected Codex desktop names were found.");
            return 1;
        }
        return 0;
    }

    private static bool CollectDesktopName(string desktopName, IntPtr parameter)
    {
        if (desktopName.Contains("Codex", StringComparison.OrdinalIgnoreCase))
            CodexDesktopNames.Add(desktopName);
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate bool EnumDesktopProcedure([MarshalAs(UnmanagedType.LPWStr)] string desktopName, IntPtr parameter);

    private sealed class WindowStationHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public WindowStationHandle(IntPtr value) : base(ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseWindowStation(handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenWindowStationW")]
    private static extern IntPtr OpenWindowStation(string stationName, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumDesktopsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktops(WindowStationHandle station, EnumDesktopProcedure callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseWindowStation(IntPtr handle);
}

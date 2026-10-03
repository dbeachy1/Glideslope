using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Glideslope.Core;

// Where the Windows start-at-sign-in entry is kept: the Run value store interface, the real store
// (the HKCU Run and StartupApproved registry values) and the file store used under a proof root and
// by the specs, so they never touch the user's registry. Used by PlatformStartupRegistration.

/// <summary>One value read from the Windows Run key. <see cref="Data"/> is null when the value exists but
/// is not a plain string (REG_SZ), a shape this app never writes.</summary>
internal readonly record struct WindowsRunValue(bool Exists, string? Data);

/// <summary>
/// Where the Windows start-at-sign-in entry lives. The real store is
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run plus the matching Explorer\StartupApproved\Run value;
/// the headless command under a proof root, the packaging proof and the specs use
/// <see cref="FileWindowsRunValueStore"/>, so they never touch the user's registry.
/// </summary>
internal interface IWindowsRunValueStore
{
    WindowsRunValue ReadRunValue(string name);
    void WriteRunValue(string name, string data);
    bool DeleteRunValue(string name);
    byte[]? ReadApprovalValue(string name);
    bool DeleteApprovalValue(string name);

    /// <summary>The StartupApproved\StartupFolder approval value for a
    /// retired Startup-folder artifact, keyed by its file name (e.g. "Glideslope.startup.cmd"), or null when
    /// absent. Same binary shape as <see cref="ReadApprovalValue"/> (first byte's low bit: off when set).
    /// Read-only: nothing writes or deletes this value anywhere in this app; Windows owns it exclusively.</summary>
    byte[]? ReadStartupFolderApprovalValue(string fileName);

    /// <summary>Where the Run value lives: a file under the proof root, or the registry path (which carries
    /// no user-profile path).</summary>
    string Describe(string name);

    /// <summary>Every file this store may create or delete for <paramref name="name"/>; empty for the registry.</summary>
    IReadOnlyList<string> FileLocations(string name);
}

/// <summary>The real Windows store, HKCU only. Values are read without expanding
/// environment names, and a value that is not REG_SZ is reported with null data (not ours).</summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryWindowsRunValueStore : IWindowsRunValueStore
{
    public WindowsRunValue ReadRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PlatformStartupRegistration.RunKeyPath, writable: false);
        if (key is null || !HasValue(key, name))
            return new WindowsRunValue(false, null);

        var kind = key.GetValueKind(name);
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new WindowsRunValue(true, kind == RegistryValueKind.String ? raw as string : null);
    }

    public void WriteRunValue(string name, string data)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PlatformStartupRegistration.RunKeyPath, writable: true);
        key.SetValue(name, data, RegistryValueKind.String);
    }

    public bool DeleteRunValue(string name) => DeleteValue(PlatformStartupRegistration.RunKeyPath, name);

    public byte[]? ReadApprovalValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PlatformStartupRegistration.StartupApprovedRunKeyPath, writable: false);
        return key?.GetValue(name) as byte[];
    }

    // Same read for the StartupFolder key family; opened read-only, since
    // this value is never written or deleted from this store.
    public byte[]? ReadStartupFolderApprovalValue(string fileName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PlatformStartupRegistration.StartupApprovedStartupFolderKeyPath, writable: false);
        return key?.GetValue(fileName) as byte[];
    }

    public bool DeleteApprovalValue(string name) => DeleteValue(PlatformStartupRegistration.StartupApprovedRunKeyPath, name);

    public string Describe(string name) => $"HKCU\\{PlatformStartupRegistration.RunKeyPath}\\{name}";

    public IReadOnlyList<string> FileLocations(string name) => [];

    private static bool DeleteValue(string keyPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key is null || !HasValue(key, name))
            return false;

        key.DeleteValue(name, throwOnMissingValue: false);
        return true;
    }

    private static bool HasValue(RegistryKey key, string name) =>
        key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Keeps the Run value (UTF-8 text, exactly the REG_SZ data) and its
/// startup-approval value (raw bytes) as files under a proof root, so the headless command, the packaging
/// proof and the specs run the Windows rules without touching the registry.
/// Also keeps the retired Startup-folder entries' own approval value the
/// same way, one file per Startup-folder file name, read-only.</summary>
internal sealed class FileWindowsRunValueStore(string root) : IWindowsRunValueStore
{
    internal const string RunDirectoryName = "HKCU-Run";
    internal const string ApprovalDirectoryName = "HKCU-StartupApproved-Run";

    // Sibling directory for the StartupFolder approval value.
    internal const string StartupFolderApprovalDirectoryName = "HKCU-StartupApproved-StartupFolder";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public WindowsRunValue ReadRunValue(string name)
    {
        var path = RunPath(name);
        return File.Exists(path) ? new WindowsRunValue(true, File.ReadAllText(path, Utf8NoBom)) : new WindowsRunValue(false, null);
    }

    public void WriteRunValue(string name, string data)
    {
        var path = RunPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, data, Utf8NoBom);
    }

    public bool DeleteRunValue(string name) => DeleteFile(RunPath(name));

    public byte[]? ReadApprovalValue(string name)
    {
        var path = ApprovalPath(name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // Same encoding as the Run approval file: raw bytes, one file per
    // value name, never written from this store.
    public byte[]? ReadStartupFolderApprovalValue(string fileName)
    {
        var path = StartupFolderApprovalPath(fileName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public bool DeleteApprovalValue(string name) => DeleteFile(ApprovalPath(name));

    public string Describe(string name) => RunPath(name);

    public IReadOnlyList<string> FileLocations(string name) => [RunPath(name), ApprovalPath(name), StartupFolderApprovalPath(name)];

    private string RunPath(string name) => Path.Combine(root, RunDirectoryName, name + ".txt");

    private string ApprovalPath(string name) => Path.Combine(root, ApprovalDirectoryName, name + ".bin");

    private string StartupFolderApprovalPath(string fileName) => Path.Combine(root, StartupFolderApprovalDirectoryName, fileName + ".bin");

    private static bool DeleteFile(string path)
    {
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }
}

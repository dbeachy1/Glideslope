using System.Security;

namespace Glideslope.Core;

public sealed record StartupRegistrationState(bool IsRegistered, string? IssueCode = null);

public interface IStartupRegistration
{
    Task<StartupRegistrationState> ReadAsync(CancellationToken cancellationToken = default);
    Task<StartupRegistrationState> SetAsync(bool enabled, string launcherPath, CancellationToken cancellationToken = default);

    /// <summary>Migrates a pre-rename start-at-sign-in artifact this app owns to the new name, once
    /// per launch. See <see cref="PlatformStartupRegistration.ReconcileLegacyArtifactAsync"/>.</summary>
    /// <remarks>On Windows this also moves the retired Startup-folder .cmd entries
    /// (the Glideslope and the GlidePath name) to the per-user Run value.</remarks>
    Task<StartupRegistrationState> ReconcileLegacyArtifactAsync(string? launcherPath, CancellationToken cancellationToken = default);
}

/// <summary>Everything <see cref="PlatformStartupRegistration"/> takes from the environment, fixed at
/// construction so specs can run the Windows and the Linux rules on either host without touching the
/// user's real startup state.</summary>
/// <param name="Windows">Selects the Windows rules (Run value, retired .cmd migration) or the Linux rules
/// (.desktop entry in the XDG autostart folder).</param>
/// <param name="StartupFolder">Windows: the folder holding the retired .cmd entries; empty when unknown.</param>
/// <param name="RunValues">Windows: the Run value store.</param>
/// <param name="LinuxArtifactPath">Linux: the .desktop entry this app writes.</param>
/// <param name="LinuxLegacyArtifactPath">Linux: the pre-rename GlidePath .desktop entry.</param>
/// <param name="SelfLauncherPath">Windows, a build that is not an installed copy: its own apphost (the
/// window design §16.1 fallback). The startup reconcile may move a retired .cmd entry that starts this same exe
/// to the Run value; it never re-points an entry at anything else.</param>
internal sealed record StartupRegistrationOptions(
    bool Windows,
    string StartupFolder,
    IWindowsRunValueStore? RunValues,
    string LinuxArtifactPath,
    string LinuxLegacyArtifactPath,
    string? SelfLauncherPath);

/// <summary>
/// Start at sign-in. Linux uses a .desktop entry in the XDG autostart folder. Windows uses the per-user Run value
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Glideslope = "&lt;installed Glideslope.App.exe&gt;" --autostart</c>.
/// Until 1.1 the Windows entry was a batch file in the Startup folder that ran glideslope-launcher.cmd.
/// A batch file waits for the program it starts, so its console window (with a taskbar button) stayed open
/// for the app's whole life, against Requirements §4 ("automatic startup is tray-only"); and cmd.exe reads a
/// batch file in the OEM code page and expands "%", so an install path such as "Zoë" or "100%" silently broke
/// sign-in while ReadAsync still reported the entry as owned. The Run value starts the GUI exe directly with
/// no console, and a REG_SZ value is neither re-encoded nor expanded.
/// </summary>
/// <remarks>
/// Ownership: only an entry this app wrote is ever changed. A Run
/// value named "Glideslope" is ours only when its data is exactly <c>"&lt;absolute path&gt;\Glideslope.App.exe" --autostart</c>;
/// anything else under that name is left alone and reported as not owned. A retired .cmd entry is ours only
/// in the exact four-line shape this app wrote. A build that is not an installed copy (no installer marker
/// beside its exe) never re-points an entry that starts an installed copy.
/// </remarks>
public sealed partial class PlatformStartupRegistration : IStartupRegistration
{
    /// <summary>The Run value name; also the name Windows shows in its startup-apps list.</summary>
    public const string WindowsRunValueName = "Glideslope";

    /// <summary>The only file name a Windows Run value of ours may start.</summary>
    public const string WindowsAppFileName = "Glideslope.App.exe";

    /// <summary>The Windows installer puts this file beside Glideslope.App.exe.
    /// Its presence is what makes that exe the installed launcher; a build run from its output folder has
    /// none, and so can neither reconcile at startup nor re-point an entry that starts an installed copy.</summary>
    public const string WindowsInstalledMarkerFileName = "glideslope-installed.marker";

    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    // Same key family as StartupApprovedRunKeyPath, one binary value per
    // Startup-folder file name. This is where Windows records that the user turned a Startup-folder entry
    // off in Task Manager or Settings > Apps > Startup, before 1.1 ever migrated it to the Run value.
    internal const string StartupApprovedStartupFolderKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private const string RunValueSuffix = "\" --autostart";

    // Retired Windows launcher beside the exe. A retired .cmd entry that
    // started it belongs to the exe in the same folder.
    private const string RetiredWindowsLauncherName = "glideslope-launcher.cmd";

    // Retired Windows Startup-folder entry format. The managed marker, bundle-cache setting, and --autostart
    // command identify entries that can be migrated to the Run value and removed.
    private const string WindowsArtifactName = "Glideslope.startup.cmd";
    private const string LinuxArtifactName = "Glideslope.desktop";
    private const string LinuxLauncherFileName = "glideslope-launcher.sh";
    private const string ManagedMarker = "X-Glideslope-Managed=true";
    private const string CacheBundleSegment = "Glideslope\\cache\\bundle";
    private const string DesktopEntryName = "Glideslope";

    // Legacy artifact identity. Kept so an upgrade can find and
    // remove the old-named entry it owns during ReconcileLegacyArtifactAsync; new artifacts are never
    // written in this old shape again.
    private const string LegacyWindowsArtifactName = "GlidePath.startup.cmd";
    private const string LegacyLinuxArtifactName = "GlidePath.desktop";
    private const string LegacyManagedMarker = "X-GlidePath-Managed=true";
    private const string LegacyCacheBundleSegment = "GlidePath\\cache\\bundle";
    private const string LegacyDesktopEntryName = "Glide Path";

    private readonly IDiagnosticSink _diagnostics;
    private readonly StartupRegistrationOptions _options;

    private enum ArtifactKind
    {
        Missing,
        Owned,
        Stale,
        NotOwned,
    }

    private readonly record struct ArtifactInspection(ArtifactKind Kind, string? LauncherPath);

    private readonly record struct StartupFolderArtifact(string FilePath, string NameKind, ArtifactKind Kind, string LauncherPath);

    /// <summary>Uses the real per-user location, or the proof root when one is configured. Tests pass explicit
    /// <see cref="StartupRegistrationOptions"/> so no spec can reach the real Run key or Startup folder.</summary>
    /// <param name="diagnostics">Where decisions are logged (never with a path).</param>
    /// <param name="selfLauncherPath">See <see cref="StartupRegistrationOptions.SelfLauncherPath"/>.</param>
    public PlatformStartupRegistration(IDiagnosticSink? diagnostics = null, string? selfLauncherPath = null)
        : this(diagnostics, CreateEnvironmentOptions(selfLauncherPath))
    {
    }

    internal PlatformStartupRegistration(IDiagnosticSink? diagnostics, StartupRegistrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Windows && options.RunValues is null)
            throw new ArgumentException("windows_run_value_store_required", nameof(options));
        _diagnostics = diagnostics ?? new NullDiagnosticSink();
        _options = options;
    }

    private IWindowsRunValueStore RunValues => _options.RunValues!;

    /// <summary>Returns where this service reads and writes the registration, without touching it: on
    /// Windows the Run value (a file under the proof root, otherwise the registry path), on Linux the
    /// .desktop file.</summary>
    public string ResolveArtifactPath() => _options.Windows ? RunValues.Describe(WindowsRunValueName) : _options.LinuxArtifactPath;

    /// <summary>Every file-system path this service may create or delete. The headless installer command
    /// checks each one stays inside a proof root.</summary>
    public IReadOnlyList<string> ResolveFileLocations()
    {
        if (!_options.Windows)
            return [_options.LinuxArtifactPath, _options.LinuxLegacyArtifactPath];

        var locations = new List<string>(RunValues.FileLocations(WindowsRunValueName));
        if (!string.IsNullOrEmpty(_options.StartupFolder))
        {
            locations.Add(Path.Combine(_options.StartupFolder, WindowsArtifactName));
            locations.Add(Path.Combine(_options.StartupFolder, LegacyWindowsArtifactName));
        }

        return locations;
    }

    public async Task<StartupRegistrationState> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return _options.Windows ? ReadWindows() : await ReadLinuxAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            _diagnostics.Record(new DiagnosticEvent("startup_registration_io_error", Status: $"startup_read_failed,type={ex.GetType().Name}"));
            return new StartupRegistrationState(false, "startup_registration_io_error");
        }
    }

    /// <summary>
    /// Removes the legacy start-at-sign-in artifact if this app still
    /// owns it, then writes the new-named artifact so the user keeps exactly one entry, under the new
    /// name, carrying forward the enabled state the old artifact represented. A foreign file at the
    /// legacy path (one this app never wrote, per the same ownership check <see cref="ReadAsync"/> uses)
    /// is left untouched. Call this once per launch, after the single-instance ownership check succeeds,
    /// so two concurrent processes cannot race to remove/rewrite the same files; an old-version instance
    /// already running during an upgrade will not observe this reconciliation; the next launch that owns the
    /// instance performs it.
    /// </summary>
    /// <remarks>
    /// On Windows, the retired Startup-folder .cmd entries (both the Glideslope and the
    /// GlidePath name, owned or stale) are what gets migrated, to the Run value. The writer is the installed
    /// exe (<paramref name="launcherPath"/>); with none, a build that is not an installed copy may still move a
    /// .cmd that starts its own exe (<see cref="StartupRegistrationOptions.SelfLauncherPath"/>), because that
    /// changes nothing about what starts at sign-in except the console window. Anything else is kept.
    /// The migration also checks whether the user had already turned that
    /// .cmd entry off in Windows (StartupApproved\StartupFolder); when every artifact the new Run value would
    /// replace was off, only the leftover .cmd files are removed and the Run value is never written, so the
    /// user's choice survives migration.
    /// </remarks>
    public async Task<StartupRegistrationState> ReconcileLegacyArtifactAsync(string? launcherPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return _options.Windows
                ? await ReconcileWindowsAsync(launcherPath, cancellationToken).ConfigureAwait(false)
                : await ReconcileLinuxAsync(launcherPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            _diagnostics.Record(new DiagnosticEvent("startup_registration_io_error", Status: $"legacy_reconcile_failed,type={ex.GetType().Name}"));
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StartupRegistrationState> SetAsync(bool enabled, string launcherPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return _options.Windows
                ? await SetWindowsAsync(enabled, launcherPath, cancellationToken).ConfigureAwait(false)
                : await SetLinuxAsync(enabled, launcherPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            _diagnostics.Record(new DiagnosticEvent("startup_registration_io_error", Status: $"startup_registration_failed,type={ex.GetType().Name}"));
            return new StartupRegistrationState(false, "startup_registration_io_error");
        }
    }

    /// <summary>True for an existing Glideslope.App.exe with the installer's marker beside it (Windows), or
    /// the packaged glideslope-launcher.sh (Linux).</summary>
    internal bool IsInstalledLauncher(string? path)
    {
        if (!IsExistingFile(path))
            return false;

        var fileName = Path.GetFileName(path);
        if (!_options.Windows)
            return string.Equals(fileName, LinuxLauncherFileName, StringComparison.Ordinal);

        var directory = Path.GetDirectoryName(path);
        return string.Equals(fileName, WindowsAppFileName, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrEmpty(directory) &&
               IsExistingFile(Path.Combine(directory, WindowsInstalledMarkerFileName));
    }

    // ---- Shared ownership rules --------------------------------------------------------------------

    /// <summary>
    /// A build that is not an installed copy (the apphost fallback, a Debug
    /// build) never re-points an entry that starts an existing installed copy. Settings in that build still
    /// shows start at sign-in as on, because it is. An entry that starts nothing (stale) or another
    /// non-installed build may be replaced.
    /// </summary>
    private bool KeepsInstalledEntry(ArtifactInspection existing, string launcherPath)
    {
        if (existing.Kind != ArtifactKind.Owned || existing.LauncherPath is null)
            return false;
        if (SamePath(existing.LauncherPath, launcherPath))
            return false;
        if (!IsInstalledLauncher(existing.LauncherPath) || IsInstalledLauncher(launcherPath))
            return false;

        _diagnostics.Record(new DiagnosticEvent("startup_registration_kept_installed_entry", Status: "caller_not_installed"));
        return true;
    }

    private bool SamePath(string left, string right)
    {
        try
        {
            var comparison = _options.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _diagnostics.Record(new DiagnosticEvent("startup_path_compare_failed", Status: ex.GetType().Name));
            return false;
        }
    }

    private static bool IsExistingFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) && !Directory.Exists(path);

    private static bool IsStorageException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException;

    private static string KindName(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Missing => "missing",
        ArtifactKind.Owned => "owned",
        ArtifactKind.Stale => "stale",
        _ => "not_owned",
    };

    private async Task<ArtifactInspection> InspectExistingArtifactAsync(string path, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
            return new ArtifactInspection(ArtifactKind.NotOwned, null);
        if (!File.Exists(path))
            return new ArtifactInspection(ArtifactKind.Missing, null);

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return InspectArtifact(content, windows: false);
    }

    private static ArtifactInspection InspectArtifact(string content, bool windows) =>
        InspectArtifact(content, windows, ManagedMarker, CacheBundleSegment, DesktopEntryName);

    // Recognizes only the pre-rename artifact shape; used solely by ReconcileLegacyArtifactAsync to
    // find and remove an entry this app itself wrote under the old name, including when startup is disabled.
    private static ArtifactInspection InspectLegacyArtifact(string content, bool windows) =>
        InspectArtifact(content, windows, LegacyManagedMarker, LegacyCacheBundleSegment, LegacyDesktopEntryName);

    private static ArtifactInspection InspectArtifact(string content, bool windows, string managedMarker, string cacheBundleSegment, string desktopEntryName)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];

        var launcherPath = windows
            ? ParseWindowsArtifact(lines, managedMarker, cacheBundleSegment)
            : ParseLinuxArtifact(lines, managedMarker, desktopEntryName);
        if (launcherPath is null)
            return new ArtifactInspection(ArtifactKind.NotOwned, null);

        return new ArtifactInspection(
            File.Exists(launcherPath) && !Directory.Exists(launcherPath) ? ArtifactKind.Owned : ArtifactKind.Stale,
            launcherPath);
    }

    private static StartupRegistrationOptions CreateEnvironmentOptions(string? selfLauncherPath)
    {
        var windows = OperatingSystem.IsWindows();
        var proofRoot = ProofRoot.Current ?? Environment.GetEnvironmentVariable("GLIDESLOPE_PROOF_ROOT");
        if (!string.IsNullOrWhiteSpace(proofRoot))
        {
            // Under a proof root the Run value is a file, so a proof never writes the user's registry.
            var startup = Path.Combine(Path.GetFullPath(proofRoot), "startup");
            return new StartupRegistrationOptions(
                windows,
                startup,
                windows ? new FileWindowsRunValueStore(startup) : null,
                Path.Combine(startup, LinuxArtifactName),
                Path.Combine(startup, LegacyLinuxArtifactName),
                selfLauncherPath);
        }

        if (OperatingSystem.IsWindows())
        {
            return new StartupRegistrationOptions(
                true,
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                new RegistryWindowsRunValueStore(),
                string.Empty,
                string.Empty,
                selfLauncherPath);
        }

        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var autostart = Path.Combine(config, "autostart");
        return new StartupRegistrationOptions(
            false,
            string.Empty,
            null,
            Path.Combine(autostart, LinuxArtifactName),
            Path.Combine(autostart, LegacyLinuxArtifactName),
            selfLauncherPath);
    }
}

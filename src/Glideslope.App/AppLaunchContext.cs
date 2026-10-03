using Glideslope.Core;
using System.Globalization;

namespace Glideslope.App;

internal sealed class AppLaunchContext
{
    public required InstanceBroker Broker { get; init; }

    public required ActivationIntent Intent { get; init; }

    public required StartupActivationRouter ActivationRouter { get; init; }

    public required IUserPathProvider Paths { get; init; }

    public required IDiagnosticSink Diagnostics { get; init; }

    public required CultureInfo CapturedUiCulture { get; init; }
    public required CultureInfo CapturedFormatCulture { get; init; }
    public required IReadOnlyDictionary<string, string?> LocaleEnvironment { get; init; }

    /// <summary>The path an explicit Settings save registers for start at sign-in: the installed launcher,
    /// or the running apphost when there is none (design doc §16.1).</summary>
    public required string? LauncherPath { get; init; }

    /// <summary>The installed launcher only, or null. Startup reconciliation must not point an existing sign-in
    /// entry at a build in a scratch folder.</summary>
    public required string? InstalledLauncherPath { get; init; }

    /// <summary>
    /// The running apphost when this is not an installed copy, else null. On Windows, startup reconciliation
    /// may use it only to move a retired Startup-folder .cmd
    /// entry that already starts this same exe (written by the §16.1 fallback) to the Run value, which ends
    /// that entry's console window without changing what starts at sign-in. It never re-points an entry at
    /// this build, so the scratch-folder rule above still holds.
    /// </summary>
    public string? SelfLauncherPath => InstalledLauncherPath is null ? LauncherPath : null;
}

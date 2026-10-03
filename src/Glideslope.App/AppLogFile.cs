using Glideslope.Core;

namespace Glideslope.App;

/// <summary>
/// Shared log path used by the file sink and the Settings window.
/// </summary>
internal static class AppLogFile
{
    public const string FileName = "glideslope.log";

    public static string PathFor(UserPaths paths) => Path.Combine(paths.LogsDirectory, FileName);
}

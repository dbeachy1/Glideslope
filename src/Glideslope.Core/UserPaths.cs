using System.Security.Cryptography;
using System.Text;

namespace Glideslope.Core;

/// <param name="RuntimeFallbackDirectory">Set only on Linux when XDG_RUNTIME_DIR
/// is unset, which puts <paramref name="RuntimeDirectory"/> at a predictable name in the shared system temp
/// directory. The instance broker then uses RuntimeDirectory only if it and its parent are real directories
/// this user owns with mode 0700, and otherwise this per-user directory under the cache root.</param>
public sealed record UserPaths(string SettingsFile, string DataDirectory, string RuntimeDirectory, string CacheDirectory,
    string LogsDirectory, string? RuntimeFallbackDirectory = null);
public interface IUserPathProvider { UserPaths Get(); }

public sealed class DefaultUserPathProvider : IUserPathProvider
{
    /// <summary>Current per-user directory name used for settings, data, cache, and runtime paths.</summary>
    internal const string ProductDirectoryName = "Glideslope";

    /// <summary>Legacy product directory name where settings and history lived
    /// here before the product was renamed; see <see cref="UserDataMigration"/>, which moves them into
    /// the current <see cref="ProductDirectoryName"/> locations on first launch after upgrade.</summary>
    internal const string LegacyProductDirectoryName = "GlidePath";

    public UserPaths Get() => BuildPaths(ProductDirectoryName);

    /// <summary>
    /// Builds the same per-user path layout for an arbitrary product directory name. Used both for the
    /// current Glideslope paths (<see cref="Get"/>) and, by <see cref="UserDataMigration"/>, to locate the
    /// pre-rename GlidePath paths under the identical platform rules so the two layouts line up exactly.
    /// </summary>
    internal static UserPaths BuildPaths(string productDirectoryName)
    {
        var proofRoot = ProofRoot.Current ?? Environment.GetEnvironmentVariable("GLIDESLOPE_PROOF_ROOT");
        if (!string.IsNullOrWhiteSpace(proofRoot))
        {
            var root = Path.GetFullPath(proofRoot);
            return new UserPaths(
                Path.Combine(root, "config", productDirectoryName, "settings.json"),
                Path.Combine(root, "data", productDirectoryName),
                Path.Combine(root, "runtime", productDirectoryName),
                Path.Combine(root, "cache", productDirectoryName),
                Path.Combine(root, "logs", productDirectoryName));
        }

        if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), productDirectoryName);
            return new UserPaths(
                Path.Combine(root, "settings.json"),
                root,
                Path.Combine(root, "runtime"),
                Path.Combine(root, "cache"),
                Path.Combine(root, "logs"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(home, ".config");
        }

        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data))
        {
            data = Path.Combine(home, ".local", "share");
        }

        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrWhiteSpace(cache))
        {
            cache = Path.Combine(home, ".cache");
        }

        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? runtimeFallback = null;
        if (string.IsNullOrWhiteSpace(runtime))
        {
            runtime = Path.Combine(Path.GetTempPath(), $"{productDirectoryName}-runtime-{UserRuntimeSuffix()}");
            // The shared-temp name above is predictable, so another user can
            // create it first. The broker checks it is private to this user and otherwise uses this
            // per-user directory, which is the same for every launch of this user (single instance holds).
            runtimeFallback = Path.Combine(cache, productDirectoryName, "runtime");
        }

        // XDG_STATE_HOME is the standard per-user location for persistent diagnostic state on Linux;
        // when unset, use
        // its documented default (~/.local/state), consistent with config, data, and cache paths.
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(state))
        {
            state = Path.Combine(home, ".local", "state");
        }

        return new UserPaths(
            Path.Combine(config, productDirectoryName, "settings.json"),
            Path.Combine(data, productDirectoryName),
            Path.Combine(runtime, productDirectoryName),
            Path.Combine(cache, productDirectoryName),
            Path.Combine(state, productDirectoryName, "logs"),
            runtimeFallback);
    }

    private static string UserRuntimeSuffix()
    {
        var userName = Environment.UserName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userName)).AsSpan(0, 8));
    }
}

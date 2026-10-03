using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glideslope.Providers;

/// <summary>
/// Describes how to launch a located CLI. <see cref="FileName"/> is started with
/// <see cref="LeadingArguments"/> before the CLI's own fixed arguments. <see cref="Kind"/> is "native" (an
/// executable run as is) or "npm" (node running the package's entry script). <see cref="VersionKey"/> identifies
/// the installed build (see <see cref="CliVersionKey"/>), or is null when its file metadata could not be read.
/// </summary>
public sealed record CliLaunchTarget(string FileName, ImmutableArray<string> LeadingArguments, string Kind, string? VersionKey)
{
    internal static CliLaunchTarget Native(string executable, string providerId, IProviderDiagnosticSink? diagnostics) =>
        new(executable, [], "native", CliVersionKey.TryCompute([executable], providerId, diagnostics));
}

/// <summary>
/// Identifies an installed CLI build without running it: SHA-256 over each file's
/// final link target path, size and last-write time. A native installer replaces the executable (and on Linux
/// repoints ~/.local/bin/claude at a new versions/ file); npm rewrites the package's entry script and
/// package.json. Only the first 12 hex digits ever reach the log.
/// </summary>
internal static class CliVersionKey
{
    public static string? TryCompute(IReadOnlyList<string> files, string providerId, IProviderDiagnosticSink? diagnostics)
    {
        try
        {
            var material = new StringBuilder();
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                var target = info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? info;
                target.Refresh();
                material.Append(target.FullName).Append('|')
                    .Append(target.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(target.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics?.Record(new ProviderDiagnostic("cli_version_key_unavailable", providerId, exception.GetType().Name, 0));
            return null;
        }
    }

    public static string Short(string key) => key.Length <= 12 ? key : key[..12];
}

/// <summary>
/// Resolves an npm CLI shim without invoking its .cmd file. On Windows, npm's global install puts claude.cmd / codex.cmd in the prefix
/// directory (%APPDATA%\npm by default) and the package under node_modules beside it. Running the .cmd would put
/// cmd.exe between Glideslope and the CLI. cmd re-parses the command line by its own rules (commas, carets,
/// percent signs), which Process.ArgumentList does not quote for, and the Claude usage arguments include
/// "project,local": an argument split there could become a prompt, which spends quota. So the shim is never
/// executed. node runs the entry script named by the package's own package.json "bin" field, the fixed argument
/// list passes straight through, and a process-tree kill covers node and whatever the CLI starts. A package whose
/// "bin" is a native .exe runs that directly. Any other layout is reported as unresolved (the card shows the CLI
/// as missing) and logged as cli_npm_shim_unresolved with the reason.
/// </summary>
internal static class NpmShim
{
    private const int MaxManifestBytes = 256 * 1024;

    public static CliLaunchTarget? Resolve(string shimPath, string packageName, string binName, string? path,
        string providerId, IProviderDiagnosticSink? diagnostics)
    {
        var shimDirectory = Path.GetDirectoryName(Path.GetFullPath(shimPath))!;
        var packageDirectory = Path.GetFullPath(Path.Combine(shimDirectory, "node_modules",
            packageName.Replace('/', Path.DirectorySeparatorChar)));
        var manifest = Path.Combine(packageDirectory, "package.json");

        string? relativeEntry;
        try
        {
            var info = new FileInfo(manifest);
            if (!info.Exists) return Unresolved("manifest_missing");
            if (info.Length is <= 0 or > MaxManifestBytes) return Unresolved("manifest_size");
            // ReadAllText drops a UTF-8 byte-order mark, which JsonDocument would reject.
            using var document = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            relativeEntry = BinEntry(document.RootElement, binName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Unresolved($"manifest_unreadable,{exception.GetType().Name}");
        }
        if (string.IsNullOrWhiteSpace(relativeEntry)) return Unresolved("bin_missing");

        var entry = Path.GetFullPath(Path.Combine(packageDirectory, relativeEntry));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!entry.StartsWith(packageDirectory + Path.DirectorySeparatorChar, comparison)) return Unresolved("bin_outside_package");
        if (!File.Exists(entry)) return Unresolved("bin_file_missing");

        var extension = Path.GetExtension(entry);
        string fileName;
        ImmutableArray<string> leading;
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = entry;
            leading = [];
        }
        else if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase))
        {
            var node = FindNode(shimDirectory, path);
            if (node is null) return Unresolved("node_missing");
            fileName = node;
            leading = [entry];
        }
        else
        {
            return Unresolved("bin_unsupported");
        }

        return new CliLaunchTarget(fileName, leading, "npm", CliVersionKey.TryCompute([entry, manifest], providerId, diagnostics));

        CliLaunchTarget? Unresolved(string reason)
        {
            diagnostics?.Record(new ProviderDiagnostic("cli_npm_shim_unresolved", providerId, reason, 0));
            return null;
        }
    }

    private static string? BinEntry(JsonElement root, string binName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("bin", out var bin)) return null;
        if (bin.ValueKind == JsonValueKind.String) return bin.GetString();
        return bin.ValueKind == JsonValueKind.Object && bin.TryGetProperty(binName, out var named) &&
               named.ValueKind == JsonValueKind.String
            ? named.GetString()
            : null;
    }

    /// <summary>The same order the npm shim itself uses: node.exe beside the shim, else node.exe on PATH.</summary>
    private static string? FindNode(string shimDirectory, string? path)
    {
        var beside = Path.Combine(shimDirectory, "node.exe");
        if (File.Exists(beside)) return beside;
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, "node.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}

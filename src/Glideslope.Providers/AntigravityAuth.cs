using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glideslope.Providers;

/// <summary>
/// Locates the official Antigravity CLI (`agy`). The known per-platform install location is checked before
/// PATH to match the official installer layout.
/// </summary>
public static class AntigravityExecutableLocator
{
    public static string? Find(string? localApplicationData, string? homeDirectory, string? path, bool isWindows)
    {
        if (isWindows)
        {
            if (!string.IsNullOrWhiteSpace(localApplicationData))
            {
                var knownInstall = Path.Combine(localApplicationData, "agy", "bin", "agy.exe");
                if (File.Exists(knownInstall)) return Path.GetFullPath(knownInstall);
            }
        }
        else if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            var knownInstall = Path.Combine(homeDirectory, ".local", "bin", "agy");
            if (File.Exists(knownInstall)) return Path.GetFullPath(knownInstall);
        }

        if (string.IsNullOrWhiteSpace(path)) return null;
        var name = isWindows ? "agy.exe" : "agy";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}

/// <summary>Reads only the two small local files this provider needs from the Antigravity CLI's own
/// state directory. Never runs `agy` itself and never reads anything else under the user's home.</summary>
public interface IAntigravityHomeContextReader
{
    /// <summary>
    /// True only when the CLI's own onboarding-completion cache says sign-in finished. The docs say `agy`
    /// opens a browser sign-in flow when no session exists; a background poll must never trigger that, so
    /// this check runs before the CLI is ever launched, and a missing/unreadable/incomplete file is treated
    /// as not signed in rather than optimistically launching `agy` to find out.
    /// </summary>
    ValueTask<bool> IsSignedInAsync(CancellationToken cancellationToken);

    /// <summary>The signed-in account's Google-assigned project name, validated against the CLI's own
    /// naming shape, or null when the file is missing, unreadable, or does not match that shape.</summary>
    ValueTask<string?> ReadProjectIdAsync(CancellationToken cancellationToken);
}

/// <summary>Reads `~/.gemini/antigravity-cli/cache/onboarding.json` and `default_project_id.txt`.</summary>
public sealed class AntigravityHomeContextReader : IAntigravityHomeContextReader
{
    private const int MaxOnboardingBytes = 64 * 1024;
    private const int MaxProjectIdBytes = 4 * 1024;
    // The CLI's own project-name shape: lowercase, starts with a letter, 5-63 characters total.
    private static readonly Regex ProjectIdPattern = new("^[a-z][a-z0-9-]{4,62}$", RegexOptions.Compiled);

    private readonly string? _homeDirectory;

    public AntigravityHomeContextReader() : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    public AntigravityHomeContextReader(string? homeDirectory) => _homeDirectory = homeDirectory;

    public async ValueTask<bool> IsSignedInAsync(CancellationToken cancellationToken)
    {
        var path = CacheFilePath("onboarding.json");
        if (path is null) return false;
        try
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            if (stream.Length is <= 0 or > MaxOnboardingBytes) return false;
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            // Defensive: a Windows-authored JSON file can carry a UTF-8 byte-order mark; JsonDocument
            // does not skip it on its own, and a BOM must never be mistaken for a malformed file.
            using var document = JsonDocument.Parse(StripUtf8Bom(bytes).ToArray());
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("onboardingComplete", out var value) &&
                   value.ValueKind == JsonValueKind.True;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async ValueTask<string?> ReadProjectIdAsync(CancellationToken cancellationToken)
    {
        var path = CacheFilePath("default_project_id.txt");
        if (path is null) return null;
        try
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            if (stream.Length is <= 0 or > MaxProjectIdBytes) return null;
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var trimmed = System.Text.Encoding.UTF8.GetString(StripUtf8Bom(bytes)).Trim();
            return ProjectIdPattern.IsMatch(trimmed) ? trimmed : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Strips a leading UTF-8 byte-order mark, if present, so a Windows-authored file is never
    /// mistaken for a malformed one.</summary>
    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;

    private string? CacheFilePath(string fileName) => string.IsNullOrWhiteSpace(_homeDirectory)
        ? null
        : Path.Combine(_homeDirectory, ".gemini", "antigravity-cli", "cache", fileName);
}

using Glideslope.Core;

namespace Glideslope.Providers;

/// <summary>The directory one CLI run uses, and whether it is deleted after the run.</summary>
internal sealed record CliWorkingDirectoryLease(string Path, bool DeleteAfterRun);

/// <summary>
/// Provides an isolated working directory for each CLI run. A stable directory is reused and emptied between runs
/// because the Claude CLI records working directories under ~/.claude/projects/{slug}. The directory is emptied
/// before each run to retain the isolation of a fresh directory (no CLAUDE.md, no .claude/ settings, no .mcp.json in the working
/// directory) and gives the CLI one slug for the life of the install. Glideslope never deletes anything under
/// ~/.claude; the directories the CLI already made are its own and stay.
/// The stable directory sits under Glideslope's per-user cache (DefaultUserPathProvider,
/// which honors --proof-root): on Windows %LOCALAPPDATA%\Glideslope\cache\cli-workdir\{cli}, with the same
/// ancestors up to %LOCALAPPDATA% as the temp directory. The run falls back to a per-run temp
/// directory when the stable directory (or cli-workdir) is a link, when it cannot be created or emptied, or when
/// it sits inside a Git work tree that %TEMP% is not also inside (a --proof-root under a checkout, where the CLI
/// could take the repository as its project and load its settings). A null stable path means per-run temp
/// directories only; the specs' internal constructors use that so no spec ever touches the real cache.
/// </summary>
internal sealed class CliWorkingDirectory
{
    internal const string StableDirectoryName = "cli-workdir";
    private readonly string? _stablePath;
    private readonly string _tempPrefix;
    private readonly string _providerId;
    private readonly string _codePrefix;
    private readonly IProviderDiagnosticSink _diagnostics;
    private string? _lastFallbackReason;

    public CliWorkingDirectory(string? stablePath, string tempPrefix, string providerId, string codePrefix,
        IProviderDiagnosticSink diagnostics)
    {
        _stablePath = stablePath is null ? null : Path.GetFullPath(stablePath);
        _tempPrefix = tempPrefix;
        _providerId = providerId;
        _codePrefix = codePrefix;
        _diagnostics = diagnostics;
    }

    /// <summary>The stable directory for <paramref name="cliName"/> under the app's cache root, or null (per-run
    /// temp directories) when the platform gives no rooted cache location.</summary>
    public static string? DefaultStablePath(string cliName, IProviderDiagnosticSink? diagnostics, string providerId)
    {
        try
        {
            var cache = new DefaultUserPathProvider().Get().CacheDirectory;
            if (Path.IsPathFullyQualified(cache)) return Path.Combine(cache, StableDirectoryName, cliName);
            diagnostics?.Record(new ProviderDiagnostic("cli_workdir_root_unavailable", providerId, "not_rooted", 0));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
                                              or PlatformNotSupportedException or System.Security.SecurityException)
        {
            diagnostics?.Record(new ProviderDiagnostic("cli_workdir_root_unavailable", providerId, exception.GetType().Name, 0));
        }
        return null;
    }

    /// <summary>The directory for the next run, or null when not even a temp directory could be created
    /// (logged as {prefix}_workdir_failed).</summary>
    public CliWorkingDirectoryLease? Prepare()
    {
        if (_stablePath is not null)
        {
            var reason = TryPrepareStable(_stablePath, out var removed);
            if (reason is null)
            {
                _lastFallbackReason = null;
                if (removed > 0)
                    Record($"{_codePrefix}_workdir_emptied", $"removed_entries={removed}");
                return new CliWorkingDirectoryLease(_stablePath, DeleteAfterRun: false);
            }

            // Logged when the reason changes, not on every poll of a lasting fallback.
            if (!string.Equals(reason, _lastFallbackReason, StringComparison.Ordinal))
                Record($"{_codePrefix}_workdir_fallback", reason);
            _lastFallbackReason = reason;
        }

        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"{_tempPrefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);
            return new CliWorkingDirectoryLease(temp, DeleteAfterRun: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Record($"{_codePrefix}_workdir_failed", exception.GetType().Name);
            return null;
        }
    }

    /// <summary>Deletes a per-run temp directory; the stable directory is left for the next Prepare to empty.</summary>
    public void Release(CliWorkingDirectoryLease lease)
    {
        if (!lease.DeleteAfterRun) return;
        try
        {
            Directory.Delete(lease.Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Record($"{_codePrefix}_workdir_delete_failed", $"temp_directory_not_removed,{exception.GetType().Name}");
        }
    }

    /// <summary>Null when the stable directory exists and is empty; otherwise the fallback reason.</summary>
    private static string? TryPrepareStable(string path, out int removed)
    {
        removed = 0;
        try
        {
            if (FindGitRoot(path) is { } repository && !IsSameOrUnder(Path.GetTempPath(), repository))
                return "inside_git_tree";

            var parent = Directory.GetParent(path);
            if (parent is { Exists: true } && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                return "parent_is_link";
            var directory = Directory.CreateDirectory(path);
            // Emptying a directory that is really a link would empty whatever it points at.
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return "stable_path_is_link";

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo child && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    child.Delete(recursive: true);
                }
                else
                {
                    // A file, or a link to a directory: removing it never touches what a link points to.
                    if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    entry.Delete();
                }
                removed++;
            }
            return directory.EnumerateFileSystemInfos().Any() ? "not_empty_after_clear" : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.GetType().Name;
        }
    }

    private static string? FindGitRoot(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker)) return directory.FullName;
        }
        return null;
    }

    private static bool IsSameOrUnder(string child, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child)) + Path.DirectorySeparatorChar;
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return normalizedChild.StartsWith(normalizedParent, comparison);
    }

    private void Record(string code, string status) =>
        _diagnostics.Record(new ProviderDiagnostic(code, _providerId, status, 0));
}

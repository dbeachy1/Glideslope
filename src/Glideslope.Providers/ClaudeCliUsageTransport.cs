using System.Diagnostics;
using System.Globalization;
using System.Text;
using Glideslope.Domain;

namespace Glideslope.Providers;

public enum ClaudeCliOutcome
{
    Completed,
    TimedOut,
    ExecutableNotFound,
    LaunchFailed
}

/// <summary>What one `claude -p /usage --output-format json` run produced. <see cref="StandardOutput"/> is
/// the captured text (bounded), also on TimedOut so a partial envelope can still be inspected for spend.
/// Usage text may contain account identifiers and is never logged.</summary>
public sealed record ClaudeCliResult(ClaudeCliOutcome Outcome, int ExitCode, string? StandardOutput, bool StandardOutputTooLarge)
{
    /// <summary>True when stderr contains login wording (<see cref="ClaudeUsageProvider.LoginWords"/>). Only the
    /// boolean is kept, never the text.</summary>
    public bool StandardErrorMentionsLogin { get; init; }

    /// <summary>The identity of the CLI build that ran (<see cref="CliVersionKey"/>),
    /// or null when none was found or its file metadata could not be read.</summary>
    public string? VersionKey { get; init; }
}

public interface IClaudeUsageTransport
{
    ValueTask<ClaudeCliResult> ReadAsync(CancellationToken cancellationToken);

    /// <summary>The latch recorded, by this or an earlier launch, for the CLI build
    /// this transport would run now, or null. File metadata and one small app-owned file only; never runs the
    /// CLI. A latch recorded for another build is cleared.</summary>
    ClaudeCliPersistedLatch? ReadPersistedLatch();

    /// <summary>Records a latch for the build identified by
    /// <paramref name="versionKey"/> (the <see cref="ClaudeCliResult.VersionKey"/> of the run that tripped it).</summary>
    void PersistLatch(string versionKey, string reason);

    /// <summary>Removes the recorded latch, such as when a manual Refresh clears an unproven timeout latch.</summary>
    void ClearPersistedLatch(string reason);
}

/// <summary>
/// This is the only Claude usage read. It runs the official `claude` executable in print mode with the local
/// `/usage` command and JSON output, from an owned empty working directory so project settings, hooks, and MCP
/// servers do not load. `--bare` is omitted because it skips subscription login renewal. No model request is
/// made (`num_turns` 0); the provider verifies this on every run and latches off if the response changes.
/// Glideslope does not read Claude credentials or call Anthropic usage endpoints.
/// The transport uses an owned working directory and stores latches per CLI build. The provider never accesses
/// the latch file directly.
/// </summary>
public sealed class ClaudeCliUsageTransport : IClaudeUsageTransport
{
    private const int MaxStandardOutputBytes = 256 * 1024;
    /// <summary>The Claude CLI's documented switch for disabling its auto-updater during provider launches.</summary>
    internal const string DisableAutoUpdaterVariable = "DISABLE_AUTOUPDATER";
    // Keep this timeout below the provider backstop so partial output can be checked before the read is cancelled.
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ProcessTreeShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Time allowed for process termination and bounded pipe draining after timeout or cancellation.</summary>
    internal static readonly TimeSpan KillAndDrainAllowance = ProcessTreeShutdownTimeout + CliProcessSupport.PipeDrainAfterExit;

    /// <summary>Longest duration of a usage run with the production timeout.</summary>
    public static readonly TimeSpan WorstCaseRunDuration = DefaultTimeout + KillAndDrainAllowance;

    private readonly string? _path;
    private readonly string? _homeDirectory;
    private readonly string? _configDirectory;
    private readonly string? _roamingApplicationData;
    private readonly bool _isWindows;
    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly TimeSpan _timeout;
    private readonly CliWorkingDirectory _workingDirectory;
    private readonly ClaudeCliLatchStore? _latchStore;

    public ClaudeCliUsageTransport(IProviderDiagnosticSink? diagnostics = null)
        : this(Environment.GetEnvironmentVariable("PATH"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), OperatingSystem.IsWindows(), diagnostics, DefaultTimeout,
            CliWorkingDirectory.DefaultStablePath("claude", diagnostics, ProviderIds.Claude),
            ClaudeCliLatchStore.DefaultPath(diagnostics),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
    {
    }

    /// <summary>Test-only entry point with a temporary working directory and no latch file.</summary>
    internal ClaudeCliUsageTransport(string? path, string? homeDirectory, string? configDirectory, bool isWindows,
        IProviderDiagnosticSink? diagnostics, TimeSpan timeout)
        : this(path, homeDirectory, configDirectory, isWindows, diagnostics, timeout, null, null, null)
    {
    }

    /// <summary>Test-only entry point with an explicit stable working directory and latch file (either may be null).</summary>
    internal ClaudeCliUsageTransport(string? path, string? homeDirectory, string? configDirectory, bool isWindows,
        IProviderDiagnosticSink? diagnostics, TimeSpan timeout, string? stableWorkingDirectory, string? latchFile,
        string? roamingApplicationData)
    {
        _path = path;
        _homeDirectory = homeDirectory;
        _configDirectory = configDirectory;
        _isWindows = isWindows;
        _roamingApplicationData = roamingApplicationData;
        _diagnostics = diagnostics ?? new NullProviderDiagnosticSink();
        _timeout = timeout;
        _workingDirectory = new CliWorkingDirectory(stableWorkingDirectory, "Glideslope-ClaudeUsage-", ProviderIds.Claude,
            "claude_cli_usage", _diagnostics);
        _latchStore = latchFile is null ? null : new ClaudeCliLatchStore(latchFile, _diagnostics);
    }

    public async ValueTask<ClaudeCliResult> ReadAsync(CancellationToken cancellationToken)
    {
        var launch = ClaudeExecutableLocator.Resolve(_path, _homeDirectory, _isWindows, _roamingApplicationData, _diagnostics);
        if (launch is null)
        {
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_usage_finished", ProviderIds.Claude, "executable_not_found", 0));
            return new ClaudeCliResult(ClaudeCliOutcome.ExecutableNotFound, -1, null, false);
        }

        var lease = _workingDirectory.Prepare();
        if (lease is null)
        {
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_usage_finished", ProviderIds.Claude, "workdir_failed", 0));
            return new ClaudeCliResult(ClaudeCliOutcome.LaunchFailed, -1, null, false) { VersionKey = launch.VersionKey };
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await RunAsync(launch, lease.Path, cancellationToken).ConfigureAwait(false) with { VersionKey = launch.VersionKey };
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_usage_finished", ProviderIds.Claude,
                $"{result.Outcome.ToString().ToLowerInvariant()},exit={result.ExitCode},stdout_chars={result.StandardOutput?.Length ?? 0},too_large={result.StandardOutputTooLarge},stderr_login={result.StandardErrorMentionsLogin},launch={launch.Kind}",
                Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
            return result;
        }
        finally
        {
            _workingDirectory.Release(lease);
        }
    }

    public ClaudeCliPersistedLatch? ReadPersistedLatch()
    {
        if (_latchStore is null) return null;
        var launch = ClaudeExecutableLocator.Resolve(_path, _homeDirectory, _isWindows, _roamingApplicationData, _diagnostics);
        // Without a build key, honor any existing latch rather than risk rerunning an unverified CLI.
        return launch is null ? null : _latchStore.Read(launch.VersionKey);
    }

    public void PersistLatch(string versionKey, string reason)
    {
        if (_latchStore is null)
        {
            _diagnostics.Record(new ProviderDiagnostic("claude_latch_not_persisted", ProviderIds.Claude, "store_unavailable", 0));
            return;
        }
        _latchStore.Write(versionKey, reason);
    }

    public void ClearPersistedLatch(string reason) => _latchStore?.Clear(reason);

    private async Task<ClaudeCliResult> RunAsync(CliLaunchTarget launch, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            // The CLI (Node) writes UTF-8; use it to preserve usage-line separators on Windows.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(_configDirectory))
            process.StartInfo.Environment["CLAUDE_CONFIG_DIR"] = _configDirectory;
        // Disable the background updater during provider reads; normal interactive CLI launches can still update.
        process.StartInfo.Environment[DisableAutoUpdaterVariable] = "1";
        // For npm installs, node runs the package entry script before the fixed CLI arguments.
        foreach (var argument in launch.LeadingArguments) process.StartInfo.ArgumentList.Add(argument);
        // -p with /usage makes no model request; it prints the session, weekly and featured usage lines.
        // --no-session-persistence avoids writing a transcript for this throwaway invocation.
        // --strict-mcp-config with no --mcp-config, plus --setting-sources project,local run from this
        // empty owned directory, keep the user's hooks and MCP servers from firing. --output-format json
        // wraps the text in an envelope whose num_turns/total_cost_usd prove nothing was spent.
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add("--no-session-persistence");
        process.StartInfo.ArgumentList.Add("--strict-mcp-config");
        process.StartInfo.ArgumentList.Add("--setting-sources");
        process.StartInfo.ArgumentList.Add("project,local");
        process.StartInfo.ArgumentList.Add("--output-format");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add("/usage");

        try
        {
            if (!process.Start()) return new ClaudeCliResult(ClaudeCliOutcome.LaunchFailed, -1, null, false);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_launch_error", ProviderIds.Claude,
                $"native_error={exception.NativeErrorCode.ToString(CultureInfo.InvariantCulture)}", 0));
            return new ClaudeCliResult(ClaudeCliOutcome.LaunchFailed, -1, null, false);
        }

        try { process.StandardInput.Close(); }
        catch (IOException exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_stdin_close_failed", ProviderIds.Claude, exception.GetType().Name, 0));
        }

        var started = Stopwatch.GetTimestamp();
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        // Readers have a linked token so they can be stopped separately if a descendant keeps a pipe open.
        using var pipes = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var capture = new BoundedCapture(MaxStandardOutputBytes);
        var stderrScan = new PipeScan(ClaudeUsageProvider.LoginWords);
        var stdoutTask = CaptureAsync(process.StandardOutput, capture, pipes.Token);
        var stderrTask = CliProcessSupport.DrainScanningAsync(process.StandardError, stderrScan, pipes.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAfterCancelAsync(process, pipes, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await StopAfterCancelAsync(process, pipes, stdoutTask, stderrTask).ConfigureAwait(false);
            // What the CLI printed before the kill is still inspected for spend by the provider.
            return new ClaudeCliResult(ClaudeCliOutcome.TimedOut, -1, capture.Text, capture.TooLarge)
            {
                StandardErrorMentionsLogin = stderrScan.Matched
            };
        }

        // Bound draining because a descendant can inherit the pipes after the CLI exits.
        var drain = await CliProcessSupport.AwaitPipesAfterExitAsync(pipes, stdoutTask, stderrTask).ConfigureAwait(false);
        if (drain.HeldOpen)
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_pipe_held_after_exit", ProviderIds.Claude, "drain_cancelled",
                Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
        if (drain.FaultTypeName is { } fault)
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_pipe_read_failed", ProviderIds.Claude, fault, 0));
        cancellationToken.ThrowIfCancellationRequested();
        return new ClaudeCliResult(ClaudeCliOutcome.Completed, process.ExitCode, capture.Text, capture.TooLarge)
        {
            StandardErrorMentionsLogin = stderrScan.Matched
        };
    }

    private static async Task CaptureAsync(StreamReader reader, BoundedCapture capture, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            capture.Append(buffer.AsSpan(0, read));
        }
    }

    private async Task StopAfterCancelAsync(Process process, CancellationTokenSource pipes, params Task[] readers)
    {
        try
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
        }
        finally
        {
            var drain = await CliProcessSupport.AwaitPipesAfterExitAsync(pipes, readers).ConfigureAwait(false);
            if (drain.HeldOpen)
                _diagnostics.Record(new ProviderDiagnostic("claude_cli_pipe_held_after_kill", ProviderIds.Claude, "drain_cancelled", 0));
        }
    }

    private async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_kill_failed", ProviderIds.Claude, exception.GetType().Name, 0));
        }
        try { await process.WaitForExitAsync().WaitAsync(ProcessTreeShutdownTimeout).ConfigureAwait(false); }
        catch (InvalidOperationException exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_kill_failed", ProviderIds.Claude, exception.GetType().Name, 0));
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("The Claude usage process did not exit after cancellation.", exception);
        }
    }

    /// <summary>Keeps at most <c>maximum</c> characters; anything past that is dropped and flagged.</summary>
    private sealed class BoundedCapture(int maximum)
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();

        public bool TooLarge { get; private set; }

        public string? Text
        {
            get { lock (_gate) return _text.Length == 0 ? null : _text.ToString(); }
        }

        public void Append(ReadOnlySpan<char> chunk)
        {
            lock (_gate)
            {
                if (TooLarge) return;
                if (_text.Length + chunk.Length > maximum)
                {
                    TooLarge = true;
                    return;
                }
                _text.Append(chunk);
            }
        }
    }
}

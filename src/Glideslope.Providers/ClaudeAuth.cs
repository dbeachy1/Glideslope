using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Glideslope.Domain;

namespace Glideslope.Providers;

/// <summary>Who the official CLI says is signed in. The CLI uses its own login to read usage; Glideslope never
/// reads its access token.</summary>
public sealed record ClaudeAuthContext(string Subject, string? Plan);

public sealed record ClaudeAuthOutcome(
    ProviderStatus? FailureStatus,
    ClaudeAuthContext? Context,
    string? SafeErrorCode)
{
    public static ClaudeAuthOutcome Failed(ProviderStatus status, string code) => new(status, null, code);
    public static ClaudeAuthOutcome Authenticated(ClaudeAuthContext context) => new(null, context, null);
}

public interface IClaudeAuthContextReader
{
    ValueTask<ClaudeAuthOutcome> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads only Claude Code's first-party sign-in status through the official CLI (`claude auth status --json`).
/// The CLI-owned credential file is never read.</summary>
public sealed class ClaudeAuthContextReader : IClaudeAuthContextReader
{
    private const int MaxStatusBytes = 64 * 1024;
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProcessShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The longest one auth-status run can take: its timeout, the kill wait
    /// and the bounded pipe drain. ClaudeUsageProvider's read budget is sized from it.</summary>
    public static readonly TimeSpan WorstCaseRunDuration = CliTimeout + ProcessShutdownTimeout + CliProcessSupport.PipeDrainAfterExit;

    private readonly string? _homeDirectory;
    private readonly string? _configDirectory;
    private readonly string? _path;
    private readonly string? _roamingApplicationData;
    private readonly bool _isWindows;
    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly CliWorkingDirectory _workingDirectory;

    public ClaudeAuthContextReader()
        : this((IProviderDiagnosticSink?)null)
    {
    }

    /// <summary>Production entry point. Runs `auth status` from the same app-owned, emptied working directory
    /// as the usage command.</summary>
    public ClaudeAuthContextReader(IProviderDiagnosticSink? diagnostics)
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), diagnostics,
            CliWorkingDirectory.DefaultStablePath("claude", diagnostics, ProviderIds.Claude))
    {
    }

    /// <summary>Test entry point with a fresh temporary working directory per run, never the
    /// app's cache.</summary>
    public ClaudeAuthContextReader(string? homeDirectory, string? configDirectory, string? path, bool isWindows)
        : this(homeDirectory, configDirectory, path, isWindows, null, null, null)
    {
    }

    internal ClaudeAuthContextReader(string? homeDirectory, string? configDirectory, string? path, bool isWindows,
        string? roamingApplicationData, IProviderDiagnosticSink? diagnostics, string? stableWorkingDirectory)
    {
        _homeDirectory = homeDirectory;
        _configDirectory = configDirectory;
        _path = path;
        _isWindows = isWindows;
        _roamingApplicationData = roamingApplicationData;
        _diagnostics = diagnostics ?? new NullProviderDiagnosticSink();
        _workingDirectory = new CliWorkingDirectory(stableWorkingDirectory, "Glideslope-ClaudeAuth-", ProviderIds.Claude,
            "claude_auth_status", _diagnostics);
    }

    public async ValueTask<ClaudeAuthOutcome> ReadAsync(CancellationToken cancellationToken)
    {
        var launch = ClaudeExecutableLocator.Resolve(_path, _homeDirectory, _isWindows, _roamingApplicationData, _diagnostics);
        if (launch is null)
            return ClaudeAuthOutcome.Failed(ProviderStatus.MissingApplication, "claude_cli_missing");

        var lease = _workingDirectory.Prepare();
        if (lease is null)
            return ClaudeAuthOutcome.Failed(ProviderStatus.UnknownError, "claude_auth_status_workdir_failed");
        CommandResult command;
        try
        {
            command = await RunAuthStatusAsync(launch, lease.Path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workingDirectory.Release(lease);
        }
        if (command.TimedOut)
            return ClaudeAuthOutcome.Failed(ProviderStatus.Offline, "claude_auth_status_timeout");
        if (command.TooLarge || command.StandardOutput is null)
            return ClaudeAuthOutcome.Failed(ProviderStatus.SchemaChanged, "claude_auth_status_unreadable");

        // Ignore update notices or warning lines printed before the JSON. Extract the object from the first '{'
        // exactly as the usage envelope is (ClaudeCliUsageParser.TryParseDocument).
        using var status = ClaudeCliUsageParser.TryParseDocument(command.StandardOutput);
        if (status is null)
        {
            _diagnostics.Record(new ProviderDiagnostic("claude_auth_status_rejected", ProviderIds.Claude, "not_json", 0));
            return ClaudeAuthOutcome.Failed(ProviderStatus.SchemaChanged, "claude_auth_status_json");
        }

        var root = status.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return ClaudeAuthOutcome.Failed(ProviderStatus.SchemaChanged, "claude_auth_status_shape");
        if (!TryBoolean(root, "loggedIn", out var loggedIn))
            return ClaudeAuthOutcome.Failed(ProviderStatus.SchemaChanged, "claude_auth_status_shape");
        if (!loggedIn)
            return ClaudeAuthOutcome.Failed(ProviderStatus.NeedsSignIn, "claude_signed_out");

        string? plan = null;
        if (!TryString(root, "authMethod", out var authMethod) ||
            !TryString(root, "apiProvider", out var apiProvider) ||
            !IsSubscriptionOAuth(authMethod, apiProvider))
            return ClaudeAuthOutcome.Failed(ProviderStatus.UnsupportedAccount, "claude_not_subscription_oauth");
        if (TryString(root, "subscriptionType", out var reportedPlan)) plan = reportedPlan;

        if (!TryString(root, "email", out var email) || !TryString(root, "orgId", out var organization))
            return ClaudeAuthOutcome.Failed(ProviderStatus.SchemaChanged, "claude_account_subject_missing");
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var normalizedOrganization = organization.Trim().ToLowerInvariant();
        var subject = $"{normalizedEmail.Length}:{normalizedEmail}{normalizedOrganization.Length}:{normalizedOrganization}";

        return ClaudeAuthOutcome.Authenticated(new ClaudeAuthContext(subject, plan));
    }

    private static bool IsSubscriptionOAuth(string authMethod, string apiProvider)
    {
        var firstParty = apiProvider.Equals("firstParty", StringComparison.OrdinalIgnoreCase) ||
                         apiProvider.Equals("claude.ai", StringComparison.OrdinalIgnoreCase);
        var oauth = authMethod.Equals("claude.ai", StringComparison.OrdinalIgnoreCase) ||
                    authMethod.Equals("oauth", StringComparison.OrdinalIgnoreCase);
        return firstParty && oauth;
    }

    private async Task<CommandResult> RunAuthStatusAsync(CliLaunchTarget launch, string workingDirectory,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Use the app-owned working directory, not the user's home.
            WorkingDirectory = workingDirectory,
            // The CLI (Node) writes UTF-8; use that encoding instead of the Windows code page.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(_configDirectory))
            process.StartInfo.Environment["CLAUDE_CONFIG_DIR"] = _configDirectory;
        // Disable self-update during background probes (see ClaudeCliUsageTransport).
        process.StartInfo.Environment[ClaudeCliUsageTransport.DisableAutoUpdaterVariable] = "1";
        // For npm installs, Node runs the package entry script, which comes first.
        foreach (var argument in launch.LeadingArguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.ArgumentList.Add("auth");
        process.StartInfo.ArgumentList.Add("status");
        process.StartInfo.ArgumentList.Add("--json");

        try
        {
            if (!process.Start())
            {
                Record("claude_auth_status_finished", "launch_failed", started);
                return new CommandResult(null, false, false);
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Record("claude_auth_status_finished", $"launch_failed,native_error={exception.NativeErrorCode}", started);
            return new CommandResult(null, false, false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CliTimeout);
        // Give pipe readers their own token, linked to the run's, so they can be
        // stopped alone when a helper process still holds a pipe after the CLI exited.
        using var pipes = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var stdout = new BoundedTextCapture(MaxStatusBytes);
        var stdoutTask = BoundedTextCapture.ReadToEndAsync(process.StandardOutput, stdout, pipes.Token);
        var stderrTask = CliProcessSupport.DrainScanningAsync(process.StandardError, new PipeScan(), pipes.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAfterCancelAsync(process, pipes, stdoutTask, stderrTask).ConfigureAwait(false);
            Record("claude_auth_status_finished", "timed_out", started);
            return new CommandResult(null, true, false);
        }
        catch
        {
            await StopAfterCancelAsync(process, pipes, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var drain = await CliProcessSupport.AwaitPipesAfterExitAsync(pipes, stdoutTask, stderrTask).ConfigureAwait(false);
        if (drain.HeldOpen) Record("claude_auth_status_pipe_held_after_exit", "drain_cancelled", started);
        if (drain.FaultTypeName is { } fault) Record("claude_auth_status_pipe_read_failed", fault, started);
        cancellationToken.ThrowIfCancellationRequested();
        var (text, tooLarge) = stdout.Snapshot();
        Record("claude_auth_status_finished",
            $"completed,exit={process.ExitCode.ToString(CultureInfo.InvariantCulture)},stdout_chars={text.Length},too_large={tooLarge},launch={launch.Kind}",
            started);
        return new CommandResult(text, false, tooLarge);
    }

    private async Task StopAfterCancelAsync(Process process, CancellationTokenSource pipes, params Task[] readers)
    {
        try
        {
            await StopExactProcessAsync(process).ConfigureAwait(false);
        }
        finally
        {
            var drain = await CliProcessSupport.AwaitPipesAfterExitAsync(pipes, readers).ConfigureAwait(false);
            if (drain.HeldOpen) Record("claude_auth_status_pipe_held_after_kill", "drain_cancelled", 0);
        }
    }

    private async Task StopExactProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Record("claude_auth_status_kill_failed", exception.GetType().Name, 0);
        }
        try { await process.WaitForExitAsync().WaitAsync(ProcessShutdownTimeout).ConfigureAwait(false); }
        catch (InvalidOperationException exception) { Record("claude_auth_status_kill_failed", exception.GetType().Name, 0); }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("The Claude auth-status process did not exit after cancellation.", exception);
        }
    }

    private void Record(string code, string status, long started) =>
        _diagnostics.Record(new ProviderDiagnostic(code, ProviderIds.Claude, status,
            started == 0 ? 0 : Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryBoolean(JsonElement parent, string name, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private sealed record CommandResult(string? StandardOutput, bool TimedOut, bool TooLarge);
}

public static class ClaudeExecutableLocator
{
    /// <summary>The first claude on PATH, else the native installer's ~/.local/bin/claude(.exe). On Windows,
    /// prefer a directory's claude.exe to its npm shim claude.cmd (the PATHEXT
    /// order), and %APPDATA%\npm\claude.cmd, npm's default global prefix, is checked last.</summary>
    public static string? Find(string? path, string? homeDirectory, bool isWindows, string? roamingApplicationData = null)
    {
        var names = isWindows ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            var knownInstall = Path.Combine(homeDirectory, ".local", "bin", names[0]);
            if (File.Exists(knownInstall)) return Path.GetFullPath(knownInstall);
        }

        if (isWindows && !string.IsNullOrWhiteSpace(roamingApplicationData))
        {
            var npmShim = Path.Combine(roamingApplicationData, "npm", "claude.cmd");
            if (File.Exists(npmShim)) return Path.GetFullPath(npmShim);
        }
        return null;
    }

    /// <summary>What to launch for the claude <see cref="Find"/> locates, and
    /// that build's version key. A native executable runs as is; an npm shim is never run through cmd.exe (see
    /// NpmShim). Null when nothing is installed or an npm layout cannot be resolved (logged).</summary>
    public static CliLaunchTarget? Resolve(string? path, string? homeDirectory, bool isWindows, string? roamingApplicationData,
        IProviderDiagnosticSink? diagnostics)
    {
        var found = Find(path, homeDirectory, isWindows, roamingApplicationData);
        if (found is null) return null;
        return found.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? NpmShim.Resolve(found, "@anthropic-ai/claude-code", "claude", path, ProviderIds.Claude, diagnostics)
            : CliLaunchTarget.Native(found, ProviderIds.Claude, diagnostics);
    }
}

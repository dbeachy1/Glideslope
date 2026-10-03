using System.Diagnostics;
using System.Globalization;
using System.Text;
using Glideslope.Domain;

namespace Glideslope.Providers;

/// <summary>How one attempt to run `agy` for a usage probe concluded.</summary>
public enum AntigravityProcessOutcome
{
    Completed,
    ExecutableNotFound,
    LaunchFailed,
    TimedOut
}

/// <summary>Raw result of one `agy` invocation. <see cref="StandardOutput"/> is untouched text: the
/// caller is responsible for finding and parsing the JSON result line. The provider classifies completed
/// results independently of <see cref="ExitCode"/>; oversized output alone latches it off. Timed-out results
/// retain captured output and set <see cref="ExitCode"/> to -1.</summary>
public sealed record AntigravityProcessResult(
    AntigravityProcessOutcome Outcome,
    int ExitCode,
    string StandardOutput,
    bool StandardOutputTooLarge)
{
    /// <summary>True when stderr contains the sign-in URL marker. Only this boolean is kept; stderr text is
    /// never stored or logged.</summary>
    public bool StandardErrorShowedSignIn { get; init; }
}

public interface IAntigravityUsageTransport
{
    ValueTask<AntigravityProcessResult> ReadAsync(string executablePath, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the real, official `agy` executable to read Antigravity's own usage figures. Glideslope never
/// reads or writes any Antigravity credential or session file itself; this class only launches the CLI
/// with a fixed, read-only argument list, captures its stdout, and reports what happened.
/// </summary>
public sealed class AntigravityCliUsageTransport : IAntigravityUsageTransport
{
    public const int MaxStandardOutputBytes = 256 * 1024;

    /// <summary>
    /// Marker for recognizing a Google sign-in URL in stderr. Unrecognized timed-out output is treated as
    /// unproven.
    /// </summary>
    internal const string SignInUrlMarker = "accounts.google.com";

    /// <summary>Environment variable for disabling agy's background updater during a usage probe. The value
    /// must be the exact lowercase "true".</summary>
    internal const string DisableAutoUpdateVariable = "AGY_CLI_DISABLE_AUTO_UPDATE";
    internal const string DisableAutoUpdateValue = "true";

    // Keep the CLI timeout below the scheduler's whole-read timeout so a slow run returns a structured TimedOut
    // result after the auth precheck and before the read is cancelled by the scheduler.
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ProcessTreeShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Maximum duration of one agy run, including timeout, kill wait, and pipe drain.</summary>
    public static readonly TimeSpan WorstCaseRunDuration = DefaultTimeout + ProcessTreeShutdownTimeout + CliProcessSupport.PipeDrainAfterExit;

    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;
    private readonly CliWorkingDirectory _workingDirectory;

    public AntigravityCliUsageTransport(IProviderDiagnosticSink? diagnostics = null)
        : this(diagnostics, DefaultTimeout, CliWorkingDirectory.DefaultStablePath("agy", diagnostics, ProviderIds.Gemini), null)
    {
    }

    /// <summary>Test-only entry point with a short timeout and a fake executable.</summary>
    internal AntigravityCliUsageTransport(IProviderDiagnosticSink? diagnostics, TimeSpan timeout)
        : this(diagnostics, timeout, null, null)
    {
    }

    /// <summary>Test-only entry point that also injects a
    /// TimeProvider, so a spec can drive this transport's own timeout deterministically -- advancing a
    /// ManualTimeProvider by exactly <paramref name="timeout"/> once it has observed
    /// antigravity_usage_process_started -- instead of waiting on a real timeout.</summary>
    internal AntigravityCliUsageTransport(IProviderDiagnosticSink? diagnostics, TimeSpan timeout, TimeProvider? timeProvider)
        : this(diagnostics, timeout, null, timeProvider)
    {
    }

    /// <summary>Test-only entry point with an explicit stable working directory.</summary>
    internal AntigravityCliUsageTransport(IProviderDiagnosticSink? diagnostics, TimeSpan timeout, string? stableWorkingDirectory)
        : this(diagnostics, timeout, stableWorkingDirectory, null)
    {
    }

    /// <summary>Test-only entry point with both an explicit stable working directory and an injected
    /// TimeProvider.</summary>
    internal AntigravityCliUsageTransport(IProviderDiagnosticSink? diagnostics, TimeSpan timeout, string? stableWorkingDirectory,
        TimeProvider? timeProvider)
    {
        _diagnostics = diagnostics ?? new NullProviderDiagnosticSink();
        _timeout = timeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _workingDirectory = new CliWorkingDirectory(stableWorkingDirectory, "Glideslope-AntigravityUsage-", ProviderIds.Gemini,
            "antigravity_usage", _diagnostics);
    }

    public async ValueTask<AntigravityProcessResult> ReadAsync(string executablePath, CancellationToken cancellationToken)
    {
        // Reuse one stable, emptied working directory instead of creating a temporary directory per poll.
        var lease = _workingDirectory.Prepare();
        if (lease is null)
            return new AntigravityProcessResult(AntigravityProcessOutcome.LaunchFailed, -1, string.Empty, false);

        try
        {
            return await RunAsync(executablePath, lease.Path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workingDirectory.Release(lease);
        }
    }

    private async Task<AntigravityProcessResult> RunAsync(
        string executablePath, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            // agy writes UTF-8; using the system code page can corrupt its output on Windows.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // Keep this fixed, read-only argument list. An unsupported slash command may be treated as a prompt
        // and consume quota, so the parser also verifies that the response matches the usage command.
        // agy's -p consumes the next token as its prompt, so /usage must immediately follow -p.
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add("/usage");
        process.StartInfo.ArgumentList.Add("--output-format");
        process.StartInfo.ArgumentList.Add("json");
        // Disable agy's detached updater for this probe. CreateNoWindow cannot hide a console opened by a
        // descendant process; normal user-launched agy runs retain updater behavior.
        process.StartInfo.Environment[DisableAutoUpdateVariable] = DisableAutoUpdateValue;

        try
        {
            if (!process.Start())
                return new AntigravityProcessResult(AntigravityProcessOutcome.LaunchFailed, -1, string.Empty, false);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_launch_error", ProviderIds.Gemini,
                $"native_error={exception.NativeErrorCode.ToString(CultureInfo.InvariantCulture)}", 0));
            return new AntigravityProcessResult(AntigravityProcessOutcome.ExecutableNotFound, -1, string.Empty, false);
        }

        var started = Stopwatch.GetTimestamp();
        using var timeoutSource = new CancellationTokenSource(_timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        // Give pipe readers a separate linked token so they can be stopped if a helper keeps a pipe open.
        using var pipes = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);

        _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_process_started", ProviderIds.Gemini, "started",
            Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));

        try { process.StandardInput.Close(); }
        catch (IOException exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_stdin_close_failed", ProviderIds.Gemini, exception.GetType().Name, 0));
        }

        // Stdout accumulates into a capture that outlives the reader task, so a
        // run killed at the timeout still reports what it printed before the kill (see the TimedOut return).
        var stdoutCapture = new BoundedTextCapture(MaxStandardOutputBytes);
        var stdoutTask = BoundedTextCapture.ReadToEndAsync(process.StandardOutput, stdoutCapture, pipes.Token);
        // Stderr may contain account data; count characters and retain only the sign-in URL flag, never the text.
        var stderrScan = new PipeScan(SignInUrlMarker);
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
            // Preserve bounded output captured before timeout so the provider can distinguish a sign-in wait
            // from a command that produced an untrusted response.
            var (capturedText, capturedTooLarge) = stdoutCapture.Snapshot();
            _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_timeout_output", ProviderIds.Gemini,
                capturedTooLarge ? "captured_too_large" : "captured", capturedText.Length));
            RecordStderr(stderrScan);
            return new AntigravityProcessResult(AntigravityProcessOutcome.TimedOut, -1, capturedText, capturedTooLarge)
            {
                StandardErrorShowedSignIn = stderrScan.Matched
            };
        }

        // Bound pipe draining because a descendant process may inherit the handles after agy exits.
        var drain = await CliProcessSupport.AwaitPipesAfterExitAsync(pipes, stdoutTask, stderrTask).ConfigureAwait(false);
        if (drain.HeldOpen)
            _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_pipe_held_after_exit", ProviderIds.Gemini, "drain_cancelled",
                Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
        if (drain.FaultTypeName is { } fault)
            _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_pipe_read_failed", ProviderIds.Gemini, fault, 0));
        cancellationToken.ThrowIfCancellationRequested();
        RecordStderr(stderrScan);
        var (text, tooLarge) = stdoutCapture.Snapshot();
        return new AntigravityProcessResult(AntigravityProcessOutcome.Completed, process.ExitCode, text, tooLarge)
        {
            StandardErrorShowedSignIn = stderrScan.Matched
        };
    }

    private void RecordStderr(PipeScan scan)
    {
        if (scan.Characters > 0)
            _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_stderr_seen", ProviderIds.Gemini,
                scan.Matched ? "discarded,sign_in_url" : "discarded", scan.Characters));
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
                _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_pipe_held_after_kill", ProviderIds.Gemini, "drain_cancelled", 0));
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
            _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_kill_failed", ProviderIds.Gemini, exception.GetType().Name, 0));
        }
        try { await process.WaitForExitAsync().WaitAsync(ProcessTreeShutdownTimeout).ConfigureAwait(false); }
        catch (InvalidOperationException exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_kill_failed", ProviderIds.Gemini, exception.GetType().Name, 0));
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("The Antigravity usage process did not exit after cancellation.", exception);
        }
    }
}

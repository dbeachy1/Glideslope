using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glideslope.Domain;

namespace Glideslope.Providers;

/// <summary>
/// Reads read-only Gemini quota from the official Google Antigravity CLI (`agy`). The user must install
/// Antigravity and sign in. Gemini website usage is out of scope. See
/// AntigravityUsageParser for the quota-spend guard this provider depends on, and
/// AntigravityCliUsageTransport for how `agy` is launched.
/// </summary>
public sealed class AntigravityUsageProvider : IIntentAwareUsageProvider
{
    public const string HistoryScopeUnavailableCode = "antigravity_history_scope_unavailable";
    public const string CommandUnsupportedCode = "antigravity_usage_command_unsupported";
    private const string MissingProjectIdSubject = "antigravity-project-id-unavailable";
    private static readonly Uri InstallInstructions = new("https://antigravity.google/docs/cli/install/");

    private readonly IAntigravityHomeContextReader _homeReader;
    private readonly IAntigravityUsageTransport _transport;
    private readonly AccountScopeResolver _scopeResolver;
    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly TimeProvider _timeProvider;
    private readonly string? _path;
    private readonly string? _localApplicationData;
    private readonly string? _homeDirectory;
    private readonly bool _isWindows;
    private readonly byte[] _sessionScopeKey = RandomNumberGenerator.GetBytes(32);
    private int _scopeWarningIssued;
    // A completed run latches if its output cannot be proven zero-spend, regardless of exit code. Empty
    // output is retryable; unrecognized, oversized, or nonzero-spend output is not. Silent timeouts use a
    // separate latch that Refresh can clear.
    private int _commandUnsupportedLatched;
    // After agy reports it has no usable sign-in, scheduled polls pause to avoid repeatedly opening browser login.
    // A timeout can indicate that agy is waiting for browser sign-in. The hold prevents scheduled polls from
    // reopening sign-in; a user-initiated read can bypass it, and only a Ready result clears it.
    private static readonly TimeSpan SignInRequiredHold = TimeSpan.FromMinutes(30);
    private const string SignInRequiredCode = "antigravity_sign_in_required";
    private readonly object _signInGate = new();
    private DateTimeOffset? _signInRequiredUntilUtc;
    // A silent timeout without a sign-in URL is ambiguous: a long model turn may not have printed its result.
    // Latch after two consecutive ambiguous timeouts, unless an intervening completed run resets the count.
    // The scheduler serializes reads, so a plain integer is sufficient.
    private const int MaxConsecutiveUnprovenTimeouts = 2;
    private int _consecutiveUnprovenTimeouts;
    // Distinguish a hang latch from unproven output. Refresh clears only a hang latch; other latches require
    // restart. A hang latch reports that the tool stopped responding.
    public const string UnresponsiveCode = "antigravity_unresponsive";
    private volatile bool _latchedByUnprovenTimeouts;

    public AntigravityUsageProvider(
        IAntigravityHomeContextReader homeReader,
        IAntigravityUsageTransport transport,
        AccountScopeResolver scopeResolver,
        IProviderDiagnosticSink? diagnostics = null,
        TimeProvider? timeProvider = null)
        : this(homeReader, transport, scopeResolver,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows(), diagnostics, timeProvider)
    {
    }

    /// <summary>Test-only entry point: lets specs point discovery at synthetic PATH/home/local-app-data
    /// directories and a fake platform.</summary>
    internal AntigravityUsageProvider(
        IAntigravityHomeContextReader homeReader,
        IAntigravityUsageTransport transport,
        AccountScopeResolver scopeResolver,
        string? path,
        string? localApplicationData,
        string? homeDirectory,
        bool isWindows,
        IProviderDiagnosticSink? diagnostics,
        TimeProvider? timeProvider)
    {
        _homeReader = homeReader ?? throw new ArgumentNullException(nameof(homeReader));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _path = path;
        _localApplicationData = localApplicationData;
        _homeDirectory = homeDirectory;
        _isWindows = isWindows;
        _diagnostics = diagnostics ?? new NullProviderDiagnosticSink();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A scheduled poll. Honors the sign-in hold.</summary>
    public ValueTask<ProviderReadResult> ReadAsync(CancellationToken cancellationToken) =>
        ReadCoreAsync(userInitiated: false, clearsHangLatch: false, cancellationToken);

    /// <summary>A user-initiated read (card Refresh/Retry). It runs agy once even
    /// while the sign-in hold is active, so a user who has just signed in sees the result immediately
    /// instead of waiting out the 30 minutes. The onboarding precheck and the quota-spend latch still
    /// </summary>
    public ValueTask<ProviderReadResult> ReadUserInitiatedAsync(CancellationToken cancellationToken) =>
        ReadCoreAsync(userInitiated: true, clearsHangLatch: true, cancellationToken);

    /// <summary>Scheduler entry point. Manual maps to <see cref="ReadUserInitiatedAsync"/>; scheduled polls
    /// and automatic retries honor the sign-in hold.</summary>
    public ValueTask<ProviderReadResult> ReadAsync(ProviderReadIntent intent, CancellationToken cancellationToken) =>
        intent switch
        {
            ProviderReadIntent.Manual => ReadUserInitiatedAsync(cancellationToken),
            ProviderReadIntent.AutomaticRetry => ReadCoreAsync(userInitiated: false, clearsHangLatch: true, cancellationToken),
            _ => ReadAsync(cancellationToken)
        };

    private async ValueTask<ProviderReadResult> ReadCoreAsync(bool userInitiated, bool clearsHangLatch, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ProviderReadResult? result = null;
        try
        {
            if (Volatile.Read(ref _commandUnsupportedLatched) != 0)
            {
                if (clearsHangLatch && _latchedByUnprovenTimeouts)
                {
                    ClearUnprovenTimeoutLatch(started);
                }
                else
                {
                    result = LatchedFailure();
                    return result;
                }
            }

            var executable = AntigravityExecutableLocator.Find(_localApplicationData, _homeDirectory, _path, _isWindows);
            if (executable is null)
            {
                _diagnostics.Record(new ProviderDiagnostic("antigravity_precheck_skipped", ProviderIds.Gemini,
                    "cli_not_found", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                result = InstallFailure(ProviderStatus.MissingApplication, "antigravity_cli_not_found");
                return result;
            }

            // The docs say agy opens a browser sign-in flow when no session exists. A background poll must
            // never trigger that, so the CLI's own onboarding-completion cache is checked here, before agy
            // is ever launched; a missing, unreadable, or incomplete file is treated as not signed in
            // rather than optimistically launching agy to find out.
            var signedIn = await _homeReader.IsSignedInAsync(cancellationToken).ConfigureAwait(false);
            if (!signedIn)
            {
                _diagnostics.Record(new ProviderDiagnostic("antigravity_precheck_skipped", ProviderIds.Gemini,
                    "not_signed_in", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                result = InstallFailure(ProviderStatus.NeedsSignIn, "antigravity_not_signed_in");
                return result;
            }

            if (SignInHoldActive())
            {
                if (!userInitiated)
                {
                    _diagnostics.Record(new ProviderDiagnostic("antigravity_precheck_skipped", ProviderIds.Gemini,
                        "sign_in_hold_active", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                    result = InstallFailure(ProviderStatus.NeedsSignIn, SignInRequiredCode);
                    return result;
                }

                // The user explicitly requested this read, so run agy once despite the hold.
                _diagnostics.Record(new ProviderDiagnostic("antigravity_sign_in_hold_bypassed", ProviderIds.Gemini,
                    "user_initiated_read", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
            }

            var process = await _transport.ReadAsync(executable, cancellationToken).ConfigureAwait(false);
            // Retry the first consecutive silent timeout once immediately. A second silent timeout latches
            // until Refresh; the scheduler's one automatic retry may also clear that hang latch.
            if (process.Outcome == AntigravityProcessOutcome.TimedOut && IsSilentHang(process) && _consecutiveUnprovenTimeouts == 0)
            {
                _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_timeout", ProviderIds.Gemini,
                    "process_killed", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                _consecutiveUnprovenTimeouts = 1;
                _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_retry_after_hang", ProviderIds.Gemini,
                    "consecutive=1", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                process = await _transport.ReadAsync(executable, cancellationToken).ConfigureAwait(false);
            }
            switch (process.Outcome)
            {
                case AntigravityProcessOutcome.ExecutableNotFound:
                    result = InstallFailure(ProviderStatus.MissingApplication, "antigravity_cli_not_found");
                    return result;
                case AntigravityProcessOutcome.LaunchFailed:
                    _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_launch_failed", ProviderIds.Gemini,
                        "process_start_failed", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                    result = Failure(ProviderStatus.UnknownError, "antigravity_usage_launch_failed");
                    return result;
                case AntigravityProcessOutcome.TimedOut:
                    _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_timeout", ProviderIds.Gemini,
                        "process_killed", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                    // A timeout enters the sign-in hold only when output is empty and stderr contains the
                    // sign-in marker. Proven usage output is processed; other output is treated as unproven.
                    result = await ClassifyTimeoutAsync(process, started, userInitiated, cancellationToken).ConfigureAwait(false);
                    return result;
            }

            // A completed run breaks the sequence of consecutive timeouts.
            ResetUnprovenTimeouts(started, "run_completed");

            // Classify every completed run by its output, regardless of exit code. Only zero-turn,
            // zero-token results avoid the latch.
            if (process.StandardOutputTooLarge)
            {
                // The usage response is small; output beyond the cap cannot be proven zero-spend.
                result = LatchOff(started, "output_too_large", process.ExitCode);
                return result;
            }

            if (string.IsNullOrWhiteSpace(process.StandardOutput))
            {
                // Empty output is an ordinary retryable failure unless stderr identifies a sign-in request.
                if (process.StandardErrorShowedSignIn)
                {
                    result = EnterSignInHold(started, "hold_started_stderr_sign_in");
                    return result;
                }
                _diagnostics.Record(new ProviderDiagnostic("antigravity_empty_output", ProviderIds.Gemini,
                    $"exit_code={process.ExitCode}", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                result = Failure(ProviderStatus.UnknownError, "antigravity_usage_failed");
                return result;
            }

            ParsedAntigravityUsage parsed;
            try
            {
                parsed = AntigravityUsageParser.Parse(process.StandardOutput);
            }
            catch (AntigravityUsageCommandUnsupportedException)
            {
                result = LatchOff(started, "not_zero_spend", process.ExitCode);
                return result;
            }
            catch (AntigravitySignInRequiredException)
            {
                result = EnterSignInHold(started, "hold_started");
                return result;
            }
            catch (AntigravityUsageFailedException)
            {
                // agy reported a failure status with zero turns and zero tokens: proven zero-spend, so
                // an ordinary retryable failure.
                result = Failure(ProviderStatus.UnknownError, "antigravity_usage_failed");
                return result;
            }
            catch (JsonException) when (!ContainsJsonObjectLine(process.StandardOutput))
            {
                // No JSON result line means the response cannot prove that the run spent nothing.
                result = LatchOff(started, "output_not_json", process.ExitCode);
                return result;
            }
            catch (JsonException)
            {
                // The parser throws this only after the zero-turn, zero-token usage check has passed (the
                // Gemini buckets are missing or malformed), so it is a non-latching schema change.
                result = process.ExitCode != 0
                    ? Failure(ProviderStatus.UnknownError, "antigravity_usage_failed")
                    : Failure(ProviderStatus.SchemaChanged, "antigravity_usage_schema_changed");
                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Any other parse failure is unexpected and leaves the run unproven, so it latches too.
                result = LatchOff(started, $"output_unrecognized,{exception.GetType().Name}", process.ExitCode);
                return result;
            }

            if (process.ExitCode != 0)
            {
                // A zero-spend SUCCESS usage result with a failing exit code: nothing was spent, but agy
                // itself says the run failed, so the reading is not trusted.
                _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_failed", ProviderIds.Gemini,
                    $"exit_code_{process.ExitCode}", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                result = Failure(ProviderStatus.UnknownError, "antigravity_usage_failed");
                return result;
            }

            result = await CompleteReadyAsync(parsed, started, userInitiated, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("antigravity_read_cancelled_internally", ProviderIds.Gemini,
                exception.GetType().Name, Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
            result = Failure(ProviderStatus.Offline, "antigravity_read_timeout");
            return result;
        }
        catch (Exception exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("antigravity_read_failed", ProviderIds.Gemini,
                exception.GetType().Name, Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
            result = Failure(ProviderStatus.UnknownError, "antigravity_read_failed");
            return result;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var status = result?.Status.ToString() ?? "cancelled";
            if (result?.SafeErrorCode == HistoryScopeUnavailableCode)
            {
                if (Interlocked.Exchange(ref _scopeWarningIssued, 1) == 0)
                    _diagnostics.Record(new ProviderDiagnostic("provider_history_scope_unavailable", ProviderIds.Gemini,
                        "session_scope", Math.Max(0, (long)elapsed.TotalMilliseconds)));
            }
            else
            {
                Interlocked.Exchange(ref _scopeWarningIssued, 0);
            }
            _diagnostics.Record(new ProviderDiagnostic("provider_read_finished", ProviderIds.Gemini,
                $"{status},code={result?.SafeErrorCode ?? "none"}", Math.Max(0, (long)elapsed.TotalMilliseconds)));
        }
    }

    /// <summary>
    /// Classifies a run killed at the transport timeout.
    /// <code>
    /// Output over the cap or without proven zero spend -> latch (timeout_unproven_output).
    /// Complete zero-spend SUCCESS result               -> use the reading (Ready).
    /// Zero-spend sign-in error                         -> sign-in hold (NeedsSignIn).
    /// Other zero-spend result or missing buckets       -> UnknownError or SchemaChanged.
    /// No stdout and a sign-in URL on stderr            -> sign-in hold (NeedsSignIn).
    /// No stdout and no sign-in URL                     -> Offline; latch after a second consecutive ambiguous timeout.
    /// </code>
    /// A complete, proven reading remains usable even if the process times out. Manual Refresh bypasses the
    /// sign-in hold but does not reset the consecutive ambiguous-timeout count.
    /// </summary>
    private async ValueTask<ProviderReadResult> ClassifyTimeoutAsync(AntigravityProcessResult process, long started,
        bool userInitiated, CancellationToken cancellationToken)
    {
        if (process.StandardOutputTooLarge)
            return LatchOff(started, "timeout_unproven_output", process.ExitCode);

        if (string.IsNullOrWhiteSpace(process.StandardOutput))
        {
            if (process.StandardErrorShowedSignIn)
                return EnterSignInHold(started, "hold_started_after_timeout");
            return RecordUnprovenTimeout(started);
        }

        var (verdict, parsed, failureType) = ClassifyOutput(process.StandardOutput);
        if (!IsProvenZeroSpend(verdict))
            return LatchOff(started, failureType is null ? "timeout_unproven_output" : $"timeout_unproven_output,{failureType}",
                process.ExitCode);

        ResetUnprovenTimeouts(started, "proven_output_at_timeout");
        switch (verdict)
        {
            case OutputVerdict.SignInRequired:
                return EnterSignInHold(started, "hold_started_after_timeout");
            case OutputVerdict.UsageFailed:
                return Failure(ProviderStatus.UnknownError, "antigravity_usage_failed");
            case OutputVerdict.SchemaChanged:
                return Failure(ProviderStatus.SchemaChanged, "antigravity_usage_schema_changed");
            default:
                _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_timeout_output_used", ProviderIds.Gemini,
                    "zero_spend_success", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                return await CompleteReadyAsync(parsed!, started, userInitiated, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A run killed at the timeout that printed nothing on stdout, did not overflow it, and showed no sign-in
    /// URL: the case ClassifyTimeoutAsync counts as an unproven timeout.</summary>
    private static bool IsSilentHang(AntigravityProcessResult process) =>
        !process.StandardOutputTooLarge && string.IsNullOrWhiteSpace(process.StandardOutput) && !process.StandardErrorShowedSignIn;

    /// <summary>Counts consecutive unproven timeouts; the second latches.</summary>
    private ProviderReadResult RecordUnprovenTimeout(long started)
    {
        var consecutive = ++_consecutiveUnprovenTimeouts;
        if (consecutive >= MaxConsecutiveUnprovenTimeouts)
            return LatchOff(started, $"unproven_timeout,consecutive={consecutive}", -1, byUnprovenTimeouts: true);
        _diagnostics.Record(new ProviderDiagnostic("antigravity_usage_timeout_unproven", ProviderIds.Gemini,
            $"consecutive={consecutive}", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
        return Failure(ProviderStatus.Offline, "antigravity_usage_timeout");
    }

    /// <summary>Resets the count after a completed run or proven output at timeout.</summary>
    private void ResetUnprovenTimeouts(long started, string reason)
    {
        if (_consecutiveUnprovenTimeouts == 0) return;
        _diagnostics.Record(new ProviderDiagnostic("antigravity_unproven_timeouts_reset", ProviderIds.Gemini,
            $"{reason},previous={_consecutiveUnprovenTimeouts}", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
        _consecutiveUnprovenTimeouts = 0;
    }

    /// <summary>The Ready result for a proven zero-spend SUCCESS reading. Shared so a
    /// complete reading printed before a timeout kill takes exactly the same path as a completed run's.</summary>
    private async ValueTask<ProviderReadResult> CompleteReadyAsync(ParsedAntigravityUsage parsed, long started,
        bool userInitiated, CancellationToken cancellationToken)
    {
        // A successful read ends any sign-in hold.
        ClearSignInHold(started, userInitiated);

        var receivedAt = _timeProvider.GetUtcNow();
        var projectId = await _homeReader.ReadProjectIdAsync(cancellationToken).ConfigureAwait(false);
        string scope;
        var historyScopeUnavailable = false;
        if (projectId is null)
        {
            // No valid subject to resolve at all: go straight to the per-session fallback rather than
            // calling the resolver with a placeholder.
            scope = CreateSessionScope(MissingProjectIdSubject);
            historyScopeUnavailable = true;
        }
        else
        {
            try
            {
                scope = await _scopeResolver(ProviderIds.Gemini, "google.antigravity.cli", projectId,
                    cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(scope)) throw new InvalidOperationException("Scope resolver returned an empty scope.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A key-store failure must not hide a current quota reading. The per-instance random
                // scope limits this reading to in-memory history for this app session.
                _diagnostics.Record(new ProviderDiagnostic("antigravity_scope_resolve_failed", ProviderIds.Gemini,
                    exception.GetType().Name, 0));
                scope = CreateSessionScope(projectId);
                historyScopeUnavailable = true;
            }
        }

        var snapshot = new AccountSnapshot(ProviderIds.Gemini, scope, plan: null, receivedAt, receivedAt,
            "google.antigravity.cli", [parsed.Short, parsed.Weekly]);
        return new ProviderReadResult(ProviderStatus.Ready, snapshot, actions: RetryAction(),
            safeErrorCode: historyScopeUnavailable ? HistoryScopeUnavailableCode : null);
    }

    private static ProviderReadResult Failure(ProviderStatus status, string code) =>
        new(status, actions: RetryAction(), safeErrorCode: code);

    private static ProviderReadResult InstallFailure(ProviderStatus status, string code) =>
        new(status, actions:
        [
            new ProviderAction(ProviderActionKind.OpenOfficialInstructions, InstallInstructions.AbsoluteUri),
            new ProviderAction(ProviderActionKind.Retry)
        ], safeErrorCode: code);

    private static ProviderAction[] RetryAction() => [new ProviderAction(ProviderActionKind.Retry)];

    private bool SignInHoldActive()
    {
        lock (_signInGate)
            return _signInRequiredUntilUtc is { } until && _timeProvider.GetUtcNow() < until;
    }

    /// <summary>Starts or restarts the 30-minute hold. <paramref name="reason"/> is the logged status.</summary>
    private ProviderReadResult EnterSignInHold(long started, string reason)
    {
        DateTimeOffset until;
        lock (_signInGate)
        {
            until = _timeProvider.GetUtcNow() + SignInRequiredHold;
            _signInRequiredUntilUtc = until;
        }
        _diagnostics.Record(new ProviderDiagnostic("antigravity_sign_in_required", ProviderIds.Gemini,
            reason, Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
        return InstallFailure(ProviderStatus.NeedsSignIn, SignInRequiredCode);
    }

    /// <summary>Drops the hold after a successful read and logs it when one was
    /// still active, with whether a user-initiated read is what cleared it.</summary>
    private void ClearSignInHold(long started, bool userInitiated)
    {
        bool wasActive;
        lock (_signInGate)
        {
            wasActive = _signInRequiredUntilUtc is { } until && _timeProvider.GetUtcNow() < until;
            _signInRequiredUntilUtc = null;
        }
        if (wasActive)
            _diagnostics.Record(new ProviderDiagnostic("antigravity_sign_in_hold_cleared", ProviderIds.Gemini,
                userInitiated ? "user_initiated_read_succeeded" : "read_succeeded",
                Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
    }

    /// <summary>
    /// Turns the provider off for the rest of this process because a completed run
    /// could not be proven zero-spend. Logs `antigravity_latched` once, with the reason code and exit
    /// code; the output itself is never logged.
    /// </summary>
    private ProviderReadResult LatchOff(long started, string reason, int exitCode, bool byUnprovenTimeouts = false)
    {
        _latchedByUnprovenTimeouts = byUnprovenTimeouts;
        if (Interlocked.Exchange(ref _commandUnsupportedLatched, 1) == 0)
            _diagnostics.Record(new ProviderDiagnostic("antigravity_latched", ProviderIds.Gemini,
                $"{reason};exit_code={exitCode}", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
        return LatchedFailure();
    }

    /// <summary>The result every read reports while latched: a hang latch says the tool stopped responding, any
    /// other latch means the response could not be proven safe.</summary>
    private ProviderReadResult LatchedFailure() => _latchedByUnprovenTimeouts
        ? Failure(ProviderStatus.Offline, UnresponsiveCode)
        : Failure(ProviderStatus.SchemaChanged, CommandUnsupportedCode);

    /// <summary>Refresh clears a latch caused by consecutive silent hangs. Leave the count at one so another
    /// silent hang immediately relatches; a completed run resets the count.</summary>
    private void ClearUnprovenTimeoutLatch(long started)
    {
        _latchedByUnprovenTimeouts = false;
        _consecutiveUnprovenTimeouts = MaxConsecutiveUnprovenTimeouts - 1;
        Interlocked.Exchange(ref _commandUnsupportedLatched, 0);
        _diagnostics.Record(new ProviderDiagnostic("antigravity_latch_cleared_by_refresh", ProviderIds.Gemini,
            "reason=unproven_timeout", Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
    }

    /// <summary>
    /// True when <paramref name="standardOutput"/> is a result the parser proves
    /// spent nothing: a SUCCESS usage result, agy's zero-turn, zero-token failure or sign-in error, or a
    /// safe usage result whose Gemini buckets are missing (the parser raises that only after its zero-spend
    /// check has passed). Anything else -- no JSON line, a model turn, an unexpected parse failure -- is
    /// unproven. Used for the partial output of a run killed at the timeout.
    /// </summary>
    private static bool IsProvenZeroSpend(OutputVerdict verdict) =>
        verdict is not (OutputVerdict.NotZeroSpend or OutputVerdict.NotJson or OutputVerdict.Unrecognized);

    /// <summary>Classification of agy's stdout after one parse.</summary>
    private enum OutputVerdict { UsageReady, SignInRequired, UsageFailed, SchemaChanged, NotZeroSpend, NotJson, Unrecognized }

    /// <summary>Classifies agy's stdout with the same rules as a completed run. An
    /// unexpected parse failure is Unrecognized, and its exception type is returned for the latch reason.</summary>
    private static (OutputVerdict Verdict, ParsedAntigravityUsage? Parsed, string? FailureType) ClassifyOutput(string standardOutput)
    {
        try { return (OutputVerdict.UsageReady, AntigravityUsageParser.Parse(standardOutput), null); }
        catch (AntigravityUsageCommandUnsupportedException) { return (OutputVerdict.NotZeroSpend, null, null); }
        catch (AntigravitySignInRequiredException) { return (OutputVerdict.SignInRequired, null, null); }
        catch (AntigravityUsageFailedException) { return (OutputVerdict.UsageFailed, null, null); }
        catch (JsonException) when (!ContainsJsonObjectLine(standardOutput)) { return (OutputVerdict.NotJson, null, null); }
        catch (JsonException) { return (OutputVerdict.SchemaChanged, null, null); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (OutputVerdict.Unrecognized, null, exception.GetType().Name);
        }
    }

    /// <summary>
    /// True when any line of <paramref name="standardOutput"/> parses as a JSON object. This mirrors the
    /// line search in AntigravityUsageParser.FindLastJsonObjectLine, so a JsonException from the parser
    /// can be split into "no JSON result at all" (latches) and "a JSON result with missing Gemini
    /// buckets" (a schema change that does not).
    /// </summary>
    private static bool ContainsJsonObjectLine(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput)) return false;
        foreach (var line in standardOutput.Split('\n'))
        {
            var candidate = line.Trim();
            if (candidate.Length == 0) continue;
            try
            {
                using var probe = JsonDocument.Parse(candidate);
                if (probe.RootElement.ValueKind == JsonValueKind.Object) return true;
            }
            catch (JsonException)
            {
                // Banner or warning text; keep looking, exactly as the parser does.
            }
        }
        return false;
    }

    private string CreateSessionScope(string subject)
    {
        var input = Encoding.UTF8.GetBytes($"{ProviderIds.Gemini}\0google.antigravity.cli\0{subject}");
        try
        {
            var digest = HMACSHA256.HashData(_sessionScopeKey, input);
            return $"session:{Convert.ToHexString(digest).ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Glideslope.Domain;

namespace Glideslope.Providers;

/// <summary>
/// Reads normalized subscription quota from Claude Code's official CLI and nothing else. `claude auth status
/// --json` says who is signed in and on what plan, `claude -p --output-format json /usage` prints the
/// usage lines, and the CLI renews its own login while doing so. Glideslope never calls an Anthropic web
/// endpoint, never reads the CLI's credential file and never sends a model prompt; every run's envelope
/// must prove zero spend or the provider latches off for the session.
/// A latch persists across restarts for the same CLI build. A manual Refresh can clear an unproven-timeout latch.
/// </summary>
public sealed class ClaudeUsageProvider : IIntentAwareUsageProvider
{
    public const string HistoryScopeUnavailableCode = "claude_history_scope_unavailable";
    internal const string SpentTurnReason = "spent_turn";
    internal const string UnprovenTimeoutReason = "unproven_timeout";
    public const string UnresponsiveCode = "claude_unresponsive";

    /// <summary>Backstop for the auth-status and usage runs; their individual timeouts allow partial output to be
    /// checked before this cancellation fires.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Longest one read can take, including process cleanup after the backstop fires.</summary>
    public static readonly TimeSpan WorstCaseReadDuration = ReadTimeout + ClaudeCliUsageTransport.KillAndDrainAllowance;

    /// <summary>Words that mark a failed run as a sign-in problem; stdout and stderr are checked.</summary>
    internal static readonly string[] LoginWords = ["log in", "login", "sign in", "authenticat", "expired", "unauthorized"];

    private static readonly Uri AuthenticationInstructions = new("https://code.claude.com/docs/en/authentication");
    private readonly IClaudeAuthContextReader _authContextReader;
    private readonly IClaudeUsageTransport _transport;
    private readonly AccountScopeResolver _scopeResolver;
    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _localZone;
    private readonly byte[] _sessionScopeKey = RandomNumberGenerator.GetBytes(32);
    private int _scopeWarningIssued;
    // Design §2 rule 2: set once a run proves a model turn was spent; the CLI is not launched again this session.
    // A persisted latch for this CLI build also sets this; manual Refresh can clear an unproven-timeout latch.
    private volatile bool _latched;
    // The reason determines whether manual Refresh may clear the latch.
    private volatile string? _latchReason;
    // AutomaticRetry may clear only an in-memory latch; a persisted latch requires manual Refresh.
    private volatile bool _latchFromEarlierLaunch;
    // A timed-out run without an envelope cannot prove zero spend. Repeated such runs latch the provider to avoid
    // retrying a command that may spend quota.
    private const int MaxConsecutiveUnprovenTimeouts = 2;
    private int _consecutiveUnprovenTimeouts;

    public ClaudeUsageProvider(
        IClaudeAuthContextReader authContextReader,
        IClaudeUsageTransport transport,
        AccountScopeResolver scopeResolver,
        IProviderDiagnosticSink? diagnostics = null,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? localZone = null)
    {
        _authContextReader = authContextReader ?? throw new ArgumentNullException(nameof(authContextReader));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _diagnostics = diagnostics ?? new NullProviderDiagnosticSink();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localZone = localZone ?? TimeZoneInfo.Local;
    }

    /// <summary>True while a model-spend or unproven-timeout latch is active.</summary>
    public bool IsLatched => _latched;

    /// <summary>A scheduled poll.</summary>
    public ValueTask<ProviderReadResult> ReadAsync(CancellationToken cancellationToken) =>
        ReadCoreAsync(ProviderReadIntent.Scheduled, cancellationToken);

    /// <summary>A manual read may clear an unproven-timeout latch. Automatic retries can clear only a latch set
    /// during this launch; persisted latches require manual Refresh.</summary>
    public ValueTask<ProviderReadResult> ReadAsync(ProviderReadIntent intent, CancellationToken cancellationToken) =>
        ReadCoreAsync(intent, cancellationToken);

    private async ValueTask<ProviderReadResult> ReadCoreAsync(ProviderReadIntent intent, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ProviderReadResult? result = null;
        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            result = await PerformReadAsync(intent, linked.Token).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The provider backstop fired before either CLI run returned.
            _diagnostics.Record(new ProviderDiagnostic("claude_read_timeout", ProviderIds.Claude, "backstop",
                Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
            result = Failure(ProviderStatus.Offline, "claude_read_timeout");
            return result;
        }
        catch (Exception exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("claude_read_failed", ProviderIds.Claude, exception.GetType().Name,
                Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
            result = Failure(ProviderStatus.UnknownError, "claude_read_failed");
            return result;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var status = result?.Status.ToString() ?? "cancelled";
            if (result?.SafeErrorCode == HistoryScopeUnavailableCode)
            {
                if (Interlocked.Exchange(ref _scopeWarningIssued, 1) == 0)
                    _diagnostics.Record(new ProviderDiagnostic("provider_history_scope_unavailable", ProviderIds.Claude,
                        "session_scope", Math.Max(0, (long)elapsed.TotalMilliseconds)));
            }
            else
            {
                Interlocked.Exchange(ref _scopeWarningIssued, 0);
            }
            _diagnostics.Record(new ProviderDiagnostic("provider_read_finished", ProviderIds.Claude,
                $"{status},code={result?.SafeErrorCode ?? "none"}", Math.Max(0, (long)elapsed.TotalMilliseconds)));
        }
    }

    private async ValueTask<ProviderReadResult> PerformReadAsync(ProviderReadIntent intent, CancellationToken cancellationToken)
    {
        var manual = intent == ProviderReadIntent.Manual;
        var automaticRetry = intent == ProviderReadIntent.AutomaticRetry;
        var auth = await _authContextReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (auth.FailureStatus is { } failure)
            return Failure(failure, auth.SafeErrorCode);
        var identity = auth.Context ?? throw new InvalidOperationException("Auth reader returned neither context nor failure.");

        if (_latched)
        {
            if (_latchReason == UnprovenTimeoutReason && (manual || (automaticRetry && !_latchFromEarlierLaunch)))
            {
                ClearUnprovenLatch(manual ? "in_memory" : "in_memory,automatic_retry");
            }
            else
            {
                if (automaticRetry && _latchReason == UnprovenTimeoutReason)
                    _diagnostics.Record(new ProviderDiagnostic("claude_latch_kept", ProviderIds.Claude,
                        "automatic_retry,latched_by_earlier_launch", 0));
                _diagnostics.Record(new ProviderDiagnostic("claude_read_skipped", ProviderIds.Claude, "latched", 0));
                return LatchedFailure(_latchReason);
            }
        }

        // A latch for this CLI build holds across restarts; the transport clears it when the build changes.
        if (_transport.ReadPersistedLatch() is { } persisted)
        {
            if (manual && persisted.Reason == UnprovenTimeoutReason)
            {
                ClearUnprovenLatch("persisted");
            }
            else
            {
                _latched = true;
                _latchReason = persisted.Reason;
                _latchFromEarlierLaunch = true;
                _diagnostics.Record(new ProviderDiagnostic("claude_latched", ProviderIds.Claude,
                    $"persisted,reason={persisted.Reason},since={persisted.LatchedAtUtc:u}", 0));
                return LatchedFailure(persisted.Reason);
            }
        }

        var run = await _transport.ReadAsync(cancellationToken).ConfigureAwait(false);
        var receivedAt = _timeProvider.GetUtcNow();

        // A timed-out or failed run may still have printed an envelope; spend is checked before anything else.
        var envelope = ClaudeCliUsageParser.ReadEnvelope(run.StandardOutput);
        if (envelope.Verdict == ClaudeCliEnvelopeVerdict.SpentTurn)
            return Latch($"spent_turn,{envelope.Reason}", SpentTurnReason, run);

        switch (run.Outcome)
        {
            case ClaudeCliOutcome.ExecutableNotFound:
                return Failure(ProviderStatus.MissingApplication, "claude_cli_missing");
            case ClaudeCliOutcome.LaunchFailed:
                return Failure(ProviderStatus.UnknownError, "claude_cli_launch_failed");
            case ClaudeCliOutcome.TimedOut:
                if (envelope.Verdict == ClaudeCliEnvelopeVerdict.ZeroSpend)
                {
                    // The envelope arrived but the process lingered: the reading is proven and usable.
                    _consecutiveUnprovenTimeouts = 0;
                    break;
                }
                if (++_consecutiveUnprovenTimeouts >= MaxConsecutiveUnprovenTimeouts)
                    return Latch($"unproven_timeout,consecutive={_consecutiveUnprovenTimeouts}", UnprovenTimeoutReason, run);
                _diagnostics.Record(new ProviderDiagnostic("claude_cli_timeout_unproven", ProviderIds.Claude,
                    $"consecutive={_consecutiveUnprovenTimeouts}", 0));
                return Failure(ProviderStatus.Offline, "claude_cli_timeout");
        }
        _consecutiveUnprovenTimeouts = 0;
        if (run.StandardOutputTooLarge)
            return Failure(ProviderStatus.SchemaChanged, "claude_cli_output_too_large");
        if (envelope.Verdict != ClaudeCliEnvelopeVerdict.ZeroSpend)
        {
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_envelope_rejected", ProviderIds.Claude,
                $"{envelope.Reason},exit={run.ExitCode}", 0));
            // The CLI can report revoked cached credentials as login wording on either output stream.
            if (run.ExitCode != 0 || envelope.Reason == "is_error")
            {
                var inStdout = MentionsLogin(run.StandardOutput);
                if (inStdout || run.StandardErrorMentionsLogin)
                {
                    _diagnostics.Record(new ProviderDiagnostic("claude_cli_login_required", ProviderIds.Claude,
                        inStdout ? "stdout" : "stderr", 0));
                    return Failure(ProviderStatus.AuthenticationExpired, "claude_cli_login_required");
                }
            }
            return run.ExitCode != 0
                ? Failure(ProviderStatus.UnknownError, "claude_cli_failed")
                : Failure(ProviderStatus.SchemaChanged, "claude_cli_envelope");
        }

        ParsedClaudeCliUsage parsed;
        try
        {
            parsed = ClaudeCliUsageParser.Parse(envelope.ResultText!, receivedAt, _localZone);
        }
        catch (FormatException ex)
        {
            // The message names the line label or the unparsed reset text, never the account.
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_usage_rejected", ProviderIds.Claude, ex.Message, 0));
            return Failure(ProviderStatus.SchemaChanged, "claude_cli_usage_shape");
        }
        if (parsed.UnknownZone is { } zone)
            _diagnostics.Record(new ProviderDiagnostic("claude_cli_zone_unknown", ProviderIds.Claude, zone, 0));

        string scope;
        var historyScopeUnavailable = false;
        try
        {
            scope = await _scopeResolver(ProviderIds.Claude, "anthropic.claude-code", identity.Subject,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(scope)) throw new InvalidOperationException("Scope resolver returned an empty scope.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A key-store failure must not hide a current quota reading. The per-instance
            // random scope limits this reading to in-memory history for this app session.
            _diagnostics.Record(new ProviderDiagnostic("claude_scope_resolve_failed", ProviderIds.Claude, exception.GetType().Name, 0));
            scope = CreateSessionScope(identity.Subject);
            historyScopeUnavailable = true;
        }

        var buckets = new List<QuotaBucket>(3);
        if (parsed.ShortWindow is not null) buckets.Add(parsed.ShortWindow);
        buckets.Add(parsed.WeeklyWindow);
        if (parsed.FeaturedWeeklyWindow is not null) buckets.Add(parsed.FeaturedWeeklyWindow);
        var snapshot = new AccountSnapshot(ProviderIds.Claude, scope, identity.Plan, receivedAt, receivedAt,
            ClaudeCliUsageParser.SourceId, buckets);
        return new ProviderReadResult(ProviderStatus.Ready, snapshot, actions: RetryAction(),
            safeErrorCode: historyScopeUnavailable ? HistoryScopeUnavailableCode : null);
    }

    /// <summary>Latches the provider off, logs claude_latched with <paramref name="logStatus"/>, and persists the
    /// latch against the CLI build that ran.</summary>
    private ProviderReadResult Latch(string logStatus, string reason, ClaudeCliResult run)
    {
        _latched = true;
        _latchReason = reason;
        _latchFromEarlierLaunch = false;
        _diagnostics.Record(new ProviderDiagnostic("claude_latched", ProviderIds.Claude, logStatus, 0));
        if (run.VersionKey is { } versionKey)
            _transport.PersistLatch(versionKey, reason);
        else
            _diagnostics.Record(new ProviderDiagnostic("claude_latch_not_persisted", ProviderIds.Claude, "version_unknown", 0));
        return reason == UnprovenTimeoutReason
            ? Failure(ProviderStatus.Offline, UnresponsiveCode)
            : Failure(ProviderStatus.UnknownError, "claude_cli_spent_turn");
    }

    /// <summary>Reports not responding for a timeout latch and a latched error for a spent turn.</summary>
    private static ProviderReadResult LatchedFailure(string? reason) => reason == UnprovenTimeoutReason
        ? Failure(ProviderStatus.Offline, UnresponsiveCode)
        : Failure(ProviderStatus.UnknownError, "claude_latched");

    /// <summary>Manual Refresh clears an unproven-timeout latch, never a spent-turn latch. The next run is checked
    /// normally and latches again if it spends a turn.</summary>
    private void ClearUnprovenLatch(string source)
    {
        _latched = false;
        _latchReason = null;
        _latchFromEarlierLaunch = false;
        _consecutiveUnprovenTimeouts = 0;
        _transport.ClearPersistedLatch("manual_refresh");
        _diagnostics.Record(new ProviderDiagnostic("claude_latch_cleared_by_refresh", ProviderIds.Claude,
            $"reason={UnprovenTimeoutReason},source={source}", 0));
    }

    private static bool MentionsLogin(string? text) =>
        text is not null && LoginWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static ProviderReadResult Failure(ProviderStatus status, string? code) =>
        new(status, actions: status is ProviderStatus.NeedsSignIn or ProviderStatus.AuthenticationExpired
            ? AuthenticationActions()
            : RetryAction(), safeErrorCode: code);

    private static ProviderAction[] RetryAction() => [new ProviderAction(ProviderActionKind.Retry)];

    private static ProviderAction[] AuthenticationActions() =>
    [
        new ProviderAction(ProviderActionKind.LaunchOfficialApplication, "claude"),
        new ProviderAction(ProviderActionKind.OpenOfficialInstructions, AuthenticationInstructions.AbsoluteUri),
        new ProviderAction(ProviderActionKind.Retry)
    ];

    private string CreateSessionScope(string subject)
    {
        var input = Encoding.UTF8.GetBytes($"{ProviderIds.Claude}\0anthropic.claude-code\0{subject}");
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

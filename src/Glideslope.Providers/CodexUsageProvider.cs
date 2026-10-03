using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glideslope.Domain;

namespace Glideslope.Providers;

/// <summary>Reads read-only Codex subscription quota through the CLI-owned app-server session.</summary>
public sealed class CodexUsageProvider
    : IUsageProvider
{
    public const string HistoryScopeUnavailableCode = "codex_history_scope_unavailable";
    private static readonly Uri AuthenticationInstructions = new("https://developers.openai.com/codex/cli");
    private readonly ICodexAppServerTransport _transport;
    private readonly AccountScopeResolver _scopeResolver;
    private readonly IProviderDiagnosticSink _diagnostics;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _sessionScopeKey = RandomNumberGenerator.GetBytes(32);
    private int _scopeWarningIssued;

    public CodexUsageProvider(
        ICodexAppServerTransport transport,
        AccountScopeResolver scopeResolver,
        IProviderDiagnosticSink? diagnostics = null,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _diagnostics = diagnostics ?? new NullProviderDiagnosticSink();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ProviderReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        ProviderReadResult? result = null;
        try
        {
            var response = await _transport.ReadAsync(cancellationToken).ConfigureAwait(false);
            ParsedCodexUsage parsed;
            try
            {
                parsed = CodexUsageParser.Parse(response.AccountJson, response.RateLimitsJson, _timeProvider.GetUtcNow());
            }
            catch (CodexAccountStateException exception)
            {
                result = Failure(exception.Status, exception.SafeErrorCode);
                return result;
            }
            catch (JsonException)
            {
                result = Failure(ProviderStatus.SchemaChanged, "codex_usage_schema_changed");
                return result;
            }

            var receivedAt = _timeProvider.GetUtcNow();
            string scope;
            var historyScopeUnavailable = false;
            try
            {
                scope = await _scopeResolver(ProviderIds.Codex, "openai.codex", parsed.Subject,
                    cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(scope)) throw new InvalidOperationException("Scope resolver returned an empty scope.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _diagnostics.Record(new ProviderDiagnostic("codex_scope_resolve_failed", ProviderIds.Codex, exception.GetType().Name, 0));
                scope = CreateSessionScope(parsed.Subject);
                historyScopeUnavailable = true;
            }

            var snapshot = new AccountSnapshot(ProviderIds.Codex, scope, parsed.Plan, receivedAt, receivedAt,
                "openai.codex.app-server", parsed.Buckets, parsed.ResetCredits);
            result = new ProviderReadResult(ProviderStatus.Ready, snapshot, actions: RetryAction(),
                safeErrorCode: historyScopeUnavailable
                    ? HistoryScopeUnavailableCode
                    : parsed.CreditWarnings.IsEmpty ? null : "codex_reset_credit_schema_warning");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CodexAppServerTransportException exception)
        {
            result = Failure(exception.Status, exception.SafeErrorCode);
            return result;
        }
        catch (JsonException)
        {
            result = Failure(ProviderStatus.SchemaChanged, "codex_app_server_protocol");
            return result;
        }
        catch (IOException)
        {
            result = Failure(ProviderStatus.Offline, "codex_app_server_io");
            return result;
        }
        catch (Exception exception)
        {
            _diagnostics.Record(new ProviderDiagnostic("codex_read_failed", ProviderIds.Codex, exception.GetType().Name,
                Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
            result = Failure(ProviderStatus.UnknownError, "codex_read_failed");
            return result;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var status = result?.Status.ToString() ?? "cancelled";
            if (result?.SafeErrorCode == HistoryScopeUnavailableCode)
            {
                if (Interlocked.Exchange(ref _scopeWarningIssued, 1) == 0)
                    _diagnostics.Record(new ProviderDiagnostic("provider_history_scope_unavailable", ProviderIds.Codex,
                        "session_scope", Math.Max(0, (long)elapsed.TotalMilliseconds)));
            }
            else
            {
                Interlocked.Exchange(ref _scopeWarningIssued, 0);
            }
            if (result?.SafeErrorCode == "codex_reset_credit_schema_warning")
                _diagnostics.Record(new ProviderDiagnostic("provider_optional_schema_warning", ProviderIds.Codex,
                    "reset_credit", Math.Max(0, (long)elapsed.TotalMilliseconds)));
            _diagnostics.Record(new ProviderDiagnostic("provider_read_finished", ProviderIds.Codex,
                $"{status},code={result?.SafeErrorCode ?? "none"}", Math.Max(0, (long)elapsed.TotalMilliseconds)));
        }
    }

    private static ProviderReadResult Failure(ProviderStatus status, string? code) =>
        new(status, actions: status is ProviderStatus.NeedsSignIn or ProviderStatus.AuthenticationExpired
            ? AuthenticationActions()
            : RetryAction(), safeErrorCode: code);

    private static ProviderAction[] RetryAction() => [new ProviderAction(ProviderActionKind.Retry)];

    private static ProviderAction[] AuthenticationActions() =>
    [
        new ProviderAction(ProviderActionKind.LaunchOfficialApplication, "codex"),
        new ProviderAction(ProviderActionKind.OpenOfficialInstructions, AuthenticationInstructions.AbsoluteUri),
        new ProviderAction(ProviderActionKind.Retry)
    ];

    private string CreateSessionScope(string subject)
    {
        var input = Encoding.UTF8.GetBytes($"{ProviderIds.Codex}\0openai.codex\0{subject}");
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
